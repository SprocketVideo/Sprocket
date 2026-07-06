using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Shimmer Reverb (PLAN.md step 50): the "Creative Reverb ▸ shimmer" tier as its own dedicated effect — an
/// octave-shifted feedback path layered under a conventional reverb tail, producing the ethereal pitched-up
/// wash (an ambient/post/scoring tool, not a dialogue/room tool). The base tail reuses the
/// <see cref="ReverbEffect"/> Freeverb-style network (eight damped feedback combs into four series allpasses
/// per channel, stereo spread) with <see cref="EffectParamNames.Size"/> scaling the line lengths and
/// <see cref="EffectParamNames.Decay"/> (RT60-style seconds) setting the comb feedback. The wet tail is
/// averaged to mono, pitch-shifted up by <see cref="EffectParamNames.ShimmerInterval"/> semitones through a
/// granular (dual-tap crossfaded delay-line) shifter, and re-injected into the comb inputs at
/// <see cref="EffectParamNames.ShimmerAmount"/>, so the shimmer builds and sustains under continued feedback.
/// </summary>
/// <remarks>
/// <para><b>The feedback loop cannot run away, regardless of parameter combination</b> (the step's
/// correctness requirement): comb feedback maps from Decay as <c>0.001^(combSeconds/decay)</c> clamped to
/// <see cref="MaxCombFeedback"/> (&lt; 1 always), the re-injection gain is normalized by the comb bank's
/// frequency-averaged gain so full Shimmer targets a mean loop gain below unity-with-margin, and the shimmer
/// return additionally passes through <see cref="MathF.Tanh(float)"/> — a hard ±1 bound that keeps the
/// network BIBO-stable by construction (g &lt; 1 with a bounded input) even where aligned comb resonances
/// push the local loop gain past unity, as the deliberately near-sustain "Drone / Infinite" preset does.</para>
/// <para>All buffers are allocated once per (sample rate, channel count) at their maximum (Size = 1)
/// lengths, so parameter changes never allocate and steady state is allocation-free (§1, §19). The pitch
/// shifter's grain phase is a pure function of its own sample-by-sample advance (no RNG), so output is
/// deterministic run-to-run — freeze/export replay matches preview. <see cref="EffectParamNames.Mix"/> = 0
/// is an exact pass-through that advances no state, and Shimmer = 0 collapses to the plain base tail (the
/// pitch path contributes nothing), matching <see cref="ReverbEffect"/>'s conventions.</para>
/// </remarks>
public sealed class ShimmerReverbEffect : IAudioEffect
{
    // Freeverb tunings at 44.1 kHz (same network as ReverbEffect).
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float FixedGain = 0.015f;   // input scaling into the comb bank
    private const float ScaleDamp = 0.5f;     // Damping → in-loop one-pole coefficient
    private const float AllpassFeedback = 0.5f;
    private const float MaxCombFeedback = 0.98f; // Decay ceiling: the combs alone can never reach unity
    private const float MaxLoopGain = 0.9f;      // full Shimmer targets this mean-power loop gain (< 1)
    private const double MinSizeFactor = 0.4;    // Size 0 is still a small tank, not zero-length lines
    private const double GrainWindowSeconds = 0.075; // granular shifter splice window (~75 ms, the classic size)
    private const double ReturnHighPassHz = 150.0;   // high-pass on the shimmer return: keeps intermodulation
                                                     // rumble out of the feedback loop (standard shimmer practice)

    private sealed class Delay
    {
        public required float[] Buffer;
        public int Index;
        public float FilterStore; // combs only: the damping one-pole state
    }

    private Delay[][] _combs = [];     // [channel][comb]
    private Delay[][] _allpasses = []; // [channel][allpass]
    private int[] _combLengths = [];   // [channel * 8 + comb], recomputed per block from Size
    private float[] _combFeedbacks = []; // per comb, recomputed per block from Decay
    private int[] _allpassLengths = [];  // [channel * 4 + allpass]
    private DelayLine _grain = null!;    // the pitch shifter's line, written with the mono wet tail
    private int _grainWindow;
    private double _grainPhase;          // grain read-tap phase in [0, 1) — the shifter's only clock
    private float _shimmerOut;           // previous sample's shifter output, re-injected this sample
    private float _returnLowState;       // one-pole low tracker backing the shimmer-return high-pass
    private int _rate, _channels;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double mix = Math.Clamp(parameters.Get(EffectParamNames.Mix, 0.3), 0, 1);
        if (mix == 0)
            return; // fully dry — exact pass-through, no state to advance

        if (sampleRate != _rate || channels != _channels)
            Allocate(sampleRate, channels);

        double size = Math.Clamp(parameters.Get(EffectParamNames.Size, 0.5), 0, 1);
        double sizeFactor = MinSizeFactor + (1 - MinSizeFactor) * size;
        double decaySeconds = Math.Max(0.05, parameters.Get(EffectParamNames.Decay, 4.0));
        double damping = Math.Clamp(parameters.Get(EffectParamNames.Damping, 0.3), 0, 1);
        double shimmer = Math.Clamp(parameters.Get(EffectParamNames.ShimmerAmount, 0.5), 0, 1);
        double interval = Math.Clamp(parameters.Get(EffectParamNames.ShimmerInterval, 12.0), 1, 12);

        // Per-block line lengths (Size-scaled inside the max-size buffers) and per-comb feedback from Decay:
        // g = 0.001^(combSeconds/decay) — the RT60 mapping, < 1 always, clamped for the shimmer margin below.
        float maxFeedback = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int spread = ch * StereoSpread;
            for (int c = 0; c < CombTunings.Length; c++)
            {
                int length = ScaledLength(CombTunings[c] + spread, sampleRate, sizeFactor);
                _combLengths[ch * CombTunings.Length + c] = length;
                var g = (float)Math.Min(MaxCombFeedback, Math.Pow(0.001, length / (double)sampleRate / decaySeconds));
                _combFeedbacks[ch * CombTunings.Length + c] = g;
                maxFeedback = Math.Max(maxFeedback, g);
            }
            for (int a = 0; a < AllpassTunings.Length; a++)
                _allpassLengths[ch * AllpassTunings.Length + a] = ScaledLength(AllpassTunings[a] + spread, sampleRate, sizeFactor);
        }

        // Shimmer re-injection gain, normalized so full Shimmer targets MaxLoopGain around the loop in the
        // mean-power sense: shifter out → injection → FixedGain → comb bank, whose frequency-averaged
        // amplitude gain per comb is 1/√(1 − g²) (the comb lengths differ, so their narrow resonant peaks
        // never align — the worst-case Σ 1/(1 − g) bound would leave the shimmer inaudible). At an aligned
        // resonance the local loop gain can exceed unity, which is what lets a full-Shimmer wash sustain —
        // the Tanh below is the absolute ±1 bound that keeps even that case BIBO-stable.
        var injection = (float)(shimmer * MaxLoopGain * Math.Sqrt(1 - (double)maxFeedback * maxFeedback)
            / (FixedGain * CombTunings.Length));

        var damp = (float)(damping * ScaleDamp);
        var wet = (float)mix;
        float dry = 1 - wet;

        // Granular shifter setup: two read taps advancing at 2^(st/12) against the write pointer, phase-offset
        // by half a window and sin-crossfaded so each splice lands at a zero-gain point.
        double ratio = Math.Pow(2, interval / 12.0);
        double phaseInc = (ratio - 1) / _grainWindow;
        var hpPole = (float)(1 - Math.Exp(-2 * Math.PI * ReturnHighPassHz / sampleRate));

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;

            // The pitched-up return from the previous sample's tail: high-passed so tanh intermodulation
            // rumble can't accumulate in the loop (comb gain peaks near DC), then bounded by construction.
            _returnLowState += hpPole * (_shimmerOut - _returnLowState);
            float shimmerReturn = MathF.Tanh((_shimmerOut - _returnLowState) * injection);

            float wetMono = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                float input = interleaved[baseIndex + ch];
                float scaled = (input + shimmerReturn) * FixedGain;

                float outSample = 0f;
                Delay[] combs = _combs[ch];
                for (int c = 0; c < combs.Length; c++)
                {
                    Delay comb = combs[c];
                    int length = _combLengths[ch * CombTunings.Length + c];
                    float feedback = _combFeedbacks[ch * CombTunings.Length + c];
                    if (comb.Index >= length)
                        comb.Index = 0;
                    float delayed = comb.Buffer[comb.Index];
                    outSample += delayed;
                    comb.FilterStore = delayed * (1 - damp) + comb.FilterStore * damp;
                    comb.Buffer[comb.Index] = scaled + comb.FilterStore * feedback;
                    if (++comb.Index >= length)
                        comb.Index = 0;
                }

                Delay[] allpasses = _allpasses[ch];
                for (int a = 0; a < allpasses.Length; a++)
                {
                    Delay allpass = allpasses[a];
                    int length = _allpassLengths[ch * AllpassTunings.Length + a];
                    if (allpass.Index >= length)
                        allpass.Index = 0;
                    float delayed = allpass.Buffer[allpass.Index];
                    allpass.Buffer[allpass.Index] = outSample + delayed * AllpassFeedback;
                    outSample = delayed - outSample;
                    if (++allpass.Index >= length)
                        allpass.Index = 0;
                }

                wetMono += outSample;
                interleaved[baseIndex + ch] = dry * input + wet * outSample;
            }
            wetMono /= channels;

            // Pitch-shift the mono tail: tap before push (a tap of d is the sample pushed d pushes ago).
            double p1 = _grainPhase;
            double p2 = p1 + 0.5;
            if (p2 >= 1)
                p2 -= 1;
            float tap1 = _grain.TapFrac(1 + (_grainWindow - 2) * (1 - p1)) * (float)Math.Sin(Math.PI * p1);
            float tap2 = _grain.TapFrac(1 + (_grainWindow - 2) * (1 - p2)) * (float)Math.Sin(Math.PI * p2);
            _grain.Push(wetMono);
            _shimmerOut = tap1 + tap2;
            _grainPhase += phaseInc;
            if (_grainPhase >= 1)
                _grainPhase -= 1;
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
        _grain?.Clear();
        _grainPhase = 0;
        _shimmerOut = 0;
        _returnLowState = 0;
    }

    private static int ScaledLength(int tuning44K, int sampleRate, double sizeFactor) =>
        Math.Max(1, (int)((long)tuning44K * sampleRate / 44100 * sizeFactor));

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
        _combLengths = new int[channels * CombTunings.Length];
        _combFeedbacks = new float[channels * CombTunings.Length];
        _allpassLengths = new int[channels * AllpassTunings.Length];
        _grainWindow = Math.Max(4, (int)(GrainWindowSeconds * sampleRate));
        _grain = new DelayLine(_grainWindow + 2);
        _grainPhase = 0;
        _shimmerOut = 0;
        _returnLowState = 0;
    }

    private static Delay NewDelay(int tuning44K, int sampleRate) =>
        new() { Buffer = new float[Math.Max(1, (int)((long)tuning44K * sampleRate / 44100))] };
}
