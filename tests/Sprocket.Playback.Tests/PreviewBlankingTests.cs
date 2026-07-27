using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// The preview must stop showing a picture the instant the playhead leaves the clip that produced it — the
/// composite going empty is what makes the surface repaint to black (past the last clip, in a gap between clips,
/// or over a clip the user just disabled). <see cref="PreviewSurface"/>-equivalent drawing holds no cached image,
/// so the whole guarantee reduces to the pump raising <see cref="PlaybackEngine.FramePresented"/>: if it doesn't,
/// nothing asks the surface to redraw and the last decoded frame is stranded on screen indefinitely.
/// <para>Decode-free: the video track carries a <see cref="ClipKind.Generator"/> clip, which contributes a real
/// layer with no decoder at all, so these tests exercise <c>UseLayers</c> / <c>HasComposite</c> / the repaint
/// decision without FFmpeg. The pump is stepped by hand via <c>PumpOnceAsync</c> — no background thread, no
/// real-time delay loop — so every present is deterministic and countable.</para>
/// </summary>
public class PreviewBlankingTests
{
    private static readonly Timecode ClipEnd = Timecode.FromSeconds(10);
    private static readonly Timecode Inside = Timecode.FromSeconds(5);
    private static readonly Timecode PastEnd = Timecode.FromSeconds(15);
    private static readonly Rational Fps30 = new(30, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A one-video-track timeline holding a single generator clip spanning <c>[0, ClipEnd)</c>.</summary>
    private static (Project Project, Clip Clip) BuildProject()
    {
        var timeline = new Timeline(Fps30, new Resolution(1920, 1080), 48000);
        var track = new VideoTrack { Name = "V1" };
        Clip clip = Clip.CreateGenerator(
            new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF3366AA"),
            ClipEnd, Timecode.Zero);
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return (new Project(timeline), clip);
    }

    private static PlaybackEngine BuildEngine(Project project) =>
        new(project, _ => null, new SoftwareClock(() => TimeSpan.Zero)) { AllowPlayheadPastEnd = true };

    private static IReadOnlyList<PresentedVideoLayer> Layers(PlaybackEngine engine)
    {
        IReadOnlyList<PresentedVideoLayer> captured = [];
        engine.UseLayers(layers => captured = layers.ToList());
        return captured;
    }

    [Fact]
    public async Task Composite_Is_Empty_Past_The_Last_Clip()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildProject();
        await using PlaybackEngine engine = BuildEngine(project);

        engine.SeekTo(Inside);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.NotEmpty(Layers(engine));

        engine.SeekTo(PastEnd);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Empty(Layers(engine));
    }

    /// <summary>The clip's span is half-open, so the very first position past its last frame — the content end
    /// itself — already shows nothing. "As soon as the cursor goes past it", not a frame later.</summary>
    [Fact]
    public async Task Composite_Is_Empty_At_The_Content_End_Itself()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildProject();
        await using PlaybackEngine engine = BuildEngine(project);

        engine.SeekTo(ClipEnd);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Empty(Layers(engine));
    }

    /// <summary>Nothing is promoted out past the last clip (every player just clears), so the emptying transition
    /// is the only thing that can trigger the repaint — and it must fire, exactly once.</summary>
    [Fact]
    public async Task Emptying_Composite_Raises_Exactly_One_Present()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildProject();
        await using PlaybackEngine engine = BuildEngine(project);

        engine.SeekTo(Inside);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        int presents = 0;
        engine.FramePresented += () => presents++;

        engine.SeekTo(PastEnd);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(1, presents);

        // Already blank: idle ticks out in the trailing space must not keep repainting the surface.
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(1, presents);
    }

    /// <summary>
    /// The emptying transition is edge-triggered and single-shot, so it can be missed: <c>SuspendAsync</c> resets
    /// the engine's had-composite flag (and <c>Resume</c> only re-seeks), and a pump iteration that faults is
    /// swallowed by <c>PumpError</c> having already consumed it. A seek that lands where no clip is active must
    /// repaint anyway — this engine has never seen a composite, exactly as one resuming from suspend has not.
    /// </summary>
    [Fact]
    public async Task Seek_Past_The_End_Repaints_Even_When_The_Emptying_Edge_Was_Never_Seen()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildProject();
        await using PlaybackEngine engine = BuildEngine(project);

        int presents = 0;
        engine.FramePresented += () => presents++;

        engine.SeekTo(PastEnd);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Equal(1, presents);
        Assert.Empty(Layers(engine));
    }

    /// <summary>A disabled clip is dropped by <c>RenderGraph.ResolveClipLayer</c>, so the preview must drop it too
    /// — otherwise hiding a clip leaves its frame stranded on the monitor and preview disagrees with export.</summary>
    [Fact]
    public async Task Disabled_Clip_Drops_Out_Of_The_Composite()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, Clip clip) = BuildProject();
        await using PlaybackEngine engine = BuildEngine(project);

        engine.SeekTo(Inside);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.NotEmpty(Layers(engine));

        int presents = 0;
        engine.FramePresented += () => presents++;

        clip.Enabled = false;
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Empty(Layers(engine));
        Assert.Equal(1, presents); // and the surface was told, so the picture actually disappears
    }

    [Fact]
    public async Task Disabled_Track_Drops_Out_Of_The_Composite()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildProject();
        await using PlaybackEngine engine = BuildEngine(project);

        engine.SeekTo(Inside);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.NotEmpty(Layers(engine));

        project.Timeline.VideoTracks.First().Enabled = false;
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Empty(Layers(engine));
    }

    /// <summary>The guard on the blank-on-seek rule: scrubbing back onto a clip presents its content, never a
    /// black flash on the way in.</summary>
    [Fact]
    public async Task Scrub_Back_Onto_A_Clip_Presents_Its_Content()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildProject();
        await using PlaybackEngine engine = BuildEngine(project);

        engine.SeekTo(PastEnd);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Empty(Layers(engine));

        int presents = 0;
        engine.FramePresented += () => presents++;

        engine.SeekTo(Inside);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Equal(1, presents);
        Assert.NotEmpty(Layers(engine));
    }
}
