namespace Sprocket.Core.Timing;

/// <summary>
/// Tick clamping and on-screen-tolerance snapping shared by the App's timeline geometry (<c>TimelineMath</c>) and
/// the Core edit builders that place clips (<see cref="Commands.ClipPlacement"/>). The snap tolerance is in screen
/// pixels at a zoom of <c>pxPerSecond</c>, so a snap feels the same at every zoom level.
/// </summary>
public static class TickSnapping
{
    /// <summary>Clamps a tick value to be non-negative (the timeline starts at 0).</summary>
    public static long ClampNonNegative(long ticks) => ticks < 0 ? 0 : ticks;

    /// <summary>
    /// Snaps <paramref name="ticks"/> to the nearest <paramref name="candidates"/> entry that is within
    /// <paramref name="tolerancePx"/> on screen; returns the original value if none is close enough. Used to
    /// snap a dragged clip edge to other clip edges, the playhead, and the timeline origin.
    /// </summary>
    public static long Snap(long ticks, IReadOnlyList<long> candidates, double tolerancePx, double pxPerSecond)
    {
        long best = ticks;
        double bestPx = tolerancePx;
        foreach (long c in candidates)
        {
            double dpx = Math.Abs((double)(ticks - c) * pxPerSecond / Timecode.TicksPerSecond);
            if (dpx <= bestPx)
            {
                bestPx = dpx;
                best = c;
            }
        }
        return best;
    }
}
