using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Reverse playback end-to-end through the engine (PLAN.md step 21 remainder): a reversed clip's presented frames
/// run through the source in descending order via the GOP-aware <see cref="ReverseVideoDecodeRing"/> feed the
/// direction-aware factory builds, and the fixed-feed fallback (a forward-only feed) still lands on the right
/// frames by re-seeking.
/// </summary>
public class ReversePlaybackTests
{
    private static readonly Rational Fps = new(TestVideo.Fps, 1);
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static (Project project, MediaRefId id) BuildProject(bool reverse, Rational? speed = null)
    {
        ProbedMediaInfo info = MediaSource.ProbeInfo(TestVideo.Path);
        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));
        var track = new VideoTrack { Name = "V1" };
        // Source [0, 90 frames) — the whole fixture — reversed.
        var clip = new Clip(id, Timecode.Zero, Timecode.FromFrames(TestVideo.FrameCount, Fps), Timecode.Zero) { Reverse = reverse };
        if (speed is { } s)
            clip.SpeedRatio = s;
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return (project, id);
    }

    private static IVideoFrameFeed? DirectionAwareFeed(MediaRefId _, bool reverse)
    {
        MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled);
        return reverse
            ? new ReverseRingVideoFrameFeed(new ReverseVideoDecodeRing(source, windowCapacity: 8))
            : new RingVideoFrameFeed(new VideoDecodeRing(source));
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
    public async Task Reversed_Clip_Presents_Frames_In_Descending_Order_Through_The_Reverse_Feed()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, _) = BuildProject(reverse: true);
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, DirectionAwareFeed, clock);

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        // Timeline 0 maps to the exclusive out-point (90 frames) → the latest frame before it, frame 89.
        Assert.Equal(89 * FrameTicks, CurrentPts(engine));

        clock.Start();
        var seen = new List<long> { CurrentPts(engine) / FrameTicks };
        for (int k = 1; k <= 40; k++)
        {
            elapsed = TimeSpan.FromSeconds((k + 0.5) / TestVideo.Fps);
            await engine.PumpOnceAsync(forcePresent: false, cts.Token);
            seen.Add(CurrentPts(engine) / FrameTicks);
        }

        // Frame k of the timeline shows source frame 89 − k: strictly descending, one per tick, across GOP windows.
        for (int k = 0; k < seen.Count; k++)
            Assert.Equal(89 - k, seen[k]);
    }

    [Fact]
    public async Task Reversed_Clip_Seek_Lands_On_The_Mirrored_Frame()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildProject(reverse: true);
        await using var engine = new PlaybackEngine(project, DirectionAwareFeed, new SoftwareClock(() => TimeSpan.Zero));

        // Timeline frame 30 maps to source 90f − 30f = 60f, an *exclusive* bound: the frame shown is the latest
        // strictly before it, frame 59 — timeline frame k mirrors exactly to source frame 89 − k.
        engine.SeekTo(Timecode.FromFrames(30, Fps));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(59 * FrameTicks, CurrentPts(engine));

        // Seeking again elsewhere re-targets the reverse feed.
        engine.SeekTo(Timecode.FromFrames(80, Fps));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        Assert.Equal(9 * FrameTicks, CurrentPts(engine));
    }

    [Fact]
    public async Task Reversed_Clip_Works_On_A_Forward_Only_Fixed_Feed_By_Reseeking()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, _) = BuildProject(reverse: true);
        var feed = new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled)));
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        long first = CurrentPts(engine) / FrameTicks;

        clock.Start();
        elapsed = TimeSpan.FromSeconds(5.5 / TestVideo.Fps);
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        long later = CurrentPts(engine) / FrameTicks;

        // The forward ring's seek lands on the frame at/after the mapped time (frame-aligned here), so frame 0 of
        // the timeline shows source frame 90 − 1 → the ring yields 89 or 90-clamped; what matters is direction.
        Assert.True(later < first, $"a reversed clip must walk the source backwards even on a forward feed ({first} → {later})");
        Assert.Equal(first - 5, later);
    }

    [Fact]
    public async Task Reversed_Half_Speed_Clip_Holds_Each_Source_Frame_For_Two_Timeline_Frames()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, _) = BuildProject(reverse: true, speed: new Rational(1, 2));
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, DirectionAwareFeed, clock);

        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        clock.Start();
        var seen = new List<long>();
        for (int k = 1; k <= 8; k++)
        {
            elapsed = TimeSpan.FromSeconds((k + 0.5) / TestVideo.Fps);
            await engine.PumpOnceAsync(forcePresent: false, cts.Token);
            seen.Add(CurrentPts(engine) / FrameTicks);
        }
        // Source frame index = 89 − floor(k/2) (½× reversed): 89, 88, 88, 87, 87, 86, 86, 85.
        Assert.Equal(new long[] { 89, 88, 88, 87, 87, 86, 86, 85 }, seen);
    }
}
