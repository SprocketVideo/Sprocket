using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// The integrated time map behind a keyframed speed ramp (PLAN.md step 21 remainder, variable retime): a
/// clip whose speed varies over its span consumes source time at the rate <c>speed(t)</c>, so the source
/// offset at clip-local time <c>t</c> is <c>∫₀ᵗ speed(τ) dτ</c> and the clip's timeline duration is the
/// <c>t</c> at which that integral reaches the trimmed source span. This is the same model as Premiere's
/// Time Remapping / Resolve's retime curve: the user draws speed over <em>timeline</em> time and the clip
/// grows/shrinks to fit the source it covers.
/// </summary>
/// <remarks>
/// <para>Speed keyframes live on a <see cref="Clip.SpeedCurve"/> whose values are speed as a fraction of
/// normal (1.0 = 100%) and whose keyframe times are <b>clip-local ticks</b> (0 = the clip's timeline start) —
/// unlike effect keyframes, which are absolute timeline time. The ramp determines the clip's own duration, so
/// anchoring it to the clip (rather than the sequence) keeps a moved clip's length and content unchanged
/// without a rebase step; the Inspector lane shifts by the clip start for display.</para>
/// <para>Every routine is a pure, deterministic function of the curve (ARCHITECTURE.md §5): preview and export
/// compute the identical map. Hold and Linear segments integrate exactly (in double precision); the eased and
/// Bezier segments use a fixed-step Simpson rule (<see cref="SimpsonSteps"/> panels per segment span), which is
/// exact for the polynomial eases and well below a tick of error for Bezier at editorial spans.</para>
/// <para>Speed is clamped to at least <see cref="MinSpeed"/> so the integral always advances and the duration
/// solve terminates; a true hold (speed 0) is the step-43 frame hold, not a ramp value.</para>
/// </remarks>
public static class SpeedRamp
{
    /// <summary>The floor applied to a ramp's speed values (1% of normal) — the lowest slow-motion speed a ramp
    /// can express, matching the practical minimum in leading editors' time-remap curves.</summary>
    public const double MinSpeed = 0.01;

    /// <summary>The ceiling applied to a ramp's speed values (100× normal).</summary>
    public const double MaxSpeed = 100.0;

    // Simpson panels per non-linear segment (must be even). Simpson is exact for the polynomial eases (quadratic /
    // cubic), so they need only a handful of panels; 64 keep even a sharply-curved Bezier segment's integral within
    // a small fraction of a tick over multi-minute spans, and the cost is negligible per frame.
    private const int SimpsonStepsBezier = 64;
    private const int SimpsonStepsEased = 4;

    // Longest duration the solve will search (24 h) — a safety bound; at MinSpeed a 14-minute source already
    // spans it, so anything beyond is a degenerate curve rather than an editorial case.
    private const long MaxDurationTicks = 24L * 3600 * Timecode.TicksPerSecond;

    /// <summary>The clamped speed of <paramref name="curve"/> at clip-local tick <paramref name="localTicks"/>.</summary>
    public static double SpeedAt(AnimatableValue curve, long localTicks)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return Clamp(curve.Evaluate(new Timecode(localTicks)));
    }

    /// <summary>Clamps a raw speed value into the ramp's legal range [<see cref="MinSpeed"/>, <see cref="MaxSpeed"/>].</summary>
    public static double Clamp(double speed) =>
        double.IsNaN(speed) ? 1.0 : Math.Clamp(speed, MinSpeed, MaxSpeed);

    /// <summary>
    /// The source ticks consumed between clip-local ticks <paramref name="fromLocal"/> and
    /// <paramref name="toLocal"/> (<c>∫ speed dt</c>), as a real number of ticks. Negative when
    /// <paramref name="toLocal"/> precedes <paramref name="fromLocal"/>.
    /// </summary>
    public static double Integrate(AnimatableValue curve, long fromLocal, long toLocal)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (fromLocal == toLocal)
            return 0;
        if (toLocal < fromLocal)
            return -Integrate(curve, toLocal, fromLocal);

        if (!curve.IsAnimated)
            return Clamp(curve.Evaluate(Timecode.Zero)) * (toLocal - fromLocal);

        IReadOnlyList<Keyframe> keys = curve.Keyframes;
        double total = 0;
        long cursor = fromLocal;

        // Before the first keyframe the curve is the clamped first value.
        long firstT = keys[0].Time.Ticks;
        if (cursor < firstT)
        {
            long end = Math.Min(toLocal, firstT);
            total += Clamp(keys[0].Value) * (end - cursor);
            cursor = end;
            if (cursor >= toLocal)
                return total;
        }

        // Interior segments [k_i, k_{i+1}).
        for (int i = 0; i < keys.Count - 1 && cursor < toLocal; i++)
        {
            long segStart = keys[i].Time.Ticks;
            long segEnd = keys[i + 1].Time.Ticks;
            if (segEnd <= cursor)
                continue;
            long a = Math.Max(cursor, segStart);
            long b = Math.Min(toLocal, segEnd);
            if (b > a)
                total += IntegrateSegment(curve, keys[i], keys[i + 1], a, b);
            cursor = b;
        }

        // After the last keyframe the curve is the clamped last value.
        if (cursor < toLocal)
            total += Clamp(keys[^1].Value) * (toLocal - cursor);

        return total;
    }

    /// <summary>
    /// The source offset (in whole ticks, rounded) reached at clip-local tick <paramref name="localTicks"/>
    /// from the clip's start: <c>round(∫₀ᵗ speed)</c>. Monotonic non-decreasing in <paramref name="localTicks"/>.
    /// </summary>
    public static long SourceOffsetAt(AnimatableValue curve, long localTicks) =>
        (long)Math.Round(Integrate(curve, 0, localTicks), MidpointRounding.AwayFromZero);

    /// <summary>
    /// The clip-local duration (in ticks) over which the ramp consumes <paramref name="sourceSpanTicks"/> of source
    /// — the smallest whole tick <c>t</c> whose rounded offset (<see cref="SourceOffsetAt"/>) reaches the span, i.e.
    /// <c>∫₀ᵗ speed &gt; span − ½</c>. Using the same rounding rule as the map keeps the two consistent: a clip
    /// split at <c>t</c> (whose left half spans <c>round(∫₀ᵗ)</c>) re-solves to a duration at most <c>t</c>, so the
    /// halves never overlap (any gap is below one source tick of rounding). Zero for an empty span.
    /// </summary>
    public static long SolveDuration(AnimatableValue curve, long sourceSpanTicks)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (sourceSpanTicks <= 0)
            return 0;

        if (!curve.IsAnimated)
            return CeilTicks(0, (sourceSpanTicks - 0.5) / Clamp(curve.Evaluate(Timecode.Zero)));

        // Walk the segments to bracket the answer, then bisect inside the bracketing segment (the integral is
        // strictly increasing since speed ≥ MinSpeed, so the bisection is well-posed).
        IReadOnlyList<Keyframe> keys = curve.Keyframes;
        double target = sourceSpanTicks - 0.5;
        double acc = 0;
        long lo = 0;

        // Constant region before the first keyframe.
        long firstT = keys[0].Time.Ticks;
        if (firstT > 0)
        {
            double v = Clamp(keys[0].Value);
            double reach = v * firstT;
            if (acc + reach > target)
                return CeilTicks(lo, (target - acc) / v);
            acc += reach;
            lo = firstT;
        }

        for (int i = 0; i < keys.Count - 1; i++)
        {
            long segStart = keys[i].Time.Ticks;
            long segEnd = keys[i + 1].Time.Ticks;
            if (segEnd <= lo)
                continue;
            long a = Math.Max(lo, segStart);
            double segIntegral = IntegrateSegment(curve, keys[i], keys[i + 1], a, segEnd);
            if (acc + segIntegral > target)
                return Bisect(curve, a, segEnd, target - acc);
            acc += segIntegral;
            lo = segEnd;
        }

        // Constant tail after the last keyframe.
        double tail = Clamp(keys[^1].Value);
        return CeilTicks(lo, (target - acc) / tail);
    }

    // Smallest whole tick t ≥ start such that the constant-speed integral from start *exceeds* the remaining amount
    // (strictly — see SolveDuration's rounding rule), capped at the safety bound.
    private static long CeilTicks(long start, double ticksNeeded)
    {
        double t = start + Math.Max(0, ticksNeeded);
        long r = (long)Math.Floor(t + 1e-9) + 1;
        return Math.Max(start, Math.Min(r, MaxDurationTicks));
    }

    // Finds the smallest whole tick t in [a, b] with ∫ₐᵗ speed > amount (the integral is strictly increasing).
    private static long Bisect(AnimatableValue curve, long a, long b, double amount)
    {
        long lo = a, hi = b;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            if (Integrate(curve, a, mid) > amount + 1e-9)
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    // Integrates one keyframe segment over the sub-range [a, b] ⊆ [k0.Time, k1.Time].
    private static double IntegrateSegment(AnimatableValue curve, Keyframe k0, Keyframe k1, long a, long b)
    {
        long span = k1.Time.Ticks - k0.Time.Ticks;
        if (span <= 0 || b <= a)
            return 0;

        double v0 = Clamp(k0.Value), v1 = Clamp(k1.Value);
        switch (k0.Interpolation)
        {
            case Interpolation.Hold:
                return v0 * (b - a);

            case Interpolation.Linear when !CrossesClamp(k0.Value, k1.Value):
            {
                // Exact trapezoid over the sub-range of a straight line.
                double fa = v0 + (v1 - v0) * ((double)(a - k0.Time.Ticks) / span);
                double fb = v0 + (v1 - v0) * ((double)(b - k0.Time.Ticks) / span);
                return (fa + fb) * 0.5 * (b - a);
            }

            default:
            {
                // Composite Simpson over the sub-range, sampling the curve itself (so eased/Bezier shapes and the
                // clamp are honoured exactly as the per-frame evaluation sees them).
                int n = k0.Interpolation == Interpolation.Bezier ? SimpsonStepsBezier : SimpsonStepsEased;
                double h = (double)(b - a) / n;
                double sum = SpeedAt(curve, a) + SpeedAt(curve, b);
                for (int i = 1; i < n; i++)
                {
                    long t = a + (long)Math.Round(i * h);
                    sum += (i % 2 == 1 ? 4 : 2) * SpeedAt(curve, t);
                }
                return sum * h / 3.0;
            }
        }
    }

    // Whether a linear segment between raw values leaves the clamp range (then the trapezoid isn't exact and we
    // fall back to sampling).
    private static bool CrossesClamp(double v0, double v1) =>
        v0 < MinSpeed || v1 < MinSpeed || v0 > MaxSpeed || v1 > MaxSpeed || double.IsNaN(v0) || double.IsNaN(v1);
}
