using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Core-side tests for the input color transform of PLAN.md step 37: the <see cref="ColorProfiles"/>
/// registry + DJI log detection policy, the <c>builtin.colortransform</c> catalog entry, the
/// insert-at-front command, resolve-through to the render plan, and the exporter's pass-through plan
/// surgery (<see cref="RenderGraph.StripEffects"/>).
/// </summary>
public class ColorTransformTests
{
    // ── ColorProfiles ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("D-Log M", ColorProfiles.DjiDLogM)]
    [InlineData("dlog-m", ColorProfiles.DjiDLogM)]
    [InlineData("DLogM", ColorProfiles.DjiDLogM)]
    [InlineData("d_log_m", ColorProfiles.DjiDLogM)]
    [InlineData("D-Log", ColorProfiles.DjiDLog)]
    [InlineData("dlog", ColorProfiles.DjiDLog)]
    [InlineData("Shot in D-Log on Mavic 3", ColorProfiles.DjiDLog)]
    [InlineData("rec709", "")]
    [InlineData("", "")]
    public void DetectDjiLog_Recognises_Profile_Spellings(string tagValue, string expected)
    {
        var metadata = new Dictionary<string, string> { ["comment"] = tagValue };
        Assert.Equal(expected, ColorProfiles.DetectDjiLog(metadata));
    }

    [Fact]
    public void DetectDjiLog_Prefers_The_More_Specific_DLogM_Over_A_DLog_Tag()
    {
        var metadata = new Dictionary<string, string>
        {
            ["a"] = "d-log",        // scanned first — the generic profile
            ["b"] = "d-log m",      // the more specific signal must win regardless of order
        };
        Assert.Equal(ColorProfiles.DjiDLogM, ColorProfiles.DetectDjiLog(metadata));
    }

    [Fact]
    public void Profile_Indices_Round_Trip_And_Clamp()
    {
        for (int i = 0; i < ColorProfiles.All.Count; i++)
            Assert.Equal(i, ColorProfiles.IndexOf(ColorProfiles.FromIndex(i)));
        Assert.Equal(-1, ColorProfiles.IndexOf("unknown.profile"));
        Assert.Equal(ColorProfiles.All[0], ColorProfiles.FromIndex(-3));
        Assert.Equal(ColorProfiles.All[^1], ColorProfiles.FromIndex(99));
        Assert.Equal(ColorProfiles.All.Count, ColorProfiles.DisplayNames.Count);
    }

    // ── Catalog ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_Registers_The_Color_Transform_With_A_Profile_Parameter()
    {
        EffectDescriptor? d = EffectCatalog.Find(EffectTypeIds.ColorTransform);
        Assert.NotNull(d);
        Assert.Equal(EffectCategory.Color, d!.Category);
        EffectParameterDescriptor p = Assert.Single(d.Parameters);
        Assert.Equal(EffectParamNames.SourceProfile, p.Name);
        Assert.Equal(0.0, p.Default);
        Assert.Equal(ColorProfiles.All.Count - 1, p.Max); // the slider range spans the profile indices
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.ColorTransform)); // routes to the video shader chain
    }

    // ── InsertEffectAtCommand ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void InsertEffectAtCommand_Prepends_And_Undo_Removes()
    {
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero);
        var grade = new EffectInstance(EffectTypeIds.Color);
        clip.Effects.Add(grade);

        var transform = new EffectInstance(EffectTypeIds.ColorTransform).Set(EffectParamNames.SourceProfile, 1);
        var history = new EditHistory();
        history.Execute(new InsertEffectAtCommand(clip, transform, 0));

        Assert.Equal(2, clip.Effects.Count);
        Assert.Same(transform, clip.Effects[0]); // the input transform runs before the creative grade
        Assert.Same(grade, clip.Effects[1]);

        history.Undo();
        Assert.Same(grade, Assert.Single(clip.Effects));

        history.Redo();
        Assert.Same(transform, clip.Effects[0]);
    }

    [Fact]
    public void InsertEffectAtCommand_Clamps_An_Out_Of_Range_Index()
    {
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero);
        var effect = new EffectInstance(EffectTypeIds.Brightness);
        new InsertEffectAtCommand(clip, effect, 99).Apply();
        Assert.Same(effect, Assert.Single(clip.Effects));
    }

    // ── Resolve + plan ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolved_Plan_Carries_The_Transform_First_With_Its_Profile()
    {
        Project project = OneClipProject(out Clip clip);
        clip.Effects.Insert(0, new EffectInstance(EffectTypeIds.ColorTransform).Set(EffectParamNames.SourceProfile, 1));

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(0.5));
        VideoLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(2, layer.Effects.Count);
        Assert.Equal(EffectTypeIds.ColorTransform, layer.Effects[0].EffectTypeId);
        Assert.Equal(1.0, layer.Effects[0].Get(EffectParamNames.SourceProfile));
        Assert.Equal(EffectTypeIds.Brightness, layer.Effects[1].EffectTypeId);
    }

    [Fact]
    public void StripEffects_Removes_Only_The_Transform_And_Reuses_Untouched_Plans()
    {
        Project project = OneClipProject(out Clip clip);
        clip.Effects.Insert(0, new EffectInstance(EffectTypeIds.ColorTransform));

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(0.5));
        VideoFramePlan stripped = RenderGraph.StripEffects(plan, EffectTypeIds.ColorTransform);

        ResolvedEffect remaining = Assert.Single(Assert.Single(stripped.Layers).Effects);
        Assert.Equal(EffectTypeIds.Brightness, remaining.EffectTypeId); // the grade survives
        Assert.NotSame(plan, stripped);

        // A plan with no transform anywhere comes back as the same instance (no wasted copies).
        Assert.Same(stripped, RenderGraph.StripEffects(stripped, EffectTypeIds.ColorTransform));
    }

    private static Project OneClipProject(out Clip clip)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(64, 64), 48000);
        var track = new VideoTrack { Name = "V1" };
        clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return new Project(timeline);
    }
}
