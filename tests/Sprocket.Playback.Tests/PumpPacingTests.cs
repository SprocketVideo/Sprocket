using System.Diagnostics;
using Sprocket.Core.Timing;
using Sprocket.Playback;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Frame pacing of the pump loop (ARCHITECTURE.md §8).
/// <para>
/// The pump used to schedule each tick on its own absolute wall-clock grid, one sequence frame-interval apart.
/// That grid free-runs in phase against the <em>master clock's</em> frame grid — the grid the pump is actually
/// scored against — so ticks drift across frame boundaries: one lands just short and re-services the frame it
/// already did (a hold that presents nothing), and the next lands past the following boundary and is charged a
/// skipped frame. Those (hold, skip) pairs are pure aliasing, but they put a permanent floor of roughly one
/// "dropped" frame per second under the counter on a machine that is comfortably keeping up.
/// </para>
/// <para>
/// The fix derives each deadline from the master clock instead, so the invariant below holds by construction.
/// It is asserted directly rather than by playing in real time and counting drops: on a loaded machine
/// scheduler noise swamps the effect (measured 0–3 drops per 2.5 s with the fix against 0–9 without — the
/// distributions overlap), so a timing-based test would be flaky in both directions.
/// </para>
/// </summary>
public class PumpPacingTests
{
    public static TheoryData<int, int> FrameRates => new()
    {
        { 30, 1 },        // 30p
        { 30000, 1001 },  // 29.97 NTSC — boundaries land off the tick grid
        { 24, 1 },
        { 60, 1 },        // the shortest frame, where the lead has least room
        { 25, 1 },
    };

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void Every_Scheduled_Tick_Lands_Exactly_One_Frame_Further_On(int num, int den)
    {
        var fps = new Rational(num, den);
        double frameSec = (double)den / num;
        long frameTicks = Math.Max(1, (long)(frameSec * Stopwatch.Frequency));

        // Sweep the whole of frame 100 — a tick can arrive at any offset within a frame, and the boundary-relative
        // extremes (just after the previous boundary, a hair before the next) are exactly where an off-by-one in
        // the deadline shows up as a re-serviced or skipped frame.
        long frameStart = Timecode.FromFrames(100, fps).Ticks;
        long nextStart = Timecode.FromFrames(101, fps).Ticks;

        for (long offset = 0; offset < nextStart - frameStart; offset++)
        {
            var clockNow = new Timecode(frameStart + offset);
            long waitTicks = PlaybackEngine.NextBoundaryWaitTicks(clockNow, fps, frameTicks);

            // Where the clock will read when the tick fires (the clock and Stopwatch both run at real time).
            double waitSec = waitTicks / (double)Stopwatch.Frequency;
            var landsAt = new Timecode(clockNow.Ticks + (long)(waitSec * Timecode.TicksPerSecond));

            Assert.Equal(clockNow.ToFrameIndex(fps) + 1, landsAt.ToFrameIndex(fps));
        }
    }

    [Fact]
    public void A_Clock_Reading_Exactly_On_A_Boundary_Waits_A_Whole_Frame()
    {
        // The degenerate case of the sweep above, called out because it is the one an "add a frame interval to the
        // last deadline" scheduler also gets right — the pacing must not regress to that by special-casing it.
        var fps = new Rational(30, 1);
        long frameTicks = (long)(Stopwatch.Frequency / 30.0);
        long wait = PlaybackEngine.NextBoundaryWaitTicks(Timecode.FromFrames(10, fps), fps, frameTicks);

        Assert.InRange(wait, frameTicks, frameTicks + frameTicks / 8);
    }

    [Fact]
    public void A_Sequence_With_No_Frame_Rate_Free_Runs_At_The_Caller_S_Interval()
    {
        // There is no frame grid to lock to without a rate, so the pump must fall back to the interval it was given
        // rather than dividing by zero or stalling.
        long frameTicks = (long)(Stopwatch.Frequency / 30.0);

        Assert.Equal(frameTicks, PlaybackEngine.NextBoundaryWaitTicks(Timecode.Zero, new Rational(0, 1), frameTicks));
    }
}
