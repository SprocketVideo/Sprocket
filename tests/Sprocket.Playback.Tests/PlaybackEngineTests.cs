using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Deterministic tests for the <see cref="PlaybackEngine"/> pump: frame selection (present/hold/drop) and
/// transport end-of-timeline behaviour, driven by stepping <see cref="PlaybackEngine.PumpOnceAsync"/> with a
/// controllable clock — no background loop, no real-time sleeping. Frame correctness is deterministic because
/// the fixture decodes to fixed PTS and seeks are frame-accurate.
/// </summary>
public class PlaybackEngineTests
{
    private static readonly Rational Fps = new(TestVideo.Fps, 1);
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps; // 8000 ticks/frame @ 30fps
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static (Project project, RingVideoFrameFeed feed) BuildSession()
    {
        MediaSource source = MediaSource.Open(TestVideo.Path);
        ProbedMediaInfo info = source.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero));
        timeline.Tracks.Add(track);

        var feed = new RingVideoFrameFeed(new VideoDecodeRing(source));
        return (project, feed);
    }

    private static long CurrentPts(PlaybackEngine engine)
    {
        long pts = -1;
        engine.UseCurrentFrame(f =>
        {
            if (f is { } frame)
                pts = frame.Pts.Ticks;
        });
        return pts;
    }

    [Fact]
    public async Task Presents_First_Frame_After_Seek_To_Start()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        await using var engine = new PlaybackEngine(project, feed, new SoftwareClock(() => TimeSpan.Zero));

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        Assert.Equal(0, CurrentPts(engine));
    }

    [Fact]
    public async Task Seek_Presents_The_Target_Frame()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        await using var engine = new PlaybackEngine(project, feed, new SoftwareClock(() => TimeSpan.Zero));

        feed.Start();
        engine.SeekTo(Timecode.FromFrames(60, Fps));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        Assert.Equal(60 * FrameTicks, CurrentPts(engine));
    }

    [Fact]
    public async Task Holds_Current_Frame_When_Playhead_Has_Not_Reached_The_Next_Frame()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        await using var engine = new PlaybackEngine(project, feed, new SoftwareClock(() => TimeSpan.Zero));

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token); // present frame 0, prefetch frame 1

        // Clock is still parked at 0, so the next frame (frame 1) is not due — the engine must hold frame 0.
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Equal(0, CurrentPts(engine));
    }

    [Fact]
    public async Task Advances_And_Drops_To_Catch_Up_With_The_Clock()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token); // frame 0

        clock.Start();                          // anchors at elapsed 0
        elapsed = TimeSpan.FromSeconds(1.0);    // 1.0s @ 30fps → frame 30
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        // The pump dropped frames 1..29 and landed on the frame due at 1.0s.
        Assert.Equal(30 * FrameTicks, CurrentPts(engine));
    }

    [Fact]
    public async Task Reports_Dropped_Frames_In_Statistics_When_Catching_Up()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token); // present frame 0 (forced ⇒ no drops counted)

        Assert.Equal(0, engine.GetStatistics().FramesDropped);

        clock.Start();
        elapsed = TimeSpan.FromSeconds(1.0);                       // jump to frame 30 in one non-forced pump
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        PlaybackStatistics stats = engine.GetStatistics();
        // The playhead jumped from timeline frame 0 to frame 30 in one (non-forced) present, so the 29 timeline
        // frames it skipped over without presenting are counted as drops. (Here the source rate equals the
        // sequence rate, so this also equals the number of decoded source frames skipped.)
        Assert.Equal(29, stats.FramesDropped);
        Assert.Equal(2, stats.FramesPresented);   // forced frame 0 + the catch-up present
        Assert.Equal(2, stats.PumpIterations);
    }

    [Fact]
    public async Task Dropped_Frames_This_Span_Resets_On_Play_While_Cumulative_Persists()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token); // baseline at frame 0

        // A play span that falls behind: clock jumps to frame 30, skipping 29.
        clock.Start();
        elapsed = TimeSpan.FromSeconds(1.0);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        PlaybackStatistics s1 = engine.GetStatistics();
        Assert.Equal(29, s1.FramesDropped);
        Assert.Equal(29, s1.FramesDroppedThisSpan);

        // Starting a new play span resets the per-span count but not the cumulative one.
        engine.Play();
        PlaybackStatistics s2 = engine.GetStatistics();
        Assert.Equal(29, s2.FramesDropped);
        Assert.Equal(0, s2.FramesDroppedThisSpan);

        // The first present of the new span is startup catch-up (re-baseline) — it must NOT count, even though
        // the playhead jumped from frame 30 to frame 48.
        elapsed = TimeSpan.FromSeconds(1.6); // frame 48
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        PlaybackStatistics s3 = engine.GetStatistics();
        Assert.Equal(29, s3.FramesDropped);
        Assert.Equal(0, s3.FramesDroppedThisSpan);

        // A genuine fall-behind within the span now accumulates afresh (frame 48 → 60 skips 11), and adds to the
        // cumulative total too.
        elapsed = TimeSpan.FromSeconds(2.0); // frame 60
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        PlaybackStatistics s4 = engine.GetStatistics();
        Assert.Equal(11, s4.FramesDroppedThisSpan);
        Assert.Equal(40, s4.FramesDropped); // 29 (first span) + 11 (this span)
    }

    [Fact]
    public async Task Reaches_End_Then_Stops_And_Signals()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        bool ended = false;
        engine.PlaybackEnded += () => ended = true;

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        engine.Play();                                  // state → Playing (anchors clock at elapsed 0)
        elapsed = TimeSpan.FromSeconds(100);            // well past the ~3s clip
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.True(ended);
        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(engine.Duration.Ticks, engine.Position.Ticks);
    }
}
