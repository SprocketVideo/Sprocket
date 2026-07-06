using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Three-band parametric EQ (PLAN.md step 31): a low shelf, a mid peaking band with Q, and a high shelf —
/// the classic channel-strip layout — implemented as RBJ Audio-EQ-Cookbook biquads (<see cref="BiquadBand"/>),
/// one filter per band per channel, run in series. Coefficients are recomputed only when a parameter (or the
/// sample rate) changes; a band at 0 dB is bypassed entirely, so a default instance is an exact pass-through.
/// </summary>
public sealed class ParametricEqEffect : IAudioEffect
{
    private BiquadBand _low, _mid, _high;
    private double _lowGain = double.NaN, _lowFreq, _midGain = double.NaN, _midFreq, _midQ, _highGain = double.NaN, _highFreq;
    private int _rate, _channels;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double lowGain = parameters.Get(EffectParamNames.LowGainDb);
        double lowFreq = parameters.Get(EffectParamNames.LowFreq, 100);
        double midGain = parameters.Get(EffectParamNames.MidGainDb);
        double midFreq = parameters.Get(EffectParamNames.MidFreq, 1000);
        double midQ = Math.Max(0.05, parameters.Get(EffectParamNames.MidQ, 1.0));
        double highGain = parameters.Get(EffectParamNames.HighGainDb);
        double highFreq = parameters.Get(EffectParamNames.HighFreq, 8000);

        if (sampleRate != _rate || channels != _channels ||
            lowGain != _lowGain || lowFreq != _lowFreq ||
            midGain != _midGain || midFreq != _midFreq || midQ != _midQ ||
            highGain != _highGain || highFreq != _highFreq)
        {
            bool formatChanged = sampleRate != _rate || channels != _channels;
            _rate = sampleRate;
            _channels = channels;
            _lowGain = lowGain; _lowFreq = lowFreq;
            _midGain = midGain; _midFreq = midFreq; _midQ = midQ;
            _highGain = highGain; _highFreq = highFreq;

            _low.Active = lowGain != 0;
            if (_low.Active)
            {
                _low.EnsureState(channels, formatChanged);
                _low.ConfigureShelf(lowGain, lowFreq, slope: 1.0, high: false, sampleRate);
            }
            _mid.Active = midGain != 0;
            if (_mid.Active)
            {
                _mid.EnsureState(channels, formatChanged);
                _mid.ConfigurePeak(midGain, midFreq, midQ, sampleRate);
            }
            _high.Active = highGain != 0;
            if (_high.Active)
            {
                _high.EnsureState(channels, formatChanged);
                _high.ConfigureShelf(highGain, highFreq, slope: 1.0, high: true, sampleRate);
            }
        }

        _low.Run(interleaved, frames, channels);
        _mid.Run(interleaved, frames, channels);
        _high.Run(interleaved, frames, channels);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _low.ClearState();
        _mid.ClearState();
        _high.ClearState();
    }
}
