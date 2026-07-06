using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the Noise Gate (PLAN.md step 47): signal above threshold passes at unity,
/// signal below attenuates to the configured range floor, the attack ramp matches its one-pole time
/// constant, hold prevents premature closing on a brief dip, hysteresis prevents chatter on
/// threshold-straddling input, state carries across blocks, and steady-state processing is allocation-free.
/// Pure C#, golden-PCM testable (ARCHITECTURE.md §19).
/// </summary>
public class NoiseGateEffectTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static ResolvedEffect Params(params (string Name, double Value)[] values) =>
        new(EffectTypeIds.AudioNoiseGate, values.ToDictionary(v => v.Name, v => v.Value));

    /// <summary>A stereo buffer of <paramref name="frames"/> frames, every sample <paramref name="value"/>.</summary>
    private static float[] Dc(int frames, float value)
    {
        var buffer = new float[frames * Channels];
        buffer.AsSpan().Fill(value);
        return buffer;
    }

    /// <summary>Fills frames [<paramref name="from"/>, <paramref name="to"/>) with <paramref name="value"/>.</summary>
    private static void Fill(float[] buffer, int from, int to, float value) =>
        buffer.AsSpan(from * Channels, (to - from) * Channels).Fill(value);

    /// <summary>Asserts steady-state <c>Process</c> is allocation-free (§1, §19) after a JIT/one-off warm-up.</summary>
    private static void AssertSteadyStateDoesNotAllocate(IAudioEffect effect, ResolvedEffect parameters)
    {
        var block = new float[1024 * Channels];
        block.AsSpan().Fill(0.25f);
        for (int i = 0; i < 100; i++)
            effect.Process(block, 1024, Rate, Channels, parameters);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
            effect.Process(block, 1024, Rate, Channels, parameters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"steady-state Process allocated {allocated} bytes");
    }

    [Fact]
    public void Factory_Creates_The_Noise_Gate()
    {
        Assert.IsType<NoiseGateEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioNoiseGate));
    }

    [Fact]
    public void Zero_Range_Is_An_Exact_Pass_Through()
    {
        // A 0 dB floor never attenuates, whatever the threshold says — exact pass-through.
        var effect = new NoiseGateEffect();
        var buffer = new float[2048 * Channels];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)(0.001 * Math.Sin(i * 0.01)); // far below any threshold
        float[] expected = (float[])buffer.Clone();
        effect.Process(buffer, 2048, Rate, Channels,
            Params((EffectParamNames.RangeDb, 0.0), (EffectParamNames.ThresholdDb, -20.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Signal_Above_Threshold_Passes_At_Unity()
    {
        // A steady -6 dB signal against a -40 dB threshold: the gate opens immediately and settles at unity.
        float[] buffer = Dc(Rate, 0.5f);
        new NoiseGateEffect().Process(buffer, Rate, Rate, Channels, Params(
            (EffectParamNames.ThresholdDb, -40.0), (EffectParamNames.AttackMs, 1.0),
            (EffectParamNames.RangeDb, -80.0)));
        Assert.Equal(0.5f, buffer[^1], 0.0001);
        Assert.Equal(0.5f, buffer[^2], 0.0001);
    }

    [Fact]
    public void Signal_Below_Threshold_Attenuates_To_The_Configured_Range()
    {
        // A steady -34 dB signal against a -20 dB threshold with a -40 dB range: the gate never opens and
        // the gain settles at the range floor (0.01), not at zero — the partial-attenuation convention.
        float[] buffer = Dc(Rate, 0.02f);
        new NoiseGateEffect().Process(buffer, Rate, Rate, Channels, Params(
            (EffectParamNames.ThresholdDb, -20.0), (EffectParamNames.ReleaseMs, 100.0),
            (EffectParamNames.RangeDb, -40.0)));
        Assert.Equal(0.02f * 0.01f, buffer[^1], 0.00002);
    }

    [Fact]
    public void Attack_Ramp_Matches_The_One_Pole_Time_Constant()
    {
        // From fully closed, an opening gate's gain is g(n) = 1 − a^(n+1) with a = e^(−1/(τ·fs)), τ = 10 ms
        // (480 samples). At n = 479 the output must sit at 0.5·(1 − e^−1); by 4τ it is within 2 % of unity.
        const double Tau = 480;
        double a = Math.Exp(-1.0 / Tau);
        float[] buffer = Dc(Rate / 10, 0.5f);
        new NoiseGateEffect().Process(buffer, Rate / 10, Rate, Channels, Params(
            (EffectParamNames.ThresholdDb, -40.0), (EffectParamNames.AttackMs, 10.0),
            (EffectParamNames.RangeDb, -80.0)));

        Assert.Equal(0.5f * (float)(1 - Math.Pow(a, 480)), buffer[479 * Channels], 0.001);
        Assert.Equal(0.5f * (float)(1 - Math.Pow(a, 4 * 480)), buffer[(4 * 480 - 1) * Channels], 0.001);
    }

    [Fact]
    public void Release_Timing_Scales_With_The_Release_Parameter()
    {
        // 0.5 s at -6 dB (gate fully open), then a -54 dB tail below the -40 dB threshold. 200 ms into the
        // tail a 10 ms release has collapsed to the -80 dB floor while a 500 ms release is still well open.
        static float[] Run(double releaseMs)
        {
            var buffer = Dc(Rate, 0.5f);
            Fill(buffer, Rate / 2, Rate, 0.002f);
            new NoiseGateEffect().Process(buffer, Rate, Rate, Channels, Params(
                (EffectParamNames.ThresholdDb, -40.0), (EffectParamNames.AttackMs, 1.0),
                (EffectParamNames.HoldMs, 0.0), (EffectParamNames.ReleaseMs, releaseMs),
                (EffectParamNames.RangeDb, -80.0), (EffectParamNames.HysteresisDb, 0.0)));
            return buffer;
        }

        int probe = (Rate / 2 + Rate / 5) * Channels; // 200 ms into the tail
        float fast = Math.Abs(Run(10.0)[probe]);
        float slow = Math.Abs(Run(500.0)[probe]);
        Assert.True(fast < 0.00001f, $"10 ms release should have closed the gate, got {fast:E2}");
        Assert.True(slow > 0.0005f, $"500 ms release should still be mid-ramp, got {slow:E2}");
    }

    [Fact]
    public void Hold_Prevents_Premature_Closing_On_A_Brief_Dip()
    {
        // 0.25 s at -6 dB, a 100 ms silent dip, then -6 dB again. With a 200 ms hold the gate never starts
        // closing, so the signal returns at unity; with no hold (and a fast release / slow attack) the gate
        // has closed during the dip and the return is still ramping from the floor.
        const int DipStart = Rate / 4, DipEnd = DipStart + Rate / 10;
        static float[] Run(double holdMs)
        {
            var buffer = Dc(Rate / 2, 0.5f);
            Fill(buffer, DipStart, DipEnd, 0f);
            new NoiseGateEffect().Process(buffer, Rate / 2, Rate, Channels, Params(
                (EffectParamNames.ThresholdDb, -40.0), (EffectParamNames.AttackMs, 50.0),
                (EffectParamNames.HoldMs, holdMs), (EffectParamNames.ReleaseMs, 5.0),
                (EffectParamNames.RangeDb, -80.0), (EffectParamNames.HysteresisDb, 0.0)));
            return buffer;
        }

        int probe = DipEnd * Channels; // the first frame after the dip
        Assert.Equal(0.5f, Run(200.0)[probe], 0.01);
        Assert.True(Run(0.0)[probe] < 0.05f, "with no hold the gate must have closed during the dip");
    }

    [Fact]
    public void Hysteresis_Prevents_Chatter_On_Threshold_Straddling_Input()
    {
        // A steady level in the hysteresis band (-30.5 dB against a -30 dB open / -42 dB close threshold)
        // cannot OPEN the gate from below — but once a loud burst has opened it, the same level KEEPS it
        // open. Without hysteresis the gate closes again after the burst on identical input.
        static float[] Run(double hysteresisDb)
        {
            var buffer = Dc(Rate + Rate / 2, 0.03f);           // 1.5 s of in-band level...
            Fill(buffer, Rate / 2, Rate / 2 + Rate / 10, 0.5f); // ...with a 100 ms burst at 0.5 s
            new NoiseGateEffect().Process(buffer, Rate + Rate / 2, Rate, Channels, Params(
                (EffectParamNames.ThresholdDb, -30.0), (EffectParamNames.AttackMs, 1.0),
                (EffectParamNames.HoldMs, 0.0), (EffectParamNames.ReleaseMs, 10.0),
                (EffectParamNames.RangeDb, -40.0), (EffectParamNames.HysteresisDb, hysteresisDb)));
            return buffer;
        }

        float[] with = Run(12.0);
        Assert.Equal(0.03f * 0.01f, with[(Rate / 2 - 1) * Channels], 0.0002); // never opened before the burst
        Assert.Equal(0.03f, with[^1], 0.001);                                 // stayed open after it

        float[] without = Run(0.0);
        Assert.Equal(0.03f * 0.01f, without[^1], 0.0002); // no hysteresis: closed again after the burst
    }

    [Fact]
    public void State_Carries_Across_Blocks()
    {
        ResolvedEffect p = Params(
            (EffectParamNames.ThresholdDb, -40.0), (EffectParamNames.AttackMs, 25.0),
            (EffectParamNames.HoldMs, 20.0), (EffectParamNames.ReleaseMs, 80.0),
            (EffectParamNames.RangeDb, -60.0));
        var whole = Dc(4096, 0.4f);
        Fill(whole, 1500, 2500, 0.001f);
        float[] halves = (float[])whole.Clone();

        new NoiseGateEffect().Process(whole, 4096, Rate, Channels, p);
        var split = new NoiseGateEffect();
        split.Process(halves.AsSpan(0, 2048 * Channels), 2048, Rate, Channels, p);
        split.Process(halves.AsSpan(2048 * Channels), 2048, Rate, Channels, p);

        for (int i = 0; i < whole.Length; i++)
            Assert.Equal(whole[i], halves[i], 0.00001);
    }

    [Fact]
    public void Reset_Returns_The_Gate_To_Fully_Closed()
    {
        var effect = new NoiseGateEffect();
        ResolvedEffect p = Params(
            (EffectParamNames.ThresholdDb, -40.0), (EffectParamNames.AttackMs, 50.0),
            (EffectParamNames.RangeDb, -80.0));
        float[] open = Dc(Rate, 0.5f);
        effect.Process(open, Rate, Rate, Channels, p);
        Assert.Equal(0.5f, open[^1], 0.001); // fully open

        effect.Reset();
        float[] after = Dc(256, 0.5f);
        effect.Process(after, 256, Rate, Channels, p);
        Assert.True(after[0] < 0.01f, $"after Reset the gate must reopen from closed, got {after[0]}");
    }

    [Fact]
    public void Steady_State_Processing_Does_Not_Allocate() =>
        AssertSteadyStateDoesNotAllocate(new NoiseGateEffect(), Params(
            (EffectParamNames.ThresholdDb, -40.0), (EffectParamNames.RangeDb, -80.0)));
}
