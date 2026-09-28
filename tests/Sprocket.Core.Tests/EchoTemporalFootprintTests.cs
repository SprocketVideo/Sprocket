using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Echo and the render plan's temporal footprint (plan/features/toy-cassette-camera.md phase 6): the descriptor's
/// footprint turns Number of Echoes / Echo Time into prior timeline times, and the planner maps each through the
/// clip's <em>video</em> time map — so speed, ramps, reverse and Posterize Time all apply — clamping to the media at
/// its ends and reaching into handles where the source has them. The prior source times and each echo's re-resolved
/// upstream effects are plain data on the plan, so every frame stays a pure function of (project, time).
/// </summary>
public class EchoTemporalFootprintTests
{
    private const long Sec = Timecode.TicksPerSecond;
    private const long Step15 = Sec / 15; // 16000 ticks

    private static readonly Timecode MediaDuration = Timecode.FromSeconds(20);

    private static (Project Project, Clip Clip) ProjectWith(Clip clip)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var info = new ProbedMediaInfo(MediaDuration, true, new Rational(30, 1), 1920, 1080, false, 0, 0);
        project.MediaPool.Add(new MediaRef(clip.MediaRefId, "C:/media/clip.mp4", info));
        var track = new VideoTrack();
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return (project, clip);
    }

    /// <summary>Source span [sourceIn, sourceIn + 10s) placed at timeline 4 s.</summary>
    private static Clip ClipAt4s(double sourceInSeconds = 2, Rational? speed = null, bool reverse = false)
    {
        var clip = new Clip(MediaRefId.New(), Timecode.FromSeconds(sourceInSeconds),
            Timecode.FromSeconds(sourceInSeconds + 10), Timecode.FromSeconds(4)) { Reverse = reverse };
        if (speed is { } s)
            clip.SpeedRatio = s;
        return clip;
    }

    private static EffectInstance Echo(int echoes, double echoTimeSeconds = -1.0 / 15)
    {
        EffectInstance echo = EffectCatalog.Find(EffectTypeIds.Echo)!.CreateInstance();
        echo.Set(EffectParamNames.EchoCount, echoes);
        echo.Set(EffectParamNames.EchoTime, echoTimeSeconds);
        return echo;
    }

    private static VideoLayer Plan(Project project, Timecode t) => Assert.Single(RenderGraph.PlanVideoFrame(project, t).Layers);

    private static ResolvedEffect EchoOf(VideoLayer layer) => layer.Effects.Single(e => e.EffectTypeId == EffectTypeIds.Echo);

    private static Timecode[] Expected(Clip clip, Timecode t, int echoes, long spacing) =>
        [.. Enumerable.Range(1, echoes).Select(k => clip.MapToSourceVideo(t + new Timecode(spacing * k)))];

    // ── Descriptor ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Echo_Is_A_Temporal_Video_Effect_With_After_Effects_Defaults()
    {
        EffectDescriptor d = EffectCatalog.Find(EffectTypeIds.Echo)!;
        Assert.Equal(EffectCategory.Video, d.Category);
        Assert.Equal("EC", d.ShortCode);
        Assert.True(EffectCatalog.IsTemporal(EffectTypeIds.Echo));
        Assert.False(EffectCatalog.IsTemporal(EffectTypeIds.ToyCam));
        Assert.False(d.IsTimeModifier);

        double Default(string name) => d.Parameters.Single(p => p.Name == name).Default;
        Assert.Equal(-0.033, Default(EffectParamNames.EchoTime));
        Assert.Equal(1.0, Default(EffectParamNames.EchoCount));
        Assert.Equal(1.0, Default(EffectParamNames.StartingIntensity));
        Assert.Equal(1.0, Default(EffectParamNames.Decay));
        Assert.Equal(EchoOperators.Add, Default(EffectParamNames.EchoOperator));
        Assert.Equal(0.0, Default(EffectParamNames.HighlightKey)); // 0 = AE behaviour
    }

    [Theory]
    [InlineData(4.0, -1.0 / 15, 4, -16000L)]
    [InlineData(1.0, -0.033, 1, -7920L)]
    [InlineData(0.0, -0.033, 0, 0L)]           // no echoes
    [InlineData(3.0, 0.0, 0, 0L)]              // zero spacing reads nothing
    [InlineData(12.0, -0.1, 8, -24000L)]       // clamped to the 8-frame cap
    [InlineData(2.0, 0.5, 0, 0L)]              // a future (hand-edited) Echo Time clamps to 0 → none
    [InlineData(double.NaN, double.NaN, 1, -7920L)] // non-finite reads as the defaults
    public void Footprint_Follows_Number_Of_Echoes_And_Echo_Time(double count, double seconds, int expectedCount, long expectedSpacing)
    {
        Func<IReadOnlyDictionary<string, double>, TemporalFootprint> footprint = EffectCatalog.Find(EffectTypeIds.Echo)!.TemporalFootprint!;
        TemporalFootprint f = footprint(new Dictionary<string, double>
        {
            [EffectParamNames.EchoCount] = count,
            [EffectParamNames.EchoTime] = seconds,
        });
        Assert.Equal(expectedCount, f.IsEmpty ? 0 : f.Count);
        if (expectedCount > 0)
            Assert.Equal(expectedSpacing, f.Spacing.Ticks);
    }

    // ── Planner ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Planner_Emits_The_Prior_Source_Times_In_Echo_Order()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.Effects.Add(Echo(4));
        Timecode t = Timecode.FromSeconds(6); // 2 s into the clip → source 4 s

        VideoLayer layer = Plan(project, t);
        ResolvedEffect echo = EchoOf(layer);

        Timecode[] expected = [.. Enumerable.Range(1, 4).Select(k => new Timecode(4 * Sec - k * Step15))];
        Assert.Equal(expected, echo.TemporalInputs!.Select(i => i.SourceTime));
        Assert.Equal(expected.Order(), layer.PriorSourceTimes);
        Assert.All(echo.TemporalInputs!, i => Assert.Empty(i.Upstream)); // Echo first → nothing to re-apply
        Assert.Equal(layer.PriorSourceTimes, RenderGraph.ResolvePriorSourceTimes(clip, t, MediaDuration));
    }

    [Fact]
    public void At_The_Clip_Start_Echoes_Reach_Into_The_Head_Handle()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s(sourceInSeconds: 2));
        clip.Effects.Add(Echo(3));

        VideoLayer layer = Plan(project, clip.TimelineStart);
        Assert.Equal(new[] { 2 * Sec - Step15, 2 * Sec - 2 * Step15, 2 * Sec - 3 * Step15 },
            EchoOf(layer).TemporalInputs!.Select(i => i.SourceTime.Ticks));
    }

    [Fact]
    public void At_The_Clip_Start_Without_Handles_Echoes_Clamp_To_The_First_Source_Frame()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s(sourceInSeconds: 0));
        clip.Effects.Add(Echo(3));

        VideoLayer layer = Plan(project, clip.TimelineStart + new Timecode(Step15)); // one echo still in the clip
        Assert.Equal(new[] { 0L, 0L, 0L }, EchoOf(layer).TemporalInputs!.Select(i => i.SourceTime.Ticks));
        Assert.Equal(new[] { Timecode.Zero }, layer.PriorSourceTimes); // listed once for the frame provider
    }

    [Fact]
    public void Double_Speed_Doubles_The_Source_Spacing()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s(speed: new Rational(2, 1)));
        clip.Effects.Add(Echo(3));
        Timecode t = Timecode.FromSeconds(5);

        ResolvedEffect echo = EchoOf(Plan(project, t));
        Assert.Equal(new[] { 4 * Sec - 2 * Step15, 4 * Sec - 4 * Step15, 4 * Sec - 6 * Step15 },
            echo.TemporalInputs!.Select(i => i.SourceTime.Ticks));
    }

    [Fact]
    public void A_Speed_Ramp_Maps_Each_Prior_Time_Through_The_Ramp()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.SpeedCurve = AnimatableValue.Animated([
            new Keyframe(Timecode.Zero, 0.5, Interpolation.Linear),
            new Keyframe(Timecode.FromSeconds(4), 2.0, Interpolation.Linear),
        ]);
        clip.Effects.Add(Echo(4));
        Timecode t = Timecode.FromSeconds(7);

        ResolvedEffect echo = EchoOf(Plan(project, t));
        Assert.Equal(Expected(clip, t, 4, -Step15), echo.TemporalInputs!.Select(i => i.SourceTime));
        // The ramp is accelerating here, so the source gaps between successive echoes shrink going back.
        long[] ticks = [.. echo.TemporalInputs!.Select(i => i.SourceTime.Ticks)];
        Assert.True(ticks[0] - ticks[1] > ticks[2] - ticks[3]);
    }

    [Fact]
    public void Reverse_Echoes_Are_Later_Source_Frames_And_Clamp_To_The_Media_End()
    {
        (Project project, Clip clip) = ProjectWith(new Clip(MediaRefId.New(), Timecode.FromSeconds(10), MediaDuration,
            Timecode.FromSeconds(4)) { Reverse = true });
        clip.Effects.Add(Echo(3));

        Timecode t = Timecode.FromSeconds(5); // 1 s in → source 19 s (exclusive bound)
        VideoLayer layer = Plan(project, t);
        Assert.True(layer.Reverse);
        Assert.Equal(new[] { 19 * Sec + Step15, 19 * Sec + 2 * Step15, 19 * Sec + 3 * Step15 },
            EchoOf(layer).TemporalInputs!.Select(i => i.SourceTime.Ticks));

        // At the clip start the source ends at the out-point: no tail handle, so the echoes clamp to the media's end.
        VideoLayer atStart = Plan(project, clip.TimelineStart);
        Assert.All(EchoOf(atStart).TemporalInputs!, i => Assert.Equal(MediaDuration, i.SourceTime));
    }

    [Fact]
    public void Posterize_Time_Steps_The_Echoes_And_Duplicates_Are_Listed_Once()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.PosterizeTime)!.CreateInstance()
            .Set(EffectParamNames.PosterizeFrameRate, 15));
        clip.Effects.Add(Echo(4, echoTimeSeconds: -1.0 / 30)); // two echoes per 15 fps step
        Timecode t = Timecode.FromSeconds(6) + new Timecode(Step15 + 100);

        VideoLayer layer = Plan(project, t);
        ResolvedEffect echo = EchoOf(layer);
        Timecode[] perEcho = [.. echo.TemporalInputs!.Select(i => i.SourceTime)];
        Assert.Equal(Expected(clip, t, 4, -(Sec / 30)), perEcho);
        Assert.All(perEcho, s => Assert.Equal(0, (s.Ticks - 2 * Sec) % Step15)); // every echo sits on the 15 fps grid
        Assert.True(perEcho.Distinct().Count() < perEcho.Length, "several echoes should land on the same step");
        Assert.Equal(perEcho.Distinct().Order(), layer.PriorSourceTimes);
    }

    [Fact]
    public void No_Echoes_Means_No_Temporal_Data()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.Effects.Add(Echo(0));
        VideoLayer layer = Plan(project, Timecode.FromSeconds(6));
        Assert.Null(EchoOf(layer).TemporalInputs);
        Assert.Null(layer.PriorSourceTimes);
        Assert.Empty(RenderGraph.ResolvePriorSourceTimes(clip, Timecode.FromSeconds(6)));
    }

    [Fact]
    public void The_Layer_Total_Is_Capped_At_Eight_Prior_Frames()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.Effects.Add(Echo(6));
        clip.Effects.Add(Echo(6, echoTimeSeconds: -0.2));

        VideoLayer layer = Plan(project, Timecode.FromSeconds(8));
        ResolvedEffect[] echoes = [.. layer.Effects.Where(e => e.EffectTypeId == EffectTypeIds.Echo)];
        Assert.Equal(6, echoes[0].TemporalInputs!.Count);
        Assert.Equal(2, echoes[1].TemporalInputs!.Count);
        Assert.True(layer.PriorSourceTimes!.Count <= TemporalFootprint.MaxPriorFrames);
    }

    [Fact]
    public void Effects_Below_Echo_Are_Re_Resolved_At_Each_Prior_Frame()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        EffectInstance brightness = new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount,
            AnimatableValue.Animated([
                new Keyframe(Timecode.FromSeconds(4), 0.0, Interpolation.Linear),
                new Keyframe(Timecode.FromSeconds(6), 2.0, Interpolation.Linear),
            ]));
        clip.Effects.Add(brightness);
        clip.Effects.Add(Echo(2, echoTimeSeconds: -0.5));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity, 0.5)); // above: not replayed

        Timecode t = Timecode.FromSeconds(6);
        ResolvedEffect echo = EchoOf(Plan(project, t));
        TemporalInput[] inputs = [.. echo.TemporalInputs!];
        Assert.Equal(2, inputs.Length);
        for (int k = 1; k <= 2; k++)
        {
            ResolvedEffect up = Assert.Single(inputs[k - 1].Upstream);
            Assert.Equal(EffectTypeIds.Brightness, up.EffectTypeId);
            Timecode priorT = t - Timecode.FromSeconds(0.5 * k);
            Assert.Equal(brightness.Parameters[EffectParamNames.Amount].Evaluate(priorT), up.Get(EffectParamNames.Amount), 9);
            Assert.Equal(priorT.Ticks, up.FrameTime);
            Assert.Equal(inputs[k - 1].SourceTime, up.SourceTime);
            Assert.Null(up.TemporalInputs);
        }
    }

    [Fact]
    public void Upstream_Effects_Evaluate_At_The_Prior_Frames_Posterize_Step()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.PosterizeTime)!.CreateInstance()
            .Set(EffectParamNames.PosterizeFrameRate, 15));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.0));
        clip.Effects.Add(Echo(2));

        Timecode t = Timecode.FromSeconds(6) + new Timecode(Step15 / 2);
        ResolvedEffect echo = EchoOf(Plan(project, t));
        for (int k = 1; k <= 2; k++)
        {
            Timecode priorT = t + new Timecode(-Step15 * k);
            Assert.Equal(clip.EffectEvalTime(priorT).Ticks, echo.TemporalInputs![k - 1].Upstream.Single().FrameTime);
        }
    }

    [Fact]
    public void Generator_Layers_Carry_No_Prior_Frames()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        Clip clip = Clip.CreateGenerator(new GeneratorSpec(GeneratorTypeIds.SolidColor), Timecode.FromSeconds(4), Timecode.Zero);
        clip.Effects.Add(Echo(3));
        var track = new VideoTrack();
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);

        VideoLayer layer = Plan(project, Timecode.FromSeconds(2));
        Assert.Null(EchoOf(layer).TemporalInputs);
        Assert.Null(layer.PriorSourceTimes);
    }

    [Fact]
    public void The_Plan_Is_Deterministic()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.Effects.Add(Echo(4));
        Timecode t = Timecode.FromSeconds(7.3);
        VideoLayer a = Plan(project, t), b = Plan(project, t);
        Assert.Equal(a.PriorSourceTimes, b.PriorSourceTimes);
        Assert.Equal(EchoOf(a).TemporalInputs!.Select(i => i.SourceTime), EchoOf(b).TemporalInputs!.Select(i => i.SourceTime));
    }

    [Fact]
    public void The_Preview_Resolution_Matches_The_Planner()
    {
        (Project project, Clip clip) = ProjectWith(ClipAt4s());
        clip.Effects.Add(Echo(4));
        Timecode t = Timecode.FromSeconds(6.5);

        ResolvedEffect planned = EchoOf(Plan(project, t));
        ResolvedEffect preview = RenderGraph.ResolveEffects(clip, t, MediaDuration).Single(e => e.EffectTypeId == EffectTypeIds.Echo);
        Assert.Equal(planned.TemporalInputs!.Select(i => i.SourceTime), preview.TemporalInputs!.Select(i => i.SourceTime));
    }
}
