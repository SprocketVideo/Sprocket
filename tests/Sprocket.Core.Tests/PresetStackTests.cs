using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The one-tap Toy Cassette Camera look (plan/features/toy-cassette-camera.md, phases 5–6): a <see cref="PresetStack"/>
/// expands over a clip and its linked companion into one undoable edit — video entries on the video half, the
/// Cassette entry on the audio half — reports what an unlinked clip could not take, and re-applies idempotently.
/// </summary>
public sealed class PresetStackTests
{
    private static Timeline LinkedPair(out Clip video, out Clip audio)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var v = new VideoTrack();
        var a = new AudioTrack();
        Guid link = Guid.NewGuid();
        MediaRefId media = MediaRefId.New();
        video = new Clip(media, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero) { LinkGroupId = link };
        audio = new Clip(media, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero) { LinkGroupId = link };
        v.Clips.Add(video);
        a.Clips.Add(audio);
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        return timeline;
    }

    private static string[] Types(Clip clip) => [.. clip.Effects.Select(e => e.EffectTypeId)];

    // ── Catalog ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Toy_Cassette_Camera_Stacks_Are_Clean_Worn_Tape_And_Low_Light()
    {
        Assert.Equal(new[] { "Clean", "Worn Tape", "Low Light" }, ToyCassetteCameraStacks.All.Select(s => s.Name));
        Assert.All(ToyCassetteCameraStacks.All, s => Assert.Equal("Toy Cassette Camera", s.Group));
        Assert.Equal("Toy Cassette Camera ▸ Worn Tape", ToyCassetteCameraStacks.WornTape.Title);
        Assert.All(ToyCassetteCameraStacks.All, s => Assert.Contains(s, PresetStackCatalog.All));
    }

    [Fact]
    public void Every_Stack_Names_Real_Descriptors_And_Presets_Of_The_Right_Kind()
    {
        foreach (PresetStack stack in PresetStackCatalog.All)
        {
            Assert.NotEmpty(stack.VideoEntries);
            Assert.False(string.IsNullOrWhiteSpace(stack.Description));
            foreach ((PresetStackEntry entry, bool audio) in stack.VideoEntries.Select(e => (e, false))
                         .Concat(stack.AudioEntries.Select(e => (e, true))))
            {
                EffectDescriptor d = EffectCatalog.Find(entry.EffectTypeId)
                    ?? throw new Xunit.Sdk.XunitException($"{stack.Title}: unknown effect {entry.EffectTypeId}");
                Assert.Equal(audio, EffectTypeIds.IsAudio(entry.EffectTypeId));
                if (entry.PresetName is not null)
                    Assert.NotNull(d.FindPreset(entry.PresetName));
                foreach ((string name, double value) in entry.Values ?? new Dictionary<string, double>())
                {
                    EffectParameterDescriptor p = d.Parameters.Single(x => x.Name == name);
                    Assert.InRange(value, p.Min, p.Max);
                }
            }
        }
    }

    [Fact]
    public void Toy_Cassette_Camera_Stacks_Pair_Posterize_15_And_Echo_With_Same_Named_Presets()
    {
        foreach (PresetStack stack in ToyCassetteCameraStacks.All)
        {
            Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.ToyCam },
                stack.VideoEntries.Select(e => e.EffectTypeId));
            Assert.Equal(15.0, stack.VideoEntries[0].Values![EffectParamNames.PosterizeFrameRate]);

            // Phase 6: the highlight trail is Echo on the source — Maximum, keyed to highlights, one echo per camera
            // frame, 3–4 echoes fading fast — placed at the front so it is the first shader stage.
            PresetStackEntry echo = stack.VideoEntries[1];
            Assert.Equal(PresetStackPlacement.Front, echo.Placement);
            Assert.Null(echo.PresetName);
            Assert.Equal(EchoOperators.Maximum, echo.Values![EffectParamNames.EchoOperator]);
            Assert.Equal(-1.0 / 15.0, echo.Values[EffectParamNames.EchoTime], 9);
            Assert.InRange(echo.Values[EffectParamNames.HighlightKey], 0.75, 0.85);
            Assert.InRange(echo.Values[EffectParamNames.EchoCount], 3.0, 4.0);
            Assert.True(echo.Values[EffectParamNames.Decay] < 1.0, "the trail should fade");

            // The stage's spatial smear is switched off — Echo's temporal smear replaces it.
            PresetStackEntry toy = stack.VideoEntries[2];
            Assert.Equal(stack.Name, toy.PresetName);
            Assert.Equal(0.0, toy.Values![EffectParamNames.SmearLength]);

            PresetStackEntry audio = Assert.Single(stack.AudioEntries);
            Assert.Equal((EffectTypeIds.AudioCassette, stack.Name), (audio.EffectTypeId, audio.PresetName));
        }
    }

    [Fact]
    public void Find_Matches_Name_Or_Title_Case_Insensitively()
    {
        Assert.Same(ToyCassetteCameraStacks.WornTape, PresetStackCatalog.Find("worn tape"));
        Assert.Same(ToyCassetteCameraStacks.LowLight, PresetStackCatalog.Find("Toy Cassette Camera ▸ Low Light"));
        Assert.Null(PresetStackCatalog.Find("Nope"));
        Assert.Null(PresetStackCatalog.Find(" "));
    }

    // ── Apply ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Linked_Pair_Gets_Three_Video_And_One_Audio_Entries_As_One_Undo_Step()
    {
        Timeline timeline = LinkedPair(out Clip video, out Clip audio);
        var history = new EditHistory();

        PresetStackApplyResult result = PresetStackApplication.Build(timeline, video, ToyCassetteCameraStacks.WornTape);
        history.Execute(result.Command!);

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Apply Toy Cassette Camera ▸ Worn Tape", history.UndoLabel);
        Assert.Empty(result.Skipped);
        Assert.Equal(0, result.Replaced);
        Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.ToyCam }, Types(video));
        Assert.Equal(new[] { EffectTypeIds.AudioCassette }, Types(audio));
        Assert.Equal(4, result.Applied.Count);

        // The values are the presets' (Posterize Time runs at the camera's 15 fps, the stage's own smear is off).
        Assert.Equal(15.0, video.Effects[0].Parameters[EffectParamNames.PosterizeFrameRate].Evaluate(Timecode.Zero));
        Assert.Equal(EchoOperators.Maximum, video.Effects[1].Parameters[EffectParamNames.EchoOperator].Evaluate(Timecode.Zero));
        Assert.Equal(ToyCamPresets.WornTape.Values[EffectParamNames.NoiseLines],
            video.Effects[2].Parameters[EffectParamNames.NoiseLines].Evaluate(Timecode.Zero));
        Assert.Equal(0.0, video.Effects[2].Parameters[EffectParamNames.SmearLength].Evaluate(Timecode.Zero));
        Assert.Equal(CassettePresets.WornTape.Values[EffectParamNames.HissDb],
            audio.Effects[0].Parameters[EffectParamNames.HissDb].Evaluate(Timecode.Zero));

        Assert.True(history.Undo());
        Assert.Empty(video.Effects);
        Assert.Empty(audio.Effects);
        Assert.True(history.Redo());
        Assert.Equal(4, video.Effects.Count + audio.Effects.Count);
    }

    [Fact]
    public void Dropping_On_The_Audio_Half_Applies_Both_Halves()
    {
        Timeline timeline = LinkedPair(out Clip video, out Clip audio);
        PresetStackApplyResult result = PresetStackApplication.Build(timeline, audio, ToyCassetteCameraStacks.Clean);
        result.Command!.Apply();
        Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.ToyCam }, Types(video));
        Assert.Equal(new[] { EffectTypeIds.AudioCassette }, Types(audio));
    }

    [Fact]
    public void Unlinked_Video_Clip_Gets_Video_Only_And_Reports_The_Audio_Skip()
    {
        Timeline timeline = LinkedPair(out Clip video, out Clip audio);
        video.LinkGroupId = null;
        audio.LinkGroupId = null;

        PresetStackApplyResult result = PresetStackApplication.Build(timeline, video, ToyCassetteCameraStacks.WornTape);
        result.Command!.Apply();

        Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.ToyCam }, Types(video));
        Assert.Empty(audio.Effects);
        PresetStackSkip skip = Assert.Single(result.Skipped);
        Assert.Equal((EffectTypeIds.AudioCassette, PresetStackApplication.NoAudioClip), (skip.EffectTypeId, skip.Reason));
    }

    [Fact]
    public void Unlinked_Audio_Clip_Gets_Cassette_Only()
    {
        Timeline timeline = LinkedPair(out Clip video, out Clip audio);
        audio.LinkGroupId = null;

        PresetStackApplyResult result = PresetStackApplication.Build(timeline, audio, ToyCassetteCameraStacks.LowLight);
        result.Command!.Apply();

        Assert.Empty(video.Effects);
        Assert.Equal(new[] { EffectTypeIds.AudioCassette }, Types(audio));
        Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.ToyCam }, result.Skipped.Select(s => s.EffectTypeId));
        Assert.All(result.Skipped, s => Assert.Equal(PresetStackApplication.NoVideoClip, s.Reason));
    }

    [Fact]
    public void Reapply_Is_Idempotent_And_Swaps_The_Variant_In_Place()
    {
        Timeline timeline = LinkedPair(out Clip video, out Clip audio);
        var history = new EditHistory();
        // An existing correction the user placed; the look's effects land around it without disturbing it.
        video.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 0.1));

        history.Execute(PresetStackApplication.Build(timeline, video, ToyCassetteCameraStacks.Clean).Command!);
        string[] once = Types(video);
        Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.Brightness, EffectTypeIds.ToyCam }, once);

        // The user picks a Seed and a Mix; re-applying another variant keeps them, swaps the rest.
        video.Effects[3].Set(EffectParamNames.Seed, 42);
        audio.Effects[0].Set(EffectParamNames.Mix, 0.5);
        video.Effects[3].Tag = "TC-7";

        PresetStackApplyResult again = PresetStackApplication.Build(timeline, audio, ToyCassetteCameraStacks.WornTape);
        history.Execute(again.Command!);
        Assert.Equal(4, again.Replaced);
        Assert.Equal(once, Types(video));
        Assert.Equal(new[] { EffectTypeIds.AudioCassette }, Types(audio));
        EffectInstance toy = video.Effects[3];
        Assert.Equal(ToyCamPresets.WornTape.Values[EffectParamNames.Dropouts],
            toy.Parameters[EffectParamNames.Dropouts].Evaluate(Timecode.Zero));
        Assert.Equal(42.0, toy.Parameters[EffectParamNames.Seed].Evaluate(Timecode.Zero));
        Assert.Equal("TC-7", toy.Tag);
        Assert.Equal(0.5, audio.Effects[0].Parameters[EffectParamNames.Mix].Evaluate(Timecode.Zero));

        // The same variant again is still no duplication, and one undo restores the previous look exactly.
        EffectInstance cleanToy = history.Undo() ? video.Effects[3] : null!;
        Assert.Equal(ToyCamPresets.Clean.Values[EffectParamNames.Dropouts],
            cleanToy.Parameters[EffectParamNames.Dropouts].Evaluate(Timecode.Zero));
        history.Redo();
        history.Execute(PresetStackApplication.Build(timeline, video, ToyCassetteCameraStacks.WornTape).Command!);
        Assert.Equal(once, Types(video));
        Assert.Single(audio.Effects);
    }

    [Fact]
    public void Reapply_Collapses_Duplicate_Entries_Of_A_Stack_Type()
    {
        Timeline timeline = LinkedPair(out Clip video, out _);
        video.Effects.Add(new EffectInstance(EffectTypeIds.PosterizeTime).Set(EffectParamNames.PosterizeFrameRate, 8));
        video.Effects.Add(new EffectInstance(EffectTypeIds.PosterizeTime).Set(EffectParamNames.PosterizeFrameRate, 24));

        var history = new EditHistory();
        history.Execute(PresetStackApplication.Build(timeline, video, ToyCassetteCameraStacks.Clean).Command!);
        Assert.Equal(new[] { EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.ToyCam }, Types(video));
        Assert.Equal(15.0, video.Effects[0].Parameters[EffectParamNames.PosterizeFrameRate].Evaluate(Timecode.Zero));

        history.Undo();
        Assert.Equal(new[] { 8.0, 24.0 },
            video.Effects.Select(e => e.Parameters[EffectParamNames.PosterizeFrameRate].Evaluate(Timecode.Zero)));
    }

    [Fact]
    public void Posterize_Time_Goes_After_An_Input_Color_Transform()
    {
        Timeline timeline = LinkedPair(out Clip video, out _);
        video.Effects.Add(new EffectInstance(EffectTypeIds.ColorTransform));
        video.Effects.Add(new EffectInstance(EffectTypeIds.Brightness));

        PresetStackApplication.Build(timeline, video, ToyCassetteCameraStacks.Clean).Command!.Apply();

        Assert.Equal(
            new[] { EffectTypeIds.ColorTransform, EffectTypeIds.PosterizeTime, EffectTypeIds.Echo, EffectTypeIds.Brightness, EffectTypeIds.ToyCam },
            Types(video));
    }

    [Fact]
    public void Unknown_Effect_Or_Preset_Entries_Are_Skipped_Not_Fatal()
    {
        Timeline timeline = LinkedPair(out Clip video, out _);
        var stack = new PresetStack("Test", "Test", "Test",
            [
                new PresetStackEntry("plugin.missing", null),
                new PresetStackEntry(EffectTypeIds.ToyCam, "No Such Preset"),
                new PresetStackEntry(EffectTypeIds.ToyCam, null),
            ],
            []);

        PresetStackApplyResult result = PresetStackApplication.Build(timeline, video, stack);
        Assert.Equal(new[] { PresetStackApplication.UnknownEffect, PresetStackApplication.UnknownPreset },
            result.Skipped.Select(s => s.Reason));
        result.Command!.Apply();
        Assert.Equal(new[] { EffectTypeIds.ToyCam }, Types(video));
    }

    [Fact]
    public void Nothing_Applicable_Yields_No_Command()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var stray = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero); // on no track
        PresetStackApplyResult result = PresetStackApplication.Build(timeline, stray, ToyCassetteCameraStacks.Clean);
        Assert.Null(result.Command);
        Assert.Equal(4, result.Skipped.Count);
    }
}
