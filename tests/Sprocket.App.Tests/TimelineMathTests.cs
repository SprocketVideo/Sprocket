using Sprocket.App;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the timeline's pure geometry (PLAN.md step 12): tick↔pixel mapping, snapping, edge
/// hit-testing, and ruler-interval selection. The control's rendering + pointer interaction rest on these
/// helpers and on manual verification (the App is a UI-bound WinExe).
/// </summary>
public class TimelineMathTests
{
    private const double Header = 132;

    [Fact]
    public void X_And_Ticks_Round_Trip()
    {
        long ticks = Timecode.FromSeconds(7.5).Ticks;
        double x = TimelineMath.XAtTicks(ticks, pxPerSecond: 80, scrollX: 40, headerWidth: Header);
        long back = TimelineMath.TicksAtX(x, pxPerSecond: 80, scrollX: 40, headerWidth: Header);
        Assert.Equal(ticks, back);
    }

    [Fact]
    public void XAtTicks_Accounts_For_Header_And_Scroll()
    {
        // Tick 0 sits at the header edge, minus any horizontal scroll.
        Assert.Equal(Header, TimelineMath.XAtTicks(0, 80, 0, Header), 6);
        Assert.Equal(Header - 25, TimelineMath.XAtTicks(0, 80, 25, Header), 6);
    }

    [Fact]
    public void WidthOfTicks_Scales_With_Zoom()
    {
        long oneSecond = Timecode.TicksPerSecond;
        Assert.Equal(80, TimelineMath.WidthOfTicks(oneSecond, 80), 6);
        Assert.Equal(160, TimelineMath.WidthOfTicks(oneSecond, 160), 6);
    }

    [Fact]
    public void ClampNonNegative_Floors_At_Zero()
    {
        Assert.Equal(0, TimelineMath.ClampNonNegative(-500));
        Assert.Equal(42, TimelineMath.ClampNonNegative(42));
    }

    [Fact]
    public void Snap_Pulls_To_A_Candidate_Within_Tolerance()
    {
        long target = Timecode.FromSeconds(5).Ticks;
        long near = target + Timecode.FromSeconds(0.05).Ticks; // 0.05s ≈ 4px at 80px/s, within 8px tolerance
        long snapped = TimelineMath.Snap(near, [target], tolerancePx: 8, pxPerSecond: 80);
        Assert.Equal(target, snapped);
    }

    [Fact]
    public void Snap_Leaves_Value_When_No_Candidate_Is_Close()
    {
        long target = Timecode.FromSeconds(5).Ticks;
        long far = target + Timecode.FromSeconds(0.5).Ticks; // 0.5s = 40px at 80px/s, outside tolerance
        long snapped = TimelineMath.Snap(far, [target], tolerancePx: 8, pxPerSecond: 80);
        Assert.Equal(far, snapped);
    }

    [Fact]
    public void Snap_Picks_The_Nearest_Of_Several_Candidates()
    {
        long a = Timecode.FromSeconds(5).Ticks;
        long b = Timecode.FromSeconds(5.08).Ticks;
        long probe = Timecode.FromSeconds(5.07).Ticks; // closer to b
        Assert.Equal(b, TimelineMath.Snap(probe, [a, b], tolerancePx: 12, pxPerSecond: 80));
    }

    [Theory]
    [InlineData(100, ClipDragMode.TrimStart)] // on left edge (x0=100)
    [InlineData(300, ClipDragMode.TrimEnd)]   // on right edge (x1=300)
    [InlineData(200, ClipDragMode.Move)]      // body
    [InlineData(50, ClipDragMode.None)]       // left of the clip
    [InlineData(360, ClipDragMode.None)]      // right of the clip
    public void HitMode_Classifies_Pointer_Against_A_Clip(double pointerX, ClipDragMode expected)
    {
        Assert.Equal(expected, TimelineMath.HitMode(pointerX, clipX0: 100, clipX1: 300, edgeGrip: 7));
    }

    [Fact]
    public void HitMode_Prefers_TrimStart_On_A_Narrow_Clip()
    {
        // Edges within a grip of each other: the start wins so a sliver clip is still trimmable.
        Assert.Equal(ClipDragMode.TrimStart, TimelineMath.HitMode(101, clipX0: 100, clipX1: 104, edgeGrip: 7));
    }

    [Fact]
    public void ClampSlip_Allows_A_Slip_Within_The_Media()
    {
        long oneSec = Timecode.TicksPerSecond;
        // Source window [2s, 6s) inside a 10s media: a +1s slip is fully within bounds.
        long slip = TimelineMath.ClampSlip(origIn: 2 * oneSec, origOut: 6 * oneSec, mediaDuration: 10 * oneSec, delta: oneSec);
        Assert.Equal(oneSec, slip);
    }

    [Fact]
    public void ClampSlip_Stops_At_The_Media_Edges()
    {
        long oneSec = Timecode.TicksPerSecond;
        // Window [2s, 6s) in a 10s media: can pull SourceIn back at most 2s, push SourceOut at most 4s.
        Assert.Equal(-2 * oneSec, TimelineMath.ClampSlip(2 * oneSec, 6 * oneSec, 10 * oneSec, -100 * oneSec));
        Assert.Equal(4 * oneSec, TimelineMath.ClampSlip(2 * oneSec, 6 * oneSec, 10 * oneSec, 100 * oneSec));
    }

    [Fact]
    public void ClampSlip_Is_A_No_Op_When_The_Clip_Spans_The_Whole_Source()
    {
        long oneSec = Timecode.TicksPerSecond;
        // Window [0, 10s) in a 10s media: no headroom either direction.
        Assert.Equal(0, TimelineMath.ClampSlip(0, 10 * oneSec, 10 * oneSec, 5 * oneSec));
        Assert.Equal(0, TimelineMath.ClampSlip(0, 10 * oneSec, 10 * oneSec, -5 * oneSec));
    }

    [Fact]
    public void RulerInterval_Grows_As_You_Zoom_Out()
    {
        long tight = TimelineMath.RulerIntervalTicks(pxPerSecond: 300, targetPx: 90); // zoomed in → small interval
        long loose = TimelineMath.RulerIntervalTicks(pxPerSecond: 10, targetPx: 90);  // zoomed out → large interval
        Assert.True(loose > tight);
        // Each chosen interval is a whole number of seconds-or-half-seconds in ticks.
        Assert.True(tight > 0 && loose > 0);
    }

    [Theory]
    [InlineData(10, -1)]   // above the ruler
    [InlineData(26, 0)]    // first pixel below the ruler → lane 0
    [InlineData(60, 0)]    // still inside lane 0 (stride 50)
    [InlineData(76, 1)]    // start of lane 1
    [InlineData(180, 3)]   // lane 3
    public void LaneIndexAtY_Maps_Y_To_Lane(double y, int expected)
    {
        // ruler 26px, stride 50px (the control's RulerHeight, TrackHeight + TrackGap).
        Assert.Equal(expected, TimelineMath.LaneIndexAtY(y, rulerHeight: 26, laneStride: 50));
    }

    [Fact]
    public void LaneIndexAtY_Returns_Negative_For_Degenerate_Stride()
    {
        Assert.Equal(-1, TimelineMath.LaneIndexAtY(100, rulerHeight: 26, laneStride: 0));
    }

    // ── Ripple / roll / slide clamping (PLAN.md step 22) ───────────────────────────────────────────
    // Quantities are unit-agnostic timeline ticks; the control converts source/media headroom to timeline
    // ticks (÷ speed) before calling these.

    [Fact]
    public void ClampRollDelta_Passes_A_Delta_That_Is_Within_Bounds()
    {
        // Plenty of room both ways → unchanged.
        Assert.Equal(7, TimelineMath.ClampRollDelta(
            delta: 7, leftDuration: 100, leftSourceHeadroom: 100,
            rightDuration: 100, rightSourceHeadroom: 100, minDuration: 10));
    }

    [Fact]
    public void ClampRollDelta_Stops_The_Cut_At_The_Left_Clip_Media_End()
    {
        // Rightward roll grows the left clip; only 5 ticks of media remain → cut can move at most +5.
        Assert.Equal(5, TimelineMath.ClampRollDelta(
            delta: 50, leftDuration: 100, leftSourceHeadroom: 5,
            rightDuration: 100, rightSourceHeadroom: 100, minDuration: 10));
    }

    [Fact]
    public void ClampRollDelta_Stops_The_Right_Clip_At_Min_Duration()
    {
        // Rightward roll shrinks the right clip; it has duration 30 and min 10 → at most +20.
        Assert.Equal(20, TimelineMath.ClampRollDelta(
            delta: 99, leftDuration: 100, leftSourceHeadroom: 100,
            rightDuration: 30, rightSourceHeadroom: 100, minDuration: 10));
    }

    [Fact]
    public void ClampRollDelta_Stops_A_Left_Roll_At_The_Right_Clip_Source_Start()
    {
        // Leftward roll pulls the right clip's in-point back; only 8 ticks of head remain → at most -8.
        Assert.Equal(-8, TimelineMath.ClampRollDelta(
            delta: -99, leftDuration: 100, leftSourceHeadroom: 100,
            rightDuration: 100, rightSourceHeadroom: 8, minDuration: 10));
    }

    [Fact]
    public void ClampSlideDelta_Mirrors_Roll_With_Neighbour_Headroom()
    {
        // Slide right limited by the previous clip's media headroom (20).
        Assert.Equal(20, TimelineMath.ClampSlideDelta(
            delta: 50, prevDuration: 100, prevSourceHeadroom: 20,
            nextDuration: 100, nextSourceHeadroom: 100, minDuration: 10));
        // Slide left limited by the next clip's source-start headroom (8).
        Assert.Equal(-8, TimelineMath.ClampSlideDelta(
            delta: -50, prevDuration: 100, prevSourceHeadroom: 100,
            nextDuration: 100, nextSourceHeadroom: 8, minDuration: 10));
    }

    [Fact]
    public void RippleTrimBounds_For_The_Trailing_Edge()
    {
        // OUT trim: extend up to the remaining media (outHeadroom=40), retract until the clip hits min duration.
        (long lower, long upper) = TimelineMath.RippleTrimBounds(
            trimEnd: true, durationTicks: 100, inHeadroom: 30, outHeadroom: 40, minDuration: 10);
        Assert.Equal(10 - 100, lower); // duration may shrink to the 10-tick minimum
        Assert.Equal(40, upper);
    }

    [Fact]
    public void RippleTrimBounds_For_The_Leading_Edge()
    {
        // IN trim: extend the head back to the source start (inHeadroom=30, a negative delta), or trim it until
        // the clip hits min duration.
        (long lower, long upper) = TimelineMath.RippleTrimBounds(
            trimEnd: false, durationTicks: 100, inHeadroom: 30, outHeadroom: 40, minDuration: 10);
        Assert.Equal(-30, lower);
        Assert.Equal(100 - 10, upper);
    }

    // ── Idle-hover cursor mapping ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EditTool.Select, ClipDragMode.TrimStart, TimelineCursor.TrimStart)]
    [InlineData(EditTool.Select, ClipDragMode.TrimEnd, TimelineCursor.TrimEnd)]
    [InlineData(EditTool.Select, ClipDragMode.Move, TimelineCursor.Default)]
    [InlineData(EditTool.Select, ClipDragMode.None, TimelineCursor.Default)]
    [InlineData(EditTool.Ripple, ClipDragMode.TrimStart, TimelineCursor.RippleStart)]
    [InlineData(EditTool.Ripple, ClipDragMode.TrimEnd, TimelineCursor.RippleEnd)]
    [InlineData(EditTool.Ripple, ClipDragMode.Move, TimelineCursor.Default)]
    [InlineData(EditTool.Roll, ClipDragMode.TrimStart, TimelineCursor.Roll)]
    [InlineData(EditTool.Roll, ClipDragMode.TrimEnd, TimelineCursor.Roll)]
    [InlineData(EditTool.Roll, ClipDragMode.Move, TimelineCursor.Default)]
    public void HoverCursor_Is_Side_Specific_For_Edge_Tools(EditTool tool, ClipDragMode mode, TimelineCursor expected)
    {
        Assert.Equal(expected, TimelineMath.HoverCursor(tool, mode));
    }

    [Theory]
    [InlineData(EditTool.Blade, TimelineCursor.Blade)]
    [InlineData(EditTool.Slip, TimelineCursor.Slip)]
    [InlineData(EditTool.Slide, TimelineCursor.Slide)]
    [InlineData(EditTool.Hand, TimelineCursor.Hand)]
    [InlineData(EditTool.Zoom, TimelineCursor.Zoom)]
    public void HoverCursor_Shows_The_Tool_Cursor_Everywhere_For_Whole_Clip_Tools(EditTool tool, TimelineCursor expected)
    {
        // These tools act anywhere (or on the whole clip body), so the hover target doesn't change the cursor.
        Assert.Equal(expected, TimelineMath.HoverCursor(tool, ClipDragMode.None));
        Assert.Equal(expected, TimelineMath.HoverCursor(tool, ClipDragMode.Move));
        Assert.Equal(expected, TimelineMath.HoverCursor(tool, ClipDragMode.TrimStart));
    }

    // ── Blade cut position (hover cut-line + actual split share this) ───────────────────────────────

    [Fact]
    public void BladeCutTicks_Returns_The_Pointer_Tick_Inside_The_Clip()
    {
        long start = Timecode.FromSeconds(2).Ticks, end = Timecode.FromSeconds(10).Ticks;
        long probe = Timecode.FromSeconds(6).Ticks;
        double x = TimelineMath.XAtTicks(probe, 80, 0, Header);
        long? cut = TimelineMath.BladeCutTicks(x, start, end, snapping: false, playheadTicks: 0,
            snapTolerancePx: 8, pxPerSecond: 80, scrollX: 0, headerWidth: Header);
        Assert.Equal(probe, cut);
    }

    [Fact]
    public void BladeCutTicks_Snaps_To_The_Playhead_When_Snapping_Is_On()
    {
        long start = Timecode.FromSeconds(2).Ticks, end = Timecode.FromSeconds(10).Ticks;
        long playhead = Timecode.FromSeconds(6).Ticks;
        long near = playhead + Timecode.FromSeconds(0.05).Ticks; // ≈4px at 80px/s, inside the 8px tolerance
        double x = TimelineMath.XAtTicks(near, 80, 0, Header);
        long? cut = TimelineMath.BladeCutTicks(x, start, end, snapping: true, playheadTicks: playhead,
            snapTolerancePx: 8, pxPerSecond: 80, scrollX: 0, headerWidth: Header);
        Assert.Equal(playhead, cut);
    }

    [Theory]
    [InlineData(2.0)]  // exactly on the start edge — not strictly inside
    [InlineData(10.0)] // exactly on the end edge
    [InlineData(1.0)]  // before the clip
    [InlineData(11.0)] // after the clip
    public void BladeCutTicks_Is_Null_When_The_Cut_Would_Not_Fall_Strictly_Inside(double seconds)
    {
        long start = Timecode.FromSeconds(2).Ticks, end = Timecode.FromSeconds(10).Ticks;
        double x = TimelineMath.XAtTicks(Timecode.FromSeconds(seconds).Ticks, 80, 0, Header);
        Assert.Null(TimelineMath.BladeCutTicks(x, start, end, snapping: false, playheadTicks: 0,
            snapTolerancePx: 8, pxPerSecond: 80, scrollX: 0, headerWidth: Header));
    }

    [Fact]
    public void BladeCutTicks_Is_Null_When_The_Snap_Pulls_The_Cut_Onto_An_Edge()
    {
        // The playhead sits exactly on the clip's start; a pointer just inside snaps onto the edge → no cut.
        long start = Timecode.FromSeconds(2).Ticks, end = Timecode.FromSeconds(10).Ticks;
        long near = start + Timecode.FromSeconds(0.05).Ticks;
        double x = TimelineMath.XAtTicks(near, 80, 0, Header);
        Assert.Null(TimelineMath.BladeCutTicks(x, start, end, snapping: true, playheadTicks: start,
            snapTolerancePx: 8, pxPerSecond: 80, scrollX: 0, headerWidth: Header));
    }

    // ── Marquee hit math (PLAN.md step 54) ──────────────────────────────────────────────────────────
    // The control's lane geometry: RulerHeight 26, TrackHeight 46, TrackGap 4 → stride 50.

    [Fact]
    public void MarqueeLaneRange_Spans_The_Touched_Lanes_In_Either_Drag_Direction()
    {
        // Lane 0 body: 26–72; lane 1 body: 76–122; lane 2 body: 126–172.
        Assert.Equal((0, 1), TimelineMath.MarqueeLaneRange(30, 80, 26, 50, 46, 3));
        Assert.Equal((0, 1), TimelineMath.MarqueeLaneRange(80, 30, 26, 50, 46, 3)); // upward drag
        Assert.Equal((1, 1), TimelineMath.MarqueeLaneRange(80, 100, 26, 50, 46, 3));
        Assert.Equal((0, 2), TimelineMath.MarqueeLaneRange(26, 172, 26, 50, 46, 3));
    }

    [Fact]
    public void MarqueeLaneRange_Skips_A_Lane_Whose_Body_The_Band_Never_Touches()
    {
        // The band starts in the gap below lane 0 (72–76) — lane 0's body is untouched.
        Assert.Equal((1, 1), TimelineMath.MarqueeLaneRange(73, 100, 26, 50, 46, 3));
    }

    [Fact]
    public void MarqueeLaneRange_Clamps_To_The_Ruler_And_The_Last_Lane()
    {
        // A band that starts over the ruler clamps to lane 0; one past the last lane clamps to it.
        Assert.Equal((0, 0), TimelineMath.MarqueeLaneRange(5, 40, 26, 50, 46, 3));
        Assert.Equal((2, 2), TimelineMath.MarqueeLaneRange(160, 900, 26, 50, 46, 3));
        // Entirely above the ruler / no lanes → empty range.
        Assert.True(TimelineMath.MarqueeLaneRange(2, 20, 26, 50, 46, 3) is (int f1, int l1) && f1 > l1);
        Assert.True(TimelineMath.MarqueeLaneRange(30, 80, 26, 50, 46, 0) is (int f2, int l2) && f2 > l2);
    }

    [Fact]
    public void MarqueeHitsSpan_Requires_Strict_Overlap()
    {
        long s2 = Timecode.FromSeconds(2).Ticks, s5 = Timecode.FromSeconds(5).Ticks;
        Assert.True(TimelineMath.MarqueeHitsSpan(s2, s5, Timecode.FromSeconds(4).Ticks, Timecode.FromSeconds(9).Ticks));
        Assert.True(TimelineMath.MarqueeHitsSpan(s5, s2, Timecode.FromSeconds(4).Ticks, Timecode.FromSeconds(9).Ticks)); // either order
        Assert.False(TimelineMath.MarqueeHitsSpan(s2, s5, s5, Timecode.FromSeconds(9).Ticks)); // edge graze
        Assert.False(TimelineMath.MarqueeHitsSpan(s2, s2, Timecode.Zero.Ticks, s5));           // zero-width band
        Assert.False(TimelineMath.MarqueeHitsSpan(s2, s5, Timecode.FromSeconds(6).Ticks, Timecode.FromSeconds(9).Ticks));
    }
}
