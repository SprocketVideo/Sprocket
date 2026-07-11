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
/// (Ripple/Roll/Slip/Slide) mirrors the toolset found in professional NLEs.
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
/// The cursor shape the timeline shows for the current tool + what the pointer is over. Kept as a pure enum
/// (mapped by <see cref="TimelineMath.HoverCursor"/>) so the tool/hover → cursor decision is unit-tested; the
/// control translates each kind to an Avalonia <c>Cursor</c>. The trim/ripple kinds are side-specific so the
/// cursor can show which edge is grabbed (the <c>[</c> / <c>]</c> bracket convention shared by the leading editors).
/// </summary>
public enum TimelineCursor
{
    /// <summary>The default arrow.</summary>
    Default,

    /// <summary>Trim the clip's leading edge (Select tool over the left grip).</summary>
    TrimStart,

    /// <summary>Trim the clip's trailing edge (Select tool over the right grip).</summary>
    TrimEnd,

    /// <summary>The Blade (razor) tool.</summary>
    Blade,

    /// <summary>Ripple-trim the leading edge.</summary>
    RippleStart,

    /// <summary>Ripple-trim the trailing edge.</summary>
    RippleEnd,

    /// <summary>Roll the cut between two adjacent clips.</summary>
    Roll,

    /// <summary>Slip a clip's source window.</summary>
    Slip,

    /// <summary>Slide a clip between its neighbours.</summary>
    Slide,

    /// <summary>Pan the view (Hand tool).</summary>
    Hand,

    /// <summary>Zoom the view (Zoom tool).</summary>
    Zoom,
}

/// <summary>Which fade handle a pointer is over (PLAN.md step 39) — the small triangles in a clip's top
/// corners that set the fade-in/out length.</summary>
public enum FadeHandleKind
{
    /// <summary>Not over a fade handle.</summary>
    None,

    /// <summary>The left (fade-in) handle.</summary>
    FadeIn,

    /// <summary>The right (fade-out) handle.</summary>
    FadeOut,
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
    /// The cursor the timeline should show while idle-hovering, from the active tool and what the pointer is
    /// over (the <see cref="HitMode"/> result, or <see cref="ClipDragMode.None"/> off-clip). Mirrors the leading
    /// editors: the Select tool shows a side-specific trim cursor only inside an edge grip; Ripple/Roll act on
    /// edges so they show their cursor on an edge and the plain arrow elsewhere; Slip/Slide/Hand/Zoom/Blade act
    /// anywhere, so their tool cursor shows regardless of the hover target.
    /// </summary>
    public static TimelineCursor HoverCursor(EditTool tool, ClipDragMode mode) => tool switch
    {
        EditTool.Select => mode switch
        {
            ClipDragMode.TrimStart => TimelineCursor.TrimStart,
            ClipDragMode.TrimEnd => TimelineCursor.TrimEnd,
            _ => TimelineCursor.Default,
        },
        EditTool.Ripple => mode switch
        {
            ClipDragMode.TrimStart => TimelineCursor.RippleStart,
            ClipDragMode.TrimEnd => TimelineCursor.RippleEnd,
            _ => TimelineCursor.Default,
        },
        EditTool.Roll => mode is ClipDragMode.TrimStart or ClipDragMode.TrimEnd
            ? TimelineCursor.Roll
            : TimelineCursor.Default,
        EditTool.Blade => TimelineCursor.Blade,
        EditTool.Slip => TimelineCursor.Slip,
        EditTool.Slide => TimelineCursor.Slide,
        EditTool.Hand => TimelineCursor.Hand,
        EditTool.Zoom => TimelineCursor.Zoom,
        _ => TimelineCursor.Default,
    };

    /// <summary>
    /// Where a blade cut at pointer X would land on a clip spanning <c>[clipStartTicks, clipEndTicks]</c>:
    /// the pointer's timeline tick, snapped to the playhead when <paramref name="snapping"/> is on, or null
    /// when the (snapped) cut would not fall strictly inside the clip. Shared by the Blade tool's hover
    /// cut-line preview and the actual split, so the line always shows exactly where the cut will land.
    /// </summary>
    public static long? BladeCutTicks(
        double pointerX, long clipStartTicks, long clipEndTicks, bool snapping, long playheadTicks,
        double snapTolerancePx, double pxPerSecond, double scrollX, double headerWidth)
    {
        long at = TicksAtX(pointerX, pxPerSecond, scrollX, headerWidth);
        if (snapping)
            at = Snap(at, [playheadTicks], snapTolerancePx, pxPerSecond);
        return at > clipStartTicks && at < clipEndTicks ? at : null;
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
    /// The inclusive lane-index range a marquee band spanning <paramref name="y0"/>–<paramref name="y1"/>
    /// (either order) touches (PLAN.md step 54): a lane is included only when the band overlaps its
    /// <paramref name="laneHeight"/>-tall track body, not the gap below it. Returns an empty range
    /// (<c>First &gt; Last</c>) when the band lies entirely above the ruler, in gaps, or past the last lane.
    /// </summary>
    public static (int First, int Last) MarqueeLaneRange(
        double y0, double y1, double rulerHeight, double laneStride, double laneHeight, int laneCount)
    {
        if (laneCount <= 0 || laneStride <= 0 || laneHeight <= 0)
            return (0, -1);
        double top = Math.Max(Math.Min(y0, y1), rulerHeight);
        double bottom = Math.Max(y0, y1);
        if (bottom <= rulerHeight)
            return (0, -1);

        int first = (int)((top - rulerHeight) / laneStride);
        // The band's top may sit in the gap below lane `first`'s body — the next lane is the first touched.
        if (top - (rulerHeight + first * laneStride) >= laneHeight)
            first++;
        int last = (int)((bottom - rulerHeight) / laneStride);
        // The band's bottom must reach past the lane's top edge to touch it (an exact-top graze counts).
        first = Math.Max(0, first);
        last = Math.Min(laneCount - 1, last);
        return first <= last ? (first, last) : (0, -1);
    }

    /// <summary>
    /// Whether a marquee band's tick span <c>[bandStart, bandEnd)</c> overlaps a clip's
    /// <c>[clipStart, clipEnd)</c> (PLAN.md step 54). Strict overlap: a band that only grazes a clip's edge
    /// (or a zero-width band) selects nothing.
    /// </summary>
    public static bool MarqueeHitsSpan(long bandStart, long bandEnd, long clipStart, long clipEnd)
    {
        long b0 = Math.Min(bandStart, bandEnd);
        long b1 = Math.Max(bandStart, bandEnd);
        return b0 < b1 && b0 < clipEnd && b1 > clipStart;
    }

    /// <summary>
    /// Classifies a pointer against a clip's fade handles (PLAN.md step 39): the handles live in a band of
    /// <paramref name="handleBand"/> px along the clip's top edge, at <paramref name="fadeInX"/> /
    /// <paramref name="fadeOutX"/> (the inner ends of the fade ramps — the top corners when the fades are
    /// zero). Within the band, the nearer handle inside <paramref name="grip"/> px wins — checked <em>before</em>
    /// the edge-trim zones so a zero-length fade's corner handle stays reachable above the trim grip.
    /// </summary>
    public static FadeHandleKind HitFadeHandle(
        double pointerX, double pointerY, double clipTop, double handleBand,
        double fadeInX, double fadeOutX, double grip)
    {
        if (pointerY < clipTop || pointerY > clipTop + handleBand)
            return FadeHandleKind.None;
        double dIn = Math.Abs(pointerX - fadeInX);
        double dOut = Math.Abs(pointerX - fadeOutX);
        if (dIn > grip && dOut > grip)
            return FadeHandleKind.None;
        return dIn <= dOut ? FadeHandleKind.FadeIn : FadeHandleKind.FadeOut;
    }

    /// <summary>
    /// Maps a pointer Y inside a clip's body to an opacity level in [0, 1] — the rubber-band's vertical axis
    /// (top = 1, bottom = 0, inset by <paramref name="pad"/> px). The inverse of <see cref="FadeYAtLevel"/>.
    /// </summary>
    public static double FadeLevelAtY(double y, double clipTop, double clipHeight, double pad)
    {
        double h = clipHeight - 2 * pad;
        if (h <= 0)
            return 1;
        return Math.Clamp(1 - (y - clipTop - pad) / h, 0, 1);
    }

    /// <summary>The on-screen Y of an opacity level on a clip's rubber-band — see <see cref="FadeLevelAtY"/>.</summary>
    public static double FadeYAtLevel(double level, double clipTop, double clipHeight, double pad)
        => clipTop + pad + (1 - Math.Clamp(level, 0, 1)) * (clipHeight - 2 * pad);

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
