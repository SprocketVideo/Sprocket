using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Play In to Out (<see cref="PlaybackEngine.PlayInToOut"/>) — the dedicated ranged-play transport command of
/// leading editors: playback runs from the in mark and parks at the out mark, invoking it again replays from the
/// in mark, and the range constrains only that play span — a normal <see cref="PlaybackEngine.Play"/> (Space) or
/// a scrub is deliberately unconstrained by the marks. Driven deterministically through a test-controlled
/// <see cref="SoftwareClock"/> and <see cref="PlaybackEngine.PumpOnceAsync"/>.
/// </summary>
public class PlayInToOutTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) BuildSession()
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

        return (project, new RingVideoFrameFeed(new VideoDecodeRing(source)), info);
    }

    private static async Task PumpAsync(PlaybackEngine engine, bool force = false)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await engine.PumpOnceAsync(forcePresent: force, cts.Token);
    }

    [Fact]
    public async Task Stops_At_The_Out_Point_And_Restarts_From_The_In_Point()
    {
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await PumpAsync(engine, force: true); // prime the first frame

        var inPoint = new Timecode(info.Duration.Ticks / 4);
        var outPoint = new Timecode(info.Duration.Ticks / 2);
        int ended = 0;
        engine.PlaybackEnded += () => Interlocked.Increment(ref ended);

        engine.PlayInToOut(inPoint, outPoint);
        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.Equal(inPoint, engine.Position); // the span starts at the in mark, wherever the playhead was

        // Advance the clock past the out point: the pump must park at the OUT mark, not the timeline end.
        elapsed += TimeSpan.FromSeconds((outPoint - inPoint).ToSeconds() + 0.05);
        await PumpAsync(engine);

        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(outPoint, engine.Position);
        Assert.Equal(1, ended);

        // Invoking it again replays from the in mark (it does not resume from the parked out point).
        engine.PlayInToOut(inPoint, outPoint);
        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.Equal(inPoint, engine.Position);
    }

    [Fact]
    public async Task Normal_Play_Is_Not_Constrained_By_A_Finished_Ranged_Span()
    {
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await PumpAsync(engine, force: true);

        var outPoint = new Timecode(info.Duration.Ticks / 2);
        engine.PlayInToOut(Timecode.Zero, outPoint);
        elapsed += TimeSpan.FromSeconds(outPoint.ToSeconds() + 0.05);
        await PumpAsync(engine); // parked at the out mark

        // Plain Play from the parked out point runs to the TIMELINE end — the marks never gate normal playback.
        engine.Play();
        elapsed += TimeSpan.FromSeconds((info.Duration - outPoint).ToSeconds() + 0.05);
        await PumpAsync(engine);

        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(info.Duration, engine.Position);
    }

    [Fact]
    public async Task A_Seek_Cancels_The_Ranged_Span()
    {
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await PumpAsync(engine, force: true);

        var outPoint = new Timecode(info.Duration.Ticks / 2);
        engine.PlayInToOut(Timecode.Zero, outPoint);

        // A scrub mid-span cancels the constraint (the leading-editors behaviour): playback keeps going past
        // the out mark and only stops at the timeline end.
        engine.SeekTo(new Timecode(info.Duration.Ticks / 4));
        elapsed += TimeSpan.FromSeconds(info.Duration.ToSeconds()); // well past the out mark AND the timeline end
        await PumpAsync(engine);

        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(info.Duration, engine.Position); // parked at the timeline end, not the out mark
    }

    [Fact]
    public async Task An_Empty_Range_Degenerates_To_A_Normal_Play_From_The_In_Point()
    {
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed, ProbedMediaInfo info) = BuildSession();
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await PumpAsync(engine, force: true);

        var point = new Timecode(info.Duration.Ticks / 2);
        engine.PlayInToOut(point, point); // out == in → nothing to constrain

        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.Equal(point, engine.Position);

        elapsed += TimeSpan.FromSeconds(info.Duration.ToSeconds());
        await PumpAsync(engine);

        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(info.Duration, engine.Position);
    }
}
