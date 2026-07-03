using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Studio Reverb (PLAN.md step 41): the realtime high-quality algorithmic tier next to the Freeverb-style
/// <see cref="ReverbEffect"/> ("Reverb (Lite)"). The late tail is Dattorro's figure-8 plate tank (JAES 1997,
/// "Effect Design Part 1"): a predelay line into four input-diffusion allpasses, then two cross-coupled tank
/// branches — each a <em>modulated</em> decay allpass → delay → high/low damping → decay gain → second
/// allpass → delay — with the wet stereo pair pulled from Dattorro's decorrelated output-tap table. A small
/// tapped early-reflection line supplies the room's first echoes; <see cref="EffectParamNames.EarlyLate"/>
/// blends the two, and <see cref="EffectParamNames.Width"/> is a mid/side scale on the wet pair.
/// </summary>
/// <remarks>
/// <para><see cref="EffectParamNames.Size"/> scales every tank/tap length inside buffers allocated once for
/// the maximum size, so parameter changes (including size) never allocate — steady state is allocation-free
/// (§1, §19). <see cref="EffectParamNames.Decay"/> is RT60-style seconds, converted to the per-loop gain
/// <c>0.001^(loopSeconds/decay)</c>, always &lt; 1 so the tank cannot run away. The tank LFOs are plain
/// deterministic sine functions of the sample counter (no RNG), so renders are reproducible run-to-run and
/// export matches preview.</para>
/// <para>The tank is mono-in / stereo-out: the input block is averaged to mono, and wet left/right go to
/// even/odd output channels (a mono output takes their mean). <see cref="EffectParamNames.Mix"/> = 0 is an
/// exact pass-through that advances no state, matching <see cref="ReverbEffect"/>.</para>
/// </remarks>
public sealed class StudioReverbEffect : IAudioEffect
{
    // Dattorro's reference tunings (sample counts at 29 761 Hz). Left/right tank branches.
    private const double ReferenceRate = 29761.0;
    private static readonly int[] InputAllpassTunings = [142, 107, 379, 277];
    private const int LeftAp1 = 672, LeftDelay1 = 4453, LeftAp2 = 1800, LeftDelay2 = 3720;
    private const int RightAp1 = 908, RightDelay1 = 4217, RightAp2 = 2656, RightDelay2 = 3163;
    private const int MaxExcursion = 32;          // modulated-allpass depth at full ModDepth, reference rate
    private const float TankTapGain = 0.6f;       // Dattorro's output-tap weight
    private const double LowDampCornerHz = 180.0; // in-loop low-shelf cut corner for LowDamp

    // Early reflections: tap times (ms) and gains, alternating between the L and R output sets.
    private static readonly double[] EarlyTapMs = [11.3, 17.5, 23.1, 31.7, 40.9, 53.4, 66.2, 79.8];
    private static readonly float[] EarlyTapGain = [0.85f, 0.7f, 0.6f, 0.5f, 0.4f, 0.32f, 0.25f, 0.2f];
    private const float EarlyGain = 0.6f;
    private const double MaxPreDelaySeconds = 0.2; // matches the descriptor's 200 ms ceiling
    private const double MinSizeFactor = 0.25;     // Size 0 is still a small room, not a zero-length tank

    /// <summary>A circular delay line sized once for the maximum (Size = 1) tap it must serve.</summary>
    private sealed class Line
    {
        public required float[] Buffer;
        public int Write;

        public void Push(float value)
        {
            Buffer[Write] = value;
            if (++Write >= Buffer.Length)
                Write = 0;
        }

        /// <summary>The sample pushed <paramref name="delay"/> pushes ago (call before this sample's push).</summary>
        public float Tap(int delay)
        {
            int i = Write - delay;
            if (i < 0)
                i += Buffer.Length;
            return Buffer[i];
        }

        /// <summary>Linear-interpolated fractional tap for the modulated allpasses.</summary>
        public float TapFrac(double delay)
        {
            var whole = (int)delay;
            var frac = (float)(delay - whole);
            int i0 = Write - whole;
            if (i0 < 0)
                i0 += Buffer.Length;
            int i1 = i0 - 1;
            if (i1 < 0)
                i1 += Buffer.Length;
            return Buffer[i0] + (Buffer[i1] - Buffer[i0]) * frac;
        }

        public void Clear()
        {
            Buffer.AsSpan().Clear();
            Write = 0;
        }
    }

    private Line _preDelay = null!;
    private Line _early = null!;
    private Line[] _inputAllpasses = [];
    private Line _lAp1 = null!, _lDelay1 = null!, _lAp2 = null!, _lDelay2 = null!;
    private Line _rAp1 = null!, _rDelay1 = null!, _rAp2 = null!, _rDelay2 = null!;
    private float _lHighDampState, _rHighDampState; // one-pole low-pass (HighDamp) per branch
    private float _lLowDampState, _rLowDampState;   // one-pole low tracker (LowDamp shelf cut) per branch
    private long _sample;                            // deterministic LFO clock
    private int _rate;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double mix = Math.Clamp(parameters.Get(EffectParamNames.Mix, 0.3), 0, 1);
        if (mix == 0)
            return; // fully dry — exact pass-through, no state to advance

        if (sampleRate != _rate)
            Allocate(sampleRate);

        double scale = sampleRate / ReferenceRate;
        double size = Math.Clamp(parameters.Get(EffectParamNames.Size, 0.5), 0, 1);
        double sizeFactor = MinSizeFactor + (1 - MinSizeFactor) * size;
        double lengthScale = scale * sizeFactor;

        double diffusion = Math.Clamp(parameters.Get(EffectParamNames.Diffusion, 0.7), 0, 1);
        // Allpass coefficients hit Dattorro's nominal values at the default Diffusion (0.7) and stay < 1.
        var gInput1 = (float)Math.Min(0.90, 0.75 * diffusion / 0.7);
        var gInput2 = (float)Math.Min(0.85, 0.625 * diffusion / 0.7);
        var gTank1 = (float)Math.Min(0.85, 0.70 * diffusion / 0.7);
        var gTank2 = (float)Math.Min(0.80, 0.50 * diffusion / 0.7);

        // Effective (Size-scaled) lengths, all within the max-size buffers.
        int lAp1 = Scaled(LeftAp1, lengthScale);
        int lDelay1 = Scaled(LeftDelay1, lengthScale);
        int lAp2 = Scaled(LeftAp2, lengthScale);
        int lDelay2 = Scaled(LeftDelay2, lengthScale);
        int rAp1 = Scaled(RightAp1, lengthScale);
        int rDelay1 = Scaled(RightDelay1, lengthScale);
        int rAp2 = Scaled(RightAp2, lengthScale);
        int rDelay2 = Scaled(RightDelay2, lengthScale);

        // RT60 decay → per-loop gain: 0.001^(loopSeconds / decaySeconds), < 1 for any settings.
        double decaySeconds = Math.Max(0.05, parameters.Get(EffectParamNames.Decay, 2.0));
        double loopSeconds = (lAp1 + lDelay1 + lAp2 + lDelay2) / (double)sampleRate;
        var decay = (float)Math.Pow(0.001, loopSeconds / decaySeconds);

        double preDelayMs = Math.Clamp(parameters.Get(EffectParamNames.PreDelayMs, 10.0), 0, MaxPreDelaySeconds * 1000);
        int preDelaySamples = Math.Max(1, (int)(preDelayMs / 1000.0 * sampleRate));

        double highDamp = Math.Clamp(parameters.Get(EffectParamNames.HighDamp, 0.4), 0, 1);
        var hd = (float)(highDamp * 0.8); // keep some top end even fully damped, like ReverbEffect's ScaleDamp
        double lowDamp = Math.Clamp(parameters.Get(EffectParamNames.LowDamp, 0.1), 0, 1);
        var ld = (float)lowDamp;
        var lowPole = (float)(1 - Math.Exp(-2 * Math.PI * LowDampCornerHz / sampleRate));

        double modDepth = Math.Clamp(parameters.Get(EffectParamNames.ModDepth, 0.3), 0, 1);
        double excursion = MaxExcursion * scale * modDepth;
        double modRate = Math.Clamp(parameters.Get(EffectParamNames.ModRateHz, 0.5), 0.01, 10);
        double phaseStep = 2 * Math.PI * modRate / sampleRate;

        double earlyLate = Math.Clamp(parameters.Get(EffectParamNames.EarlyLate, 0.7), 0, 1);
        var earlyGain = (float)((1 - earlyLate) * EarlyGain);
        var lateGain = (float)earlyLate;
        double width = Math.Clamp(parameters.Get(EffectParamNames.Width, 1.0), 0, 1);
        var side = (float)width;
        var wet = (float)mix;
        float dry = 1 - wet;

        // Per-block tap tables (constant within a block — no per-sample rescaling work).
        Span<int> earlyTaps = stackalloc int[EarlyTapMs.Length];
        for (int i = 0; i < earlyTaps.Length; i++)
            earlyTaps[i] = Math.Max(1, (int)(EarlyTapMs[i] / 1000.0 * sampleRate * sizeFactor));
        Span<int> inputTaps = stackalloc int[InputAllpassTunings.Length];
        for (int i = 0; i < inputTaps.Length; i++)
            inputTaps[i] = Scaled(InputAllpassTunings[i], lengthScale);
        int tL1 = Scaled(266, lengthScale), tL2 = Scaled(2974, lengthScale), tL3 = Scaled(1913, lengthScale);
        int tL4 = Scaled(1996, lengthScale), tL5 = Scaled(1990, lengthScale), tL6 = Scaled(187, lengthScale);
        int tL7 = Scaled(1066, lengthScale);
        int tR1 = Scaled(353, lengthScale), tR2 = Scaled(3627, lengthScale), tR3 = Scaled(1228, lengthScale);
        int tR4 = Scaled(2673, lengthScale), tR5 = Scaled(2111, lengthScale), tR6 = Scaled(335, lengthScale);
        int tR7 = Scaled(121, lengthScale);

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            float input = 0;
            for (int ch = 0; ch < channels; ch++)
                input += interleaved[baseIndex + ch];
            input /= channels;

            // Predelay → input diffusion.
            _preDelay.Push(input);
            float x = _preDelay.Tap(preDelaySamples);
            for (int i = 0; i < _inputAllpasses.Length; i++)
                x = AllpassStep(_inputAllpasses[i], x, inputTaps[i], i < 2 ? gInput1 : gInput2);

            // Early reflections off the diffused input.
            _early.Push(x);
            float earlyL = 0, earlyR = 0;
            for (int i = 0; i < earlyTaps.Length; i++)
            {
                float s = _early.Tap(earlyTaps[i]) * EarlyTapGain[i];
                if ((i & 1) == 0)
                    earlyL += s;
                else
                    earlyR += s;
            }

            // Figure-8 tank: each branch is fed by the diffused input plus the other branch's decayed output.
            double phase = _sample * phaseStep;
            float feedFromRight = _rDelay2.Tap(rDelay2) * decay;
            float feedFromLeft = _lDelay2.Tap(lDelay2) * decay;

            float l = ModulatedAllpassStep(_lAp1, x + feedFromRight, lAp1, gTank1, excursion, Math.Sin(phase));
            _lDelay1.Push(l);
            l = Damp(_lDelay1.Tap(lDelay1), hd, ld, lowPole, ref _lHighDampState, ref _lLowDampState) * decay;
            l = AllpassStep(_lAp2, l, lAp2, gTank2);
            _lDelay2.Push(l);

            float r = ModulatedAllpassStep(_rAp1, x + feedFromLeft, rAp1, gTank1, excursion, Math.Sin(phase * 0.95 + Math.PI / 2));
            _rDelay1.Push(r);
            r = Damp(_rDelay1.Tap(rDelay1), hd, ld, lowPole, ref _rHighDampState, ref _rLowDampState) * decay;
            r = AllpassStep(_rAp2, r, rAp2, gTank2);
            _rDelay2.Push(r);
            _sample++;

            // Dattorro's decorrelated output taps (each index Size-scaled like the lines it taps).
            float lateL = TankTapGain * (
                  _rDelay1.Tap(tL1) + _rDelay1.Tap(tL2) - _rAp2.Tap(tL3) + _rDelay2.Tap(tL4)
                - _lDelay1.Tap(tL5) - _lAp2.Tap(tL6) - _lDelay2.Tap(tL7));
            float lateR = TankTapGain * (
                  _lDelay1.Tap(tR1) + _lDelay1.Tap(tR2) - _lAp2.Tap(tR3) + _lDelay2.Tap(tR4)
                - _rDelay1.Tap(tR5) - _rAp2.Tap(tR6) - _rDelay2.Tap(tR7));

            float wetL = earlyGain * earlyL + lateGain * lateL;
            float wetR = earlyGain * earlyR + lateGain * lateR;

            // Width as mid/side on the wet pair (0 = mono, 1 = the tank's full decorrelation).
            float mid = 0.5f * (wetL + wetR);
            float sideAmt = 0.5f * (wetL - wetR) * side;
            wetL = mid + sideAmt;
            wetR = mid - sideAmt;

            if (channels == 1)
            {
                interleaved[baseIndex] = dry * interleaved[baseIndex] + wet * mid;
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
    public void Reset()
    {
        if (_rate == 0)
            return;
        _preDelay.Clear();
        _early.Clear();
        foreach (Line ap in _inputAllpasses)
            ap.Clear();
        _lAp1.Clear();
        _lDelay1.Clear();
        _lAp2.Clear();
        _lDelay2.Clear();
        _rAp1.Clear();
        _rDelay1.Clear();
        _rAp2.Clear();
        _rDelay2.Clear();
        _lHighDampState = _rHighDampState = 0;
        _lLowDampState = _rLowDampState = 0;
        _sample = 0;
    }

    /// <summary>One Schroeder allpass step over <paramref name="line"/> at the given (current) length.</summary>
    private static float AllpassStep(Line line, float input, int length, float g)
    {
        float delayed = line.Tap(length);
        float t = input + g * delayed;
        line.Push(t);
        return delayed - g * t;
    }

    /// <summary>The tank's modulated allpass: the read tap swings by <paramref name="excursion"/> samples on a
    /// deterministic sine, de-tuning the loop just enough to break up metallic ringing.</summary>
    private static float ModulatedAllpassStep(Line line, float input, int length, float g, double excursion, double lfo)
    {
        double tap = length - excursion * (0.5 + 0.5 * lfo);
        float delayed = line.TapFrac(Math.Max(1.0, tap));
        float t = input + g * delayed;
        line.Push(t);
        return delayed - g * t;
    }

    /// <summary>The in-loop tail shaping: a one-pole low-pass (HighDamp) then a low-shelf cut built from a
    /// one-pole low tracker (LowDamp) — highs and lows each die faster than the mids when damped.</summary>
    private static float Damp(float s, float highDamp, float lowDamp, float lowPole, ref float highState, ref float lowState)
    {
        highState = s * (1 - highDamp) + highState * highDamp;
        s = highState;
        lowState += lowPole * (s - lowState);
        return s - lowDamp * lowState;
    }

    private static int Scaled(int referenceLength, double lengthScale) =>
        Math.Max(1, (int)(referenceLength * lengthScale));

    private void Allocate(int sampleRate)
    {
        _rate = sampleRate;
        double maxScale = sampleRate / ReferenceRate; // Size = 1
        int excursionMargin = (int)(MaxExcursion * maxScale) + 4;

        _preDelay = NewLine((int)(MaxPreDelaySeconds * sampleRate) + 2, 0);
        _early = NewLine((int)(0.1 * sampleRate) + 2, 0); // 100 ms ceiling ≥ the longest 79.8 ms tap
        _inputAllpasses = [.. InputAllpassTunings.Select(t => NewLine(Scaled(t, maxScale) + 2, 0))];
        _lAp1 = NewLine(Scaled(LeftAp1, maxScale), excursionMargin);
        _lDelay1 = NewLine(Scaled(LeftDelay1, maxScale), 2);
        _lAp2 = NewLine(Scaled(LeftAp2, maxScale), 2);
        _lDelay2 = NewLine(Scaled(LeftDelay2, maxScale), 2);
        _rAp1 = NewLine(Scaled(RightAp1, maxScale), excursionMargin);
        _rDelay1 = NewLine(Scaled(RightDelay1, maxScale), 2);
        _rAp2 = NewLine(Scaled(RightAp2, maxScale), 2);
        _rDelay2 = NewLine(Scaled(RightDelay2, maxScale), 2);
        _lHighDampState = _rHighDampState = 0;
        _lLowDampState = _rLowDampState = 0;
        _sample = 0;
    }

    private static Line NewLine(int length, int margin) => new() { Buffer = new float[Math.Max(2, length + margin)] };
}
