using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Tape Delay (PLAN.md step 46): the same feedback-delay core as <see cref="DigitalDelayEffect"/> plus the
/// tape-emulation coloration the name implies — soft saturation (<see cref="EffectParamNames.Drive"/>) and a
/// fixed gentle low-pass in the feedback path so the tail darkens like successive tape generations, and
/// wow &amp; flutter: the read tap swings on two deterministic sine LFOs (a slow "wow" at
/// <see cref="EffectParamNames.WowFlutterRateHz"/> and a smaller, faster "flutter" at a fixed multiple above
/// it). The LFOs are plain functions of a sample counter — no per-instance RNG — so renders are reproducible
/// run-to-run and export matches preview. <see cref="EffectParamNames.Mix"/> = 0 is an exact pass-through.
/// </summary>
public sealed class TapeDelayEffect : IAudioEffect
{
    private const double MaxDelaySeconds = DigitalDelayEffect.MaxDelaySeconds;
    private const double RepeatLowPassHz = 5000.0;  // fixed darkening per generation
    private const double MaxWowMs = 4.0;            // wow excursion at full depth
    private const double FlutterRateMultiple = 6.3; // flutter sits well above the wow rate
    private const double FlutterDepthRatio = 0.125; // flutter is a small fraction of the wow swing

    private DelayLine[] _lines = [];
    private float[] _lowPassStates = [];
    private long _sample; // deterministic LFO clock (advances once per frame, shared by all channels)
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
        double delaySamples = Math.Max(1, delayMs / 1000.0 * sampleRate);
        float feedback = Math.Min(
            (float)Math.Clamp(parameters.Get(EffectParamNames.Feedback, 0.4), 0, 1), DigitalDelayEffect.MaxFeedback);
        double depth = Math.Clamp(parameters.Get(EffectParamNames.WowFlutterDepth, 0.25), 0, 1);
        double wowSwing = depth * MaxWowMs / 1000.0 * sampleRate;
        double flutterSwing = wowSwing * FlutterDepthRatio;
        double wowRate = Math.Clamp(parameters.Get(EffectParamNames.WowFlutterRateHz, 1.0), 0.05, 20);
        double wowStep = 2 * Math.PI * wowRate / sampleRate;
        double flutterStep = wowStep * FlutterRateMultiple;
        double drive = Math.Clamp(parameters.Get(EffectParamNames.Drive, 0.3), 0, 1);
        var k = (float)(1 + drive * 4); // tanh(kx)/k: unity small-signal gain, softer knees as drive rises
        var pole = (float)(1 - Math.Exp(-2 * Math.PI * RepeatLowPassHz / sampleRate));
        var wet = (float)mix;
        float dry = 1 - wet;

        for (int f = 0; f < frames; f++)
        {
            // One tape transport: both channels share the same instantaneous speed (delay) modulation.
            double lfo = wowSwing * (0.5 + 0.5 * Math.Sin(_sample * wowStep))
                       + flutterSwing * (0.5 + 0.5 * Math.Sin(_sample * flutterStep));
            double tap = Math.Max(1.0, delaySamples - lfo);
            _sample++;

            int baseIndex = f * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                DelayLine line = _lines[ch];
                float input = interleaved[baseIndex + ch];
                float delayed = line.TapFrac(tap);

                // Feedback path: darken (fixed low-pass), then soft-saturate — each repeat is one more "generation".
                float lowPass = _lowPassStates[ch] + pole * (delayed - _lowPassStates[ch]);
                _lowPassStates[ch] = lowPass;
                var recirculated = (float)(Math.Tanh(k * lowPass) / k);

                line.Push(input + feedback * recirculated);
                interleaved[baseIndex + ch] = dry * input + wet * delayed;
            }
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        foreach (DelayLine line in _lines)
            line.Clear();
        _lowPassStates.AsSpan().Clear();
        _sample = 0;
    }

    private void Allocate(int sampleRate, int channels)
    {
        _rate = sampleRate;
        _channels = channels;
        // The LFO only shortens the tap (delaySamples − lfo ≥ 1), so max-delay capacity plus interpolation
        // margin covers every modulated read.
        int capacity = (int)(MaxDelaySeconds * sampleRate) + 4;
        _lines = new DelayLine[channels];
        for (int ch = 0; ch < channels; ch++)
            _lines[ch] = new DelayLine(capacity);
        _lowPassStates = new float[channels];
        _sample = 0;
    }
}
