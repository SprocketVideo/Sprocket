using System.Linq;
using Sprocket.App.Inspector;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the keyframe-lane editing helpers (PLAN.md step 16b): the new
/// <see cref="AnimatableEditing"/> transforms (move / remove / interpolation) and the pure
/// <see cref="KeyframeLaneMath"/> geometry. The lane control's drawing + pointer interaction rest on these +
/// manual verification (the App is a UI-bound WinExe).
/// </summary>
public class KeyframeEditingTests
{
    private static AnimatableValue ThreeKeys() => AnimatableValue.Animated(
    [
        new Keyframe(Timecode.FromSeconds(0), 0.0, Interpolation.Linear),
        new Keyframe(Timecode.FromSeconds(1), 1.0, Interpolation.Linear),
        new Keyframe(Timecode.FromSeconds(2), 0.0, Interpolation.Linear),
    ]);

    [Fact]
    public void MoveKeyframe_Reschedules_Preserving_Value_And_Order()
    {
        AnimatableValue moved = AnimatableEditing.MoveKeyframe(
            ThreeKeys(), Timecode.FromSeconds(1), Timecode.FromSeconds(1.5));

        Assert.Equal(3, moved.Keyframes.Count);
        // Still sorted; the moved keyframe kept its value (1.0) at the new time.
        Assert.Equal([0, 1.5, 2], moved.Keyframes.Select(k => k.Time.ToSeconds()).ToArray());
        Keyframe at = moved.Keyframes.Single(k => k.Time.Ticks == Timecode.FromSeconds(1.5).Ticks);
        Assert.Equal(1.0, at.Value);
    }

    [Fact]
    public void MoveKeyframe_Onto_Another_Overwrites_It()
    {
        AnimatableValue moved = AnimatableEditing.MoveKeyframe(
            ThreeKeys(), Timecode.FromSeconds(1), Timecode.FromSeconds(2));
        // The keyframe formerly at 2 (value 0) is replaced by the one moved there (value 1); 2 keyframes remain.
        Assert.Equal(2, moved.Keyframes.Count);
        Assert.Equal(1.0, moved.Evaluate(Timecode.FromSeconds(2))); // moved keyframe's value won
    }

    [Fact]
    public void MoveKeyframe_Is_A_NoOp_For_A_Missing_Source()
    {
        AnimatableValue v = ThreeKeys();
        Assert.Same(v, AnimatableEditing.MoveKeyframe(v, Timecode.FromSeconds(9), Timecode.FromSeconds(3)));
    }

    [Fact]
    public void RemoveKeyframe_Drops_One_And_Keeps_The_Rest()
    {
        AnimatableValue v = AnimatableEditing.RemoveKeyframe(ThreeKeys(), Timecode.FromSeconds(1));
        Assert.Equal([0, 2], v.Keyframes.Select(k => k.Time.ToSeconds()).ToArray());
    }

    [Fact]
    public void RemoveKeyframe_Of_The_Last_Collapses_To_Constant()
    {
        AnimatableValue one = AnimatableValue.Animated([new Keyframe(Timecode.FromSeconds(1), 0.7)]);
        AnimatableValue v = AnimatableEditing.RemoveKeyframe(one, Timecode.FromSeconds(1));
        Assert.False(v.IsAnimated);
        Assert.Equal(0.7, v.Evaluate(Timecode.Zero));
    }

    [Fact]
    public void SetInterpolation_Toggles_Hold_And_Linear()
    {
        AnimatableValue held = AnimatableEditing.SetInterpolation(
            ThreeKeys(), Timecode.FromSeconds(0), Interpolation.Hold);
        // With the first segment held, the value stays at 0 across [0,1) instead of ramping toward 1.
        Assert.Equal(0.0, held.Evaluate(Timecode.FromSeconds(0.5)));

        AnimatableValue back = AnimatableEditing.SetInterpolation(held, Timecode.FromSeconds(0), Interpolation.Linear);
        Assert.Equal(0.5, back.Evaluate(Timecode.FromSeconds(0.5)), 3); // linear again → halfway
    }

    // ── KeyframeLaneMath ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void XAt_And_TicksAt_Round_Trip_Within_The_Range()
    {
        long start = Timecode.FromSeconds(2).Ticks;
        long end = Timecode.FromSeconds(6).Ticks;
        long mid = Timecode.FromSeconds(4).Ticks;

        double x = KeyframeLaneMath.XAt(mid, start, end, laneWidth: 200);
        Assert.Equal(100, x, 3); // halfway across a 200px lane
        Assert.Equal(mid, KeyframeLaneMath.TicksAt(x, start, end, 200));
    }

    [Fact]
    public void XAt_Clamps_Outside_The_Range()
    {
        long start = Timecode.FromSeconds(2).Ticks;
        long end = Timecode.FromSeconds(6).Ticks;
        Assert.Equal(0, KeyframeLaneMath.XAt(Timecode.FromSeconds(0).Ticks, start, end, 200), 3);
        Assert.Equal(200, KeyframeLaneMath.XAt(Timecode.FromSeconds(99).Ticks, start, end, 200), 3);
    }

    [Fact]
    public void NearestKeyframeIndex_Picks_Within_Tolerance_Else_Misses()
    {
        long start = Timecode.FromSeconds(0).Ticks;
        long end = Timecode.FromSeconds(2).Ticks;
        var keys = ThreeKeys().Keyframes; // at 0s, 1s, 2s → x = 0, 100, 200 on a 200px lane

        Assert.Equal(1, KeyframeLaneMath.NearestKeyframeIndex(103, keys, start, end, 200, tolerancePx: 6));
        Assert.Equal(-1, KeyframeLaneMath.NearestKeyframeIndex(60, keys, start, end, 200, tolerancePx: 6));
    }
}
