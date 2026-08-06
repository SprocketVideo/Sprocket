using System.Diagnostics;
using Sprocket.Core.Timing;
using Sprocket.Playback;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Frame pacing over the <b>smoothed audio master clock</b> (ARCHITECTURE.md §8) — the companion to
/// <see cref="PumpPacingTests"/>. Real audio devices report their played-frame counter quantized to the device
/// update period (OpenAL Soft: 960 frames = 20 ms per mixer update, measured), and a clock that jumps by most
/// of a video frame at a time defeats the boundary-locked pacing those tests prove: a tick scheduled just past
/// a boundary can observe a clock still sitting before it (re-service) and the next tick a clock past two
/// boundaries (a phantom skip). <c>AudioEngine</c> therefore smooths the raw counter — a slope-1 estimate on a
/// monotonic time source, snapped forward when the device overtakes it and capped one mix buffer past the
/// newest raw reading. These tests replicate that estimator exactly as the pump samples it (state advanced only
/// at tick instants), feed it through <see cref="PlaybackEngine.NextBoundaryWaitTicks"/>, and prove that every
/// scheduled tick still services exactly the next frame index — the invariant that keeps the dropped-frame
/// counter honest — for each supported sequence rate, over both a clean and a jittered device period.
/// </summary>
public class SmoothedClockPacingTests
{
    private const int Rate = 48000;
    private const long MaxLeadFrames = 2048; // AudioEngine's bound: one default mix buffer

    public static TheoryData<int, int> FrameRates => new()
    {
        { 30, 1 },
        { 30000, 1001 }, // 29.97 NTSC — boundaries land off the tick grid
        { 24, 1 },
        { 25, 1 },
        { 60, 1 },       // frame interval (16.7 ms) shorter than the device period (20 ms)
    };

    /// <summary>
    /// AudioEngine's smoothing estimator, advanced only when sampled — exactly how the pump reads
    /// <c>Now</c> — over a scripted raw device counter.
    /// </summary>
    private sealed class SmoothedClock(Func<double, long> rawAt)
    {
        private long _rawMax;
        private long _anchorFrames;
        private double _anchorSec;
        private long _smoothed;

        public Timecode Sample(double tSec)
        {
            long raw = rawAt(tSec);
            if (raw > _rawMax)
                _rawMax = raw;

            long estimate = _anchorFrames + (long)((tSec - _anchorSec) * Rate);
            long cap = _rawMax + MaxLeadFrames;
            if (estimate > cap)
            {
                estimate = cap;
                _anchorFrames = cap;
                _anchorSec = tSec;
            }
            else if (estimate < _rawMax)
            {
                estimate = _rawMax;
                _anchorFrames = estimate;
                _anchorSec = tSec;
            }

            if (estimate < _smoothed)
                estimate = _smoothed;
            _smoothed = estimate;
            return Timecode.FromSamples(estimate, Rate);
        }
    }

    private static void AssertConsecutiveFrameServicing(Rational fps, Func<double, long> rawAt)
    {
        double frameSec = (double)fps.Den / fps.Num;
        long frameTicks = Math.Max(1, (long)(frameSec * Stopwatch.Frequency));

        var clock = new SmoothedClock(rawAt);
        double t = 0;
        Timecode now = clock.Sample(t);
        long frame = now.ToFrameIndex(fps);

        // ~20 s of playback: every scheduled tick must land exactly one frame index further on.
        for (int i = 0; i < 600; i++)
        {
            long wait = PlaybackEngine.NextBoundaryWaitTicks(now, fps, frameTicks);
            t += wait / (double)Stopwatch.Frequency;
            now = clock.Sample(t);
            long f = now.ToFrameIndex(fps);
            Assert.Equal(frame + 1, f);
            frame = f;
        }
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void Every_Scheduled_Tick_Services_The_Next_Frame_Over_A_20ms_Stepped_Device_Clock(int num, int den)
    {
        // The measured OpenAL Soft behavior: the raw counter advances 960 frames once per 20 ms mixer update.
        AssertConsecutiveFrameServicing(new Rational(num, den),
            t => (long)(t / 0.020) * 960);
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void Every_Scheduled_Tick_Services_The_Next_Frame_Over_A_Jittered_Device_Period(int num, int den)
    {
        // The device's updates land unevenly (scheduler jitter) while its long-run rate stays exact: periods
        // alternate 15 ms / 25 ms (720 / 1200 frames). The estimator must absorb the uneven observations.
        AssertConsecutiveFrameServicing(new Rational(num, den), t =>
        {
            long pairs = (long)(t / 0.040);
            double rem = t - pairs * 0.040;
            return pairs * 1920 + (rem >= 0.015 ? 720 : 0) + (rem >= 0.040 ? 1200 : 0);
        });
    }

    [Fact]
    public void A_Stalled_Device_Holds_The_Clock_Within_The_Documented_Bound()
    {
        // Raw counter freezes at 1 s (device stall): the smoothed clock may run at most one mix buffer past it,
        // then must hold — video never keeps advancing over unheard audio.
        var clock = new SmoothedClock(t => Math.Min((long)(t / 0.020), 50) * 960);
        Timecode last = default;
        for (double t = 0; t < 3.0; t += 0.010)
            last = clock.Sample(t);

        Assert.Equal(Timecode.FromSamples(50 * 960 + MaxLeadFrames, Rate).Ticks, last.Ticks);
    }
}
