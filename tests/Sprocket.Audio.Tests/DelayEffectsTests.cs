using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the step-46 delay family (PLAN.md step 46): Digital, Tape, Multi-Tap, and
/// Stereo delay — impulse responses land taps/echoes at the expected sample offsets and levels, feedback
/// decays and never blows up near 1.0, wow/flutter is bounded and reproducible, and ping-pong cross-feed
/// lands in the correct channel. Pure C#, golden-PCM testable (ARCHITECTURE.md §19).
/// </summary>
public class DelayEffectsTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static ResolvedEffect Params(string id, params (string Name, double Value)[] values) =>
        new(id, values.ToDictionary(v => v.Name, v => v.Value));

    /// <summary>Processes a stereo impulse (1.0 in both channels at t = 0) followed by silence through
    /// <paramref name="effect"/> in one <paramref name="frames"/>-long block, returning the block.</summary>
    private static float[] Impulse(IAudioEffect effect, ResolvedEffect parameters, int frames)
    {
        var buffer = new float[frames * Channels];
        buffer[0] = buffer[1] = 1f;
        effect.Process(buffer, frames, Rate, Channels, parameters);
        return buffer;
    }

    /// <summary>Asserts <paramref name="effect"/>'s steady-state <c>Process</c> is allocation-free (§1, §19).
    /// A generous warm-up loop first lets first-use allocation, JIT tier-up, and other one-off runtime costs
    /// settle before measuring.</summary>
    private static void AssertSteadyStateDoesNotAllocate(IAudioEffect effect, ResolvedEffect parameters)
    {
        var block = new float[1024 * Channels];
        for (int i = 0; i < 100; i++)
            effect.Process(block, 1024, Rate, Channels, parameters);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
            effect.Process(block, 1024, Rate, Channels, parameters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"steady-state Process allocated {allocated} bytes");
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
    public void Factory_Creates_All_Four_Delays()
    {
        Assert.IsType<DigitalDelayEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioDelayDigital));
        Assert.IsType<TapeDelayEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioDelayTape));
        Assert.IsType<MultiTapDelayEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioDelayMultiTap));
        Assert.IsType<StereoDelayEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioDelayStereo));
    }

    // ── Digital Delay ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Digital_Zero_Mix_Is_An_Exact_Pass_Through()
    {
        var effect = new DigitalDelayEffect();
        var buffer = new float[2048 * Channels];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)Math.Sin(i * 0.01);
        float[] expected = (float[])buffer.Clone();
        effect.Process(buffer, 2048, Rate, Channels, Params(EffectTypeIds.AudioDelayDigital, (EffectParamNames.Mix, 0.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Digital_Impulse_Echoes_At_The_Delay_Offset_With_Feedback_Decay()
    {
        // 10 ms at 48 kHz = 480 samples; high-cut at its 20 kHz ceiling bypasses the filter, so echo k lands
        // exactly at k×480 with amplitude feedback^(k-1) (mix 1 = fully wet, dry silent).
        const int D = 480;
        float[] output = Impulse(new DigitalDelayEffect(), Params(EffectTypeIds.AudioDelayDigital,
            (EffectParamNames.DelayMs, 10.0), (EffectParamNames.Feedback, 0.5),
            (EffectParamNames.HighCutHz, 20000.0), (EffectParamNames.Mix, 1.0)), 4800);

        Assert.Equal(0f, output[0], 0.00001);              // fully wet: nothing at t = 0
        Assert.Equal(1.0f, output[D * Channels], 0.0001);  // first repeat, unity
        Assert.Equal(0.5f, output[2 * D * Channels], 0.0001);
        Assert.Equal(0.25f, output[3 * D * Channels], 0.0001);
        // Between the echoes: silence.
        Assert.Equal(0f, output[(D + 100) * Channels], 0.00001);
    }

    [Fact]
    public void Digital_Feedback_Near_Unity_Stays_Bounded_And_Decays()
    {
        // Descriptor allows feedback up to 1.0; the DSP clamps below unity so the loop always converges.
        var effect = new DigitalDelayEffect();
        ResolvedEffect p = Params(EffectTypeIds.AudioDelayDigital,
            (EffectParamNames.DelayMs, 5.0), (EffectParamNames.Feedback, 1.0),
            (EffectParamNames.HighCutHz, 20000.0), (EffectParamNames.Mix, 1.0));

        var block = new float[4800 * Channels];
        block[0] = block[1] = 1f;
        float peak = 0;
        for (int b = 0; b < 100; b++) // 10 s of recirculation
        {
            effect.Process(block, 4800, Rate, Channels, p);
            foreach (float s in block)
            {
                Assert.False(float.IsNaN(s) || float.IsInfinity(s));
                peak = Math.Max(peak, Math.Abs(s));
            }
            Array.Clear(block);
        }
        Assert.True(peak <= 1.0001f, $"repeats must never exceed the impulse, peaked at {peak}");

        // And the tail is decaying: after 10 s of silence the loop is well below the first repeat.
        var probe = new float[4800 * Channels];
        effect.Process(probe, 4800, Rate, Channels, p);
        Assert.True(Rms(probe) < 0.5, $"feedback loop should decay, RMS still {Rms(probe):E2}");
    }

    [Fact]
    public void Digital_High_Cut_Darkens_Successive_Repeats()
    {
        // An 8 kHz tone burst recirculating through a 1 kHz high-cut: each pass loses top end, so the second
        // repeat is much quieter than the first (the first repeat itself is unfiltered — the cut sits in the
        // feedback path only).
        var effect = new DigitalDelayEffect();
        ResolvedEffect p = Params(EffectTypeIds.AudioDelayDigital,
            (EffectParamNames.DelayMs, 25.0), (EffectParamNames.Feedback, 0.9),
            (EffectParamNames.HighCutHz, 1000.0), (EffectParamNames.Mix, 1.0));

        const int D = 1200; // 25 ms
        var buffer = new float[Rate * Channels];
        for (int f = 0; f < 240; f++) // 5 ms burst at 8 kHz
        {
            var s = (float)(0.5 * Math.Sin(2 * Math.PI * 8000 * f / Rate));
            buffer[f * Channels] = buffer[f * Channels + 1] = s;
        }
        effect.Process(buffer, Rate, Rate, Channels, p);

        double first = Rms(buffer.AsSpan(D * Channels, 240 * Channels));
        double second = Rms(buffer.AsSpan(2 * D * Channels, 240 * Channels));
        Assert.True(second < first * 0.5,
            $"the 1 kHz high-cut should attenuate the 8 kHz repeat: first {first:E2}, second {second:E2}");
    }

    [Fact]
    public void Digital_State_Carries_Across_Blocks()
    {
        ResolvedEffect p = Params(EffectTypeIds.AudioDelayDigital,
            (EffectParamNames.DelayMs, 10.0), (EffectParamNames.Feedback, 0.5), (EffectParamNames.Mix, 0.5));
        var whole = new float[2048 * Channels];
        whole[0] = whole[1] = 1f;
        float[] halves = (float[])whole.Clone();

        new DigitalDelayEffect().Process(whole, 2048, Rate, Channels, p);
        var split = new DigitalDelayEffect();
        split.Process(halves.AsSpan(0, 1024 * Channels), 1024, Rate, Channels, p);
        split.Process(halves.AsSpan(1024 * Channels), 1024, Rate, Channels, p);

        for (int i = 0; i < whole.Length; i++)
            Assert.Equal(whole[i], halves[i], 0.00001);
    }

    [Fact]
    public void Digital_Reset_Clears_The_Tail()
    {
        var effect = new DigitalDelayEffect();
        ResolvedEffect p = Params(EffectTypeIds.AudioDelayDigital,
            (EffectParamNames.DelayMs, 10.0), (EffectParamNames.Feedback, 0.9), (EffectParamNames.Mix, 1.0));
        _ = Impulse(effect, p, 4800); // load the line
        effect.Reset();

        var silent = new float[4800 * Channels];
        effect.Process(silent, 4800, Rate, Channels, p);
        Assert.All(silent, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Digital_Steady_State_Processing_Does_Not_Allocate() =>
        AssertSteadyStateDoesNotAllocate(new DigitalDelayEffect(),
            Params(EffectTypeIds.AudioDelayDigital, (EffectParamNames.Mix, 0.5)));

    // ── Tape Delay ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tape_Zero_Mix_Is_An_Exact_Pass_Through()
    {
        var effect = new TapeDelayEffect();
        float[] buffer = new float[2048 * Channels];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)Math.Sin(i * 0.01);
        float[] expected = (float[])buffer.Clone();
        effect.Process(buffer, 2048, Rate, Channels, Params(EffectTypeIds.AudioDelayTape, (EffectParamNames.Mix, 0.0)));
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Tape_Impulse_Produces_Echoes_Near_The_Delay_Offset()
    {
        // With zero wow/flutter the read tap is exactly the configured delay: the first repeat lands at D.
        const int D = 480; // 10 ms
        float[] output = Impulse(new TapeDelayEffect(), Params(EffectTypeIds.AudioDelayTape,
            (EffectParamNames.DelayMs, 10.0), (EffectParamNames.Feedback, 0.5),
            (EffectParamNames.WowFlutterDepth, 0.0), (EffectParamNames.Mix, 1.0)), 4800);
        Assert.Equal(1.0f, output[D * Channels], 0.001);
        Assert.Equal(0f, output[(D + 100) * Channels], 0.001);
    }

    [Fact]
    public void Tape_Wow_Flutter_Is_Bounded_And_Reproducible_Run_To_Run()
    {
        ResolvedEffect p = Params(EffectTypeIds.AudioDelayTape,
            (EffectParamNames.DelayMs, 50.0), (EffectParamNames.Feedback, 0.7),
            (EffectParamNames.WowFlutterDepth, 1.0), (EffectParamNames.WowFlutterRateHz, 5.0),
            (EffectParamNames.Drive, 1.0), (EffectParamNames.Mix, 1.0));

        static float[] Run(ResolvedEffect p)
        {
            var effect = new TapeDelayEffect();
            var output = new float[Rate * Channels];
            var block = new float[4800 * Channels];
            for (int b = 0; b < 10; b++) // 1 s of sustained tone through max modulation + drive
            {
                for (int f = 0; f < 4800; f++)
                {
                    var s = (float)(0.8 * Math.Sin(2 * Math.PI * 440 * (b * 4800 + f) / Rate));
                    block[f * Channels] = block[f * Channels + 1] = s;
                }
                effect.Process(block, 4800, Rate, Channels, p);
                block.CopyTo(output, b * 4800 * Channels);
            }
            return output;
        }

        float[] a = Run(p);
        float[] b = Run(p);
        Assert.Equal(a, b); // bit-exact: the LFOs are functions of the sample counter, no RNG
        foreach (float s in a)
        {
            Assert.False(float.IsNaN(s) || float.IsInfinity(s));
            Assert.True(Math.Abs(s) < 4f, $"tape loop must stay bounded, saw {s}");
        }
    }

    [Fact]
    public void Tape_Saturation_Soft_Clips_The_Repeats()
    {
        // A full-scale impulse through heavy drive: the repeat comes back compressed (tanh soft-clip),
        // clearly below the clean repeat's level.
        const int D = 480;
        float[] clean = Impulse(new TapeDelayEffect(), Params(EffectTypeIds.AudioDelayTape,
            (EffectParamNames.DelayMs, 10.0), (EffectParamNames.Feedback, 0.9),
            (EffectParamNames.WowFlutterDepth, 0.0), (EffectParamNames.Drive, 0.0), (EffectParamNames.Mix, 1.0)), 4800);
        float[] driven = Impulse(new TapeDelayEffect(), Params(EffectTypeIds.AudioDelayTape,
            (EffectParamNames.DelayMs, 10.0), (EffectParamNames.Feedback, 0.9),
            (EffectParamNames.WowFlutterDepth, 0.0), (EffectParamNames.Drive, 1.0), (EffectParamNames.Mix, 1.0)), 4800);

        // First repeats match (saturation sits in the feedback path); the SECOND repeat has been through it.
        Assert.Equal(clean[D * Channels], driven[D * Channels], 0.001);
        Assert.True(Math.Abs(driven[2 * D * Channels]) < Math.Abs(clean[2 * D * Channels]) * 0.9,
            $"driven second repeat {driven[2 * D * Channels]:F4} should sit below clean {clean[2 * D * Channels]:F4}");
    }

    [Fact]
    public void Tape_Steady_State_Processing_Does_Not_Allocate() =>
        AssertSteadyStateDoesNotAllocate(new TapeDelayEffect(),
            Params(EffectTypeIds.AudioDelayTape, (EffectParamNames.Mix, 0.5), (EffectParamNames.WowFlutterDepth, 0.5)));

    // ── Multi-Tap Delay ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MultiTap_Taps_Land_At_Their_Offsets_And_Levels()
    {
        // Two centred taps: 10 ms at level 1.0 and 20 ms at level 0.5. Constant-power centre pan puts
        // cos(45°) ≈ 0.7071 of each tap in each channel.
        float[] output = Impulse(new MultiTapDelayEffect(), Params(EffectTypeIds.AudioDelayMultiTap,
            (EffectParamNames.TapEnable[0], 1.0), (EffectParamNames.TapTimeMs[0], 10.0),
            (EffectParamNames.TapLevel[0], 1.0), (EffectParamNames.TapPan[0], 0.0),
            (EffectParamNames.TapEnable[1], 1.0), (EffectParamNames.TapTimeMs[1], 20.0),
            (EffectParamNames.TapLevel[1], 0.5), (EffectParamNames.TapPan[1], 0.0),
            (EffectParamNames.Mix, 1.0)), 4800);

        float centre = MathF.Cos(MathF.PI / 4);
        Assert.Equal(centre, output[480 * Channels], 0.001);
        Assert.Equal(0.5f * centre, output[960 * Channels], 0.001);
        Assert.Equal(0f, output[(480 + 100) * Channels], 0.0001); // no feedback: nothing between/after taps
        Assert.Equal(0f, output[1440 * Channels], 0.0001);
    }

    [Fact]
    public void MultiTap_Pan_Places_A_Tap_In_The_Correct_Channel()
    {
        float[] output = Impulse(new MultiTapDelayEffect(), Params(EffectTypeIds.AudioDelayMultiTap,
            (EffectParamNames.TapEnable[0], 1.0), (EffectParamNames.TapTimeMs[0], 10.0),
            (EffectParamNames.TapLevel[0], 1.0), (EffectParamNames.TapPan[0], -1.0), // hard left
            (EffectParamNames.TapEnable[1], 1.0), (EffectParamNames.TapTimeMs[1], 20.0),
            (EffectParamNames.TapLevel[1], 1.0), (EffectParamNames.TapPan[1], 1.0),  // hard right
            (EffectParamNames.Mix, 1.0)), 4800);

        Assert.Equal(1.0f, output[480 * Channels], 0.001);      // tap 1 → left
        Assert.Equal(0f, output[480 * Channels + 1], 0.001);
        Assert.Equal(0f, output[960 * Channels], 0.001);        // tap 2 → right
        Assert.Equal(1.0f, output[960 * Channels + 1], 0.001);
    }

    [Fact]
    public void MultiTap_Disabled_Taps_Are_Silent()
    {
        float[] output = Impulse(new MultiTapDelayEffect(), Params(EffectTypeIds.AudioDelayMultiTap,
            (EffectParamNames.TapEnable[0], 1.0), (EffectParamNames.TapTimeMs[0], 10.0), (EffectParamNames.TapLevel[0], 1.0),
            (EffectParamNames.TapEnable[1], 0.0), (EffectParamNames.TapTimeMs[1], 20.0), (EffectParamNames.TapLevel[1], 1.0),
            (EffectParamNames.Mix, 1.0)), 4800);
        Assert.NotEqual(0f, output[480 * Channels]);
        Assert.Equal(0f, output[960 * Channels], 0.0001);
    }

    [Fact]
    public void MultiTap_Steady_State_Processing_Does_Not_Allocate() =>
        AssertSteadyStateDoesNotAllocate(new MultiTapDelayEffect(),
            Params(EffectTypeIds.AudioDelayMultiTap, (EffectParamNames.Mix, 0.5)));

    // ── Stereo Delay ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stereo_Independent_Left_And_Right_Times()
    {
        float[] output = Impulse(new StereoDelayEffect(), Params(EffectTypeIds.AudioDelayStereo,
            (EffectParamNames.LeftTimeMs, 10.0), (EffectParamNames.RightTimeMs, 20.0),
            (EffectParamNames.Feedback, 0.0), (EffectParamNames.Mix, 1.0)), 4800);

        Assert.Equal(1.0f, output[480 * Channels], 0.001);      // left repeat at 10 ms
        Assert.Equal(0f, output[480 * Channels + 1], 0.001);    // right still silent
        Assert.Equal(0f, output[960 * Channels], 0.001);
        Assert.Equal(1.0f, output[960 * Channels + 1], 0.001);  // right repeat at 20 ms
    }

    [Fact]
    public void Stereo_Ping_Pong_Cross_Feeds_Repeats_Into_The_Opposite_Channel()
    {
        // Impulse in the LEFT channel only. Ping Pong on, full cross-feed: the left repeat (10 ms) recirculates
        // into the RIGHT line, emerging in the right channel at 10 ms + 20 ms with the feedback gain.
        var buffer = new float[4800 * Channels];
        buffer[0] = 1f; // left only
        new StereoDelayEffect().Process(buffer, 4800, Rate, Channels, Params(EffectTypeIds.AudioDelayStereo,
            (EffectParamNames.LeftTimeMs, 10.0), (EffectParamNames.RightTimeMs, 20.0),
            (EffectParamNames.Feedback, 0.5), (EffectParamNames.PingPong, 1.0),
            (EffectParamNames.CrossFeed, 1.0), (EffectParamNames.Mix, 1.0)));

        Assert.Equal(1.0f, buffer[480 * Channels], 0.001);               // L bounce at 10 ms
        Assert.Equal(0.5f, buffer[(480 + 960) * Channels + 1], 0.001);   // → R at 30 ms, one feedback pass
        Assert.Equal(0f, buffer[(480 + 960) * Channels], 0.001);         // and NOT back into L
        Assert.Equal(0.25f, buffer[(2 * 480 + 960) * Channels], 0.001);  // → L again at 40 ms
    }

    [Fact]
    public void Stereo_Ping_Pong_Off_Keeps_The_Channels_Independent()
    {
        // Impulse left-only with Ping Pong off: the right channel never sounds, whatever CrossFeed says.
        var buffer = new float[4800 * Channels];
        buffer[0] = 1f;
        new StereoDelayEffect().Process(buffer, 4800, Rate, Channels, Params(EffectTypeIds.AudioDelayStereo,
            (EffectParamNames.LeftTimeMs, 10.0), (EffectParamNames.RightTimeMs, 20.0),
            (EffectParamNames.Feedback, 0.7), (EffectParamNames.PingPong, 0.0),
            (EffectParamNames.CrossFeed, 1.0), (EffectParamNames.Mix, 1.0)));

        for (int f = 0; f < 4800; f++)
            Assert.Equal(0f, buffer[f * Channels + 1]);
        Assert.Equal(1.0f, buffer[480 * Channels], 0.001);
        Assert.Equal(0.7f, buffer[960 * Channels], 0.001);
    }

    [Fact]
    public void Stereo_Reset_Clears_Both_Lines()
    {
        var effect = new StereoDelayEffect();
        ResolvedEffect p = Params(EffectTypeIds.AudioDelayStereo,
            (EffectParamNames.Feedback, 0.9), (EffectParamNames.Mix, 1.0));
        _ = Impulse(effect, p, 4800);
        effect.Reset();

        var silent = new float[4800 * Channels];
        effect.Process(silent, 4800, Rate, Channels, p);
        Assert.All(silent, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Stereo_Steady_State_Processing_Does_Not_Allocate() =>
        AssertSteadyStateDoesNotAllocate(new StereoDelayEffect(),
            Params(EffectTypeIds.AudioDelayStereo, (EffectParamNames.PingPong, 1.0), (EffectParamNames.Mix, 0.5)));
}
