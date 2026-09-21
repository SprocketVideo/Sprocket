using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Regression guard for the "Program monitor plays but everything is frozen" bug: a per-track decode error must
/// not abort the pump tick before the clock-driven playhead is reported. With a feed whose <c>ReadAsync</c>
/// throws on every call, <see cref="PlaybackEngine.PumpOnceAsync"/> must still raise <c>PumpError</c> and fire
/// <c>PositionChanged</c> with the advancing clock position, so the transport (playhead, scrubber, readout, audio)
/// keeps moving while only the broken track holds/blanks.
/// </summary>
public class DecodeErrorTransportTests
{
    private static readonly Rational Fps = new(TestVideo.Fps, 1);
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>An <see cref="IVideoFrameFeed"/> that fails every read — stands in for a decoder stuck throwing
    /// (an unrecoverable hardware-decode failure, a corrupt clip).</summary>
    private sealed class ThrowingFeed : IVideoFrameFeed
    {
        public int Reads { get; private set; }
        public void Start() { }
        public ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            throw new InvalidOperationException("avcodec_send_packet failed (simulated).");
        }
        public void RequestSeek(Timecode sourceTarget) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (Project project, ProbedMediaInfo info) BuildSession()
    {
        ProbedMediaInfo info;
        using (MediaSource probe = MediaSource.Open(TestVideo.Path))
            info = probe.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero));
        timeline.Tracks.Add(track);
        return (project, info);
    }

    [Fact]
    public async Task Decode_Error_Raises_PumpError_But_Playhead_Keeps_Advancing()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildSession();

        var feed = new ThrowingFeed();
        var elapsed = TimeSpan.Zero;
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, _ => feed, clock);

        Exception? pumpError = null;
        engine.PumpError += ex => pumpError = ex;
        Timecode? reported = null;
        engine.PositionChanged += pos => reported = pos;

        engine.SeekTo(Timecode.Zero);
        clock.Start();
        elapsed = TimeSpan.FromSeconds(1.0); // 1.0s @ 30fps → frame 30

        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        // The failing track surfaced its error (not swallowed)...
        Assert.True(feed.Reads > 0);
        Assert.NotNull(pumpError);
        Assert.IsType<InvalidOperationException>(pumpError);

        // ...yet the tick still ran to completion and reported the clock-driven position.
        Assert.Equal(30 * FrameTicks, reported?.Ticks);
    }

    [Fact]
    public async Task Repeated_Decode_Errors_Do_Not_Freeze_The_Transport()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, _) = BuildSession();

        var feed = new ThrowingFeed();
        var elapsed = TimeSpan.Zero;
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, _ => feed, clock);

        int errors = 0;
        engine.PumpError += _ => errors++;
        var positions = new List<long>();
        engine.PositionChanged += pos => positions.Add(pos.Ticks);

        engine.SeekTo(Timecode.Zero);
        clock.Start();

        // Every tick throws in the feed; the playhead must still advance with the clock across all of them.
        for (int f = 0; f < 5; f++)
        {
            elapsed = TimeSpan.FromSeconds(f / (double)TestVideo.Fps);
            await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        }

        Assert.Equal(5, errors);
        Assert.Equal(new long[] { 0, FrameTicks, 2 * FrameTicks, 3 * FrameTicks, 4 * FrameTicks }, positions);
    }
}
