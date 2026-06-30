using System;
using System.Collections.Generic;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// The active timeline tool (UI.md §3.2 palette, PLAN.md steps 13/22). <see cref="Select"/> moves/trims clips;
/// <see cref="Blade"/> splits a clip at the cursor; <see cref="Ripple"/> trims an edge and closes/opens the gap
/// downstream; <see cref="Roll"/> rolls the cut between two adjacent clips; <see cref="Slip"/> shifts a clip's
/// source in/out without moving it; <see cref="Slide"/> moves a clip while its neighbours absorb the change;
/// <see cref="Hand"/> pans the view and <see cref="Zoom"/> zooms it (view-only). The trim family
/// (Ripple/Roll/Slip/Slide) mirrors the Premiere/Resolve/FCP toolset.
/// </summary>
public enum EditTool
{
    /// <summary>Default arrow: select, move, and edge-trim clips.</summary>
    Select,

    /// <summary>Razor: click a clip to split it at the cursor.</summary>
    Blade,

    /// <summary>Ripple edit: trim a clip's edge and shift every downstream clip to keep the track gap-free.</summary>
    Ripple,

    /// <summary>Rolling edit: drag the cut between two adjacent clips, moving one's out and the next's in together.</summary>
    Roll,

    /// <summary>Slip a clip's source in/out (its visible content) without moving it on the timeline.</summary>
    Slip,

    /// <summary>Slide a clip along the timeline while its neighbours absorb the change (the complement of slip).</summary>
    Slide,

    /// <summary>Pan the timeline view (drag to scroll).</summary>
    Hand,

    /// <summary>Zoom the timeline view (click to zoom in, modifier-click to zoom out).</summary>
    Zoom,
}

/// <summary>What part of a clip a pointer is over — selects the drag behaviour.</summary>
public enum ClipDragMode
{
    /// <summary>Not over the clip.</summary>
    None,

    /// <summary>Over the body: drag moves the clip along the timeline.</summary>
    Move,

    /// <summary>Over the left edge: drag trims the in-point (and slides the start, right edge fixed).</summary>
    TrimStart,

    /// <summary>Over the right edge: drag trims the out-point.</summary>
    TrimEnd,
}

/// <summary>
/// Pure geometry for the timeline control (PLAN.md step 12): tick↔pixel mapping, snapping, edge hit-testing,
/// and ruler-interval selection. Kept free of Avalonia types so it is unit-tested headlessly — the rendering
/// and pointer interaction in <see cref="TimelineControl"/> rest on this and on manual verification.
/// </summary>
public static class TimelineMath
{
    /// <summary>The on-screen X (px) of a timeline tick, given zoom (px/second), horizontal scroll and the
    /// width of the fixed track-header column.</summary>
    public static double XAtTicks(long ticks, double pxPerSecond, double scrollX, double headerWidth)
        => headerWidth - scrollX + (double)ticks * pxPerSecond / Timecode.TicksPerSecond;

    /// <summary>The timeline tick at an on-screen X (px) — the inverse of <see cref="XAtTicks"/>.</summary>
    public static long TicksAtX(double x, double pxPerSecond, double scrollX, double headerWidth)
        => pxPerSecond <= 0 ? 0 : (long)Math.Round((x - headerWidth + scrollX) * Timecode.TicksPerSecond / pxPerSecond);

    /// <summary>The width in px of a tick span at the given zoom.</summary>
    public static double WidthOfTicks(long ticks, double pxPerSecond)
        => (double)ticks * pxPerSecond / Timecode.TicksPerSecond;

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
            double dpx = Math.Abs(WidthOfTicks(ticks - c, pxPerSecond));
            if (dpx <= bestPx)
            {
                bestPx = dpx;
                best = c;
            }
        }
        return best;
    }

    /// <summary>
    /// Classifies a pointer X against a clip's on-screen span <c>[clipX0, clipX1]</c>: within
    /// <paramref name="edgeGrip"/> px of an edge is a trim, inside is a move, outside is <see cref="ClipDragMode.None"/>.
    /// The left edge wins ties so a very narrow clip is still trimmable from the start.
    /// </summary>
    public static ClipDragMode HitMode(double pointerX, double clipX0, double clipX1, double edgeGrip)
    {
        if (pointerX >= clipX0 - edgeGrip && pointerX <= clipX0 + edgeGrip)
            return ClipDragMode.TrimStart;
        if (pointerX >= clipX1 - edgeGrip && pointerX <= clipX1 + edgeGrip)
            return ClipDragMode.TrimEnd;
        if (pointerX >= clipX0 && pointerX <= clipX1)
            return ClipDragMode.Move;
        return ClipDragMode.None;
    }

    /// <summary>
    /// Clamps a slip <paramref name="delta"/> (ticks added to both source in/out) so the source window stays
    /// within the media: <c>SourceIn ≥ 0</c> and <c>SourceOut ≤ mediaDuration</c>. The clip's duration and
    /// timeline position are unchanged — slip only changes which part of the source plays. Returns 0 when the
    /// clip already spans the whole source (no room to slip).
    /// </summary>
    public static long ClampSlip(long origIn, long origOut, long mediaDuration, long delta)
    {
        long minDelta = -origIn;                  // can't pull SourceIn below 0
        long maxDelta = mediaDuration - origOut;  // can't push SourceOut past the media end
        if (maxDelta < minDelta)
            return 0;
        return Math.Clamp(delta, minDelta, maxDelta);
    }

    /// <summary>
    /// Clamps a roll <paramref name="delta"/> (timeline ticks the shared cut moves; positive = the cut moves
    /// right) so neither clip drops below <paramref name="minDuration"/> or runs past its media (PLAN.md step 22).
    /// All quantities are in TIMELINE ticks: <paramref name="leftSourceHeadroom"/> is how far the left clip's
    /// out-point can still extend before hitting the media end (media room ÷ its speed) and
    /// <paramref name="rightSourceHeadroom"/> is how far the right clip's in-point can pull back toward its source
    /// start. Returns 0 when there is no room either way.
    /// </summary>
    public static long ClampRollDelta(
        long delta, long leftDuration, long leftSourceHeadroom,
        long rightDuration, long rightSourceHeadroom, long minDuration)
        => ClampSharedEdgeDelta(delta, leftDuration, leftSourceHeadroom, rightDuration, rightSourceHeadroom, minDuration);

    /// <summary>
    /// Clamps a slide <paramref name="delta"/> (timeline ticks the clip moves; positive = right) so neither
    /// neighbour drops below <paramref name="minDuration"/> or runs past its media (PLAN.md step 22). Sliding
    /// right extends the previous clip's out-point (limited by <paramref name="prevSourceHeadroom"/>) and shrinks
    /// the next clip toward <paramref name="minDuration"/>; sliding left does the reverse, the next clip's in-point
    /// pulling back limited by <paramref name="nextSourceHeadroom"/>. All quantities are in timeline ticks.
    /// </summary>
    public static long ClampSlideDelta(
        long delta, long prevDuration, long prevSourceHeadroom,
        long nextDuration, long nextSourceHeadroom, long minDuration)
        => ClampSharedEdgeDelta(delta, prevDuration, prevSourceHeadroom, nextDuration, nextSourceHeadroom, minDuration);

    // Roll and slide share the same clamp shape: moving "right" grows the left/prev side (limited by its media
    // headroom) and shrinks the right/next side (down to minDuration); moving "left" is the mirror image.
    private static long ClampSharedEdgeDelta(
        long delta, long leftDuration, long leftSourceHeadroom,
        long rightDuration, long rightSourceHeadroom, long minDuration)
    {
        long upper = Math.Min(leftSourceHeadroom, rightDuration - minDuration);
        long lower = -Math.Min(leftDuration - minDuration, rightSourceHeadroom);
        if (upper < lower)
            return 0;
        return Math.Clamp(delta, lower, upper);
    }

    /// <summary>
    /// The inclusive timeline-tick bounds a ripple-trim <paramref name="delta"/> may take for one edge of one
    /// clip (PLAN.md step 22). For a trailing edge (<paramref name="trimEnd"/>) the cut can extend by the clip's
    /// remaining media (<paramref name="outHeadroom"/>) and retract until the clip hits
    /// <paramref name="minDuration"/>; for a leading edge it can extend toward the source start
    /// (<paramref name="inHeadroom"/>, a negative delta) and retract until the clip hits the minimum. All
    /// quantities are timeline ticks. The control intersects these bounds across the dragged clip and any linked
    /// companions, then clamps the pointer delta into the result.
    /// </summary>
    public static (long Lower, long Upper) RippleTrimBounds(
        bool trimEnd, long durationTicks, long inHeadroom, long outHeadroom, long minDuration)
        => trimEnd
            ? (minDuration - durationTicks, outHeadroom)
            : (-inHeadroom, durationTicks - minDuration);

    /// <summary>
    /// The lane index at an on-screen Y: 0-based from the first lane below the ruler, or -1 when above the
    /// ruler. <paramref name="laneStride"/> is the per-lane vertical step (track height + gap). Callers
    /// bound-check the result against the lane count. Mirrors the control's lane layout (PLAN.md step 16e —
    /// the cross-track drag needs the target lane), kept pure so it is unit-tested headlessly.
    /// </summary>
    public static int LaneIndexAtY(double y, double rulerHeight, double laneStride)
    {
        if (y < rulerHeight || laneStride <= 0)
            return -1;
        return (int)((y - rulerHeight) / laneStride);
    }

    /// <summary>
    /// Picks a "nice" ruler tick interval (in ticks) so labels land roughly <paramref name="targetPx"/> apart
    /// at the current zoom — stepping through 0.5/1/2/5/10/15/30/60/120/300/600-second intervals.
    /// </summary>
    public static long RulerIntervalTicks(double pxPerSecond, double targetPx)
    {
        double secondsPerLabel = targetPx / Math.Max(1e-6, pxPerSecond);
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        double chosen = steps[^1];
        foreach (double s in steps)
        {
            if (s >= secondsPerLabel)
            {
                chosen = s;
                break;
            }
        }
        return (long)Math.Round(chosen * Timecode.TicksPerSecond);
    }
}
