using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Ring-buffer feed behaviour for <see cref="VideoDecodeRing"/>: ordered read-ahead, end-of-stream
/// signalling, and generation-tagged seeking (ARCHITECTURE.md §8, PLAN.md build-order step 3).
/// </summary>
public class VideoDecodeRingTests
{
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps;
    private static readonly Rational FrameRate = new(TestVideo.Fps, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static VideoDecodeRing NewRing(int capacity = VideoDecodeRing.DefaultCapacity)
    {
        var ring = new VideoDecodeRing(MediaSource.Open(TestVideo.Path), capacity);
        ring.Start();
        return ring;
    }

    /// <summary>Reads with a hard timeout so a hung worker fails the test instead of hanging the run.</summary>
    private static async ValueTask<VideoFrame?> ReadAsync(VideoDecodeRing ring)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await ring.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task Feeds_All_Frames_In_Order_Then_Signals_End_Of_Stream()
    {
        await using VideoDecodeRing ring = NewRing();

        var pts = new List<long>();
        while (await ReadAsync(ring) is { } frame)
        {
            using (frame)
                pts.Add(frame.Pts.Ticks);
        }

        Assert.Equal(TestVideo.FrameCount, pts.Count);
        Assert.Equal(0, pts[0]);
        for (int i = 1; i < pts.Count; i++)
            Assert.Equal(FrameTicks, pts[i] - pts[i - 1]);
    }

    [Fact]
    public async Task Small_Capacity_Still_Delivers_Every_Frame()
    {
        // A tight bound exercises backpressure: the worker blocks until the consumer drains.
        await using VideoDecodeRing ring = NewRing(capacity: 2);

        int count = 0;
        while (await ReadAsync(ring) is { } frame)
        {
            frame.Dispose();
            count++;
        }

        Assert.Equal(TestVideo.FrameCount, count);
    }

    [Fact]
    public async Task Seek_Yields_Frames_From_The_Target_Discarding_Stale_Buffered_Frames()
    {
        await using VideoDecodeRing ring = NewRing();

        // Read a couple of frames so the worker is well into the clip / buffering ahead.
        (await ReadAsync(ring))!.Dispose();
        (await ReadAsync(ring))!.Dispose();

        const int targetFrame = 60;
        var target = Timecode.FromFrames(targetFrame, FrameRate);
        ring.RequestSeek(target);

        // The next delivered frame must be the post-seek frame, not a stale pre-seek one.
        VideoFrame? next = await ReadAsync(ring);
        Assert.NotNull(next);
        using (next)
            Assert.Equal(target.Ticks, next!.Pts.Ticks);
    }

    [Fact]
    public async Task Seek_After_End_Of_Stream_Resumes_Decoding()
    {
        await using VideoDecodeRing ring = NewRing();

        // Drain to EOF.
        while (await ReadAsync(ring) is { } frame)
            frame.Dispose();

        // Scrub back: the worker was parked at EOF and must resume.
        var target = Timecode.FromFrames(10, FrameRate);
        ring.RequestSeek(target);

        VideoFrame? resumed = await ReadAsync(ring);
        Assert.NotNull(resumed);
        using (resumed)
            Assert.Equal(target.Ticks, resumed!.Pts.Ticks);
    }

    [Fact]
    public async Task Dispose_Is_Clean_While_Frames_Are_Still_Buffered()
    {
        var ring = NewRing();
        // Read just one frame, leaving the worker mid-stream with buffered frames.
        (await ReadAsync(ring))!.Dispose();
        await ring.DisposeAsync(); // must not throw or hang; drains and recycles internally
    }
}
