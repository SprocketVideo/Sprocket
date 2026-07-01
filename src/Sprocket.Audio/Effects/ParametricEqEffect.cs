using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Three-band parametric EQ (PLAN.md step 31): a low shelf, a mid peaking band with Q, and a high shelf —
/// the classic channel-strip layout — implemented as RBJ Audio-EQ-Cookbook biquads, one filter per band per
/// channel, run in series. Coefficients are recomputed only when a parameter (or the sample rate) changes;
/// a band at 0 dB is bypassed entirely, so a default instance is an exact pass-through.
/// </summary>
public sealed class ParametricEqEffect : IAudioEffect
{
    // Per-band coefficients (shared across channels) + per-channel state z1/z2 (transposed direct form II).
    private struct Band
    {
        public float B0, B1, B2, A1, A2;
        public float[] Z1, Z2;
        public bool Active;
    }

    private Band _low, _mid, _high;
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
            ConfigureShelf(ref _low, lowGain, lowFreq, high: false, formatChanged);
            ConfigurePeak(ref _mid, midGain, midFreq, midQ, formatChanged);
            ConfigureShelf(ref _high, highGain, highFreq, high: true, formatChanged);
        }

        RunBand(ref _low, interleaved, frames, channels);
        RunBand(ref _mid, interleaved, frames, channels);
        RunBand(ref _high, interleaved, frames, channels);
    }

    /// <inheritdoc />
    public void Reset()
    {
        ClearState(ref _low);
        ClearState(ref _mid);
        ClearState(ref _high);
    }

    private static void ClearState(ref Band band)
    {
        band.Z1?.AsSpan().Clear();
        band.Z2?.AsSpan().Clear();
    }

    private void ConfigureShelf(ref Band band, double gainDb, double freq, bool high, bool formatChanged)
    {
        band.Active = gainDb != 0;
        if (!band.Active)
            return;
        EnsureState(ref band, formatChanged);

        // RBJ cookbook low/high shelf, shelf slope S = 1.
        double a = Math.Pow(10, gainDb / 40.0);
        double w0 = 2 * Math.PI * Clamp(freq) / _rate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / 2 * Math.Sqrt(2);
        double sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;
        double sign = high ? -1 : 1; // the high shelf mirrors the low shelf's cos terms

        double b0 = a * ((a + 1) - sign * (a - 1) * cos + sqrtA2Alpha);
        double b1 = sign * 2 * a * ((a - 1) - sign * (a + 1) * cos);
        double b2 = a * ((a + 1) - sign * (a - 1) * cos - sqrtA2Alpha);
        double a0 = (a + 1) + sign * (a - 1) * cos + sqrtA2Alpha;
        double a1 = sign * -2 * ((a - 1) + sign * (a + 1) * cos);
        double a2 = (a + 1) + sign * (a - 1) * cos - sqrtA2Alpha;
        Normalize(ref band, b0, b1, b2, a0, a1, a2);
    }

    private void ConfigurePeak(ref Band band, double gainDb, double freq, double q, bool formatChanged)
    {
        band.Active = gainDb != 0;
        if (!band.Active)
            return;
        EnsureState(ref band, formatChanged);

        // RBJ cookbook peaking EQ.
        double a = Math.Pow(10, gainDb / 40.0);
        double w0 = 2 * Math.PI * Clamp(freq) / _rate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);

        double b0 = 1 + alpha * a;
        double b1 = -2 * cos;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        double a1 = -2 * cos;
        double a2 = 1 - alpha / a;
        Normalize(ref band, b0, b1, b2, a0, a1, a2);
    }

    /// <summary>Keeps the band frequency inside (0, Nyquist) so the trig stays well-defined.</summary>
    private double Clamp(double freq) => Math.Clamp(freq, 1.0, _rate / 2.0 - 1.0);

    private static void Normalize(ref Band band, double b0, double b1, double b2, double a0, double a1, double a2)
    {
        band.B0 = (float)(b0 / a0);
        band.B1 = (float)(b1 / a0);
        band.B2 = (float)(b2 / a0);
        band.A1 = (float)(a1 / a0);
        band.A2 = (float)(a2 / a0);
    }

    private void EnsureState(ref Band band, bool formatChanged)
    {
        if (band.Z1 is null || band.Z1.Length < _channels || formatChanged)
        {
            band.Z1 = new float[_channels];
            band.Z2 = new float[_channels];
        }
    }

    private static void RunBand(ref Band band, Span<float> buffer, int frames, int channels)
    {
        if (!band.Active)
            return;
        for (int ch = 0; ch < channels; ch++)
        {
            float z1 = band.Z1[ch], z2 = band.Z2[ch];
            for (int f = 0; f < frames; f++)
            {
                int i = f * channels + ch;
                float x = buffer[i];
                float y = band.B0 * x + z1;
                z1 = band.B1 * x - band.A1 * y + z2;
                z2 = band.B2 * x - band.A2 * y;
                buffer[i] = y;
            }
            band.Z1[ch] = z1;
            band.Z2[ch] = z2;
        }
    }
}
