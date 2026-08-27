using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// GOP-aware backward decode (PLAN.md step 21 remainder): <see cref="GopFrameWindow"/> retains the frames at/before
/// a target in presentation order, bounded by its capacity, and <see cref="ReverseVideoDecodeRing"/> serves a whole
/// source's frames in strictly descending order across window refills, parking at the start until re-seeked.
/// </summary>
public class ReverseDecodeTests
{
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps;
    private static readonly Rational Fps = new(TestVideo.Fps, 1);

    [Fact]
    public void Window_Retains_The_Frames_At_Or_Before_The_Target_In_Order()
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        using var window = new GopFrameWindow(source, pool, capacity: 6);

        int held = window.FillUpTo(Timecode.FromFrames(40, Fps));
        Assert.Equal(6, held);
        Assert.Equal(35 * FrameTicks, window.FirstPts!.Value.Ticks);
        Assert.Equal(40 * FrameTicks, window.LastPts!.Value.Ticks);

        // Newest-first hand-out.
        for (int expected = 40; expected >= 35; expected--)
        {
            using VideoFrame? f = window.Take();
            Assert.NotNull(f);
            Assert.Equal(expected * FrameTicks, f!.Pts.Ticks);
        }
        Assert.Null(window.Take());
    }

    [Fact]
    public void Window_Peek_Drops_Frames_Past_A_Lower_Target()
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        using var window = new GopFrameWindow(source, pool, capacity: 8);

        window.FillUpTo(Timecode.FromFrames(20, Fps));
        VideoFrame? hit = window.PeekAtOrBefore(Timecode.FromFrames(17, Fps) + new Timecode(FrameTicks / 2));
        Assert.NotNull(hit);
        Assert.Equal(17 * FrameTicks, hit!.Pts.Ticks);
        Assert.Equal(5, window.Count); // 13..17 remain; 18..20 were dropped
        Assert.Null(window.PeekAtOrBefore(Timecode.FromFrames(5, Fps))); // below the window → empty
    }

    [Fact]
    public void Window_At_The_Start_Of_The_Source_Holds_Frame_Zero()
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        using var window = new GopFrameWindow(source, pool, capacity: 8);

        Assert.Equal(3, window.FillUpTo(Timecode.FromFrames(2, Fps)));
        Assert.Equal(0, window.FirstPts!.Value.Ticks);
    }

    [Fact]
    public async Task Reverse_Ring_Yields_Every_Frame_In_Descending_Order_Then_Parks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var ring = new ReverseVideoDecodeRing(
            MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled), capacity: 4, windowCapacity: 7);
        ring.Start();
        ring.RequestSeek(Timecode.FromFrames(TestVideo.FrameCount, Fps)); // the exclusive end: start from the last frame

        var seen = new List<long>();
        while (true)
        {
            VideoFrame? f = await ring.ReadAsync(cts.Token);
            if (f is null)
                break;
            using (f)
                seen.Add(f.Pts.Ticks / FrameTicks);
        }

        Assert.True(seen.Count == TestVideo.FrameCount, $"expected {TestVideo.FrameCount} frames, got {seen.Count}: {string.Join(",", seen)}");
        for (int i = 0; i < seen.Count; i++)
            Assert.Equal(TestVideo.FrameCount - 1 - i, seen[i]); // 89, 88, …, 0 — across 13 window refills

        // Parked at the start; a new seek resumes from there — the seek target is an exclusive bound (a reversed
        // clip's mapped time), so the first frame served is the latest strictly before it.
        ring.RequestSeek(Timecode.FromFrames(10, Fps));
        VideoFrame? again = await ring.ReadAsync(cts.Token);
        Assert.NotNull(again);
        Assert.Equal(9 * FrameTicks, again!.Pts.Ticks);
        again.Dispose();
    }

    [Fact]
    public async Task Reverse_Ring_Seek_Supersedes_Buffered_Frames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var ring = new ReverseVideoDecodeRing(MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled), capacity: 4);
        ring.Start();
        ring.RequestSeek(Timecode.FromFrames(60, Fps));
        VideoFrame? first = await ring.ReadAsync(cts.Token);
        Assert.Equal(59 * FrameTicks, first!.Pts.Ticks); // exclusive bound: the latest frame strictly before 60f
        first.Dispose();

        ring.RequestSeek(Timecode.FromFrames(30, Fps));
        VideoFrame? after = await ring.ReadAsync(cts.Token);
        Assert.NotNull(after);
        Assert.Equal(29 * FrameTicks, after!.Pts.Ticks); // the stale 58/57/… were discarded
        after.Dispose();
    }
}
