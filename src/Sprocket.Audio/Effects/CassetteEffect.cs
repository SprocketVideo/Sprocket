using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Cassette (plan/features/toy-cassette-camera.md, phase 4): the sound of a cheap cassette recorder — the audio
/// half of the Toy Cassette Camera look, and useful alone for any "recorded on cassette" sound. One stage, run
/// frame by frame in record-then-playback order:
/// <list type="number">
/// <item><b>Mono</b> (<see cref="EffectParamNames.Mono"/>): the channels fold to their average, the chain runs
/// once, and the result is written to every channel (off = each channel runs its own chain).</item>
/// <item><b>AGC</b> (<see cref="EffectParamNames.AgcAmount"/>, <see cref="EffectParamNames.ReleaseMs"/>): a
/// camcorder-style automatic gain control — a one-pole peak envelope follower (the
/// <see cref="CompressorEffect"/> pattern, stereo-linked) with a fixed fast attack and a slow release, pulling
/// the gain toward a target level (boost ≤ 24 dB, cut ≤ 30 dB, scaled by the amount). After a loud sound the
/// gain stays down and then swells back over the release — the audible pumping/breathing, which also drags the
/// source's own noise floor up in quiet passages.</item>
/// <item><b>Saturation</b> (<see cref="EffectParamNames.Drive"/>): Tape Delay's record-head soft clip,
/// <c>tanh(k·x)/k</c> (unity small-signal gain, softer knee as drive rises).</item>
/// <item><b>Hiss</b> (<see cref="EffectParamNames.HissDb"/>): white noise from a hash of the frame counter (and the
/// channel, when not mono) — no RNG state, rewound by <see cref="Reset"/> — added <em>after</em> the AGC (tape
/// hiss is laid down by the tape, not the microphone) and scaled for the band-limit's noise bandwidth, so the
/// output hiss sits at the set dBFS RMS whatever the cutoffs.</item>
/// <item><b>Band-limit</b> (<see cref="EffectParamNames.LowCutHz"/>, <see cref="EffectParamNames.HighCutHz"/>):
/// 24 dB/oct Butterworth high- and low-pass, each two cascaded RBJ <see cref="BiquadBand"/> sections, shaping
/// signal and hiss alike.</item>
/// <item><b>Wow &amp; flutter</b> (<see cref="EffectParamNames.WowFlutterDepth"/>,
/// <see cref="EffectParamNames.WowFlutterRateHz"/>): a short <see cref="DelayLine"/> read through
/// <see cref="DelayLine.TapFrac"/> at the shared <see cref="TapeWowFlutter"/> excursion, i.e. the playback
/// transport's speed wobble as pitch; the same LFO also dips the level slightly (up to
/// <see cref="LevelWobble"/> of the gain at full depth) like a worn tape losing head contact.</item>
/// </list>
/// The modulated read sits <em>after</em> the current write (delay = excursion ≥ 0), so at rest the wet signal
/// is sample-aligned with the input: the effect has <b>no fixed latency</b> (the sub-millisecond, depth-dependent
/// average wobble delay is part of the sound, not a host-compensable offset), hence no
/// <see cref="IAudioEffectTail"/>. <see cref="EffectParamNames.Mix"/> = 0 is an exact pass-through. Steady-state
/// processing allocates nothing: delay lines and filter state are allocated on a format change only (§1, §19).
/// </summary>
public sealed class CassetteEffect : IAudioEffect
{
    private const double MaxWowMs = 2.0;       // wow excursion at full depth (≈ 0.6 % pitch at 1 Hz, plus flutter)
    private const double AgcTargetDb = -16.0;  // the level the AGC pulls toward (peak envelope)
    private const double AgcMaxBoostDb = 24.0; // how far it can raise a quiet passage at full amount
    private const double AgcMaxCutDb = 30.0;   // how far it can pull a loud one down
    private const double AgcAttackMs = 5.0;    // fixed fast attack — camcorder AGCs clamp quickly
    private const double AgcFloor = 1e-6;      // envelope floor (−120 dBFS) so silence yields the max boost
    private const double HissOffDb = -90.0;    // the Hiss minimum: no hiss at all
    private const double NoiseRms = 0.40824829046386301; // RMS of the sum of two uniforms in [−0.5, 0.5]·2 = √(1/6)
    private const double ButterworthNoiseBandwidth = 1.0262; // 4th-order Butterworth noise bandwidth / corner

    /// <summary>The fraction of the gain the wow LFO dips at full depth (≈ −3 dB at depth 1).</summary>
    internal const double LevelWobble = 0.3;

    private BiquadBand _hp1, _hp2, _lp1, _lp2;
    private TapeWowFlutter _wowFlutter;
    private DelayLine[] _lines = [];
    private double _envelope; // AGC peak envelope (linear), shared by all channels
    private long _frame;      // hiss clock (advances once per frame)
    private double _maxExcursion;
    private int _rate, _channels;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double mix = Math.Clamp(parameters.Get(EffectParamNames.Mix, 1.0), 0, 1);
        if (mix == 0)
            return; // fully dry — exact pass-through, no state to advance

        bool formatChanged = sampleRate != _rate || channels != _channels;
        if (formatChanged)
            Allocate(sampleRate, channels);

        bool mono = parameters.Get(EffectParamNames.Mono, 1.0) >= 0.5;
        int lanes = mono ? 1 : channels;

        // Band-limit: two cascaded Butterworth sections per edge (24 dB/oct). Low cut stays below the high cut.
        double highCut = Math.Clamp(parameters.Get(EffectParamNames.HighCutHz, 5000.0), 20, sampleRate / 2.0 - 1);
        double lowCut = Math.Clamp(parameters.Get(EffectParamNames.LowCutHz, 100.0), 1, highCut * 0.5);
        _hp1.ConfigureHighPass(lowCut, BiquadBand.ButterworthQ1, sampleRate);
        _hp2.ConfigureHighPass(lowCut, BiquadBand.ButterworthQ2, sampleRate);
        _lp1.ConfigureLowPass(highCut, BiquadBand.ButterworthQ1, sampleRate);
        _lp2.ConfigureLowPass(highCut, BiquadBand.ButterworthQ2, sampleRate);

        // Hiss: scale white noise so that, after the band-limit, its RMS lands at the set dBFS.
        double hissDb = Math.Clamp(parameters.Get(EffectParamNames.HissDb, -42.0), HissOffDb, 0);
        double bandFraction = Math.Clamp(
            ButterworthNoiseBandwidth * (highCut - lowCut) / (sampleRate / 2.0), 1e-3, 1.0);
        float hissGain = hissDb <= HissOffDb
            ? 0f
            : (float)(Math.Pow(10, hissDb / 20.0) / (NoiseRms * Math.Sqrt(bandFraction)));

        double depth = Math.Clamp(parameters.Get(EffectParamNames.WowFlutterDepth, 0.25), 0, 1);
        double wowRate = Math.Clamp(parameters.Get(EffectParamNames.WowFlutterRateHz, 0.9), 0.05, 20);
        _wowFlutter.Configure(depth, MaxWowMs, wowRate, sampleRate);
        double levelPerExcursion = LevelWobble / _maxExcursion; // excursion / max ∈ [0, depth] → level dip

        double drive = Math.Clamp(parameters.Get(EffectParamNames.Drive, 0.3), 0, 1);
        var k = (float)(1 + drive * 4); // Tape Delay's soft clip: tanh(kx)/k

        double agcAmount = Math.Clamp(parameters.Get(EffectParamNames.AgcAmount, 0.5), 0, 1);
        double releaseMs = Math.Clamp(parameters.Get(EffectParamNames.ReleaseMs, 600.0), 1, 10000);
        double attack = Math.Exp(-1.0 / (AgcAttackMs / 1000.0 * sampleRate));
        double release = Math.Exp(-1.0 / (releaseMs / 1000.0 * sampleRate));

        var wet = (float)mix;
        float dry = 1 - wet;
        float inverseChannels = 1f / channels;

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;

            // AGC detector: stereo-linked peak (mono: the folded sample's magnitude), one-pole attack/release.
            float sum = 0f, peak = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                float s = interleaved[baseIndex + ch];
                sum += s;
                float abs = Math.Abs(s);
                if (abs > peak)
                    peak = abs;
            }
            float monoIn = sum * inverseChannels;
            double detect = mono ? Math.Abs(monoIn) : peak;
            double coeff = detect > _envelope ? attack : release;
            _envelope = coeff * _envelope + (1 - coeff) * detect;

            double gain = 1.0;
            if (agcAmount > 0)
            {
                double envDb = 20 * Math.Log10(Math.Max(_envelope, AgcFloor));
                double correctionDb = Math.Clamp(AgcTargetDb - envDb, -AgcMaxCutDb, AgcMaxBoostDb) * agcAmount;
                gain = Math.Pow(10, correctionDb / 20.0);
            }

            double excursion = _wowFlutter.Next();
            var level = (float)(1.0 - excursion * levelPerExcursion);
            double tap = 1.0 + excursion; // read after this frame's push: tap 1 = the current sample
            var recordGain = (float)gain;

            for (int lane = 0; lane < lanes; lane++)
            {
                float x = (mono ? monoIn : interleaved[baseIndex + lane]) * recordGain;
                x = (float)(Math.Tanh(k * x) / k);
                if (hissGain != 0f)
                    x += hissGain * Hiss(_frame, lane);

                x = _hp1.Step(x, lane);
                x = _hp2.Step(x, lane);
                x = _lp1.Step(x, lane);
                x = _lp2.Step(x, lane);

                DelayLine line = _lines[lane];
                line.Push(x);
                float played = line.TapFrac(tap) * level;

                if (mono)
                {
                    for (int ch = 0; ch < channels; ch++)
                        interleaved[baseIndex + ch] = dry * interleaved[baseIndex + ch] + wet * played;
                }
                else
                {
                    interleaved[baseIndex + lane] = dry * interleaved[baseIndex + lane] + wet * played;
                }
            }
            _frame++;
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        foreach (DelayLine line in _lines)
            line.Clear();
        _hp1.ClearState();
        _hp2.ClearState();
        _lp1.ClearState();
        _lp2.ClearState();
        _wowFlutter.Reset();
        _envelope = 0;
        _frame = 0;
    }

    private void Allocate(int sampleRate, int channels)
    {
        _rate = sampleRate;
        _channels = channels;
        _maxExcursion = TapeWowFlutter.MaxExcursion(MaxWowMs, sampleRate);
        // The read is 1 + excursion behind the write; plus interpolation margin.
        int capacity = (int)Math.Ceiling(_maxExcursion) + 4;
        _lines = new DelayLine[channels];
        for (int ch = 0; ch < channels; ch++)
            _lines[ch] = new DelayLine(capacity);
        _hp1.EnsureState(channels, formatChanged: true);
        _hp2.EnsureState(channels, formatChanged: true);
        _lp1.EnsureState(channels, formatChanged: true);
        _lp2.EnsureState(channels, formatChanged: true);
        _wowFlutter.Reset();
        _envelope = 0;
        _frame = 0;
    }

    /// <summary>
    /// Deterministic white noise for frame <paramref name="frame"/> of <paramref name="lane"/>: a lowbias32 integer
    /// hash of the counter (and lane, so unlinked channels decorrelate), split into two 16-bit uniforms whose sum
    /// is a triangular sample in (−1, 1) with RMS √(1/6). A pure function of its inputs — no PRNG state to drift.
    /// </summary>
    internal static float Hiss(long frame, int lane)
    {
        var x = (uint)frame ^ (uint)(frame >> 32) * 0x27d4eb2dU ^ (uint)lane * 0x9e3779b9U;
        x ^= x >> 16;
        x *= 0x7feb352dU;
        x ^= x >> 15;
        x *= 0x846ca68bU;
        x ^= x >> 16;
        const float scale = 1f / 65535f;
        float a = (x & 0xFFFF) * scale;
        float b = (x >> 16) * scale;
        return a + b - 1f;
    }
}
