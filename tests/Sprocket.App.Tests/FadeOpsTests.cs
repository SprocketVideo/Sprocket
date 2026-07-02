using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests for the fade-handle / opacity rubber-band envelope math (PLAN.md step 39):
/// <see cref="FadeOps"/> (read fade lengths, rebuild the envelope for a handle drag, grab/adjust band
/// keyframes) and the <see cref="TimelineMath"/> fade geometry (handle hit-testing, level↔Y mapping).
/// All pure and headless; the drawing + pointer plumbing rests on these + manual verification.
/// </summary>
public class FadeOpsTests
{
    private static readonly long Second = Timecode.TicksPerSecond;

    private static AnimatableValue Canonical(long start, long end, long fadeIn, long fadeOut, double level = 1) =>
        AnimatableValue.Animated(
        [
            new Keyframe(new Timecode(start), 0),
            new Keyframe(new Timecode(start + fadeIn), level),
            new Keyframe(new Timecode(end - fadeOut), level),
            new Keyframe(new Timecode(end), 0),
        ]);

    // ── ReadFades ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadFades_Recognises_Canonical_Envelope()
    {
        long start = 2 * Second, end = 7 * Second;
        AnimatableValue opacity = Canonical(start, end, Second, 2 * Second);

        (long fadeIn, long fadeOut) = FadeOps.ReadFades(opacity, start, end);

        Assert.Equal(Second, fadeIn);
        Assert.Equal(2 * Second, fadeOut);
    }

    [Fact]
    public void ReadFades_Constant_Or_Missing_Is_Zero()
    {
        Assert.Equal((0L, 0L), FadeOps.ReadFades(null, 0, Second));
        Assert.Equal((0L, 0L), FadeOps.ReadFades(AnimatableValue.Constant(0.5), 0, Second));
    }

    [Fact]
    public void ReadFades_Interior_Keyframes_Not_At_Edges_Read_As_No_Fade()
    {
        long start = 0, end = 10 * Second;
        // A hand-authored envelope dipping mid-clip: no edge ramps, so no fade handles engage.
        AnimatableValue opacity = AnimatableValue.Animated(
        [
            new Keyframe(new Timecode(3 * Second), 1),
            new Keyframe(new Timecode(5 * Second), 0.4),
            new Keyframe(new Timecode(7 * Second), 1),
        ]);

        Assert.Equal((0L, 0L), FadeOps.ReadFades(opacity, start, end));
    }

    [Fact]
    public void ReadFades_Meeting_Fades_Share_The_Peak()
    {
        long start = 0, end = 4 * Second;
        AnimatableValue opacity = AnimatableValue.Animated(
        [
            new Keyframe(new Timecode(start), 0),
            new Keyframe(new Timecode(2 * Second), 1),
            new Keyframe(new Timecode(end), 0),
        ]);

        (long fadeIn, long fadeOut) = FadeOps.ReadFades(opacity, start, end);
        Assert.Equal(2 * Second, fadeIn);
        Assert.Equal(2 * Second, fadeOut);
    }

    // ── BuildOpacity ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildOpacity_From_Nothing_Authors_Edge_Ramps_That_Read_Back()
    {
        long start = 2 * Second, end = 8 * Second;

        AnimatableValue built = FadeOps.BuildOpacity(null, start, end, Second, 2 * Second);

        Assert.Equal((Second, 2 * Second), FadeOps.ReadFades(built, start, end));
        Assert.Equal(0, built.Evaluate(new Timecode(start)), 6);
        Assert.Equal(1, built.Evaluate(new Timecode(start + Second)), 6);
        Assert.Equal(1, built.Evaluate(new Timecode(end - 2 * Second)), 6);
        Assert.Equal(0, built.Evaluate(new Timecode(end)), 6);
    }

    [Fact]
    public void BuildOpacity_Zero_Fades_With_No_Interior_Collapses_To_Constant()
    {
        long start = 0, end = 5 * Second;
        AnimatableValue existing = Canonical(start, end, Second, Second);

        AnimatableValue built = FadeOps.BuildOpacity(existing, start, end, 0, 0);

        Assert.False(built.IsAnimated);
        Assert.Equal(1, built.Evaluate(Timecode.Zero), 6);
    }

    [Fact]
    public void BuildOpacity_Preserves_Interior_RubberBand_Points()
    {
        long start = 0, end = 10 * Second;
        AnimatableValue existing = AnimatableValue.Animated(
        [
            new Keyframe(new Timecode(start), 0),
            new Keyframe(new Timecode(Second), 1),
            new Keyframe(new Timecode(5 * Second), 0.4), // a rubber-band dip
            new Keyframe(new Timecode(9 * Second), 1),
            new Keyframe(new Timecode(end), 0),
        ]);

        AnimatableValue built = FadeOps.BuildOpacity(existing, start, end, 2 * Second, Second);

        Assert.Equal(0.4, built.Evaluate(new Timecode(5 * Second)), 6);
        Assert.Equal((2 * Second, Second), FadeOps.ReadFades(built, start, end));
    }

    [Fact]
    public void BuildOpacity_Shrinking_A_Fade_Keeps_The_Plateau_Level()
    {
        long start = 0, end = 10 * Second;
        // A lowered plateau (0.6) with 2 s ramps.
        AnimatableValue existing = Canonical(start, end, 2 * Second, 2 * Second, level: 0.6);

        // Shrink the fade-in to 1 s: the ramp must still top out at 0.6, not the mid-ramp 0.3.
        AnimatableValue built = FadeOps.BuildOpacity(existing, start, end, Second, 2 * Second);

        Assert.Equal(0.6, built.Evaluate(new Timecode(Second)), 6);
        Assert.Equal((Second, 2 * Second), FadeOps.ReadFades(built, start, end));
    }

    [Fact]
    public void BuildOpacity_Clamps_Overlapping_Fades()
    {
        long start = 0, end = 4 * Second;

        // Requesting 3 s + 3 s in a 4 s clip: the fade-out is clamped to the remaining 1 s.
        AnimatableValue built = FadeOps.BuildOpacity(null, start, end, 3 * Second, 3 * Second);

        Assert.Equal((3 * Second, Second), FadeOps.ReadFades(built, start, end));
    }

    [Fact]
    public void BuildOpacity_Meeting_Fades_Share_One_Peak_Keyframe()
    {
        long start = 0, end = 4 * Second;

        AnimatableValue built = FadeOps.BuildOpacity(null, start, end, 2 * Second, 2 * Second);

        Assert.Equal(3, built.Keyframes.Count); // (start,0) (peak,1) (end,0) — no duplicate at the peak
        Assert.Equal(1, built.Evaluate(new Timecode(2 * Second)), 6);
    }

    // ── Rubber-band grab / adjust ───────────────────────────────────────────────────────────────────

    [Fact]
    public void GrabKeyframes_Near_A_Keyframe_Grabs_Just_It()
    {
        AnimatableValue opacity = Canonical(0, 10 * Second, Second, Second);

        var grabbed = FadeOps.GrabKeyframes(opacity, Second + 100, toleranceTicks: 500);

        Assert.Equal([Second], grabbed);
    }

    [Fact]
    public void GrabKeyframes_Between_Keyframes_Grabs_The_Bounding_Pair()
    {
        AnimatableValue opacity = Canonical(0, 10 * Second, Second, Second);

        var grabbed = FadeOps.GrabKeyframes(opacity, 5 * Second, toleranceTicks: 500);

        Assert.Equal([Second, 9 * Second], grabbed);
    }

    [Fact]
    public void GrabKeyframes_Constant_Or_Missing_Is_Empty()
    {
        Assert.Empty(FadeOps.GrabKeyframes(null, 0, 500));
        Assert.Empty(FadeOps.GrabKeyframes(AnimatableValue.Constant(1), 0, 500));
    }

    [Fact]
    public void WithValueDelta_Moves_Grabbed_Keyframes_Clamped()
    {
        AnimatableValue opacity = Canonical(0, 10 * Second, Second, Second);

        AnimatableValue moved = FadeOps.WithValueDelta(opacity, [Second, 9 * Second], -0.3);
        Assert.Equal(0.7, moved.Evaluate(new Timecode(5 * Second)), 6);
        Assert.Equal(0, moved.Evaluate(Timecode.Zero), 6); // the edge anchors weren't grabbed

        AnimatableValue clamped = FadeOps.WithValueDelta(opacity, [Second], -2.0);
        Assert.Equal(0, clamped.Keyframes[1].Value, 6);
    }

    [Fact]
    public void WithValueDelta_On_Missing_Envelope_Drags_A_Flat_Level()
    {
        AnimatableValue moved = FadeOps.WithValueDelta(null, [], -0.25);

        Assert.False(moved.IsAnimated);
        Assert.Equal(0.75, moved.Evaluate(Timecode.Zero), 6);
    }

    [Fact]
    public void WithAddedPoint_Inserts_At_The_Current_Level()
    {
        AnimatableValue opacity = Canonical(0, 10 * Second, Second, Second);

        AnimatableValue added = FadeOps.WithAddedPoint(opacity, 5 * Second);

        Assert.Equal(5, added.Keyframes.Count);
        Assert.Equal(1, added.Evaluate(new Timecode(5 * Second)), 6);

        // Adding on an existing keyframe is a no-op.
        Assert.Same(added, FadeOps.WithAddedPoint(added, 5 * Second));
    }

    // ── TimelineMath fade geometry ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HitFadeHandle_Requires_The_Top_Band_And_Picks_The_Nearer_Handle()
    {
        // Clip top at 40, handles at x=100 (in) and x=200 (out), 9 px band, 6 px grip.
        Assert.Equal(FadeHandleKind.FadeIn, TimelineMath.HitFadeHandle(103, 45, 40, 9, 100, 200, 6));
        Assert.Equal(FadeHandleKind.FadeOut, TimelineMath.HitFadeHandle(197, 45, 40, 9, 100, 200, 6));
        Assert.Equal(FadeHandleKind.None, TimelineMath.HitFadeHandle(103, 55, 40, 9, 100, 200, 6)); // below the band
        Assert.Equal(FadeHandleKind.None, TimelineMath.HitFadeHandle(150, 45, 40, 9, 100, 200, 6)); // between handles
    }

    [Fact]
    public void HitFadeHandle_ZeroLength_Fades_Overlapping_Handles_Pick_By_Proximity()
    {
        // Both handles at the same corner region of a narrow clip: nearer one wins, ties go to fade-in.
        Assert.Equal(FadeHandleKind.FadeIn, TimelineMath.HitFadeHandle(99, 42, 40, 9, 100, 120, 6));
        Assert.Equal(FadeHandleKind.FadeOut, TimelineMath.HitFadeHandle(115, 42, 40, 9, 100, 120, 6));
    }

    [Fact]
    public void FadeLevel_And_Y_RoundTrip()
    {
        const double top = 40, height = 40, pad = 2;
        foreach (double level in new[] { 0.0, 0.25, 0.5, 1.0 })
        {
            double y = TimelineMath.FadeYAtLevel(level, top, height, pad);
            Assert.Equal(level, TimelineMath.FadeLevelAtY(y, top, height, pad), 6);
        }
        // Top of the band = full opacity; bottom = zero; outside clamps.
        Assert.Equal(1, TimelineMath.FadeLevelAtY(top, top, height, pad), 6);
        Assert.Equal(0, TimelineMath.FadeLevelAtY(top + height, top, height, pad), 6);
        Assert.Equal(1, TimelineMath.FadeLevelAtY(top - 20, top, height, pad), 6);
    }
}
