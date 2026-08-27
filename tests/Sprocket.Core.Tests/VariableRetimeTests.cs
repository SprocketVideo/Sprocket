using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Variable / ramped speed and reverse retime (PLAN.md step 21 remainder): the <see cref="SpeedRamp"/>
/// integrated time map (analytic cases), the clip's derived duration under a ramp, the reversed map, the
/// direction-aware blade split, and the new commands' apply/revert/coalesce behaviour.
/// </summary>
public class VariableRetimeTests
{
    private const long Sec = Timecode.TicksPerSecond;

    private static Clip ClipFor(Rational speed, bool reverse = false)
    {
        // Source span [2s, 12s) placed at t = 4s.
        var clip = new Clip(MediaRefId.New(), Timecode.FromSeconds(2), Timecode.FromSeconds(12), Timecode.FromSeconds(4));
        clip.SpeedRatio = speed;
        clip.Reverse = reverse;
        return clip;
    }

    private static AnimatableValue Curve(params (double seconds, double speed, Interpolation mode)[] keys) =>
        AnimatableValue.Animated(keys.Select(k => new Keyframe(Timecode.FromSeconds(k.seconds), k.speed, k.mode)));

    // ── SpeedRamp: analytic integrals ───────────────────────────────────────────────────────────────

    [Fact]
    public void Ramp_Constant_Segments_Integrate_Exactly()
    {
        // Hold 2× for the first 3 s, then 0.5× (held). ∫₀⁵ = 3×2 + 2×0.5 = 7 s of source.
        AnimatableValue curve = Curve((0, 2.0, Interpolation.Hold), (3, 0.5, Interpolation.Hold));
        Assert.Equal(7.0 * Sec, SpeedRamp.Integrate(curve, 0, 5 * Sec), 1e-6);
        // Before the first keyframe the curve is the first value; after the last, the last value.
        Assert.Equal(2.0 * Sec, SpeedRamp.Integrate(curve, -Sec, 0), 1e-6);
        Assert.Equal(0.5 * Sec, SpeedRamp.Integrate(curve, 10 * Sec, 11 * Sec), 1e-6);
    }

    [Fact]
    public void Ramp_Linear_Segment_Integrates_As_A_Trapezoid()
    {
        // 1× → 3× linearly over 4 s: ∫ = (1 + 3)/2 × 4 = 8 s; the first half alone (1 → 2) = 3 s.
        AnimatableValue curve = Curve((0, 1.0, Interpolation.Linear), (4, 3.0, Interpolation.Linear));
        Assert.Equal(8.0 * Sec, SpeedRamp.Integrate(curve, 0, 4 * Sec), 1e-6);
        Assert.Equal(3.0 * Sec, SpeedRamp.Integrate(curve, 0, 2 * Sec), 1e-6);
        // Reversed bounds give the negated integral.
        Assert.Equal(-3.0 * Sec, SpeedRamp.Integrate(curve, 2 * Sec, 0), 1e-6);
    }

    [Fact]
    public void Ramp_Eased_Segment_Matches_The_Polynomial_Integral()
    {
        // EaseInOut is the smoothstep 3x²−2x³ whose integral over [0,1] is exactly ½ — so a 0 → 1 smoothstep
        // over 2 s (from 1× to 2×) consumes 2×(1 + ½) = 3 s of source. Speed 1 → 2 over the segment.
        AnimatableValue curve = Curve((0, 1.0, Interpolation.EaseInOut), (2, 2.0, Interpolation.Linear));
        Assert.Equal(3.0 * Sec, SpeedRamp.Integrate(curve, 0, 2 * Sec), 2.0); // within 2 ticks (Simpson is exact for cubics; tick sampling rounds)
    }

    [Fact]
    public void Ramp_Speed_Is_Clamped_To_The_Legal_Range()
    {
        AnimatableValue curve = Curve((0, 0.0, Interpolation.Hold));
        Assert.Equal(SpeedRamp.MinSpeed, SpeedRamp.SpeedAt(curve, 0));
        Assert.Equal(SpeedRamp.MinSpeed * Sec, SpeedRamp.Integrate(curve, 0, Sec), 1e-6);
    }

    [Fact]
    public void Ramp_Duration_Solve_Inverts_The_Integral()
    {
        // 2× held for 3 s (6 s of source), then 0.5×: a 10 s span needs 3 s + (10 − 6)/0.5 = 11 s of timeline.
        AnimatableValue curve = Curve((0, 2.0, Interpolation.Hold), (3, 0.5, Interpolation.Hold));
        Assert.Equal(11 * Sec, SpeedRamp.SolveDuration(curve, 10 * Sec));

        // Linear 1× → 3× over 4 s covers 8 s of source; a 3 s span ends at t where t + t²/4 = 3 → t = 2 s.
        AnimatableValue linear = Curve((0, 1.0, Interpolation.Linear), (4, 3.0, Interpolation.Linear));
        Assert.Equal(2 * Sec, SpeedRamp.SolveDuration(linear, 3 * Sec));

        Assert.Equal(0, SpeedRamp.SolveDuration(linear, 0));
    }

    [Fact]
    public void Ramp_Map_Is_Monotonic_And_Lands_On_The_Span()
    {
        AnimatableValue curve = Curve((0, 0.25, Interpolation.EaseInOut), (2, 3.0, Interpolation.Bezier), (5, 1.0, Interpolation.Linear));
        long span = 10 * Sec;
        long duration = SpeedRamp.SolveDuration(curve, span);
        Assert.True(duration > 0);

        long prev = -1;
        for (long t = 0; t <= duration; t += duration / 200)
        {
            long s = SpeedRamp.SourceOffsetAt(curve, t);
            Assert.True(s >= prev, "the integrated map must never run backwards");
            prev = s;
        }
        // At the solved duration the map has covered the span (to within a tick of rounding), not before.
        Assert.InRange(SpeedRamp.SourceOffsetAt(curve, duration), span - 1, span + 1);
        Assert.True(SpeedRamp.SourceOffsetAt(curve, duration - 1) < span + 1);
    }

    // ── Clip: ramp duration + map ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Clip_Duration_Derives_From_The_Ramp()
    {
        Clip clip = ClipFor(Rational.One);
        clip.SpeedCurve = Curve((0, 2.0, Interpolation.Hold), (3, 0.5, Interpolation.Hold));

        Assert.True(clip.HasSpeedRamp);
        Assert.Equal(Timecode.FromSeconds(11), clip.Duration);              // see the solve test
        Assert.Equal(Timecode.FromSeconds(15), clip.TimelineEnd);
        Assert.Equal(Timecode.FromSeconds(2 + 4), clip.MapToSource(Timecode.FromSeconds(6)));   // 2 s in at 2× → 4 s of source
        Assert.Equal(Timecode.FromSeconds(2 + 6.5), clip.MapToSource(Timecode.FromSeconds(8))); // + 1 s at 0.5×
        Assert.Equal(Timecode.FromSeconds(12), clip.MapToSource(clip.TimelineEnd));            // lands on the out-point
    }

    [Fact]
    public void Clip_Duration_Cache_Follows_Trim_And_Curve_Changes()
    {
        Clip clip = ClipFor(Rational.One);
        clip.SpeedCurve = Curve((0, 2.0, Interpolation.Hold));
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);

        clip.SourceOut = Timecode.FromSeconds(8); // 6 s span at 2× → 3 s
        Assert.Equal(Timecode.FromSeconds(3), clip.Duration);

        clip.SpeedCurve = null;                   // back to the constant 1×
        Assert.False(clip.HasSpeedRamp);
        Assert.Equal(Timecode.FromSeconds(6), clip.Duration);
    }

    [Fact]
    public void A_Constant_Curve_Normalises_To_No_Ramp()
    {
        Clip clip = ClipFor(new Rational(2, 1));
        clip.SpeedCurve = AnimatableValue.Constant(0.5);
        Assert.Null(clip.SpeedCurve);
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration); // the constant SpeedRatio still rules
    }

    // ── Clip: reverse ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reverse_Mirrors_The_Map_From_The_Out_Point_Without_Changing_Duration()
    {
        Clip clip = ClipFor(new Rational(2, 1), reverse: true);
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);                                // unchanged by direction
        Assert.Equal(Timecode.FromSeconds(12), clip.MapToSource(Timecode.FromSeconds(4)));   // starts at the out-point
        Assert.Equal(Timecode.FromSeconds(10), clip.MapToSource(Timecode.FromSeconds(5)));   // 1 s in → 2 s back
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(clip.TimelineEnd));           // ends on the in-point
        // The forward offset is direction-independent.
        Assert.Equal(Timecode.FromSeconds(2), clip.SourceOffset(Timecode.FromSeconds(5)));
    }

    [Fact]
    public void Reverse_Map_Extends_Mirrored_Beyond_The_Span()
    {
        // Like the forward map, the reversed map is unclamped so transition handles resolve (PLAN.md step 25):
        // 1 s before the clip is 1 s of source *past* the out-point; 1 s after its end is 1 s before the in-point.
        Clip clip = ClipFor(Rational.One, reverse: true); // source [2, 12) at t = 4, duration 10
        Assert.Equal(Timecode.FromSeconds(13), clip.MapToSource(Timecode.FromSeconds(3)));
        Assert.Equal(Timecode.FromSeconds(1), clip.MapToSource(Timecode.FromSeconds(15)));
        Assert.Equal(Timecode.FromSeconds(-1), clip.SourceOffset(Timecode.FromSeconds(3)));
    }

    [Fact]
    public void Reverse_Composes_With_A_Ramp()
    {
        Clip clip = ClipFor(Rational.One, reverse: true);
        clip.SpeedCurve = Curve((0, 2.0, Interpolation.Hold), (3, 0.5, Interpolation.Hold));
        Assert.Equal(Timecode.FromSeconds(11), clip.Duration);
        Assert.Equal(Timecode.FromSeconds(12 - 4), clip.MapToSource(Timecode.FromSeconds(6)));
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(clip.TimelineEnd));
    }

    [Fact]
    public void Clone_Copies_Direction_And_Ramp()
    {
        Clip clip = ClipFor(new Rational(3, 2), reverse: true);
        clip.SpeedCurve = Curve((0, 1.0, Interpolation.Linear), (2, 2.0, Interpolation.Linear));
        Clip copy = clip.CloneContentForSpan(clip.SourceIn, clip.SourceOut, Timecode.FromSeconds(20));
        Assert.True(copy.Reverse);
        Assert.Same(clip.SpeedCurve, copy.SpeedCurve);
        Assert.Equal(clip.Duration, copy.Duration);
    }

    // ── Split ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_Of_A_Reversed_Clip_Hands_The_Lower_Source_To_The_Right_Half()
    {
        var track = new VideoTrack();
        Clip clip = ClipFor(new Rational(2, 1), reverse: true); // source [2, 12) at 2×, timeline [4, 9)
        track.Clips.Add(clip);
        var history = new EditHistory();

        var split = new SplitClipCommand(track, clip, Timecode.FromSeconds(6)); // 2 s in → source 12 − 4 = 8
        history.Execute(split);

        // Left half keeps playing what it showed: source 12 → 8 (the top of the span).
        Assert.Equal(Timecode.FromSeconds(8), clip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(12), clip.SourceOut);
        Assert.Equal(Timecode.FromSeconds(6), clip.TimelineEnd);
        // Right half continues from 8 down to 2.
        Assert.True(split.RightClip.Reverse);
        Assert.Equal(Timecode.FromSeconds(2), split.RightClip.SourceIn);
        Assert.Equal(Timecode.FromSeconds(8), split.RightClip.SourceOut);
        Assert.Equal(Timecode.FromSeconds(6), split.RightClip.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(9), split.RightClip.TimelineEnd);
        // Frame continuity across the cut: the right half's first frame is where the left half stopped.
        Assert.Equal(clip.MapToSource(clip.TimelineEnd), split.RightClip.MapToSource(split.RightClip.TimelineStart));

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(2), clip.SourceIn);
        Assert.Single(track.Clips);
    }

    [Fact]
    public void Split_Of_A_Ramped_Clip_Re_Anchors_The_Right_Half_Curve()
    {
        var track = new VideoTrack();
        Clip clip = ClipFor(Rational.One); // source [2, 12) at t = 4
        clip.SpeedCurve = Curve((0, 2.0, Interpolation.Hold), (3, 0.5, Interpolation.Hold)); // duration 11 s → [4, 15)
        track.Clips.Add(clip);
        var history = new EditHistory();

        Timecode at = Timecode.FromSeconds(6); // 2 s in: source 2 + 4 = 6
        Timecode endBefore = clip.TimelineEnd;
        var split = new SplitClipCommand(track, clip, at);
        history.Execute(split);

        Assert.Equal(Timecode.FromSeconds(6), clip.SourceOut);
        Assert.Equal(at, clip.TimelineEnd);                                 // the left half's duration follows its span
        Clip right = split.RightClip;
        Assert.Equal(Timecode.FromSeconds(6), right.SourceIn);
        Assert.Equal(endBefore, right.TimelineEnd);                         // the halves still partition the original span
        // The right half's curve is re-anchored: the speed change that was at t = 7 s (local 3 s) is still at t = 7 s.
        Assert.Equal(Timecode.FromSeconds(1), right.SpeedCurve!.Keyframes[1].Time);
        Assert.Equal(clip.MapToSource(clip.TimelineEnd), right.MapToSource(right.TimelineStart));
        Assert.Equal(Timecode.FromSeconds(12), right.MapToSource(right.TimelineEnd));
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetClipSpeedCurveCommand_Applies_Reverts_And_Coalesces()
    {
        Clip clip = ClipFor(Rational.One);
        var history = new EditHistory();
        AnimatableValue a = Curve((0, 2.0, Interpolation.Hold));
        AnimatableValue b = Curve((0, 4.0, Interpolation.Hold));

        using (history.BeginCoalescing())
        {
            history.Execute(new SetClipSpeedCurveCommand(clip, a));
            history.Execute(new SetClipSpeedCurveCommand(clip, b));
        }
        Assert.Same(b, clip.SpeedCurve);
        Assert.Equal(Timecode.FromSeconds(2.5), clip.Duration);
        Assert.Equal(1, history.UndoCount);

        history.Undo();
        Assert.Null(clip.SpeedCurve);
        Assert.Equal(Timecode.FromSeconds(10), clip.Duration);
    }

    [Fact]
    public void SetClipReverseCommand_Applies_And_Reverts()
    {
        Clip clip = ClipFor(Rational.One);
        var history = new EditHistory();
        history.Execute(new SetClipReverseCommand(clip, true));
        Assert.True(clip.Reverse);
        Assert.Equal(Timecode.FromSeconds(12), clip.MapToSource(clip.TimelineStart));
        history.Undo();
        Assert.False(clip.Reverse);
        Assert.Equal(Timecode.FromSeconds(2), clip.MapToSource(clip.TimelineStart));
    }

    [Fact]
    public void A_Changed_Constant_Speed_Replaces_The_Ramp_And_Undo_Restores_It()
    {
        Clip clip = ClipFor(Rational.One);
        AnimatableValue ramp = Curve((0, 2.0, Interpolation.Hold), (3, 0.5, Interpolation.Hold));
        clip.SpeedCurve = ramp;
        var history = new EditHistory();

        // Re-applying the clip's own constant speed leaves the ramp alone (the Speed dialog's "OK without changes").
        history.Execute(new SetClipSpeedCommand(clip, Rational.One));
        Assert.Same(ramp, clip.SpeedCurve);
        Assert.Equal(Timecode.FromSeconds(11), clip.Duration);

        // A different constant speed is defined as "no ramp".
        history.Execute(new SetClipSpeedCommand(clip, new Rational(2, 1)));
        Assert.Null(clip.SpeedCurve);
        Assert.Equal(Timecode.FromSeconds(5), clip.Duration);

        history.Undo();
        Assert.Same(ramp, clip.SpeedCurve);
        Assert.Equal(Rational.One, clip.SpeedRatio);
        Assert.Equal(Timecode.FromSeconds(11), clip.Duration);
    }

    [Fact]
    public void Nested_Sequence_Clips_Cannot_Be_Reversed()
    {
        Clip nested = Clip.CreateSequenceClip(SequenceId.New(), Timecode.FromSeconds(5), Timecode.Zero);
        Assert.False(nested.SupportsReverse);
        Assert.Throws<InvalidOperationException>(() => new SetClipReverseCommand(nested, true));
        Assert.True(ClipFor(Rational.One).SupportsReverse);
    }

    [Fact]
    public void Frame_Edits_Need_A_Constant_Forward_Map()
    {
        Rational fps = new(30, 1);
        Clip reversed = ClipFor(Rational.One, reverse: true);
        Assert.Throws<InvalidOperationException>(() => FrameHoldEdits.SourceFrameSpan(reversed, Timecode.FromSeconds(5), fps));
        Clip ramped = ClipFor(Rational.One);
        ramped.SpeedCurve = Curve((0, 2.0, Interpolation.Hold), (3, 0.5, Interpolation.Hold));
        Assert.Throws<InvalidOperationException>(() => FrameHoldEdits.SourceFrameSpan(ramped, Timecode.FromSeconds(5), fps));
    }

    [Fact]
    public void Ramp_Duration_Rounding_Matches_The_Map_So_Split_Halves_Never_Overlap()
    {
        // A linear ramp whose integral has a fractional tick almost everywhere: split at many points and check the
        // left half's re-solved end never passes the cut (a sub-tick gap is allowed, an overlap is not).
        AnimatableValue curve = Curve((0, 0.37, Interpolation.Linear), (7, 2.9, Interpolation.Linear));
        for (int i = 1; i < 40; i++)
        {
            var track = new VideoTrack();
            var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero) { SpeedCurve = curve };
            track.Clips.Add(clip);
            Timecode at = new(clip.Duration.Ticks * i / 40);
            var split = new SplitClipCommand(track, clip, at);
            new EditHistory().Execute(split);
            Assert.True(clip.TimelineEnd <= at, $"left half overlaps the cut at {at}: ends {clip.TimelineEnd}");
            Assert.True(at.Ticks - clip.TimelineEnd.Ticks <= 3, $"gap at {at} too large: {at.Ticks - clip.TimelineEnd.Ticks} ticks");
            Assert.Equal(at, split.RightClip.TimelineStart);
        }
    }
}
