using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Digital Delay (PLAN.md step 46): the clean baseline feedback delay — per channel, one sample-accurate
/// ring-buffer delay line with a feedback loop, and a one-pole high-cut in the feedback path (the standard
/// "analog-ish" damping tone control) so repeats can darken with each pass. At the descriptor's 20 kHz
/// high-cut ceiling the filter is bypassed entirely, making a repeat a bit-clean copy.
/// <see cref="EffectParamNames.Mix"/> = 0 is an exact pass-through that advances no state, matching
/// <see cref="ReverbEffect"/>. Delay lines allocate on first use / format change only.
/// </summary>
public sealed class DigitalDelayEffect : IAudioEffect
{
    /// <summary>Descriptor ceiling for <see cref="EffectParamNames.DelayMs"/>; sizes the lines once.</summary>
    internal const double MaxDelaySeconds = 2.0;

    /// <summary>Feedback ceiling: descriptor values up to 1.0 clamp here so the loop always decays.</summary>
    internal const float MaxFeedback = 0.98f;

    private DelayLine[] _lines = [];
    private float[] _highCutStates = [];
    private int _rate, _channels;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double mix = Math.Clamp(parameters.Get(EffectParamNames.Mix, 0.3), 0, 1);
        if (mix == 0)
            return; // fully dry — exact pass-through, no state to advance

        if (sampleRate != _rate || channels != _channels)
            Allocate(sampleRate, channels);

        double delayMs = Math.Clamp(parameters.Get(EffectParamNames.DelayMs, 500.0), 1, MaxDelaySeconds * 1000);
        int delaySamples = Math.Max(1, (int)(delayMs / 1000.0 * sampleRate));
        float feedback = Math.Min((float)Math.Clamp(parameters.Get(EffectParamNames.Feedback, 0.35), 0, 1), MaxFeedback);
        double highCutHz = Math.Clamp(parameters.Get(EffectParamNames.HighCutHz, 8000.0), 200, 20000);
        bool filter = highCutHz < 20000; // at the ceiling the repeat stays bit-clean
        var pole = (float)(1 - Math.Exp(-2 * Math.PI * highCutHz / sampleRate));
        var wet = (float)mix;
        float dry = 1 - wet;

        for (int ch = 0; ch < channels; ch++)
        {
            DelayLine line = _lines[ch];
            float lowPass = _highCutStates[ch];
            for (int f = 0; f < frames; f++)
            {
                int i = f * channels + ch;
                float input = interleaved[i];
                float delayed = line.Tap(delaySamples);
                float recirculated = delayed;
                if (filter)
                {
                    lowPass += pole * (delayed - lowPass);
                    recirculated = lowPass;
                }
                line.Push(input + feedback * recirculated);
                interleaved[i] = dry * input + wet * delayed;
            }
            _highCutStates[ch] = lowPass;
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        foreach (DelayLine line in _lines)
            line.Clear();
        _highCutStates.AsSpan().Clear();
    }

    private void Allocate(int sampleRate, int channels)
    {
        _rate = sampleRate;
        _channels = channels;
        int capacity = (int)(MaxDelaySeconds * sampleRate) + 2;
        _lines = new DelayLine[channels];
        for (int ch = 0; ch < channels; ch++)
            _lines[ch] = new DelayLine(capacity);
        _highCutStates = new float[channels];
    }
}
