using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The Looks browser's Core model (plan/features/looks-browser.md): the curated catalog stays inside the tier-2
/// scope guard of ARCHITECTURE §18, a look expands to one undoable edit appended after any tier-1 transform, and
/// Save Look captures only the clip's enabled grading effects.
/// </summary>
public sealed class LooksTests
{
    private static Clip MakeClip() =>
        new(MediaRefId.New(), Timecode.FromSeconds(0), Timecode.FromSeconds(5), Timecode.FromSeconds(2));

    // ── Catalog ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuiltIns_Never_Carry_The_Tier1_Transform_And_Only_Use_Look_Effects()
    {
        Assert.NotEmpty(LooksCatalog.BuiltIns);
        foreach (Look look in LooksCatalog.BuiltIns)
            foreach (LookEntry entry in look.Entries)
            {
                Assert.NotEqual(EffectTypeIds.ColorTransform, entry.EffectTypeId);
                Assert.True(LookRules.IsLookEffect(entry.EffectTypeId), $"{look.Name}: {entry.EffectTypeId}");
            }
    }

    [Fact]
    public void BuiltIns_Have_Unique_Ids_Known_Groups_And_Descriptions()
    {
        Assert.Equal(LooksCatalog.BuiltIns.Count, LooksCatalog.BuiltIns.Select(l => l.Id).Distinct().Count());
        Assert.Equal(LooksCatalog.BuiltIns.Count, LooksCatalog.BuiltIns.Select(l => l.Name).Distinct().Count());
        foreach (Look look in LooksCatalog.BuiltIns)
        {
            Assert.True(look.IsBuiltIn);
            Assert.StartsWith(Look.BuiltInPrefix, look.Id);
            Assert.Contains(look.Group, LooksCatalog.Groups);
            Assert.False(string.IsNullOrWhiteSpace(look.Description));
            Assert.NotEmpty(look.Entries);
        }
        // Every group is populated, and the catalog is listed in group order.
        Assert.All(LooksCatalog.Groups, g => Assert.Contains(LooksCatalog.BuiltIns, l => l.Group == g));
        int[] order = [.. LooksCatalog.BuiltIns.Select(l => LooksCatalog.Groups.ToList().IndexOf(l.Group))];
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void BuiltIn_Values_Are_Declared_In_Range_And_Not_Neutral()
    {
        foreach (Look look in LooksCatalog.BuiltIns)
            foreach (LookEntry entry in look.Entries)
            {
                EffectDescriptor d = EffectCatalog.Find(entry.EffectTypeId)!;
                Assert.NotEmpty(entry.Values); // an all-neutral entry would add a do-nothing effect
                foreach ((string name, double value) in entry.Values)
                {
                    EffectParameterDescriptor? p = d.Parameters.SingleOrDefault(x => x.Name == name);
                    Assert.True(p is not null, $"{look.Name}: {entry.EffectTypeId} does not declare {name}");
                    Assert.InRange(value, p!.Min, p.Max);
                }
            }
    }

    [Fact]
    public void IsLookEffect_Admits_Grading_Effects_Only()
    {
        Assert.True(LookRules.IsLookEffect(EffectTypeIds.Color));
        Assert.True(LookRules.IsLookEffect(EffectTypeIds.Curves));
        Assert.True(LookRules.IsLookEffect(EffectTypeIds.CreativeLut));
        Assert.True(LookRules.IsLookEffect(EffectTypeIds.BlackWhite));
        Assert.False(LookRules.IsLookEffect(EffectTypeIds.ColorTransform)); // tier 1
        Assert.False(LookRules.IsLookEffect(EffectTypeIds.Transform));
        Assert.False(LookRules.IsLookEffect(EffectTypeIds.Glow));
        Assert.False(LookRules.IsLookEffect(EffectTypeIds.AudioGain));
        Assert.False(LookRules.IsLookEffect("plugin.not.installed"));
    }

    [Fact]
    public void CreativeLut_Is_A_Color_Effect_With_A_Cube_Asset_And_Intensity()
    {
        EffectDescriptor d = EffectCatalog.Find(EffectTypeIds.CreativeLut)!;
        Assert.Equal(EffectCategory.Color, d.Category);
        Assert.Equal("LU", d.ShortCode);
        EffectParameterDescriptor file = d.Parameters.Single(p => p.Name == EffectParamNames.LutFile);
        Assert.Equal(ParameterKind.Asset, file.Kind);
        Assert.Equal(["cube"], file.AssetType!.Extensions);
        Assert.Equal(1.0, d.Parameters.Single(p => p.Name == EffectParamNames.Mix).Default);
        Assert.Empty(d.CreateInstance().Assets); // no default file
    }

    // ── Apply ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_Appends_After_The_Tier1_Transform_With_Values_And_Undoes_In_One_Step()
    {
        Clip clip = MakeClip();
        clip.Effects.Add(new EffectInstance(EffectTypeIds.ColorTransform).Set(EffectParamNames.SourceProfile, 1));
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Color)!.CreateInstance());
        Look look = LooksCatalog.Find("builtin.look.teal-orange")!;

        LookApplyResult result = LookApplication.Build(look, clip);
        var history = new EditHistory();
        history.Execute(result.Command!);

        Assert.Empty(result.Skipped);
        Assert.Equal(2 + look.Entries.Count, clip.Effects.Count);
        Assert.Equal(EffectTypeIds.ColorTransform, clip.Effects[0].EffectTypeId); // tier 1 stays first
        Assert.Equal(look.Entries.Select(e => e.EffectTypeId), clip.Effects.Skip(2).Select(e => e.EffectTypeId));
        EffectInstance wheels = clip.Effects[2];
        Assert.Equal(look.Entries[0].Values[EffectParamNames.LiftB], wheels.Parameters[EffectParamNames.LiftB].Evaluate(Timecode.Zero), 6);
        Assert.Equal(0.0, wheels.Parameters[EffectParamNames.GammaMaster].Evaluate(Timecode.Zero)); // omitted → default

        Assert.True(history.Undo()); // one undo removes the whole look
        Assert.Equal(2, clip.Effects.Count);
    }

    [Fact]
    public void Apply_Skips_Unknown_And_Tier1_Entries_Ignores_Bad_Values_And_Clamps()
    {
        var look = new Look("user.test", "Hand-edited", Look.UserGroup, null,
        [
            new LookEntry("plugin.gone", new Dictionary<string, double> { ["x"] = 1 }),
            new LookEntry(EffectTypeIds.ColorTransform, new Dictionary<string, double> { [EffectParamNames.SourceProfile] = 1 }),
            new LookEntry(EffectTypeIds.Color, new Dictionary<string, double>
            {
                [EffectParamNames.Saturation] = 99,        // clamped to the declared max (2)
                [EffectParamNames.Exposure] = double.NaN,  // ignored → default 0
                ["undeclared"] = 5,                        // ignored
            }),
        ]);
        Clip clip = MakeClip();

        LookApplyResult result = LookApplication.Build(look, clip);
        result.Command!.Apply();

        Assert.Equal(["plugin.gone", EffectTypeIds.ColorTransform], result.Skipped);
        EffectInstance color = Assert.Single(clip.Effects);
        Assert.Equal(2.0, color.Parameters[EffectParamNames.Saturation].Evaluate(Timecode.Zero));
        Assert.Equal(0.0, color.Parameters[EffectParamNames.Exposure].Evaluate(Timecode.Zero));
        Assert.False(color.Parameters.ContainsKey("undeclared"));
    }

    [Fact]
    public void Apply_With_No_Surviving_Entry_Yields_No_Command()
    {
        var look = new Look("user.x", "Gone", Look.UserGroup, null,
            [new LookEntry("plugin.gone", new Dictionary<string, double>())]);
        LookApplyResult result = LookApplication.Build(look, MakeClip());
        Assert.Null(result.Command);
        Assert.Empty(result.Added);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Apply_Carries_The_Creative_Lut_Path_But_Only_Onto_Declared_Assets()
    {
        var look = new Look("user.lut", "My LUT", Look.UserGroup, null,
        [
            new LookEntry(EffectTypeIds.CreativeLut,
                new Dictionary<string, double> { [EffectParamNames.Mix] = 0.5 },
                new Dictionary<string, string> { [EffectParamNames.LutFile] = "/looks/a.cube", ["bogus"] = "/x" }),
        ]);
        Clip clip = MakeClip();
        LookApplication.Build(look, clip).Command!.Apply();

        EffectInstance lut = Assert.Single(clip.Effects);
        Assert.Equal("/looks/a.cube", lut.Assets[EffectParamNames.LutFile]);
        Assert.False(lut.Assets.ContainsKey("bogus"));
        Assert.Equal(0.5, lut.Parameters[EffectParamNames.Mix].Evaluate(Timecode.Zero));
    }

    [Fact]
    public void Applying_A_Look_Twice_Gives_Independent_Instances()
    {
        Look look = LooksCatalog.BuiltIns[0];
        Clip clip = MakeClip();
        LookApplication.Build(look, clip).Command!.Apply();
        LookApplication.Build(look, clip).Command!.Apply();
        Assert.Equal(2 * look.Entries.Count, clip.Effects.Distinct().Count());
    }

    // ── Capture (Save Look…) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_Keeps_Only_Enabled_Tier2_Grading_Effects_In_Order()
    {
        Clip clip = MakeClip();
        clip.Effects.Add(new EffectInstance(EffectTypeIds.ColorTransform).Set(EffectParamNames.SourceProfile, 2));
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Transform)!.CreateInstance());
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.WhiteBalance)!.CreateInstance().Set(EffectParamNames.Temperature, 25));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Curves) { Enabled = false });
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Glow)!.CreateInstance());
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.CreativeLut)!.CreateInstance().SetAsset(EffectParamNames.LutFile, "/l.cube"));

        Look? look = LookApplication.Capture(clip, "  Mine  ", Timecode.Zero);

        Assert.NotNull(look);
        Assert.Equal("Mine", look!.Name);
        Assert.Equal(Look.UserGroup, look.Group);
        Assert.False(look.IsBuiltIn);
        Assert.StartsWith(Look.UserPrefix, look.Id);
        Assert.Equal([EffectTypeIds.WhiteBalance, EffectTypeIds.CreativeLut], look.Entries.Select(e => e.EffectTypeId));
        Assert.Equal(25, look.Entries[0].Values[EffectParamNames.Temperature]);
        Assert.Equal("/l.cube", look.Entries[1].Assets![EffectParamNames.LutFile]);
    }

    [Fact]
    public void Capture_Snapshots_Keyframed_Values_At_The_Given_Time()
    {
        Clip clip = MakeClip();
        EffectInstance color = EffectCatalog.Find(EffectTypeIds.Color)!.CreateInstance();
        color.Set(EffectParamNames.Exposure, AnimatableValue.Animated(
        [
            new Keyframe(Timecode.FromSeconds(0), 0.0),
            new Keyframe(Timecode.FromSeconds(2), 2.0),
        ]));
        clip.Effects.Add(color);

        Look look = LookApplication.Capture(clip, "Mid", Timecode.FromSeconds(1))!;
        Assert.Equal(1.0, look.Entries[0].Values[EffectParamNames.Exposure], 6);
    }

    [Fact]
    public void Capture_Of_A_Clip_With_No_Grade_Is_Null()
    {
        Clip clip = MakeClip();
        clip.Effects.Add(new EffectInstance(EffectTypeIds.ColorTransform));
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Transform)!.CreateInstance());
        Assert.Null(LookApplication.Capture(clip, "Nothing", Timecode.Zero));
    }

    [Fact]
    public void Captured_Look_Round_Trips_Through_Apply()
    {
        Clip source = MakeClip();
        source.Effects.Add(EffectCatalog.Find(EffectTypeIds.Color)!.CreateInstance().Set(EffectParamNames.Contrast, 1.4));
        Look look = LookApplication.Capture(source, "Copy", Timecode.Zero)!;

        Clip target = MakeClip();
        LookApplication.Build(look, target).Command!.Apply();

        EffectInstance applied = Assert.Single(target.Effects);
        Assert.NotSame(source.Effects[0], applied);
        Assert.Equal(1.4, applied.Parameters[EffectParamNames.Contrast].Evaluate(Timecode.Zero));
    }
}
