using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Multi-Tap Delay (PLAN.md step 46): up to <see cref="EffectParamNames.MultiTapCount"/> independent taps off
/// one delay line, each with its own enable / delay time / level / pan, summed into the output — rhythmic echo
/// patterns from a single instance, without stacking effects. There is no feedback path: the pattern is the
/// tap set itself (the Logic Delay Designer-style basic model). The input is summed to mono before the line
/// (a tap's pan places it in the stereo field regardless of source layout, constant-power like
/// <see cref="GainPanEffect"/>); wet left/right go to even/odd channels, a mono output takes their mean.
/// <see cref="EffectParamNames.Mix"/> = 0 is an exact pass-through that advances no state.
/// </summary>
public sealed class MultiTapDelayEffect : IAudioEffect
{
    private const double MaxDelaySeconds = DigitalDelayEffect.MaxDelaySeconds;

    private DelayLine _line = null!;
    private int _rate;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double mix = Math.Clamp(parameters.Get(EffectParamNames.Mix, 0.3), 0, 1);
        if (mix == 0)
            return; // fully dry — exact pass-through, no state to advance

        if (sampleRate != _rate)
        {
            _rate = sampleRate;
            _line = new DelayLine((int)(MaxDelaySeconds * sampleRate) + 2);
        }

        // Per-block tap tables (parameter names are precomputed constants — no per-block string building).
        Span<int> tapSamples = stackalloc int[EffectParamNames.MultiTapCount];
        Span<float> tapGainL = stackalloc float[EffectParamNames.MultiTapCount];
        Span<float> tapGainR = stackalloc float[EffectParamNames.MultiTapCount];
        int taps = 0;
        for (int i = 0; i < EffectParamNames.MultiTapCount; i++)
        {
            if (parameters.Get(EffectParamNames.TapEnable[i], i < 2 ? 1 : 0) < 0.5)
                continue;
            double timeMs = Math.Clamp(parameters.Get(EffectParamNames.TapTimeMs[i], 150.0 * (i + 1)), 1, MaxDelaySeconds * 1000);
            var level = (float)Math.Clamp(parameters.Get(EffectParamNames.TapLevel[i], 1.0 - i * 0.1), 0, 1);
            double pan = Math.Clamp(parameters.Get(EffectParamNames.TapPan[i], 0.0), -1, 1);
            double angle = (pan + 1) * Math.PI / 4; // constant-power pan law, like GainPanEffect
            tapSamples[taps] = Math.Max(1, (int)(timeMs / 1000.0 * sampleRate));
            tapGainL[taps] = level * (float)Math.Cos(angle);
            tapGainR[taps] = level * (float)Math.Sin(angle);
            taps++;
        }

        var wet = (float)mix;
        float dry = 1 - wet;

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            float input = 0;
            for (int ch = 0; ch < channels; ch++)
                input += interleaved[baseIndex + ch];
            input /= channels;

            float wetL = 0, wetR = 0;
            for (int t = 0; t < taps; t++)
            {
                float s = _line.Tap(tapSamples[t]);
                wetL += s * tapGainL[t];
                wetR += s * tapGainR[t];
            }
            _line.Push(input);

            if (channels == 1)
            {
                interleaved[baseIndex] = dry * interleaved[baseIndex] + wet * 0.5f * (wetL + wetR);
            }
            else
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    float w = (ch & 1) == 0 ? wetL : wetR;
                    interleaved[baseIndex + ch] = dry * interleaved[baseIndex + ch] + wet * w;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Reset() => _line?.Clear();
}
