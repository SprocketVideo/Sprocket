using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>How a keyframe's value blends into the next keyframe.</summary>
public enum Interpolation
{
    /// <summary>Hold the value until the next keyframe (step).</summary>
    Hold,

    /// <summary>Linearly interpolate toward the next keyframe's value.</summary>
    Linear,
}

/// <summary>A single animation keyframe: a value at a time, plus how it blends toward the next one.</summary>
/// <param name="Time">Timeline time of this keyframe.</param>
/// <param name="Value">The value at this time.</param>
/// <param name="Interpolation">How this keyframe blends into the following one.</param>
public readonly record struct Keyframe(Timecode Time, double Value, Interpolation Interpolation = Interpolation.Linear);

/// <summary>
/// An effect parameter that is either a constant or a list of keyframes (ARCHITECTURE.md §9).
/// The render graph calls <see cref="Evaluate"/> at the frame's time before building the effect, so
/// the very same mechanism that drives a fade also drives any future keyframed parameter — no model
/// change required.
/// </summary>
public sealed class AnimatableValue
{
    private readonly double _constant;
    private readonly Keyframe[] _keyframes; // sorted ascending by time; empty when constant

    private AnimatableValue(double constant, Keyframe[] keyframes)
    {
        _constant = constant;
        _keyframes = keyframes;
    }

    /// <summary>Whether this value is animated (has keyframes) rather than constant.</summary>
    public bool IsAnimated => _keyframes.Length > 0;

    /// <summary>The keyframes, in ascending time order (empty if constant).</summary>
    public IReadOnlyList<Keyframe> Keyframes => _keyframes;

    /// <summary>Creates a constant (non-animated) value.</summary>
    public static AnimatableValue Constant(double value) => new(value, []);

    /// <summary>
    /// Creates an animated value from one or more keyframes. The keyframes are sorted by time;
    /// at least one is required.
    /// </summary>
    public static AnimatableValue Animated(IEnumerable<Keyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        Keyframe[] sorted = [.. keyframes];
        if (sorted.Length == 0)
            throw new ArgumentException("An animated value needs at least one keyframe.", nameof(keyframes));
        Array.Sort(sorted, static (a, b) => a.Time.CompareTo(b.Time));
        return new AnimatableValue(0, sorted);
    }

    /// <summary>
    /// Evaluates the value at timeline time <paramref name="t"/>. Constant values ignore <paramref name="t"/>.
    /// For animated values: clamps to the first/last keyframe outside the keyframe range, and interpolates
    /// within it according to the outgoing keyframe's <see cref="Interpolation"/> mode.
    /// </summary>
    public double Evaluate(Timecode t)
    {
        if (_keyframes.Length == 0)
            return _constant;

        if (_keyframes.Length == 1 || t <= _keyframes[0].Time)
            return _keyframes[0].Value;

        Keyframe last = _keyframes[^1];
        if (t >= last.Time)
            return last.Value;

        // Find the segment [k0, k1] with k0.Time <= t < k1.Time.
        for (int i = 0; i < _keyframes.Length - 1; i++)
        {
            Keyframe k0 = _keyframes[i];
            Keyframe k1 = _keyframes[i + 1];
            if (t < k1.Time)
            {
                if (k0.Interpolation == Interpolation.Hold)
                    return k0.Value;

                long span = k1.Time.Ticks - k0.Time.Ticks;
                double frac = span == 0 ? 0 : (double)(t.Ticks - k0.Time.Ticks) / span;
                return k0.Value + (k1.Value - k0.Value) * frac;
            }
        }

        // Unreachable given the clamps above, but keeps the compiler happy.
        return last.Value;
    }
}
