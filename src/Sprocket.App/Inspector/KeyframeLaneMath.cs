using System;
using System.Collections.Generic;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure geometry for the Inspector's per-parameter keyframe lane (PLAN.md step 16b): mapping a keyframe's
/// timeline time to an X within the lane and back, and hit-testing the nearest keyframe to a pointer. Kept
/// free of Avalonia types so it is unit-testable headlessly, mirroring <see cref="Timeline.TimelineMath"/>;
/// the lane's drawing + pointer interaction rest on this and on manual verification. Keyframe times are
/// absolute timeline times (the domain the render graph evaluates effects in, §5/§9), so the lane spans the
/// clip's timeline range <c>[rangeStartTicks, rangeEndTicks]</c>.
/// </summary>
public static class KeyframeLaneMath
{
    /// <summary>The X (px) within a lane of width <paramref name="laneWidth"/> for a timeline tick, clamped to
    /// the lane. A degenerate range (start ≥ end) maps everything to the left edge.</summary>
    public static double XAt(long ticks, long rangeStartTicks, long rangeEndTicks, double laneWidth)
    {
        long span = rangeEndTicks - rangeStartTicks;
        if (span <= 0)
            return 0;
        double frac = (double)(ticks - rangeStartTicks) / span;
        return Math.Clamp(frac, 0, 1) * laneWidth;
    }

    /// <summary>The timeline tick at an X (px) within the lane — the inverse of <see cref="XAt"/>, clamped to
    /// the range. A degenerate range or width returns <paramref name="rangeStartTicks"/>.</summary>
    public static long TicksAt(double x, long rangeStartTicks, long rangeEndTicks, double laneWidth)
    {
        long span = rangeEndTicks - rangeStartTicks;
        if (span <= 0 || laneWidth <= 0)
            return rangeStartTicks;
        double frac = Math.Clamp(x / laneWidth, 0, 1);
        return rangeStartTicks + (long)Math.Round(frac * span);
    }

    /// <summary>
    /// The index of the keyframe nearest pointer X within <paramref name="tolerancePx"/>, or -1 if none is
    /// close enough (so the caller can add a new keyframe instead of grabbing one). Used to pick a keyframe to
    /// drag/delete on the lane.
    /// </summary>
    public static int NearestKeyframeIndex(
        double x, IReadOnlyList<Keyframe> keyframes, long rangeStartTicks, long rangeEndTicks,
        double laneWidth, double tolerancePx)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        int best = -1;
        double bestDist = tolerancePx;
        for (int i = 0; i < keyframes.Count; i++)
        {
            double kx = XAt(keyframes[i].Time.Ticks, rangeStartTicks, rangeEndTicks, laneWidth);
            double dist = Math.Abs(kx - x);
            if (dist <= bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }
}
