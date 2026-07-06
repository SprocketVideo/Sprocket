using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Stereo Delay (PLAN.md step 46): the classic dual-mono "ping-pong-capable" stereo delay — independent
/// left/right delay times over two delay lines with a shared feedback amount, plus a <b>Ping Pong</b> mode
/// (<see cref="EffectParamNames.PingPong"/>) that cross-feeds each channel's repeats into the opposite
/// channel by <see cref="EffectParamNames.CrossFeed"/> (1 = the classic full bounce), matching the
/// Ableton/Logic ping-pong convention. With Ping Pong off the two sides are fully independent mono delays.
/// Even output channels are "left", odd "right" (a mono stream feeds and takes both sides equally, like
/// <see cref="StudioReverbEffect"/>'s channel mapping). <see cref="EffectParamNames.Mix"/> = 0 is an exact
/// pass-through that advances no state.
/// </summary>
public sealed class StereoDelayEffect : IAudioEffect
{
    private const double MaxDelaySeconds = DigitalDelayEffect.MaxDelaySeconds;

    private DelayLine _left = null!, _right = null!;
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
            int capacity = (int)(MaxDelaySeconds * sampleRate) + 2;
            _left = new DelayLine(capacity);
            _right = new DelayLine(capacity);
        }

        double leftMs = Math.Clamp(parameters.Get(EffectParamNames.LeftTimeMs, 375.0), 1, MaxDelaySeconds * 1000);
        double rightMs = Math.Clamp(parameters.Get(EffectParamNames.RightTimeMs, 500.0), 1, MaxDelaySeconds * 1000);
        int leftSamples = Math.Max(1, (int)(leftMs / 1000.0 * sampleRate));
        int rightSamples = Math.Max(1, (int)(rightMs / 1000.0 * sampleRate));
        float feedback = Math.Min(
            (float)Math.Clamp(parameters.Get(EffectParamNames.Feedback, 0.35), 0, 1), DigitalDelayEffect.MaxFeedback);
        bool pingPong = parameters.Get(EffectParamNames.PingPong, 0.0) >= 0.5;
        var cross = pingPong ? (float)Math.Clamp(parameters.Get(EffectParamNames.CrossFeed, 1.0), 0, 1) : 0f;
        float own = 1 - cross;
        var wet = (float)mix;
        float dry = 1 - wet;

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            // Even channels feed/take the left side, odd the right; mono feeds both equally.
            float inL = 0, inR = 0;
            int countL = 0, countR = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                if ((ch & 1) == 0) { inL += interleaved[baseIndex + ch]; countL++; }
                else { inR += interleaved[baseIndex + ch]; countR++; }
            }
            inL /= countL;
            inR = countR > 0 ? inR / countR : inL;

            float delayedL = _left.Tap(leftSamples);
            float delayedR = _right.Tap(rightSamples);
            _left.Push(inL + feedback * (own * delayedL + cross * delayedR));
            _right.Push(inR + feedback * (own * delayedR + cross * delayedL));

            if (channels == 1)
            {
                interleaved[baseIndex] = dry * interleaved[baseIndex] + wet * 0.5f * (delayedL + delayedR);
            }
            else
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    float w = (ch & 1) == 0 ? delayedL : delayedR;
                    interleaved[baseIndex + ch] = dry * interleaved[baseIndex + ch] + wet * w;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        _left?.Clear();
        _right?.Clear();
    }
}
