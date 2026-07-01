using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// A Freeverb-style reverb (PLAN.md step 31): per channel, eight parallel damped feedback combs into four
/// series allpasses — the public-domain Schroeder/Moorer network every "free reverb" derives from, with the
/// standard 44.1 kHz tunings scaled to the project rate and the classic +23-sample stereo spread offsetting
/// each channel after the first. <see cref="EffectParamNames.RoomSize"/> sets comb feedback (tail length),
/// <see cref="EffectParamNames.Damping"/> the in-loop high-frequency loss, and <see cref="EffectParamNames.Mix"/>
/// the wet/dry blend (0 = exact pass-through). Delay lines allocate on first use / format change only; the
/// tail carries across buffers.
/// </summary>
public sealed class ReverbEffect : IAudioEffect
{
    // Freeverb tunings at 44.1 kHz.
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float FixedGain = 0.015f;   // input scaling into the comb bank
    private const float ScaleRoom = 0.28f;    // feedback = roomSize × ScaleRoom + OffsetRoom
    private const float OffsetRoom = 0.7f;
    private const float ScaleDamp = 0.4f;
    private const float AllpassFeedback = 0.5f;

    private sealed class Delay
    {
        public required float[] Buffer;
        public int Index;
        public float FilterStore; // combs only: the damping one-pole state
    }

    private Delay[][] _combs = [];    // [channel][comb]
    private Delay[][] _allpasses = []; // [channel][allpass]
    private int _rate, _channels;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double roomSize = Math.Clamp(parameters.Get(EffectParamNames.RoomSize, 0.5), 0, 1);
        double damping = Math.Clamp(parameters.Get(EffectParamNames.Damping, 0.5), 0, 1);
        double mix = Math.Clamp(parameters.Get(EffectParamNames.Mix, 0.3), 0, 1);
        if (mix == 0)
            return; // fully dry — exact pass-through, no state to advance

        if (sampleRate != _rate || channels != _channels)
            Allocate(sampleRate, channels);

        var feedback = (float)(roomSize * ScaleRoom + OffsetRoom);
        var damp = (float)(damping * ScaleDamp);
        var wet = (float)mix;
        float dry = 1 - wet;

        for (int ch = 0; ch < channels; ch++)
        {
            Delay[] combs = _combs[ch];
            Delay[] allpasses = _allpasses[ch];
            for (int f = 0; f < frames; f++)
            {
                int i = f * channels + ch;
                float input = interleaved[i];
                float scaled = input * FixedGain;

                float outSample = 0f;
                foreach (Delay comb in combs)
                {
                    float delayed = comb.Buffer[comb.Index];
                    outSample += delayed;
                    comb.FilterStore = delayed * (1 - damp) + comb.FilterStore * damp;
                    comb.Buffer[comb.Index] = scaled + comb.FilterStore * feedback;
                    if (++comb.Index >= comb.Buffer.Length)
                        comb.Index = 0;
                }

                foreach (Delay allpass in allpasses)
                {
                    float delayed = allpass.Buffer[allpass.Index];
                    allpass.Buffer[allpass.Index] = outSample + delayed * AllpassFeedback;
                    outSample = delayed - outSample;
                    if (++allpass.Index >= allpass.Buffer.Length)
                        allpass.Index = 0;
                }

                interleaved[i] = dry * input + wet * outSample;
            }
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        foreach (Delay[] set in _combs)
            foreach (Delay d in set)
            {
                d.Buffer.AsSpan().Clear();
                d.Index = 0;
                d.FilterStore = 0;
            }
        foreach (Delay[] set in _allpasses)
            foreach (Delay d in set)
            {
                d.Buffer.AsSpan().Clear();
                d.Index = 0;
            }
    }

    private void Allocate(int sampleRate, int channels)
    {
        _rate = sampleRate;
        _channels = channels;
        _combs = new Delay[channels][];
        _allpasses = new Delay[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            int spread = ch * StereoSpread;
            _combs[ch] = CombTunings.Select(t => NewDelay(t + spread, sampleRate)).ToArray();
            _allpasses[ch] = AllpassTunings.Select(t => NewDelay(t + spread, sampleRate)).ToArray();
        }
    }

    private static Delay NewDelay(int tuning44K, int sampleRate) =>
        new() { Buffer = new float[Math.Max(1, (int)((long)tuning44K * sampleRate / 44100))] };
}
