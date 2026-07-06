using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the Shimmer Reverb (PLAN.md step 50): the pitch-shifted feedback lands at
/// the configured interval on a pure test tone (Goertzel pitch detection), the feedback never diverges even
/// at maximum settings, zero shimmer collapses to the plain base tail, and steady-state processing is
/// allocation-free. Pure C#, golden-PCM testable (ARCHITECTURE.md §19).
/// </summary>
public class ShimmerReverbEffectTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const int Block = 4800; // 0.1 s

    private static ResolvedEffect Params(params (string Name, double Value)[] values) =>
        new(EffectTypeIds.AudioShimmerReverb, values.ToDictionary(v => v.Name, v => v.Value));

    /// <summary>Feeds <paramref name="toneBlocks"/> blocks of a stereo sine at <paramref name="freq"/> Hz then
    /// <paramref name="silentBlocks"/> blocks of silence, returning every processed block concatenated.</summary>
    private static float[] ToneThenSilence(ShimmerReverbEffect effect, ResolvedEffect parameters,
        double freq, int toneBlocks, int silentBlocks)
    {
        var output = new float[(toneBlocks + silentBlocks) * Block * Channels];
        var block = new float[Block * Channels];
        for (int b = 0; b < toneBlocks + silentBlocks; b++)
        {
            for (int f = 0; f < Block; f++)
            {
                var s = b < toneBlocks ? (float)(0.5 * Math.Sin(2 * Math.PI * freq * (b * Block + f) / Rate)) : 0f;
                for (int ch = 0; ch < Channels; ch++)
                    block[f * Channels + ch] = s;
            }
            effect.Process(block, Block, Rate, Channels, parameters);
            block.CopyTo(output, b * Block * Channels);
        }
        return output;
    }

    /// <summary>Goertzel power at <paramref name="freq"/> Hz over the left channel of <paramref name="span"/>.</summary>
    private static double PowerAt(ReadOnlySpan<float> span, double freq)
    {
        int frames = span.Length / Channels;
        double w = 2 * Math.PI * freq / Rate;
        double coeff = 2 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;
        for (int f = 0; f < frames; f++)
        {
            s0 = span[f * Channels] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    /// <summary>Total power in a ±40 Hz band around <paramref name="center"/> — wide enough to catch the
    /// granular shifter's splice sidebands alongside the carrier ("within pitch-detection tolerance").</summary>
    private static double BandPower(ReadOnlySpan<float> span, double center)
    {
        double sum = 0;
        for (double f = center - 40; f <= center + 40; f += 5)
            sum += PowerAt(span, f);
        return sum;
    }

    private static double Rms(ReadOnlySpan<float> buffer)
    {
        double sum = 0;
        foreach (float s in buffer)
            sum += (double)s * s;
        return Math.Sqrt(sum / buffer.Length);
    }

    [Fact]
    public void Factory_Creates_The_Shimmer_Reverb()
    {
        Assert.IsType<ShimmerReverbEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioShimmerReverb));
    }

    [Fact]
    public void Zero_Mix_Is_An_Exact_Pass_Through()
    {
        var effect = new ShimmerReverbEffect();
        var buffer = new float[Block * Channels];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)Math.Sin(i * 0.01);
        float[] expected = (float[])buffer.Clone();
        effect.Process(buffer, Block, Rate, Channels, Params((EffectParamNames.Mix, 0.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Octave_Shimmer_Lands_An_Octave_Up_On_A_Pure_Tone()
    {
        // 2 s of a 440 Hz tone at full shimmer, then 1 s of silence: the tail must carry energy at 880 Hz
        // (the +12 st shift) far above the fifth's 659.3 Hz, which the octave cascade never visits — the
        // control frequency mirrors the fifth-interval test so spectral leakage from 440 affects both alike.
        var effect = new ShimmerReverbEffect();
        float[] output = ToneThenSilence(effect, Params(
            (EffectParamNames.Mix, 1.0), (EffectParamNames.ShimmerAmount, 1.0),
            (EffectParamNames.ShimmerInterval, 12.0), (EffectParamNames.Decay, 5.0),
            (EffectParamNames.Damping, 0.0)), 440, 20, 10);

        ReadOnlySpan<float> tail = output.AsSpan(25 * Block * Channels); // the final 0.5 s, input long gone
        double octave = BandPower(tail, 880);
        double fifth = BandPower(tail, 440 * Math.Pow(2, 7 / 12.0));
        Assert.True(octave > fifth * 5, $"880 Hz band {octave:E2} should dominate the fifth band {fifth:E2}");
    }

    [Fact]
    public void Fifth_Interval_Lands_A_Fifth_Up_Not_An_Octave()
    {
        // Interval 7 st on 440 Hz shifts to 440·2^(7/12) ≈ 659.3 Hz; 880 Hz is NOT in the fifth cascade
        // (659.3, 987.8, …), so the fifth must dominate the octave — the interval control selects the pitch.
        var effect = new ShimmerReverbEffect();
        float[] output = ToneThenSilence(effect, Params(
            (EffectParamNames.Mix, 1.0), (EffectParamNames.ShimmerAmount, 1.0),
            (EffectParamNames.ShimmerInterval, 7.0), (EffectParamNames.Decay, 5.0),
            (EffectParamNames.Damping, 0.0)), 440, 20, 10);

        ReadOnlySpan<float> tail = output.AsSpan(25 * Block * Channels);
        double fifth = BandPower(tail, 440 * Math.Pow(2, 7 / 12.0));
        double octave = BandPower(tail, 880);
        Assert.True(fifth > octave * 5, $"fifth band {fifth:E2} should dominate octave band {octave:E2}");
    }

    [Fact]
    public void Zero_Shimmer_Contributes_No_Pitched_Up_Energy()
    {
        // With Shimmer = 0 the pitch path is silent: the 440 Hz tail carries no more 880 Hz than its own
        // harmonic residue — orders of magnitude below the full-shimmer run above.
        float[] off = ToneThenSilence(new ShimmerReverbEffect(), Params(
            (EffectParamNames.Mix, 1.0), (EffectParamNames.ShimmerAmount, 0.0),
            (EffectParamNames.Decay, 5.0), (EffectParamNames.Damping, 0.0)), 440, 20, 10);
        float[] on = ToneThenSilence(new ShimmerReverbEffect(), Params(
            (EffectParamNames.Mix, 1.0), (EffectParamNames.ShimmerAmount, 1.0),
            (EffectParamNames.ShimmerInterval, 12.0), (EffectParamNames.Decay, 5.0),
            (EffectParamNames.Damping, 0.0)), 440, 20, 10);

        double offOctave = BandPower(off.AsSpan(25 * Block * Channels), 880);
        double onOctave = BandPower(on.AsSpan(25 * Block * Channels), 880);
        Assert.True(onOctave > offOctave * 100, $"shimmer on {onOctave:E2} vs off {offOctave:E2}");
    }

    [Fact]
    public void Zero_Shimmer_Collapses_To_The_Base_Reverb_Regardless_Of_Interval()
    {
        // The interval is meaningless when the shimmer path is off: two instances differing only in
        // interval are bit-identical — 0 shimmer IS the plain base tail (the step's collapse requirement).
        float[] a = ToneThenSilence(new ShimmerReverbEffect(), Params(
            (EffectParamNames.Mix, 0.7), (EffectParamNames.ShimmerAmount, 0.0),
            (EffectParamNames.ShimmerInterval, 12.0)), 440, 5, 5);
        float[] b = ToneThenSilence(new ShimmerReverbEffect(), Params(
            (EffectParamNames.Mix, 0.7), (EffectParamNames.ShimmerAmount, 0.0),
            (EffectParamNames.ShimmerInterval, 7.0)), 440, 5, 5);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_Tone_Rings_A_Tail_After_The_Input_Stops()
    {
        var effect = new ShimmerReverbEffect();
        float[] output = ToneThenSilence(effect, Params(
            (EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 3.0)), 440, 5, 10);
        double tail = Rms(output.AsSpan(7 * Block * Channels, Block * Channels)); // 0.2 s past the input
        Assert.True(tail > 1e-4, $"expected an audible tail, got RMS {tail:E2}");
    }

    [Fact]
    public void Feedback_Never_Diverges_At_Maximum_Settings()
    {
        // The step's correctness requirement: max shimmer + max decay + zero damping over an extended run of
        // sustained loud input must stay bounded — the loop-gain clamp and the tanh bound cannot run away.
        var effect = new ShimmerReverbEffect();
        ResolvedEffect p = Params(
            (EffectParamNames.Mix, 1.0), (EffectParamNames.ShimmerAmount, 1.0),
            (EffectParamNames.ShimmerInterval, 12.0), (EffectParamNames.Decay, 20.0),
            (EffectParamNames.Size, 1.0), (EffectParamNames.Damping, 0.0));

        var block = new float[Block * Channels];
        float peak = 0;
        for (int b = 0; b < 100; b++) // 10 s of sustained loud input
        {
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)(0.9 * Math.Sin(i * 0.05 + b));
            effect.Process(block, Block, Rate, Channels, p);
            double rms = Rms(block);
            Assert.True(rms < 10, $"block {b} RMS {rms:E2} should stay bounded");
            foreach (float s in block)
            {
                Assert.False(float.IsNaN(s) || float.IsInfinity(s));
                peak = Math.Max(peak, Math.Abs(s));
            }
        }
        Assert.True(peak < 20f, $"output should stay bounded, peaked at {peak}");
    }

    [Fact]
    public void Output_Is_Deterministic_Run_To_Run()
    {
        ResolvedEffect p = Params(
            (EffectParamNames.Mix, 0.5), (EffectParamNames.ShimmerAmount, 0.8),
            (EffectParamNames.ShimmerInterval, 12.0));
        float[] a = ToneThenSilence(new ShimmerReverbEffect(), p, 440, 5, 5);
        float[] b = ToneThenSilence(new ShimmerReverbEffect(), p, 440, 5, 5);
        Assert.Equal(a, b); // bit-exact: the grain phase is a pure function of the sample advance, no RNG
    }

    [Fact]
    public void Reset_Clears_The_Tail()
    {
        var effect = new ShimmerReverbEffect();
        ResolvedEffect p = Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 10.0),
            (EffectParamNames.ShimmerAmount, 1.0));
        _ = ToneThenSilence(effect, p, 440, 5, 0); // load the tail
        effect.Reset();

        var silent = new float[Block * Channels];
        effect.Process(silent, Block, Rate, Channels, p);
        Assert.Equal(0.0, Rms(silent), 12); // nothing rings after a reset
    }

    [Fact]
    public void Steady_State_Processing_Does_Not_Allocate()
    {
        var effect = new ShimmerReverbEffect();
        ResolvedEffect p = Params((EffectParamNames.Mix, 0.5), (EffectParamNames.ShimmerAmount, 0.8));
        var block = new float[1024 * Channels];
        effect.Process(block, 1024, Rate, Channels, p); // first call allocates the lines

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
            effect.Process(block, 1024, Rate, Channels, p);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"steady-state Process allocated {allocated} bytes");
    }
}
