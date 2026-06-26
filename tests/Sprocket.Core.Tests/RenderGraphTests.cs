using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

public class RenderGraphPlanTests
{
    private static Project ProjectWithVideoClip(out VideoTrack track, out Clip clip, out MediaRefId media)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        media = MediaRefId.New();
        track = new VideoTrack();
        clip = new Clip(media, Timecode.FromSeconds(2), Timecode.FromSeconds(6), Timecode.FromSeconds(10));
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Empty_Where_No_Clip_Active()
    {
        Project project = ProjectWithVideoClip(out _, out _, out _);
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(0));
        Assert.Empty(plan.Layers);
    }

    [Fact]
    public void Maps_Timeline_Time_To_Source_Time()
    {
        Project project = ProjectWithVideoClip(out _, out _, out MediaRefId media);
        // 1s into the clip (clip starts at 10s, source in-point 2s) -> source 3s.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(11));
        VideoLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(media, layer.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(3), layer.SourceTime);
    }

    [Fact]
    public void Skips_Disabled_Track()
    {
        Project project = ProjectWithVideoClip(out VideoTrack track, out _, out _);
        track.Enabled = false;
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(11));
        Assert.Empty(plan.Layers);
    }

    [Fact]
    public void Layers_Are_Bottom_To_Top_In_Track_Order()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var bottomMedia = MediaRefId.New();
        var topMedia = MediaRefId.New();

        var bottom = new VideoTrack { Opacity = 1.0 };
        bottom.Clips.Add(new Clip(bottomMedia, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));
        var top = new VideoTrack { Opacity = 0.5, BlendMode = BlendMode.Screen };
        top.Clips.Add(new Clip(topMedia, Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero));

        project.Timeline.Tracks.Add(bottom); // index 0 = bottom
        project.Timeline.Tracks.Add(top);

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        Assert.Equal(2, plan.Layers.Count);
        Assert.Equal(bottomMedia, plan.Layers[0].MediaRefId);
        Assert.Equal(topMedia, plan.Layers[1].MediaRefId);
        Assert.Equal(0.5, plan.Layers[1].Opacity);
        Assert.Equal(BlendMode.Screen, plan.Layers[1].BlendMode);
    }

    [Fact]
    public void Effects_Preserve_Stack_Order_And_Evaluate_At_T()
    {
        Project project = ProjectWithVideoClip(out _, out Clip clip, out _);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.2));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(
            EffectParamNames.Opacity,
            AnimatableValue.Animated(new[]
            {
                new Keyframe(Timecode.FromSeconds(10), 1.0),
                new Keyframe(Timecode.FromSeconds(14), 0.0),
            })));

        // 12s = clip start 10s + 2s, halfway through the 4s fade -> opacity 0.5.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(12));
        VideoLayer layer = Assert.Single(plan.Layers);

        Assert.Equal(2, layer.Effects.Count);
        Assert.Equal(EffectTypeIds.Brightness, layer.Effects[0].EffectTypeId);
        Assert.Equal(1.2, layer.Effects[0].Get(EffectParamNames.Amount), 6);
        Assert.Equal(EffectTypeIds.Fade, layer.Effects[1].EffectTypeId);
        Assert.Equal(0.5, layer.Effects[1].Get(EffectParamNames.Opacity), 6);
    }
}

public class RenderGraphExecutorTests
{
    /// <summary>
    /// A fake compositor whose "image" is a string describing the operations applied to it. Proves the
    /// executor drives the seam in the right order without any GPU/Skia dependency.
    /// </summary>
    private sealed class StringCompositor : IVideoCompositor<string>
    {
        public List<string> CompositeLog { get; } = new();

        public string CreateTransparentSurface(Resolution size) => "surface[";

        public string ApplyEffect(string frame, ResolvedEffect effect) =>
            $"{frame}+{effect.EffectTypeId}";

        public void Composite(string surface, string layer, double opacity, BlendMode blendMode) =>
            CompositeLog.Add($"{layer}@{opacity:0.##}/{blendMode}");

        public string Snapshot(string surface) => surface + "]";
    }

    private sealed class NamedFrameSource : IFrameSource<string>
    {
        public string GetFrame(MediaRefId media, Timecode sourceTime) => $"frame({sourceTime.Ticks})";
    }

    [Fact]
    public void Render_Applies_Effects_In_Order_Then_Composites_Each_Layer()
    {
        var brightness = new ResolvedEffect(EffectTypeIds.Brightness, new Dictionary<string, double>());
        var fade = new ResolvedEffect(EffectTypeIds.Fade, new Dictionary<string, double>());
        var layer = new VideoLayer(MediaRefId.New(), new Timecode(900), new[] { brightness, fade }, 0.5, BlendMode.Multiply);
        var plan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, new[] { layer });

        var compositor = new StringCompositor();
        string result = RenderGraph.Render(plan, new NamedFrameSource(), compositor);

        // The composited layer string shows the frame fetched at the source time, then effects folded in order.
        string logged = Assert.Single(compositor.CompositeLog);
        Assert.Equal("frame(900)+builtin.brightness+builtin.fade@0.5/Multiply", logged);
        Assert.Equal("surface[]", result);
    }

    [Fact]
    public void Render_Of_Empty_Plan_Just_Returns_Snapshot()
    {
        var plan = new VideoFramePlan(new Resolution(640, 480), Timecode.Zero, Array.Empty<VideoLayer>());
        var compositor = new StringCompositor();
        string result = RenderGraph.Render(plan, new NamedFrameSource(), compositor);
        Assert.Empty(compositor.CompositeLog);
        Assert.Equal("surface[]", result);
    }
}

public class AudioPlanTests
{
    private static Project ProjectWithAudio(double gainDb, out AudioTrack track, out Clip clip)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        track = new AudioTrack { GainDb = gainDb };
        clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero);
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Unity_Gain_Is_One()
    {
        Project project = ProjectWithAudio(0, out _, out _);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(1.0, layer.GainStartLinear, 6);
        Assert.Equal(1.0, layer.GainEndLinear, 6);
        Assert.Equal(1.0, plan.MasterGainLinear, 6);
    }

    [Fact]
    public void Minus_Six_Db_Is_About_Half_Amplitude()
    {
        Project project = ProjectWithAudio(-6.0206, out _, out _);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Equal(0.5, Assert.Single(plan.Layers).GainStartLinear, 3);
    }

    [Fact]
    public void Muted_Track_Is_Excluded()
    {
        Project project = ProjectWithAudio(0, out AudioTrack track, out _);
        track.Muted = true;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Empty(plan.Layers);
    }

    [Fact]
    public void Solo_Excludes_Non_Soloed_Tracks()
    {
        Project project = ProjectWithAudio(0, out AudioTrack track1, out _);
        var track2 = new AudioTrack();
        track2.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        project.Timeline.Tracks.Add(track2);

        track2.Solo = true;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(track2.Clips[0].MediaRefId, layer.MediaRefId);
    }

    [Fact]
    public void Fade_Produces_A_Gain_Ramp_Across_The_Buffer()
    {
        Project project = ProjectWithAudio(0, out _, out Clip clip);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(
            EffectParamNames.Opacity,
            AnimatableValue.Animated(new[]
            {
                new Keyframe(Timecode.FromSeconds(0), 0.0),
                new Keyframe(Timecode.FromSeconds(1), 1.0),
            })));

        // Buffer [0, 0.5s): fade-in ramps 0.0 -> 0.5 across the buffer.
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.5));
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Equal(0.0, layer.GainStartLinear, 6);
        Assert.Equal(0.5, layer.GainEndLinear, 6);
    }

    [Fact]
    public void Master_Gain_Is_Carried_Through()
    {
        Project project = ProjectWithAudio(0, out _, out _);
        project.Settings.MasterGainDb = -6.0206;
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSeconds(0.1));
        Assert.Equal(0.5, plan.MasterGainLinear, 3);
    }
}
