using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the Shelving EQ (PLAN.md step 48): 0 dB is a bit-exact pass-through, a
/// boost/cut matches the expected shelf magnitude response at DC / Nyquist and sits at half the dB gain at
/// the corner frequency, the slope S steepens the transition monotonically, each shelf enables independently,
/// state carries across blocks, and steady-state processing is allocation-free. Pure C#, golden-PCM testable
/// (ARCHITECTURE.md §19).
/// </summary>
public class ShelvingEqEffectTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static ResolvedEffect Params(params (string Name, double Value)[] values) =>
        new(EffectTypeIds.AudioShelvingEq, values.ToDictionary(v => v.Name, v => v.Value));

    /// <summary>A stereo buffer of <paramref name="frames"/> frames, every sample <paramref name="value"/>.</summary>
    private static float[] Dc(int frames, float value)
    {
        var buffer = new float[frames * Channels];
        buffer.AsSpan().Fill(value);
        return buffer;
    }

    /// <summary>A stereo buffer alternating +/− <paramref name="value"/> per frame — a full-scale Nyquist tone.</summary>
    private static float[] Nyquist(int frames, float value)
    {
        var buffer = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
        {
            float s = f % 2 == 0 ? value : -value;
            for (int ch = 0; ch < Channels; ch++)
                buffer[f * Channels + ch] = s;
        }
        return buffer;
    }

    /// <summary>A stereo sine of <paramref name="frames"/> frames at <paramref name="freq"/> Hz, amplitude
    /// <paramref name="amplitude"/>.</summary>
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

    /// <summary>Runs a 1 s sine at <paramref name="freq"/> through the effect and returns the settled output
    /// amplitude (peak |sample| over the final 10 ms, several cycles at any tested frequency).</summary>
    private static float SettledSineAmplitude(double freq, ResolvedEffect parameters)
    {
        float[] buffer = Sine(Rate, freq, 0.25f);
        new ShelvingEqEffect().Process(buffer, Rate, Rate, Channels, parameters);
        float peak = 0f;
        foreach (float s in buffer.AsSpan((Rate - 480) * Channels))
            peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    [Fact]
    public void Factory_Creates_The_Shelving_Eq()
    {
        Assert.IsType<ShelvingEqEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioShelvingEq));
    }

    [Fact]
    public void Zero_Gain_Is_A_Bit_Exact_Pass_Through()
    {
        // Both shelves enabled but at 0 dB — the default instance — must not touch a single sample.
        float[] buffer = Sine(2048, 440, 0.5f);
        float[] expected = (float[])buffer.Clone();
        new ShelvingEqEffect().Process(buffer, 2048, Rate, Channels, Params(
            (EffectParamNames.LowGainDb, 0.0), (EffectParamNames.HighGainDb, 0.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Disabled_Shelves_Are_A_Bit_Exact_Pass_Through()
    {
        // Nonzero gains, but both enables off: exact pass-through, the per-shelf enable wins.
        float[] buffer = Sine(2048, 440, 0.5f);
        float[] expected = (float[])buffer.Clone();
        new ShelvingEqEffect().Process(buffer, 2048, Rate, Channels, Params(
            (EffectParamNames.LowGainDb, 12.0), (EffectParamNames.LowEnable, 0.0),
            (EffectParamNames.HighGainDb, 12.0), (EffectParamNames.HighEnable, 0.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Low_Shelf_Boost_Matches_The_Shelf_Response_At_DC_And_Nyquist()
    {
        // A +12 dB low shelf at 200 Hz: DC settles at ×10^(12/20) ≈ 3.981, Nyquist is untouched.
        ResolvedEffect p = Params((EffectParamNames.LowGainDb, 12.0), (EffectParamNames.LowFreq, 200.0));

        float[] dc = Dc(Rate, 0.25f);
        new ShelvingEqEffect().Process(dc, Rate, Rate, Channels, p);
        Assert.Equal(0.25f * (float)Math.Pow(10, 12 / 20.0), dc[^1], 0.005);

        float[] nyq = Nyquist(Rate, 0.25f);
        new ShelvingEqEffect().Process(nyq, Rate, Rate, Channels, p);
        Assert.Equal(0.25f, Math.Abs(nyq[^1]), 0.005);
    }

    [Fact]
    public void Low_Shelf_Cut_Matches_The_Shelf_Response_At_DC()
    {
        float[] dc = Dc(Rate, 0.25f);
        new ShelvingEqEffect().Process(dc, Rate, Rate, Channels, Params(
            (EffectParamNames.LowGainDb, -12.0), (EffectParamNames.LowFreq, 200.0)));
        Assert.Equal(0.25f * (float)Math.Pow(10, -12 / 20.0), dc[^1], 0.005);
    }

    [Fact]
    public void High_Shelf_Boost_Matches_The_Shelf_Response_At_Nyquist_And_DC()
    {
        // A +12 dB high shelf at 2 kHz: Nyquist settles at ×10^(12/20) ≈ 3.981, DC is untouched.
        ResolvedEffect p = Params((EffectParamNames.HighGainDb, 12.0), (EffectParamNames.HighFreq, 2000.0));

        float[] nyq = Nyquist(Rate, 0.25f);
        new ShelvingEqEffect().Process(nyq, Rate, Rate, Channels, p);
        Assert.Equal(0.25f * (float)Math.Pow(10, 12 / 20.0), Math.Abs(nyq[^1]), 0.005);

        float[] dc = Dc(Rate, 0.25f);
        new ShelvingEqEffect().Process(dc, Rate, Rate, Channels, p);
        Assert.Equal(0.25f, dc[^1], 0.005);
    }

    [Fact]
    public void Corner_Frequency_Sits_At_Half_The_Gain_In_Db()
    {
        // The RBJ shelf's corner is its dB midpoint: a +12 dB low shelf at 1 kHz passes a 1 kHz tone at
        // ×10^(12/40) ≈ 1.995 — half the boost in dB — for any slope.
        float amplitude = SettledSineAmplitude(1000, Params(
            (EffectParamNames.LowGainDb, 12.0), (EffectParamNames.LowFreq, 1000.0)));
        Assert.Equal(0.25f * (float)Math.Pow(10, 12 / 40.0), amplitude, 0.01);
    }

    [Fact]
    public void Slope_Steepens_The_Transition_Monotonically()
    {
        // A +12 dB low shelf at 1 kHz, probed one octave above the corner: a steeper S confines the boost
        // below the corner, so the 2 kHz level must fall monotonically as S rises.
        static float At(double slope) => SettledSineAmplitude(2000, Params(
            (EffectParamNames.LowGainDb, 12.0), (EffectParamNames.LowFreq, 1000.0),
            (EffectParamNames.LowSlope, slope)));

        float gentle = At(0.4), classic = At(1.0), steep = At(2.0);
        Assert.True(gentle > classic + 0.005f, $"S 0.4 ({gentle}) must pass more than S 1.0 ({classic}) above the corner");
        Assert.True(classic > steep + 0.005f, $"S 1.0 ({classic}) must pass more than S 2.0 ({steep}) above the corner");
    }

    [Fact]
    public void Shelves_Enable_Independently()
    {
        // Low shelf disabled, high shelf boosting: DC (the low shelf's territory) is untouched while
        // Nyquist carries the high shelf's +12 dB — a single instance running one shelf alone.
        ResolvedEffect p = Params(
            (EffectParamNames.LowGainDb, 12.0), (EffectParamNames.LowEnable, 0.0),
            (EffectParamNames.HighGainDb, 12.0), (EffectParamNames.HighFreq, 2000.0));

        float[] dc = Dc(Rate, 0.25f);
        new ShelvingEqEffect().Process(dc, Rate, Rate, Channels, p);
        Assert.Equal(0.25f, dc[^1], 0.005);

        float[] nyq = Nyquist(Rate, 0.25f);
        new ShelvingEqEffect().Process(nyq, Rate, Rate, Channels, p);
        Assert.Equal(0.25f * (float)Math.Pow(10, 12 / 20.0), Math.Abs(nyq[^1]), 0.005);
    }

    [Fact]
    public void State_Carries_Across_Blocks()
    {
        ResolvedEffect p = Params(
            (EffectParamNames.LowGainDb, 9.0), (EffectParamNames.LowFreq, 150.0),
            (EffectParamNames.HighGainDb, -6.0), (EffectParamNames.HighFreq, 6000.0));
        float[] whole = Sine(4096, 440, 0.5f);
        float[] halves = (float[])whole.Clone();

        new ShelvingEqEffect().Process(whole, 4096, Rate, Channels, p);
        var split = new ShelvingEqEffect();
        split.Process(halves.AsSpan(0, 2048 * Channels), 2048, Rate, Channels, p);
        split.Process(halves.AsSpan(2048 * Channels), 2048, Rate, Channels, p);

        for (int i = 0; i < whole.Length; i++)
            Assert.Equal(whole[i], halves[i], 0.00001);
    }

    [Fact]
    public void Steady_State_Processing_Does_Not_Allocate()
    {
        var effect = new ShelvingEqEffect();
        ResolvedEffect p = Params(
            (EffectParamNames.LowGainDb, 6.0), (EffectParamNames.HighGainDb, -6.0));
        var block = new float[1024 * Channels];
        block.AsSpan().Fill(0.25f);
        for (int i = 0; i < 100; i++)
            effect.Process(block, 1024, Rate, Channels, p);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
            effect.Process(block, 1024, Rate, Channels, p);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"steady-state Process allocated {allocated} bytes");
    }
}
