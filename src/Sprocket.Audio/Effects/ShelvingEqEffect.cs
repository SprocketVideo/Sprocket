using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Shelving EQ (PLAN.md step 48): standalone low-shelf + high-shelf tone shaping — the DAW convention
/// (Ableton EQ Three / Logic Channel EQ's shelf-only use) for the quick tilt/warmth/air pass where a full
/// 3-band parametric is heavier than needed. Each shelf is the same RBJ biquad as
/// <see cref="ParametricEqEffect"/>'s shelf bands (<see cref="BiquadBand.ConfigureShelf"/>), generalized from
/// the fixed slope S = 1 to a variable slope, with an independent enable so either shelf can run alone.
/// Coefficients are recomputed only when a parameter (or the format) changes; a shelf at 0 dB (or disabled)
/// is bypassed entirely, so a default instance is an exact pass-through — matching
/// <see cref="ParametricEqEffect"/>'s bypass-at-0dB behavior.
/// </summary>
public sealed class ShelvingEqEffect : IAudioEffect
{
    private BiquadBand _low, _high;
    private double _lowGain = double.NaN, _lowFreq, _lowSlope, _highGain = double.NaN, _highFreq, _highSlope;
    private bool _lowOn, _highOn;
    private int _rate, _channels;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        bool lowOn = parameters.Get(EffectParamNames.LowEnable, 1) >= 0.5;
        double lowGain = parameters.Get(EffectParamNames.LowGainDb);
        double lowFreq = parameters.Get(EffectParamNames.LowFreq, 100);
        double lowSlope = ClampSlope(parameters.Get(EffectParamNames.LowSlope, 1.0));
        bool highOn = parameters.Get(EffectParamNames.HighEnable, 1) >= 0.5;
        double highGain = parameters.Get(EffectParamNames.HighGainDb);
        double highFreq = parameters.Get(EffectParamNames.HighFreq, 8000);
        double highSlope = ClampSlope(parameters.Get(EffectParamNames.HighSlope, 1.0));

        if (sampleRate != _rate || channels != _channels ||
            lowOn != _lowOn || lowGain != _lowGain || lowFreq != _lowFreq || lowSlope != _lowSlope ||
            highOn != _highOn || highGain != _highGain || highFreq != _highFreq || highSlope != _highSlope)
        {
            bool formatChanged = sampleRate != _rate || channels != _channels;
            _rate = sampleRate;
            _channels = channels;
            _lowOn = lowOn; _lowGain = lowGain; _lowFreq = lowFreq; _lowSlope = lowSlope;
            _highOn = highOn; _highGain = highGain; _highFreq = highFreq; _highSlope = highSlope;

            _low.Active = lowOn && lowGain != 0;
            if (_low.Active)
            {
                _low.EnsureState(channels, formatChanged);
                _low.ConfigureShelf(lowGain, lowFreq, lowSlope, high: false, sampleRate);
            }
            _high.Active = highOn && highGain != 0;
            if (_high.Active)
            {
                _high.EnsureState(channels, formatChanged);
                _high.ConfigureShelf(highGain, highFreq, highSlope, high: true, sampleRate);
            }
        }

        _low.Run(interleaved, frames, channels);
        _high.Run(interleaved, frames, channels);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _low.ClearState();
        _high.ClearState();
    }

    /// <summary>Keeps S strictly positive and inside a range where the shelf stays well-behaved at the
    /// catalog's ±15 dB gain extremes (the derivation additionally floors its sqrt argument).</summary>
    private static double ClampSlope(double slope) => Math.Clamp(slope, 0.05, 2.0);
}
