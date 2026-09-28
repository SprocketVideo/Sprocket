namespace Sprocket.Audio.Effects;

/// <summary>
/// The deterministic tape-transport wow &amp; flutter LFO shared by <see cref="TapeDelayEffect"/> (PLAN.md step 46)
/// and <see cref="CassetteEffect"/> (plan/features/toy-cassette-camera.md, phase 4): a slow "wow" sine at the
/// configured rate plus a smaller, faster "flutter" sine at a fixed multiple above it. The output is a
/// delay-time excursion in samples, always in [0, <see cref="MaxExcursion"/>], and a pure function of a sample
/// counter — no RNG — so renders are reproducible run-to-run and export matches preview. Extracted verbatim
/// from Tape Delay's inline LFO (same expression, same evaluation order), so Tape Delay's output is bit-exact
/// across the refactor. A mutable struct held in a field of its owner, like <see cref="BiquadBand"/>.
/// </summary>
internal struct TapeWowFlutter
{
    /// <summary>Flutter sits well above the wow rate.</summary>
    public const double FlutterRateMultiple = 6.3;

    /// <summary>Flutter is a small fraction of the wow swing.</summary>
    public const double FlutterDepthRatio = 0.125;

    private double _wowSwing, _flutterSwing, _wowStep, _flutterStep;
    private long _sample; // the LFO clock: advances once per frame, shared by all channels (one transport)

    /// <summary>The largest excursion (samples) the LFO can produce at full depth with a wow excursion of
    /// <paramref name="maxWowMs"/> — the delay-line headroom an owner must reserve.</summary>
    public static double MaxExcursion(double maxWowMs, int sampleRate) =>
        maxWowMs / 1000.0 * sampleRate * (1 + FlutterDepthRatio);

    /// <summary>Sets the swing and rate for the coming block. <paramref name="depth"/> in [0, 1] scales the
    /// <paramref name="maxWowMs"/> wow excursion (flutter follows at <see cref="FlutterDepthRatio"/>);
    /// <paramref name="wowRateHz"/> is the (caller-clamped) wow rate. Does not touch the clock.</summary>
    public void Configure(double depth, double maxWowMs, double wowRateHz, int sampleRate)
    {
        _wowSwing = depth * maxWowMs / 1000.0 * sampleRate;
        _flutterSwing = _wowSwing * FlutterDepthRatio;
        _wowStep = 2 * Math.PI * wowRateHz / sampleRate;
        _flutterStep = _wowStep * FlutterRateMultiple;
    }

    /// <summary>The current frame's excursion in samples (≥ 0), then advances the clock by one frame.</summary>
    public double Next()
    {
        double lfo = _wowSwing * (0.5 + 0.5 * Math.Sin(_sample * _wowStep))
                   + _flutterSwing * (0.5 + 0.5 * Math.Sin(_sample * _flutterStep));
        _sample++;
        return lfo;
    }

    /// <summary>Rewinds the clock to 0 (on <c>Reset()</c> / format change), so a replay after a seek is
    /// identical to the first pass.</summary>
    public void Reset() => _sample = 0;
}
