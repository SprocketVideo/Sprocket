using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The action-VFX presets (plan/features/special-effects.md, phase 3): multi-layer stacks of the phase-1
/// primitive effects and phase-2 atmospheric generators inserted at the playhead as one undoable composite.
/// These pin the catalog (every preset only uses registered building blocks, valid values, a hint) and the
/// command expansion (where each layer lands, blend modes, keyframes anchored at the hit, track reuse, undo).
/// </summary>
public sealed class ActionVfxCatalogTests
{
    private static readonly Rational Fps = new(24, 1);

    private static Timeline NewTimeline() => new(Fps, new Resolution(1920, 1080), 48000);

    /// <summary>A timeline with one video track holding a 10 s plate at 0.</summary>
    private static (Timeline Timeline, VideoTrack Plate) WithPlate()
    {
        Timeline timeline = NewTimeline();
        var plate = new VideoTrack { Name = "V1" };
        plate.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        timeline.Tracks.Add(plate);
        return (timeline, plate);
    }

    public static IEnumerable<object[]> PresetIds() => ActionVfxCatalog.BuiltIns.Select(d => new object[] { d.Id });

    [Fact]
    public void Catalog_Ships_The_Planned_Presets_With_Unique_Ids()
    {
        string[] expected =
        [
            ActionVfxIds.MuzzleFlash, ActionVfxIds.SmallFireBurst, ActionVfxIds.GroundExplosion,
            ActionVfxIds.ExplosionAftermath, ActionVfxIds.BurningEdge, ActionVfxIds.DustHit, ActionVfxIds.Aftershock,
        ];
        Assert.Equal(expected, ActionVfxCatalog.BuiltIns.Select(d => d.Id));
        Assert.Equal(expected.Length, ActionVfxCatalog.BuiltIns.Select(d => d.DisplayName).Distinct().Count());
        Assert.Null(ActionVfxCatalog.Find("builtin.vfx.unknown"));
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Every_Preset_Is_Built_From_Registered_Parts_With_In_Range_Values(string id)
    {
        ActionVfxDescriptor preset = ActionVfxCatalog.Find(id)!;
        Assert.False(string.IsNullOrWhiteSpace(preset.Description));
        Assert.False(string.IsNullOrWhiteSpace(preset.PlacementHint)); // UI note: fire/explosion presets carry a hint
        Assert.True(preset.DurationSeconds > 0);
        Assert.NotEmpty(preset.Layers);

        var start = Timecode.FromSeconds(3);
        Timecode duration = preset.Duration(Fps);
        foreach (ActionVfxLayer layer in preset.Layers)
        {
            Clip clip = layer.CreateClip(start, duration); // throws on an unknown generator/effect id
            Assert.Equal(start, clip.TimelineStart);
            Assert.Equal(duration, clip.Duration);
            Assert.Equal(layer.IsAdjustment ? ClipKind.Adjustment : ClipKind.Generator, clip.Kind);
            if (!layer.IsAdjustment)
                AssertInRange(GeneratorCatalog.Find(layer.GeneratorTypeId!)!.Parameters, clip.Generator!.Parameters, clip);

            foreach (EffectInstance effect in clip.Effects)
            {
                EffectDescriptor d = EffectCatalog.Find(effect.EffectTypeId)!;
                Assert.Contains(d.Category, new[] { EffectCategory.Video, EffectCategory.Color });
                AssertInRange(d.Parameters, effect.Parameters, clip);
            }
        }
    }

    // Every value a preset writes — constant or any keyframe — sits inside the descriptor's slider range, and
    // every keyframe lies inside the clip, so the Inspector can show and edit what the preset authored.
    private static void AssertInRange(IReadOnlyList<EffectParameterDescriptor> descriptors,
        IReadOnlyDictionary<string, AnimatableValue> values, Clip clip)
    {
        foreach ((string name, AnimatableValue value) in values)
        {
            EffectParameterDescriptor? p = descriptors.FirstOrDefault(x => x.Name == name);
            Assert.True(p is not null, $"parameter '{name}' is not a registered parameter");
            IEnumerable<double> written = value.Keyframes.Count == 0
                ? [value.Evaluate(Timecode.Zero)]
                : value.Keyframes.Select(k => k.Value);
            foreach (double v in written)
                Assert.InRange(v, p!.Min, p.Max);
            foreach (Keyframe k in value.Keyframes)
                Assert.InRange(k.Time.Ticks, clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks);
        }
    }

    [Fact]
    public void Duration_Rounds_Up_To_Whole_Frames()
    {
        ActionVfxDescriptor muzzle = ActionVfxCatalog.Find(ActionVfxIds.MuzzleFlash)!; // 0.5 s
        var ntsc = new Rational(24000, 1001);
        Timecode d = muzzle.Duration(ntsc);
        Assert.Equal(12, d.ToFrameIndex(ntsc)); // 11.988 frames → 12
        Assert.Equal(Timecode.FromFrames(12, ntsc), d);
    }

    [Fact]
    public void Insert_Stacks_Every_Layer_Above_The_Plate_With_Its_Blend_Mode_As_One_Undo_Entry()
    {
        (Timeline timeline, VideoTrack plate) = WithPlate();
        ActionVfxDescriptor explosion = ActionVfxCatalog.Find(ActionVfxIds.GroundExplosion)!;
        var history = new EditHistory();
        var at = Timecode.FromSeconds(2);

        ActionVfxInsertion insertion = explosion.PlanInsert(timeline, at);
        Assert.Empty(timeline.VideoTracks.Skip(1)); // planning applies nothing
        history.Execute(insertion.Command);

        List<VideoTrack> tracks = [.. timeline.VideoTracks];
        Assert.Same(plate, tracks[0]);
        Assert.Equal(1 + explosion.Layers.Count, tracks.Count);
        for (int i = 0; i < explosion.Layers.Count; i++)
        {
            ActionVfxLayer layer = explosion.Layers[i];
            VideoTrack track = tracks[i + 1];
            Assert.Equal(layer.BlendMode, track.BlendMode);
            Assert.Same(insertion.Clips[i], track.Clips.Single());
            Assert.Equal(at, insertion.Clips[i].TimelineStart);
        }
        // The adjustment layer is on top, so its shake/shockwave moves the plate and the overlays together.
        Assert.Equal(ClipKind.Adjustment, tracks[^1].Clips.Single().Kind);

        Assert.True(history.Undo());
        Assert.Same(plate, timeline.VideoTracks.Single());
        Assert.True(history.Redo());
        Assert.Equal(1 + explosion.Layers.Count, timeline.VideoTracks.Count());
    }

    [Fact]
    public void Decay_Keyframes_Are_Anchored_At_The_Insert_Point()
    {
        (Timeline timeline, _) = WithPlate();
        var at = Timecode.FromSeconds(4);
        ActionVfxInsertion insertion = ActionVfxCatalog.Find(ActionVfxIds.Aftershock)!.PlanInsert(timeline, at);

        EffectInstance shake = insertion.Clips.Single().Effects.Single(e => e.EffectTypeId == EffectTypeIds.ImpactShake);
        AnimatableValue amount = shake.Parameters[EffectParamNames.Amount];
        Assert.Equal(at, amount.Keyframes[0].Time);
        Assert.Equal(1.0, amount.Evaluate(at), 6);                                 // full hit on the impact frame
        Assert.Equal(0.0, amount.Evaluate(at + Timecode.FromSeconds(1.5)), 6);     // settled by the end
        Assert.InRange(amount.Evaluate(at + Timecode.FromSeconds(0.5)), 0.01, 0.99);

        EffectInstance wave = insertion.Clips.Single().Effects.Single(e => e.EffectTypeId == EffectTypeIds.Shockwave);
        Assert.Equal(0.0, wave.Parameters[EffectParamNames.Radius].Evaluate(at), 6); // ring starts at the origin
    }

    [Fact]
    public void Repeated_Hits_Reuse_Free_Vfx_Tracks_With_A_Matching_Blend_Mode()
    {
        (Timeline timeline, _) = WithPlate();
        ActionVfxDescriptor explosion = ActionVfxCatalog.Find(ActionVfxIds.GroundExplosion)!;
        var history = new EditHistory();

        history.Execute(explosion.PlanInsert(timeline, Timecode.FromSeconds(0)).Command);
        int tracksAfterFirst = timeline.VideoTracks.Count();
        history.Execute(explosion.PlanInsert(timeline, Timecode.FromSeconds(5)).Command); // non-overlapping span

        Assert.Equal(tracksAfterFirst, timeline.VideoTracks.Count());
        foreach (VideoTrack vfx in timeline.VideoTracks.Skip(1))
            Assert.Equal(2, vfx.Clips.Count);
    }

    [Fact]
    public void An_Overlapping_Hit_Stacks_On_New_Tracks_Above_The_First()
    {
        (Timeline timeline, _) = WithPlate();
        ActionVfxDescriptor aftershock = ActionVfxCatalog.Find(ActionVfxIds.Aftershock)!;
        var history = new EditHistory();

        history.Execute(aftershock.PlanInsert(timeline, Timecode.FromSeconds(1)).Command);
        history.Execute(aftershock.PlanInsert(timeline, Timecode.FromSeconds(1.5)).Command);

        List<VideoTrack> tracks = [.. timeline.VideoTracks];
        Assert.Equal(3, tracks.Count);
        Assert.All(tracks.Skip(1), t => Assert.Single(t.Clips));
    }

    [Fact]
    public void The_Render_Graph_Plans_The_Stack_Plate_First_And_The_Flash_On_The_Impact()
    {
        (Timeline timeline, _) = WithPlate();
        var project = new Project(timeline);
        var at = Timecode.FromSeconds(2);
        new EditHistory().Execute(ActionVfxCatalog.Find(ActionVfxIds.GroundExplosion)!.PlanInsert(timeline, at).Command);

        // A few frames in: the flash has peaked (attack 0.04 s) and the layers are resolved bottom-up.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, at + Timecode.FromSeconds(0.04));
        Assert.Equal(
            [LayerKind.Media, LayerKind.Generator, LayerKind.Generator, LayerKind.Generator, LayerKind.Adjustment],
            plan.Layers.Select(l => l.Kind));
        Assert.Equal(
            [BlendMode.Normal, BlendMode.Normal, BlendMode.Add, BlendMode.Add, BlendMode.Normal],
            plan.Layers.Select(l => l.BlendMode));
        ResolvedEffect flash = plan.Layers[^1].Effects.First(e => e.EffectTypeId == EffectTypeIds.Color);
        Assert.Equal(2.0, flash.Parameters[EffectParamNames.Exposure], 6);

        // After the preset's span only the plate remains — the VFX never outlive their clips.
        Assert.Single(RenderGraph.PlanVideoFrame(project, at + Timecode.FromSeconds(3.5)).Layers);
    }

    [Fact]
    public void Insert_On_An_Empty_Timeline_Creates_Its_Own_Tracks()
    {
        Timeline timeline = NewTimeline();
        ActionVfxDescriptor fire = ActionVfxCatalog.Find(ActionVfxIds.SmallFireBurst)!;
        ActionVfxInsertion insertion = fire.PlanInsert(timeline, Timecode.FromSeconds(-1)); // clamped to 0
        new EditHistory().Execute(insertion.Command);

        Assert.Equal(fire.Layers.Count, timeline.VideoTracks.Count());
        Assert.All(insertion.Clips, c => Assert.Equal(Timecode.Zero, c.TimelineStart));
        Assert.Equal(BlendMode.Add, timeline.VideoTracks.First().BlendMode); // Embers overlay
    }
}
