using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the built-in managed audio DSP effects (PLAN.md step 31): gain/pan, the
/// three-band parametric EQ, the compressor, and the reverb — all pure C# and golden-PCM testable
/// (ARCHITECTURE.md §19).
/// </summary>
public class AudioEffectsTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static ResolvedEffect Params(string id, params (string Name, double Value)[] values) =>
        new(id, values.ToDictionary(v => v.Name, v => v.Value));

    private static float[] Sine(double freq, int frames, float amp = 0.5f, int channels = Channels)
    {
        var buffer = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            var s = (float)(amp * Math.Sin(2 * Math.PI * freq * f / Rate));
            for (int ch = 0; ch < channels; ch++)
                buffer[f * channels + ch] = s;
        }
        return buffer;
    }

    private static double Rms(ReadOnlySpan<float> buffer)
    {
        double sum = 0;
        foreach (float s in buffer)
            sum += (double)s * s;
        return Math.Sqrt(sum / buffer.Length);
    }

    // ── Factory ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_Creates_All_Built_Ins_And_Null_For_Unknown()
    {
        Assert.IsType<GainPanEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioGain));
        Assert.IsType<ParametricEqEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioEq));
        Assert.IsType<CompressorEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioCompressor));
        Assert.IsType<ReverbEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioReverb));
        Assert.Null(BuiltInAudioEffects.Create("plugin.acme.unknown"));
    }

    // ── Gain / Pan ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gain_Minus_Six_Db_Halves_The_Signal()
    {
        var effect = new GainPanEffect();
        var buffer = new float[256 * Channels];
        buffer.AsSpan().Fill(0.8f);
        effect.Process(buffer, 256, Rate, Channels, Params(EffectTypeIds.AudioGain, (EffectParamNames.GainDb, -6.0206)));
        Assert.All(buffer, s => Assert.Equal(0.4f, s, 0.001));
    }

    [Fact]
    public void Hard_Right_Pan_Silences_The_Left_Channel()
    {
        var effect = new GainPanEffect();
        var buffer = new float[256 * Channels];
        buffer.AsSpan().Fill(0.5f);
        effect.Process(buffer, 256, Rate, Channels, Params(EffectTypeIds.AudioGain, (EffectParamNames.Pan, 1.0)));
        for (int i = 0; i < buffer.Length; i += 2)
        {
            Assert.Equal(0f, buffer[i], 0.0001);
            Assert.Equal(0.5f, buffer[i + 1], 0.0001);
        }
    }

    // ── Parametric EQ ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Eq_At_Zero_Gain_Is_An_Exact_Pass_Through()
    {
        var effect = new ParametricEqEffect();
        float[] buffer = Sine(440, 4800);
        float[] expected = (float[])buffer.Clone();
        effect.Process(buffer, 4800, Rate, Channels, Params(EffectTypeIds.AudioEq));
        Assert.Equal(expected, buffer); // bypassed bands touch nothing
    }

    [Fact]
    public void Low_Shelf_Boost_Raises_Low_Frequencies_And_Leaves_Highs_Alone()
    {
        // +12 dB low shelf at 200 Hz: a 50 Hz sine (well below) gains ~4×; an 8 kHz sine is ~unchanged.
        ResolvedEffect p = Params(EffectTypeIds.AudioEq,
            (EffectParamNames.LowGainDb, 12.0), (EffectParamNames.LowFreq, 200.0));

        float[] low = Sine(50, Rate); // 1 s so the filter settles
        double lowBefore = Rms(low);
        new ParametricEqEffect().Process(low, Rate, Rate, Channels, p);
        double lowAfter = Rms(low.AsSpan(Rate)); // skip the first half (transient)

        float[] high = Sine(8000, Rate, amp: 0.1f);
        double highBefore = Rms(high);
        new ParametricEqEffect().Process(high, Rate, Rate, Channels, p);
        double highAfter = Rms(high.AsSpan(Rate));

        Assert.True(lowAfter > lowBefore * 3.0, $"low band should gain ~4x, got {lowAfter / lowBefore:F2}x");
        Assert.Equal(1.0, highAfter / highBefore, 0.1);
    }

    [Fact]
    public void Mid_Cut_Attenuates_The_Centre_Frequency()
    {
        ResolvedEffect p = Params(EffectTypeIds.AudioEq,
            (EffectParamNames.MidGainDb, -12.0), (EffectParamNames.MidFreq, 1000.0), (EffectParamNames.MidQ, 1.0));

        float[] mid = Sine(1000, Rate);
        double before = Rms(mid);
        new ParametricEqEffect().Process(mid, Rate, Rate, Channels, p);
        double after = Rms(mid.AsSpan(Rate));
        Assert.True(after < before * 0.35, $"1 kHz should drop ~-12 dB, got {20 * Math.Log10(after / before):F1} dB");
    }

    [Fact]
    public void Eq_State_Carries_Across_Blocks()
    {
        // Processing one long buffer must equal processing the same samples in two halves — the filter
        // memory carries across Process calls (block-based chain execution, §19).
        ResolvedEffect p = Params(EffectTypeIds.AudioEq,
            (EffectParamNames.HighGainDb, 9.0), (EffectParamNames.HighFreq, 4000.0));
        float[] whole = Sine(3000, 2048);
        float[] halves = (float[])whole.Clone();

        new ParametricEqEffect().Process(whole, 2048, Rate, Channels, p);
        var split = new ParametricEqEffect();
        split.Process(halves.AsSpan(0, 1024 * Channels), 1024, Rate, Channels, p);
        split.Process(halves.AsSpan(1024 * Channels), 1024, Rate, Channels, p);

        for (int i = 0; i < whole.Length; i++)
            Assert.Equal(whole[i], halves[i], 0.00001);
    }

    // ── Compressor ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compressor_Attenuates_Signal_Above_The_Threshold()
    {
        // 0 dBFS square-ish signal, threshold -12 dB, ratio 4 → steady-state reduction 12 × (1 - 1/4) = 9 dB.
        var effect = new CompressorEffect();
        var buffer = new float[Rate * Channels];
        buffer.AsSpan().Fill(1.0f);
        effect.Process(buffer, Rate, Rate, Channels, Params(EffectTypeIds.AudioCompressor,
            (EffectParamNames.ThresholdDb, -12.0), (EffectParamNames.Ratio, 4.0),
            (EffectParamNames.AttackMs, 1.0), (EffectParamNames.ReleaseMs, 50.0)));

        float steady = buffer[^1];
        Assert.Equal(Math.Pow(10, -9.0 / 20), steady, 0.01); // ≈ 0.355
    }

    [Fact]
    public void Compressor_Leaves_Signal_Below_The_Threshold_Alone()
    {
        var effect = new CompressorEffect();
        var buffer = new float[4800 * Channels];
        buffer.AsSpan().Fill(0.1f); // -20 dB, below the -12 dB threshold
        effect.Process(buffer, 4800, Rate, Channels, Params(EffectTypeIds.AudioCompressor,
            (EffectParamNames.ThresholdDb, -12.0), (EffectParamNames.Ratio, 4.0)));
        Assert.All(buffer, s => Assert.Equal(0.1f, s, 0.0001));
    }

    [Fact]
    public void Makeup_Gain_Applies()
    {
        var effect = new CompressorEffect();
        var buffer = new float[4800 * Channels];
        buffer.AsSpan().Fill(0.1f); // below threshold → only make-up applies
        effect.Process(buffer, 4800, Rate, Channels, Params(EffectTypeIds.AudioCompressor,
            (EffectParamNames.ThresholdDb, -12.0), (EffectParamNames.Ratio, 4.0),
            (EffectParamNames.MakeupDb, 6.0206)));
        Assert.Equal(0.2f, buffer[^1], 0.001);
    }

    // ── Reverb ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reverb_At_Zero_Mix_Is_An_Exact_Pass_Through()
    {
        var effect = new ReverbEffect();
        float[] buffer = Sine(440, 2048);
        float[] expected = (float[])buffer.Clone();
        effect.Process(buffer, 2048, Rate, Channels, Params(EffectTypeIds.AudioReverb, (EffectParamNames.Mix, 0.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Reverb_Produces_A_Tail_After_The_Input_Stops()
    {
        var effect = new ReverbEffect();
        ResolvedEffect p = Params(EffectTypeIds.AudioReverb,
            (EffectParamNames.RoomSize, 0.8), (EffectParamNames.Damping, 0.2), (EffectParamNames.Mix, 1.0));

        // Excite the network with a loud burst, then process pure silence: the tail must ring on.
        float[] burst = Sine(440, 4800, amp: 0.9f);
        effect.Process(burst, 4800, Rate, Channels, p);

        var silence = new float[4800 * Channels];
        effect.Process(silence, 4800, Rate, Channels, p);
        Assert.True(Rms(silence) > 0.001, $"expected an audible tail, RMS was {Rms(silence):E2}");

        // And Reset clears it: silence in → silence out.
        effect.Reset();
        var quiet = new float[4800 * Channels];
        effect.Process(quiet, 4800, Rate, Channels, p);
        Assert.All(quiet, s => Assert.Equal(0f, s));
    }
}
