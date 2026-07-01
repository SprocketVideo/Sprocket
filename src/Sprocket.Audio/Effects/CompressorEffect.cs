using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// A feed-forward dynamic-range compressor (PLAN.md step 31): a per-frame peak detector (max |sample| across
/// channels) smoothed by one-pole attack/release envelopes, a hard-knee gain computer
/// (<see cref="EffectParamNames.ThresholdDb"/> / <see cref="EffectParamNames.Ratio"/>), and static make-up
/// gain — the textbook design every DAW ships. All channels receive the same gain (stereo-linked), so the
/// image never shifts. The envelope carries across buffers for seamless block processing.
/// </summary>
public sealed class CompressorEffect : IAudioEffect
{
    private double _envelope; // linear peak envelope, carried across buffers

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double thresholdDb = parameters.Get(EffectParamNames.ThresholdDb, -18);
        double ratio = Math.Max(1.0, parameters.Get(EffectParamNames.Ratio, 4));
        double attackMs = Math.Max(0.01, parameters.Get(EffectParamNames.AttackMs, 10));
        double releaseMs = Math.Max(1.0, parameters.Get(EffectParamNames.ReleaseMs, 100));
        double makeupDb = parameters.Get(EffectParamNames.MakeupDb);

        double attack = Math.Exp(-1.0 / (attackMs / 1000.0 * sampleRate));
        double release = Math.Exp(-1.0 / (releaseMs / 1000.0 * sampleRate));
        var makeup = (float)Math.Pow(10, makeupDb / 20.0);
        double slope = 1.0 - 1.0 / ratio; // dB of reduction per dB over threshold

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            float peak = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                float abs = Math.Abs(interleaved[baseIndex + ch]);
                if (abs > peak)
                    peak = abs;
            }

            // One-pole smoothing: fast rise (attack), slow fall (release).
            double coeff = peak > _envelope ? attack : release;
            _envelope = coeff * _envelope + (1 - coeff) * peak;

            float gain = makeup;
            if (_envelope > 1e-9)
            {
                double envDb = 20 * Math.Log10(_envelope);
                double overDb = envDb - thresholdDb;
                if (overDb > 0)
                    gain = (float)Math.Pow(10, (makeupDb - overDb * slope) / 20.0);
            }

            for (int ch = 0; ch < channels; ch++)
                interleaved[baseIndex + ch] *= gain;
        }
    }

    /// <inheritdoc />
    public void Reset() => _envelope = 0;
}
