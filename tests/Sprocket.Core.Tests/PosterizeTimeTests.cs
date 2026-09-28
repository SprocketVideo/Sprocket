using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Posterize Time (plan/features/toy-cassette-camera.md phase 2): an effect the clip's <em>video</em> time map
/// reads. <see cref="Clip.PosterizeInterval"/> derives the grid step from the first enabled entry,
/// <see cref="Clip.EffectEvalTime"/> floors timeline time onto a sequence-anchored grid (clamped to the clip
/// start), and <see cref="Clip.MapToSourceVideo"/> runs the ordinary map on it — composing with speed, ramps and
/// reverse while a frame hold still wins. The planner steps the displayed frame, effect FrameTime/keyframes and
/// generators, drops the entry from the resolved chain, and leaves the audio plan untouched.
/// </summary>
public class PosterizeTimeTests
{
    private const long Sec = Timecode.TicksPerSecond;
    private const long Step15 = Sec / 15; // 16000 ticks — Δ at 15 fps

    // Source span [2s, 12s) placed at t = 4s (a multiple of the 15 fps grid), matching VariableRetimeTests.
    private static Clip ClipAt4s(Rational? speed = null, bool reverse = false)
    {
        var clip = new Clip(MediaRefId.New(), Timecode.FromSeconds(2), Timecode.FromSeconds(12), Timecode.FromSeconds(4))
        {
            Reverse = reverse,
        };
        if (speed is { } s)
            clip.SpeedRatio = s;
        return clip;
    }

    private static EffectInstance Posterize(double fps, bool enabled = true)
    {
        EffectInstance effect = EffectCatalog.Find(EffectTypeIds.PosterizeTime)!.CreateInstance();
        effect.Set(EffectParamNames.PosterizeFrameRate, fps);
        effect.Enabled = enabled;
        return effect;
    }

    private static Clip Posterized(Clip clip, double fps = 15)
    {
        clip.Effects.Add(Posterize(fps));
        return clip;
    }

    // The expected grid time: floor to multiples of Δ from sequence 0, clamped to the clip start.
    private static long Grid(long t, long step, long clipStart) => Math.Max(clipStart, t / step * step);

    /// <summary>Over the clip's span: the video map equals the plain map at the grid time, is constant inside each
    /// step, and changes at every step boundary (the clip is moving, so consecutive steps show different frames).</summary>
    private static void AssertSteps(Clip clip, long step)
    {
        long start = clip.TimelineStart.Ticks;
        long end = clip.TimelineEnd.Ticks;
        for (long t = start; t < end; t += 1000)
        {
            long q = Grid(t, step, start);
            Assert.Equal(clip.MapToSource(new Timecode(q)), clip.MapToSourceVideo(new Timecode(t)));
            Assert.Equal(new Timecode(q), clip.EffectEvalTime(new Timecode(t)));
        }
        for (long b = (start / step + 1) * step; b < end; b += step)
            Assert.NotEqual(clip.MapToSourceVideo(new Timecode(b - 1)), clip.MapToSourceVideo(new Timecode(b)));
    }

    // ── Clip model ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(12.0, 20000L)]
    [InlineData(15.0, 16000L)]
    [InlineData(24.0, 10000L)]
    [InlineData(30.0, 8000L)]
    [InlineData(23.976, 10010L)] // lands on the NTSC 24000/1001 grid exactly
    public void Interval_Is_Rounded_Ticks_Per_Frame(double fps, long expectedTicks)
    {
        Clip clip = Posterized(ClipAt4s(), fps);
        Assert.Equal(new Timecode(expectedTicks), clip.PosterizeInterval);
    }

    [Fact]
    public void Unposterized_Clip_Maps_Identically()
    {
        Clip clip = ClipAt4s();
        Assert.Null(clip.PosterizeInterval);
        for (long t = 4 * Sec; t < 6 * Sec; t += 777)
        {
            Assert.Equal(clip.MapToSource(new Timecode(t)), clip.MapToSourceVideo(new Timecode(t)));
            Assert.Equal(new Timecode(t), clip.EffectEvalTime(new Timecode(t)));
        }
    }

    [Fact]
    public void Disabled_Entry_Does_Not_Quantize()
    {
        Clip clip = ClipAt4s();
        clip.Effects.Add(Posterize(15, enabled: false));

        Assert.Null(clip.PosterizeInterval);
        Timecode t = new(4 * Sec + 5000);
        Assert.Equal(clip.MapToSource(t), clip.MapToSourceVideo(t));
    }

    [Fact]
    public void First_Enabled_Entry_Wins()
    {
        Clip clip = ClipAt4s();
        clip.Effects.Add(Posterize(10, enabled: false));
        clip.Effects.Add(Posterize(15));
        clip.Effects.Add(Posterize(6));

        Assert.Equal(new Timecode(Step15), clip.PosterizeInterval);
    }

    [Fact]
    public void Out_Of_Range_Or_Missing_Rate_Stays_Finite()
    {
        Clip clip = ClipAt4s();
        var bad = new EffectInstance(EffectTypeIds.PosterizeTime).Set(EffectParamNames.PosterizeFrameRate, 0.0);
        clip.Effects.Add(bad);
        Assert.Equal(new Timecode(Sec), clip.PosterizeInterval); // clamped to 1 fps

        clip.Effects.Clear();
        clip.Effects.Add(new EffectInstance(EffectTypeIds.PosterizeTime)); // no rate → the 12 fps default
        Assert.Equal(new Timecode(Sec / 12), clip.PosterizeInterval);
    }

    [Fact]
    public void Source_Time_Is_Constant_Within_A_Step_And_Advances_At_Boundaries()
    {
        Clip clip = Posterized(ClipAt4s());
        AssertSteps(clip, Step15);

        // Spot values: 4s + half a step still shows the frame at 4s (source 2s); the next step jumps a full Δ.
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSourceVideo(new Timecode(4 * Sec + Step15 / 2)));
        Assert.Equal(new Timecode(2 * Sec + Step15), clip.MapToSourceVideo(new Timecode(4 * Sec + Step15)));
    }

    [Fact]
    public void Composes_With_Double_Speed()
    {
        Clip clip = Posterized(ClipAt4s(new Rational(2, 1)));
        AssertSteps(clip, Step15);
        // One step of timeline consumes two steps of source.
        Assert.Equal(new Timecode(2 * Sec + 2 * Step15), clip.MapToSourceVideo(new Timecode(4 * Sec + Step15 + 10)));
    }

    [Fact]
    public void Composes_With_A_Speed_Ramp()
    {
        Clip clip = ClipAt4s();
        clip.SpeedCurve = AnimatableValue.Animated([
            new Keyframe(Timecode.Zero, 0.5, Interpolation.Linear),
            new Keyframe(Timecode.FromSeconds(4), 2.0, Interpolation.Linear),
        ]);
        Posterized(clip);
        Assert.True(clip.HasSpeedRamp);
        AssertSteps(clip, Step15);
    }

    [Fact]
    public void Composes_With_Reverse()
    {
        Clip clip = Posterized(ClipAt4s(reverse: true));
        AssertSteps(clip, Step15);
        // The first step shows the clip start's (exclusive, mirrored) source time — the out-point.
        Assert.Equal(Timecode.FromSeconds(12), clip.MapToSourceVideo(new Timecode(4 * Sec + 100)));
    }

    [Fact]
    public void Frame_Hold_Wins()
    {
        Clip clip = Posterized(ClipAt4s());
        clip.HoldDuration = Timecode.FromSeconds(3);
        clip.HoldFrameAt = Timecode.FromSeconds(5);

        for (long t = 4 * Sec; t < 7 * Sec; t += 3001)
            Assert.Equal(Timecode.FromSeconds(5), clip.MapToSourceVideo(new Timecode(t)));
    }

    [Fact]
    public void Grid_Is_Anchored_To_Sequence_Time_And_Clamped_To_The_Clip_Start()
    {
        // A clip starting off-grid (5000 ticks past 4s): its first partial step shows its own first frame (the
        // clamp), and later steps change exactly on the sequence grid, not on clip-relative multiples of Δ.
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), new Timecode(4 * Sec + 5000));
        Posterized(clip);

        Assert.Equal(clip.TimelineStart, clip.EffectEvalTime(clip.TimelineStart));
        Assert.Equal(Timecode.Zero, clip.MapToSourceVideo(new Timecode(4 * Sec + Step15 - 1)));
        Timecode firstBoundary = new(4 * Sec + Step15);
        Assert.Equal(firstBoundary, clip.EffectEvalTime(firstBoundary));
        Assert.Equal(new Timecode(Step15 - 5000), clip.MapToSourceVideo(firstBoundary));
        AssertSteps(clip, Step15);
    }

    [Fact]
    public void Blade_Split_Keeps_The_Grid_Phase()
    {
        Clip whole = Posterized(ClipAt4s());
        Clip reference = Posterized(ClipAt4s());
        var track = new VideoTrack();
        track.Clips.Add(whole);

        // Split off-grid; the right half inherits the effect. From the right half's first grid boundary on, every
        // displayed frame matches the unsplit clip — the grid is sequence-anchored, so a split can't shift it.
        Timecode at = new(5 * Sec + 3333);
        var split = new SplitClipCommand(track, whole, at);
        split.Apply();
        Clip right = split.RightClip;
        Assert.Equal(new Timecode(Step15), right.PosterizeInterval);

        for (long t = 4 * Sec; t < at.Ticks; t += 1000)
            Assert.Equal(reference.MapToSourceVideo(new Timecode(t)), whole.MapToSourceVideo(new Timecode(t)));
        long firstBoundary = (at.Ticks / Step15 + 1) * Step15;
        for (long t = firstBoundary; t < right.TimelineEnd.Ticks; t += 1000)
            Assert.Equal(reference.MapToSourceVideo(new Timecode(t)), right.MapToSourceVideo(new Timecode(t)));
    }

    [Fact]
    public void Add_Frame_Hold_Captures_The_Displayed_Frame()
    {
        Clip clip = Posterized(ClipAt4s());
        var track = new VideoTrack();
        track.Clips.Add(clip);
        Timecode at = new(5 * Sec + Step15 / 2); // mid-step: the displayed frame is the step start's

        (IEditCommand command, Clip held) = FrameHoldEdits.AddFrameHold(track, clip, at);
        command.Apply();

        Assert.Equal(clip.MapToSourceVideo(new Timecode(5 * Sec)), held.HoldFrameAt);
        Assert.NotEqual(clip.MapToSource(at), held.HoldFrameAt);
    }

    // ── Planner ─────────────────────────────────────────────────────────────────────────────────────

    private static Project ProjectWith(Clip clip, Track track)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void PlanVideoFrame_Emits_Identical_Source_Time_Across_A_Step()
    {
        Clip clip = Posterized(ClipAt4s());
        Project project = ProjectWith(clip, new VideoTrack());

        // At 30 fps two project frames fall in each 15 fps step.
        Timecode f0 = new(5 * Sec), f1 = new(5 * Sec + Sec / 30), f2 = new(5 * Sec + 2 * Sec / 30);
        VideoLayer l0 = Assert.Single(RenderGraph.PlanVideoFrame(project, f0).Layers);
        VideoLayer l1 = Assert.Single(RenderGraph.PlanVideoFrame(project, f1).Layers);
        VideoLayer l2 = Assert.Single(RenderGraph.PlanVideoFrame(project, f2).Layers);

        Assert.Equal(l0.SourceTime, l1.SourceTime);
        Assert.NotEqual(l1.SourceTime, l2.SourceTime);
        Assert.Equal(Timecode.FromSeconds(3), l0.SourceTime);
    }

    [Fact]
    public void Effects_Evaluate_At_The_Quantized_Time_And_The_Entry_Is_Dropped()
    {
        Clip clip = Posterized(ClipAt4s());
        // A keyframed brightness ramp 1 → 2 over [4s, 6s).
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, AnimatableValue.Animated([
            new Keyframe(Timecode.FromSeconds(4), 1.0, Interpolation.Linear),
            new Keyframe(Timecode.FromSeconds(6), 2.0, Interpolation.Linear),
        ])));
        Project project = ProjectWith(clip, new VideoTrack());

        Timecode t = new(5 * Sec + Step15 - 1); // last tick of the step starting at 5s
        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, t).Layers);

        ResolvedEffect brightness = Assert.Single(layer.Effects); // Posterize Time is consumed, not rendered
        Assert.Equal(EffectTypeIds.Brightness, brightness.EffectTypeId);
        Assert.Equal(5 * Sec, brightness.FrameTime);
        Assert.Equal(1.5, brightness.Get(EffectParamNames.Amount), 9);

        // The live-preview path resolves the same way.
        ResolvedEffect live = Assert.Single(RenderGraph.ResolveEffects(clip, t));
        Assert.Equal(5 * Sec, live.FrameTime);
        Assert.Equal(layer.SourceTime, live.SourceTime);
    }

    [Fact]
    public void Generators_Posterize_Too()
    {
        GeneratorSpec spec = GeneratorCatalog.BuiltIns[0].CreateSpec();
        Clip clip = Clip.CreateGenerator(spec, Timecode.FromSeconds(4), Timecode.FromSeconds(4));
        Posterized(clip);

        ResolvedGenerator a = RenderGraph.ResolveGenerator(clip, new Timecode(5 * Sec));
        ResolvedGenerator b = RenderGraph.ResolveGenerator(clip, new Timecode(5 * Sec + Step15 - 1));
        ResolvedGenerator c = RenderGraph.ResolveGenerator(clip, new Timecode(5 * Sec + Step15));
        Assert.Equal(a.LocalSeconds, b.LocalSeconds);
        Assert.Equal(a.Progress, b.Progress);
        Assert.NotEqual(b.Progress, c.Progress);
    }

    [Fact]
    public void Nested_Sequence_Renders_Its_Child_At_The_Quantized_Time()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var childTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var childTrack = new VideoTrack();
        childTrack.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        childTimeline.Tracks.Add(childTrack);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);
        project.Sequences.Add(child);

        Clip nesting = Posterized(Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(10), Timecode.Zero));
        var parentTrack = new VideoTrack();
        parentTrack.Clips.Add(nesting);
        project.Timeline.Tracks.Add(parentTrack);

        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, new Timecode(Sec + Step15 - 1)).Layers);
        Assert.Equal(new Timecode(Sec), layer.SourceTime);
        Assert.Equal(new Timecode(Sec), Assert.Single(layer.NestedPlan!.Layers).SourceTime);
    }

    [Fact]
    public void Audio_Plan_Is_Unchanged()
    {
        // Posterize Time never chops audio — even on an audio clip carrying the entry, every buffer's source span
        // is identical with and without it.
        Clip plain = ClipAt4s();
        Clip posterized = Posterized(ClipAt4s());
        Project a = ProjectWith(plain, new AudioTrack());
        Project b = ProjectWith(posterized, new AudioTrack());

        for (long t = 4 * Sec; t < 6 * Sec; t += 1000)
        {
            AudioLayer la = Assert.Single(RenderGraph.PlanAudioBuffer(a, new Timecode(t), new Timecode(1000)).Layers);
            AudioLayer lb = Assert.Single(RenderGraph.PlanAudioBuffer(b, new Timecode(t), new Timecode(1000)).Layers);
            Assert.Equal(la.SourceStart, lb.SourceStart);
            Assert.Equal(la.SourceEnd, lb.SourceEnd);
            Assert.Equal(la.ClipGainStartLinear, lb.ClipGainStartLinear);
            Assert.Equal(la.ClipGainEndLinear, lb.ClipGainEndLinear);
            Assert.Equal(new Timecode(t - 2 * Sec), lb.SourceStart);
        }
    }

    // ── Descriptor ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Posterize_Time_Is_The_Only_Time_Modifier()
    {
        EffectDescriptor d = Assert.Single(EffectCatalog.BuiltIns, x => x.IsTimeModifier);
        Assert.Equal(EffectTypeIds.PosterizeTime, d.Id);
        Assert.True(EffectCatalog.IsTimeModifier(EffectTypeIds.PosterizeTime));
        Assert.False(EffectCatalog.IsTimeModifier(EffectTypeIds.Mosaic));
        Assert.False(EffectCatalog.IsTimeModifier("plugin.unknown"));
    }
}
