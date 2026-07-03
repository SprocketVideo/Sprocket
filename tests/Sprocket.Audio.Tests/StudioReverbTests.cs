using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the Studio Reverb (PLAN.md step 41): the Dattorro-style plate/hall tank next
/// to the Freeverb-style "Reverb (Lite)". Pure C#, golden-PCM testable (ARCHITECTURE.md §19) — determinism is
/// what makes the freeze cache's replay equivalent to the live chain.
/// </summary>
public class StudioReverbTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static ResolvedEffect Params(params (string Name, double Value)[] values) =>
        new(EffectTypeIds.AudioStudioReverb, values.ToDictionary(v => v.Name, v => v.Value));

    /// <summary>Feeds one impulse block then <paramref name="silentBlocks"/> blocks of silence, returning every
    /// processed block concatenated — the impulse response, block by block.</summary>
    private static float[] ImpulseResponse(StudioReverbEffect effect, ResolvedEffect parameters, int blockFrames, int silentBlocks)
    {
        var output = new float[(silentBlocks + 1) * blockFrames * Channels];
        var block = new float[blockFrames * Channels];
        block[0] = block[1] = 1f; // stereo impulse at t = 0
        effect.Process(block, blockFrames, Rate, Channels, parameters);
        block.CopyTo(output, 0);
        for (int b = 1; b <= silentBlocks; b++)
        {
            Array.Clear(block);
            effect.Process(block, blockFrames, Rate, Channels, parameters);
            block.CopyTo(output, b * blockFrames * Channels);
        }
        return output;
    }

    private static double Rms(ReadOnlySpan<float> buffer)
    {
        double sum = 0;
        foreach (float s in buffer)
            sum += (double)s * s;
        return Math.Sqrt(sum / buffer.Length);
    }

    [Fact]
    public void Factory_Creates_The_Studio_Reverb()
    {
        Assert.IsType<StudioReverbEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioStudioReverb));
    }

    [Fact]
    public void Zero_Mix_Is_An_Exact_Pass_Through()
    {
        var effect = new StudioReverbEffect();
        var buffer = new float[4800 * Channels];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)Math.Sin(i * 0.01);
        float[] expected = (float[])buffer.Clone();
        effect.Process(buffer, 4800, Rate, Channels, Params((EffectParamNames.Mix, 0.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void An_Impulse_Rings_A_Tail_After_The_Input_Stops()
    {
        var effect = new StudioReverbEffect();
        float[] ir = ImpulseResponse(effect, Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 2.0)), 4800, 9);
        // Half a second in (well past the 10 ms predelay + onset) the tail is still clearly audible.
        double tail = Rms(ir.AsSpan(5 * 4800 * Channels, 4800 * Channels));
        Assert.True(tail > 1e-4, $"expected an audible tail, got RMS {tail:E2}");
    }

    [Fact]
    public void Longer_Decay_Rings_Longer()
    {
        float[] shortIr = ImpulseResponse(new StudioReverbEffect(),
            Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 0.3)), 4800, 19);
        float[] longIr = ImpulseResponse(new StudioReverbEffect(),
            Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 8.0)), 4800, 19);
        // Compare the last half second (1.5–2.0 s): the 0.3 s decay is gone, the 8 s decay still rings.
        double shortTail = Rms(shortIr.AsSpan(15 * 4800 * Channels));
        double longTail = Rms(longIr.AsSpan(15 * 4800 * Channels));
        Assert.True(longTail > shortTail * 10, $"long {longTail:E2} vs short {shortTail:E2}");
    }

    [Fact]
    public void High_Damping_Darkens_The_Tail_Faster()
    {
        float[] bright = ImpulseResponse(new StudioReverbEffect(),
            Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 3.0), (EffectParamNames.HighDamp, 0.0)), 4800, 14);
        float[] damped = ImpulseResponse(new StudioReverbEffect(),
            Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 3.0), (EffectParamNames.HighDamp, 1.0)), 4800, 14);
        double brightTail = Rms(bright.AsSpan(10 * 4800 * Channels));
        double dampedTail = Rms(damped.AsSpan(10 * 4800 * Channels));
        Assert.True(dampedTail < brightTail, $"damped {dampedTail:E2} should be quieter than bright {brightTail:E2}");
    }

    [Fact]
    public void Zero_Width_Collapses_The_Wet_Signal_To_Mono()
    {
        var effect = new StudioReverbEffect();
        float[] ir = ImpulseResponse(effect,
            Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Width, 0.0)), 4800, 4);
        for (int f = 0; f < ir.Length / Channels; f++)
            Assert.Equal(ir[f * Channels], ir[f * Channels + 1], 0.000001);
    }

    [Fact]
    public void Full_Width_Decorrelates_Left_And_Right()
    {
        var effect = new StudioReverbEffect();
        float[] ir = ImpulseResponse(effect,
            Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Width, 1.0), (EffectParamNames.EarlyLate, 1.0)), 4800, 9);
        double difference = 0;
        for (int f = 0; f < ir.Length / Channels; f++)
            difference += Math.Abs(ir[f * Channels] - ir[f * Channels + 1]);
        Assert.True(difference > 1.0, $"expected decorrelated channels, total |L-R| {difference:E2}");
    }

    [Fact]
    public void Output_Is_Deterministic_Run_To_Run()
    {
        ResolvedEffect p = Params(
            (EffectParamNames.Mix, 0.5), (EffectParamNames.ModDepth, 1.0), (EffectParamNames.ModRateHz, 2.0));
        float[] a = ImpulseResponse(new StudioReverbEffect(), p, 4800, 9);
        float[] b = ImpulseResponse(new StudioReverbEffect(), p, 4800, 9);
        Assert.Equal(a, b); // bit-exact: the LFOs are functions of the sample counter, no RNG
    }

    [Fact]
    public void Reset_Clears_The_Tail()
    {
        var effect = new StudioReverbEffect();
        ResolvedEffect p = Params((EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 5.0));
        _ = ImpulseResponse(effect, p, 4800, 4); // load the tank
        effect.Reset();

        var silent = new float[4800 * Channels];
        effect.Process(silent, 4800, Rate, Channels, p);
        Assert.Equal(0.0, Rms(silent), 12); // nothing rings after a reset
    }

    [Fact]
    public void Tank_Stays_Bounded_At_Maximum_Settings()
    {
        // The decay gain is < 1 by construction, so even the most extreme parameters cannot run away —
        // a correctness requirement for anything sitting in a realtime chain (cf. step 50's clamp rule).
        var effect = new StudioReverbEffect();
        ResolvedEffect p = Params(
            (EffectParamNames.Mix, 1.0), (EffectParamNames.Decay, 20.0), (EffectParamNames.Size, 1.0),
            (EffectParamNames.Diffusion, 1.0), (EffectParamNames.ModDepth, 1.0), (EffectParamNames.ModRateHz, 5.0),
            (EffectParamNames.LowDamp, 0.0), (EffectParamNames.HighDamp, 0.0));

        var block = new float[4800 * Channels];
        float peak = 0;
        for (int b = 0; b < 40; b++) // 4 s of sustained loud input
        {
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)(0.9 * Math.Sin(i * 0.05 + b));
            effect.Process(block, 4800, Rate, Channels, p);
            foreach (float s in block)
            {
                Assert.False(float.IsNaN(s) || float.IsInfinity(s));
                peak = Math.Max(peak, Math.Abs(s));
            }
        }
        Assert.True(peak < 20f, $"tank output should stay bounded, peaked at {peak}");
    }

    [Fact]
    public void Steady_State_Processing_Does_Not_Allocate()
    {
        var effect = new StudioReverbEffect();
        ResolvedEffect p = Params((EffectParamNames.Mix, 0.5));
        var block = new float[1024 * Channels];
        effect.Process(block, 1024, Rate, Channels, p); // first call allocates the lines

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
            effect.Process(block, 1024, Rate, Channels, p);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"steady-state Process allocated {allocated} bytes");
    }
}
