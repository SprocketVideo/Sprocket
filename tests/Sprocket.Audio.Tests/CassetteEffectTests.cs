using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the Cassette effect (plan/features/toy-cassette-camera.md, phase 4): factory
/// registration, allocation-free steady state, run-to-run / post-<c>Reset</c> determinism, block-split
/// equivalence, mono fan-out, the 24 dB/oct band-limit, hiss at the set level, pumping AGC, Mix 0 bit-exact dry,
/// and bounded output. Pure C#, golden-PCM testable (ARCHITECTURE.md §19).
/// </summary>
public class CassetteEffectTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    /// <summary>Builds the effect's parameters; a later entry for the same name overrides an earlier one.</summary>
    private static ResolvedEffect Params(params (string Name, double Value)[] values)
    {
        var map = new Dictionary<string, double>();
        foreach ((string name, double value) in values)
            map[name] = value;
        return new ResolvedEffect(EffectTypeIds.AudioCassette, map);
    }

    /// <summary>A "clean" filter-only configuration: no AGC, saturation, hiss or wobble — only the band-limit
    /// (<paramref name="extra"/> entries override).</summary>
    private static ResolvedEffect FilterOnly(params (string Name, double Value)[] extra) =>
        Params([
            (EffectParamNames.AgcAmount, 0.0), (EffectParamNames.Drive, 0.0), (EffectParamNames.HissDb, -90.0),
            (EffectParamNames.WowFlutterDepth, 0.0), .. extra,
        ]);

    /// <summary>A stereo buffer with a different sine in each channel (so mono folding is observable).</summary>
    private static float[] StereoMaterial(int frames, int offset = 0)
    {
        var buffer = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
        {
            int n = f + offset;
            buffer[f * Channels] = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * n / Rate));
            buffer[f * Channels + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 1250 * n / Rate)
                                               * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 1.7 * n / Rate)));
        }
        return buffer;
    }

    private static float[] Sine(int frames, double freq, float amplitude)
    {
        var buffer = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
        {
            var s = (float)(amplitude * Math.Sin(2 * Math.PI * freq * f / Rate));
            for (int ch = 0; ch < Channels; ch++)
                buffer[f * Channels + ch] = s;
        }
        return buffer;
    }

    /// <summary>Runs a 1 s sine at <paramref name="freq"/> through a fresh effect and returns the settled output
    /// amplitude (peak |sample| over the final 10 ms) — the <see cref="ShelvingEqEffectTests"/> helper.</summary>
    private static float SettledSineAmplitude(double freq, ResolvedEffect parameters)
    {
        float[] buffer = Sine(Rate, freq, 0.25f);
        new CassetteEffect().Process(buffer, Rate, Rate, Channels, parameters);
        float peak = 0f;
        foreach (float s in buffer.AsSpan((Rate - 480) * Channels))
            peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    private static double Rms(ReadOnlySpan<float> buffer)
    {
        double sum = 0;
        foreach (float s in buffer)
            sum += (double)s * s;
        return Math.Sqrt(sum / buffer.Length);
    }

    private static double Db(double linear) => 20 * Math.Log10(linear);

    /// <summary>Processes <paramref name="blocks"/> consecutive 1024-frame blocks of <see cref="StereoMaterial"/>
    /// and returns the concatenated output.</summary>
    private static float[] Render(IAudioEffect effect, ResolvedEffect parameters, int blocks)
    {
        const int Block = 1024;
        var output = new float[blocks * Block * Channels];
        for (int b = 0; b < blocks; b++)
        {
            float[] block = StereoMaterial(Block, b * Block);
            effect.Process(block, Block, Rate, Channels, parameters);
            block.CopyTo(output, b * Block * Channels);
        }
        return output;
    }

    // ── Registration ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_Creates_The_Cassette_Effect()
    {
        Assert.IsType<CassetteEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioCassette));
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioCassette)); // routes to the mixer
    }

    [Fact]
    public void Has_No_Fixed_Latency_So_It_Does_Not_Report_One()
    {
        // The modulated read sits after the write (delay = excursion ≥ 0): at rest the wet path is sample-aligned.
        // Checked on the type: CassetteEffect is sealed, so an `is` test on an instance is decided at compile time (CS0184).
        Assert.False(typeof(IAudioEffectTail).IsAssignableFrom(typeof(CassetteEffect)));
        var effect = new CassetteEffect();
        var impulse = new float[256 * Channels];
        impulse[0] = impulse[1] = 1f;
        effect.Process(impulse, 256, Rate, Channels, Params(
            (EffectParamNames.AgcAmount, 0.0), (EffectParamNames.HissDb, -90.0), (EffectParamNames.WowFlutterDepth, 0.0),
            (EffectParamNames.LowCutHz, 20.0), (EffectParamNames.HighCutHz, 20000.0)));
        Assert.NotEqual(0f, impulse[0]); // the response starts on the input sample, not after a delay
    }

    // ── Allocation / determinism ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Steady_State_Processing_Does_Not_Allocate()
    {
        foreach (bool mono in new[] { true, false })
        {
            var effect = new CassetteEffect();
            ResolvedEffect p = Params((EffectParamNames.Mono, mono ? 1.0 : 0.0), (EffectParamNames.WowFlutterDepth, 0.6));
            var block = new float[1024 * Channels];
            for (int i = 0; i < 100; i++)
                effect.Process(block, 1024, Rate, Channels, p);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
                effect.Process(block, 1024, Rate, Channels, p);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated == 0, $"steady-state Process (mono={mono}) allocated {allocated} bytes");
        }
    }

    [Fact]
    public void Output_Is_Deterministic_Run_To_Run_And_After_Reset()
    {
        ResolvedEffect p = Params((EffectParamNames.WowFlutterDepth, 0.7), (EffectParamNames.HissDb, -30.0));
        float[] first = Render(new CassetteEffect(), p, 24);
        float[] second = Render(new CassetteEffect(), p, 24);
        Assert.Equal(first, second);

        var reused = new CassetteEffect();
        Render(reused, Params((EffectParamNames.AgcAmount, 1.0)), 7); // dirty every piece of state
        reused.Reset();
        Assert.Equal(first, Render(reused, p, 24));
    }

    [Fact]
    public void Block_Split_Matches_One_Big_Block()
    {
        ResolvedEffect p = Params((EffectParamNames.WowFlutterDepth, 0.5), (EffectParamNames.Mono, 0.0));
        const int Frames = 9000;
        float[] whole = StereoMaterial(Frames);
        new CassetteEffect().Process(whole, Frames, Rate, Channels, p);

        float[] split = StereoMaterial(Frames);
        var effect = new CassetteEffect();
        int[] sizes = [1, 17, 480, 1024, 3, 2000, 4475, 1000];
        int at = 0;
        foreach (int size in sizes)
        {
            effect.Process(split.AsSpan(at * Channels, size * Channels), size, Rate, Channels, p);
            at += size;
        }
        Assert.Equal(Frames, at);
        Assert.Equal(whole, split);
    }

    // ── Mono / Mix ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mono_Writes_The_Same_Signal_To_Every_Channel()
    {
        float[] output = Render(new CassetteEffect(), Params((EffectParamNames.Mono, 1.0)), 8);
        for (int f = 0; f < output.Length / Channels; f++)
            Assert.Equal(output[f * Channels], output[f * Channels + 1]);
        Assert.True(Rms(output) > 0.01);
    }

    [Fact]
    public void Mono_Off_Keeps_The_Channels_Distinct()
    {
        float[] output = Render(new CassetteEffect(), Params((EffectParamNames.Mono, 0.0)), 8);
        int differing = 0;
        for (int f = 0; f < output.Length / Channels; f++)
            if (output[f * Channels] != output[f * Channels + 1])
                differing++;
        Assert.True(differing > output.Length / Channels / 2);
    }

    [Fact]
    public void Zero_Mix_Is_A_Bit_Exact_Pass_Through()
    {
        float[] buffer = StereoMaterial(4096);
        float[] expected = (float[])buffer.Clone();
        new CassetteEffect().Process(buffer, 4096, Rate, Channels, Params((EffectParamNames.Mix, 0.0)));
        Assert.Equal(expected, buffer);
    }

    // ── Band-limit ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Band_Limit_Passes_1kHz_And_Cuts_40Hz_And_12kHz_At_24dB_Per_Octave()
    {
        // Defaults: 100 Hz low cut, 5 kHz high cut, 4th-order Butterworth each. 40 Hz is 1.3 octaves below the low
        // corner (analog ≈ −32 dB) and 12 kHz 1.26 octaves above the high corner (≈ −30 dB; the bilinear transform
        // only steepens it) — a 12 dB/oct filter would manage ~16 dB, so ≥ 24 dB proves the cascade.
        ResolvedEffect p = FilterOnly();
        double passDb = Db(SettledSineAmplitude(1000, p) / 0.25);
        Assert.InRange(passDb, -0.5, 0.2);
        Assert.True(Db(SettledSineAmplitude(40, p) / 0.25) <= -24, "40 Hz should sit ≥ 24 dB down");
        Assert.True(Db(SettledSineAmplitude(12000, p) / 0.25) <= -24, "12 kHz should sit ≥ 24 dB down");
    }

    [Fact]
    public void Corner_Frequencies_Sit_About_3dB_Down()
    {
        ResolvedEffect p = FilterOnly((EffectParamNames.LowCutHz, 100.0), (EffectParamNames.HighCutHz, 5000.0));
        Assert.InRange(Db(SettledSineAmplitude(5000, p) / 0.25), -4.0, -2.0);
        Assert.InRange(Db(SettledSineAmplitude(100, p) / 0.25), -4.0, -2.0);
    }

    // ── Hiss ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-42.0, 100.0, 5000.0)]
    [InlineData(-30.0, 300.0, 3000.0)]
    [InlineData(-60.0, 50.0, 12000.0)]
    public void Hiss_On_Silence_Sits_At_The_Set_Level(double hissDb, double lowCut, double highCut)
    {
        var effect = new CassetteEffect();
        ResolvedEffect p = Params(
            (EffectParamNames.HissDb, hissDb), (EffectParamNames.LowCutHz, lowCut), (EffectParamNames.HighCutHz, highCut),
            (EffectParamNames.WowFlutterDepth, 0.0), (EffectParamNames.AgcAmount, 1.0));
        var warm = new float[4800 * Channels];
        effect.Process(warm, 4800, Rate, Channels, p); // let the filters settle
        var silence = new float[Rate * Channels];
        effect.Process(silence, Rate, Rate, Channels, p);
        // Hiss is laid down after the AGC, so even a maxed AGC on silence leaves it at the set level.
        Assert.InRange(Db(Rms(silence)), hissDb - 1.5, hissDb + 1.5);
    }

    [Fact]
    public void Hiss_At_Minus_90_Is_Off()
    {
        var silence = new float[4800 * Channels];
        new CassetteEffect().Process(silence, 4800, Rate, Channels, Params((EffectParamNames.HissDb, -90.0)));
        Assert.All(silence, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Stereo_Hiss_Is_Decorrelated_When_Mono_Is_Off()
    {
        var silence = new float[Rate * Channels];
        new CassetteEffect().Process(silence, Rate, Rate, Channels, Params((EffectParamNames.Mono, 0.0)));
        double lr = 0, ll = 0, rr = 0;
        for (int f = 0; f < Rate; f++)
        {
            double l = silence[f * Channels], r = silence[f * Channels + 1];
            lr += l * r;
            ll += l * l;
            rr += r * r;
        }
        Assert.InRange(lr / Math.Sqrt(ll * rr), -0.1, 0.1);
    }

    // ── AGC ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Agc_Raises_Quiet_Passages_And_Pumps_After_A_Loud_Burst()
    {
        // 0.5 s of a quiet 1 kHz tone, 0.5 s loud, then quiet again: with AGC the quiet tone ends up louder than
        // without, and right after the burst the gain is still pulled down, recovering over the release (pumping).
        ResolvedEffect agc = FilterOnly((EffectParamNames.AgcAmount, 1.0), (EffectParamNames.ReleaseMs, 600.0));
        ResolvedEffect none = FilterOnly();
        int half = Rate / 2;
        float[] Program()
        {
            float[] b = Sine(Rate * 2, 1000, 0.01f);
            for (int i = half * Channels; i < 2 * half * Channels; i++)
                b[i] *= 50f; // the second half-second is a loud burst
            return b;
        }
        float[] withAgc = Program();
        new CassetteEffect().Process(withAgc, Rate * 2, Rate, Channels, agc);
        float[] without = Program();
        new CassetteEffect().Process(without, Rate * 2, Rate, Channels, none);

        double quietBefore = Rms(withAgc.AsSpan((half - 4800) * Channels, 4800 * Channels));
        Assert.True(quietBefore > 4 * Rms(without.AsSpan((half - 4800) * Channels, 4800 * Channels)),
            "AGC should lift the quiet passage");

        double justAfter = Rms(withAgc.AsSpan((2 * half + 480) * Channels, 2400 * Channels));
        double recovered = Rms(withAgc.AsSpan((4 * half - 4800) * Channels, 4800 * Channels));
        Assert.True(recovered > 2 * justAfter, $"gain should swell back after the burst ({justAfter:E2} → {recovered:E2})");
    }

    // ── Wow & flutter ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Wow_Flutter_Modulates_The_Signal_And_Zero_Depth_Does_Not()
    {
        float[] steady = Render(new CassetteEffect(), FilterOnly(), 20);
        float[] wobbly = Render(new CassetteEffect(), FilterOnly((EffectParamNames.WowFlutterDepth, 1.0)), 20);
        double diff = 0;
        for (int i = 0; i < steady.Length; i++)
            diff = Math.Max(diff, Math.Abs(steady[i] - wobbly[i]));
        Assert.True(diff > 0.01, $"full-depth wobble should audibly change the waveform (max diff {diff:E2})");
    }

    // ── Robustness ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Output_Is_Finite_And_Bounded_At_Extreme_Settings()
    {
        ResolvedEffect p = Params(
            (EffectParamNames.Drive, 1.0), (EffectParamNames.AgcAmount, 1.0), (EffectParamNames.ReleaseMs, 50.0),
            (EffectParamNames.HissDb, -20.0), (EffectParamNames.WowFlutterDepth, 1.0),
            (EffectParamNames.WowFlutterRateHz, 10.0), (EffectParamNames.LowCutHz, 1000.0),
            (EffectParamNames.HighCutHz, 1000.0), (EffectParamNames.Mono, 0.0));
        var effect = new CassetteEffect();
        for (int b = 0; b < 40; b++)
        {
            var block = new float[1024 * Channels];
            for (int i = 0; i < block.Length; i++)
                block[i] = (i / Channels) % 97 < 3 ? 4f : -2f; // clipped, DC-heavy, impulsive garbage
            effect.Process(block, 1024, Rate, Channels, p);
            foreach (float s in block)
            {
                Assert.False(float.IsNaN(s) || float.IsInfinity(s));
                Assert.True(Math.Abs(s) < 4f, $"output {s} unbounded");
            }
        }
    }

    [Fact]
    public void Every_Preset_Renders_Finite_Audible_Output()
    {
        foreach (EffectPreset preset in CassettePresets.All)
        {
            var p = new ResolvedEffect(EffectTypeIds.AudioCassette, preset.Values.ToDictionary(kv => kv.Key, kv => kv.Value));
            float[] output = Render(new CassetteEffect(), p, 8);
            Assert.All(output, s => Assert.True(float.IsFinite(s)));
            Assert.True(Rms(output) > 0.01, $"preset {preset.Name} should pass the program");
        }
    }
}
