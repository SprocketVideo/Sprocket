using Sprocket.Audio.Effects;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic unit tests for the Convolution Reverb (PLAN.md step 49): an impulse reproduces the IR exactly
/// at unity mix, the zero-latency partitioned convolver matches a reference direct convolution, the surrounding
/// controls (pre-delay, length trim, damping, width) behave, output is independent of how the host frames its
/// buffers (the freeze-equivalence property), unavailable IRs degrade to pass-through, WAV import works, and
/// steady-state processing is allocation-free. Synthetic IRs are injected through the resolver seam so no test
/// depends on the background loader.
/// </summary>
public class ConvolutionReverbEffectTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const int B = ImpulseResponse.BlockSize;
    private const string IrKey = "test://ir";

    private static ImpulseResponse MonoIr(float[] taps) =>
        ImpulseResponse.FromSamples(taps, 1, Rate, Rate, normalize: false);

    private static ImpulseResponse StereoIr(float[] left, float[] right)
    {
        var interleaved = new float[left.Length * 2];
        for (int i = 0; i < left.Length; i++)
        {
            interleaved[i * 2] = left[i];
            interleaved[i * 2 + 1] = right[i];
        }
        return ImpulseResponse.FromSamples(interleaved, 2, Rate, Rate, normalize: false);
    }

    private static ConvolutionReverbEffect EffectWith(ImpulseResponse? ir) =>
        new((path, rate) => path == IrKey && rate == Rate ? ir : null);

    private static ResolvedEffect Params(bool withIr, params (string Name, double Value)[] values) =>
        new(EffectTypeIds.AudioConvolutionReverb,
            values.ToDictionary(v => v.Name, v => v.Value),
            withIr ? new Dictionary<string, string> { [EffectParamNames.ImpulseResponse] = IrKey } : null);

    private static ResolvedEffect Wet(params (string Name, double Value)[] extra) =>
        Params(true, [(EffectParamNames.Mix, 1.0), .. extra]);

    /// <summary>Runs <paramref name="input"/> (interleaved) through the effect in <paramref name="blockFrames"/>
    /// chunks — deliberately not a multiple of the convolver's internal block — returning the processed copy.</summary>
    private static float[] Run(ConvolutionReverbEffect effect, ResolvedEffect p, float[] input, int blockFrames)
    {
        float[] output = (float[])input.Clone();
        int frames = output.Length / Channels;
        for (int start = 0; start < frames; start += blockFrames)
        {
            int count = Math.Min(blockFrames, frames - start);
            effect.Process(output.AsSpan(start * Channels, count * Channels), count, Rate, Channels, p);
        }
        return output;
    }

    private static float[] Impulse(int frames, int at = 0, float amplitude = 1f)
    {
        var buffer = new float[frames * Channels];
        for (int ch = 0; ch < Channels; ch++)
            buffer[at * Channels + ch] = amplitude;
        return buffer;
    }

    private static float[] Delta(int length, int at, float amplitude = 1f)
    {
        var taps = new float[length];
        taps[at] = amplitude;
        return taps;
    }

    /// <summary>Deterministic pseudo-random taps in (−1, 1), optionally with an exponential decay envelope.</summary>
    private static float[] RandomTaps(int count, uint seed, bool decay)
    {
        var taps = new float[count];
        uint state = seed;
        for (int i = 0; i < count; i++)
        {
            state = state * 1664525u + 1013904223u;
            float r = (state >> 8) / (float)(1 << 24) * 2 - 1;
            taps[i] = decay ? r * MathF.Exp(-4f * i / count) : r;
        }
        return taps;
    }

    private static float[] NoiseStereo(int frames, uint seed, float amplitude)
    {
        float[] mono = RandomTaps(frames, seed, decay: false);
        var buffer = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
            for (int ch = 0; ch < Channels; ch++)
                buffer[f * Channels + ch] = mono[f] * amplitude;
        return buffer;
    }

    private static float[] Tone(int frames, double freq, float amplitude)
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

    private static double Rms(ReadOnlySpan<float> buffer)
    {
        double sum = 0;
        foreach (float s in buffer)
            sum += (double)s * s;
        return Math.Sqrt(sum / buffer.Length);
    }

    [Fact]
    public void Factory_Creates_The_Convolution_Reverb()
    {
        Assert.IsType<ConvolutionReverbEffect>(BuiltInAudioEffects.Create(EffectTypeIds.AudioConvolutionReverb));
    }

    [Fact]
    public void Zero_Mix_Is_An_Exact_Pass_Through()
    {
        ConvolutionReverbEffect effect = EffectWith(MonoIr(RandomTaps(2000, 1, decay: true)));
        float[] input = NoiseStereo(3000, 2, 0.8f);
        float[] output = Run(effect, Params(true, (EffectParamNames.Mix, 0.0)), input, 1000);
        Assert.Equal(input, output);
    }

    [Fact]
    public void No_IR_Asset_Passes_Through_Unchanged()
    {
        // A fresh instance has no impulse response (none are bundled): full wet mix must still be a no-op.
        ConvolutionReverbEffect effect = EffectWith(MonoIr(RandomTaps(2000, 1, decay: true)));
        float[] input = NoiseStereo(3000, 3, 0.8f);
        float[] output = Run(effect, Params(false, (EffectParamNames.Mix, 1.0)), input, 1000);
        Assert.Equal(input, output);
        Assert.False(effect.IsImpulseResponseLoaded);
    }

    [Fact]
    public void Unresolvable_IR_Passes_Through_Not_Crash()
    {
        // The asset is set but the resolver has nothing for it (still loading, missing after a project move,
        // not a WAV): the effect is transparent — the missing-IR graceful-degradation requirement.
        ConvolutionReverbEffect effect = EffectWith(null);
        float[] input = NoiseStereo(3000, 4, 0.8f);
        float[] output = Run(effect, Wet(), input, 1000);
        Assert.Equal(input, output);
        Assert.False(effect.IsImpulseResponseLoaded);
        Assert.Equal(0, effect.TailSamples);
    }

    [Fact]
    public void An_In_Use_IR_Survives_A_Cache_Eviction()
    {
        // Once loaded, the effect holds its own reference: if the shared cache later evicts the entry
        // (ImpulseResponseCache.Trim drops an in-use IR when more than MaxEntries are auditioned) the resolver
        // starts returning null, but active playback must keep convolving rather than drop to a dry gap — the
        // invariant the cache's eviction comment assumes.
        ImpulseResponse ir = MonoIr(Delta(64, 3, 0.5f));
        int calls = 0;
        var effect = new ConvolutionReverbEffect((path, rate) =>
        {
            calls++;
            return calls == 1 ? ir : null; // the first buffer loads it; a later "eviction" resolves to null
        });

        float[] first = Run(effect, Wet(), Impulse(100), 100);
        Assert.Equal(0.5f, first[3 * Channels], 1e-6f);
        Assert.True(effect.IsImpulseResponseLoaded);

        float[] second = Run(effect, Wet(), Impulse(100), 100);
        Assert.Equal(0.5f, second[3 * Channels], 1e-6f); // still convolving the retained IR
        Assert.True(effect.IsImpulseResponseLoaded);
        Assert.Equal(1, calls); // and no longer polling the cache once the IR is held
    }

    [Fact]
    public void Impulse_In_Reproduces_The_IR_At_Unity_Mix()
    {
        // The defining test: convolving δ with h gives h back — across the direct head AND the FFT partitions
        // (3000 taps = head + 5 partitions), with zero latency, at buffer framings unrelated to the block size.
        float[] taps = RandomTaps(3000, 7, decay: true);
        ConvolutionReverbEffect effect = EffectWith(MonoIr(taps));
        float[] output = Run(effect, Wet(), Impulse(4000), 1000);

        for (int f = 0; f < 4000; f++)
        {
            float expected = f < taps.Length ? taps[f] : 0f;
            for (int ch = 0; ch < Channels; ch++)
                Assert.True(Math.Abs(output[f * Channels + ch] - expected) < 2e-4f,
                    $"frame {f} ch {ch}: {output[f * Channels + ch]} vs {expected}");
        }
        Assert.True(effect.IsImpulseResponseLoaded);
    }

    [Fact]
    public void Partitioned_Output_Matches_Direct_Convolution()
    {
        // Partitioned overlap-save == the textbook O(N·L) direct convolution, within float tolerance, on a
        // dense random signal (not just an impulse) with an IR that isn't a whole number of partitions.
        float[] taps = RandomTaps(2500, 11, decay: false);
        const int Frames = 6000;
        float[] input = NoiseStereo(Frames, 12, 0.5f);
        float[] output = Run(EffectWith(MonoIr(taps)), Wet(), input, 777);

        double peak = 0, maxError = 0;
        for (int n = 0; n < Frames; n++)
        {
            double expected = 0;
            for (int m = 0; m <= Math.Min(n, taps.Length - 1); m++)
                expected += (double)taps[m] * input[(n - m) * Channels];
            peak = Math.Max(peak, Math.Abs(expected));
            maxError = Math.Max(maxError, Math.Abs(output[n * Channels] - expected));
        }
        Assert.True(maxError < 1e-3 * Math.Max(1, peak), $"max error {maxError:E2} against peak {peak:F2}");
    }

    [Fact]
    public void Stereo_IR_Maps_Left_To_Left_And_Right_To_Right()
    {
        ConvolutionReverbEffect effect = EffectWith(StereoIr(Delta(64, 0), Delta(64, 5)));
        float[] output = Run(effect, Wet(), Impulse(100), 100);
        Assert.Equal(1f, output[0 * Channels + 0], 1e-6f); // L: δ at 0
        Assert.Equal(0f, output[5 * Channels + 0], 1e-6f);
        Assert.Equal(0f, output[0 * Channels + 1], 1e-6f); // R: δ at 5
        Assert.Equal(1f, output[5 * Channels + 1], 1e-6f);
    }

    [Fact]
    public void Mono_IR_Feeds_Every_Channel()
    {
        ConvolutionReverbEffect effect = EffectWith(MonoIr(Delta(64, 3, 0.5f)));
        float[] output = Run(effect, Wet(), Impulse(100), 100);
        Assert.Equal(0.5f, output[3 * Channels + 0], 1e-6f);
        Assert.Equal(0.5f, output[3 * Channels + 1], 1e-6f);
    }

    [Fact]
    public void PreDelay_Shifts_The_Wet_Signal()
    {
        ConvolutionReverbEffect effect = EffectWith(MonoIr(Delta(16, 0)));
        float[] output = Run(effect, Wet((EffectParamNames.PreDelayMs, 10.0)), Impulse(1000), 1000);
        int expectedAt = Rate / 100; // 10 ms
        Assert.Equal(0f, output[0], 1e-6f);
        Assert.Equal(1f, output[expectedAt * Channels], 1e-6f);
    }

    [Fact]
    public void Length_Trim_Fades_The_Late_Partitions_Out()
    {
        // 8 partitions of flat 1.0 taps, trimmed to 50 %: partitions centred before 37.5 % stay at unity,
        // the one straddling the fade sits mid-fade, and everything past 50 % is silent — with no new IR.
        var flat = new float[8 * B];
        Array.Fill(flat, 1f);
        ConvolutionReverbEffect effect = EffectWith(MonoIr(flat));
        float[] output = Run(effect, Wet((EffectParamNames.IrLength, 0.5)), Impulse(8 * B), 1000);

        Assert.Equal(1f, output[100 * Channels], 1e-4f);            // partition 0 (head)
        Assert.Equal(1f, output[(2 * B + 100) * Channels], 1e-4f);  // partition 2, centre 31 % < 37.5 %
        Assert.Equal(0.5f, output[(3 * B + 100) * Channels], 1e-4f); // partition 3, centre 43.75 % = mid-fade
        Assert.Equal(0f, output[(4 * B + 100) * Channels], 1e-4f);  // partition 4 onward: trimmed away
        Assert.Equal(0f, output[(7 * B + 100) * Channels], 1e-4f);

        // Length = 1 is the untouched IR, bit-exact — the same instance recovers full length live.
        effect.Reset();
        float[] full = Run(effect, Wet((EffectParamNames.IrLength, 1.0)), Impulse(8 * B), 1000);
        Assert.Equal(1f, full[(7 * B + 100) * Channels], 1e-4f);
    }

    [Fact]
    public void HighDamp_Darkens_The_Tail()
    {
        // With an identity IR the effect is just the damping stage: an 8 kHz tone must come out much quieter
        // at full HighDamp (1 kHz low-pass) than with damping off (exact bypass).
        float[] tone = Tone(4800, 8000, 0.5f);
        float[] open = Run(EffectWith(MonoIr(Delta(16, 0))), Wet((EffectParamNames.HighDamp, 0.0)), tone, 4800);
        float[] damped = Run(EffectWith(MonoIr(Delta(16, 0))), Wet((EffectParamNames.HighDamp, 1.0)), tone, 4800);
        Assert.Equal(tone, open); // 0 damping is an exact bypass (an impulse IR passes the input verbatim)
        double openRms = Rms(open.AsSpan(2400 * Channels));
        double dampedRms = Rms(damped.AsSpan(2400 * Channels));
        Assert.True(dampedRms < openRms * 0.3, $"damped {dampedRms:F4} vs open {openRms:F4}");
    }

    [Fact]
    public void LowDamp_Thins_The_Lows()
    {
        float[] tone = Tone(9600, 40, 0.5f);
        float[] open = Run(EffectWith(MonoIr(Delta(16, 0))), Wet((EffectParamNames.LowDamp, 0.0)), tone, 4800);
        float[] damped = Run(EffectWith(MonoIr(Delta(16, 0))), Wet((EffectParamNames.LowDamp, 1.0)), tone, 4800);
        Assert.Equal(tone, open);
        double openRms = Rms(open.AsSpan(4800 * Channels));
        double dampedRms = Rms(damped.AsSpan(4800 * Channels));
        Assert.True(dampedRms < openRms * 0.3, $"damped {dampedRms:F4} vs open {openRms:F4}");
    }

    [Fact]
    public void Zero_Width_Collapses_A_Stereo_Tail_To_Mono()
    {
        ConvolutionReverbEffect effect = EffectWith(StereoIr(Delta(64, 0), Delta(64, 5)));
        float[] output = Run(effect, Wet((EffectParamNames.Width, 0.0)), Impulse(100), 100);
        for (int f = 0; f < 100; f++)
            Assert.Equal(output[f * Channels], output[f * Channels + 1]);
        Assert.Equal(0.5f, output[0], 1e-6f);
        Assert.Equal(0.5f, output[5 * Channels], 1e-6f);
    }

    [Fact]
    public void Output_Is_Independent_Of_Host_Buffer_Framing()
    {
        // The freeze-equivalence property: the freeze renderer and live playback hand the effect buffers of
        // different sizes, and must get bit-identical audio — the convolver's blocking is internal, so the
        // external framing cannot leak into the result. Every control engaged so the whole path is covered.
        ImpulseResponse ir = MonoIr(RandomTaps(3000, 21, decay: true));
        ResolvedEffect p = Params(true,
            (EffectParamNames.Mix, 0.6), (EffectParamNames.PreDelayMs, 5.0), (EffectParamNames.IrLength, 0.8),
            (EffectParamNames.HighDamp, 0.3), (EffectParamNames.LowDamp, 0.2), (EffectParamNames.Width, 0.7));
        float[] input = NoiseStereo(20000, 22, 0.5f);
        float[] live = Run(EffectWith(ir), p, input, 4800);
        float[] frozen = Run(EffectWith(ir), p, input, 331);
        Assert.Equal(live, frozen);
    }

    [Fact]
    public void Tail_Metadata_Reports_Zero_Latency_And_The_IR_Length()
    {
        ConvolutionReverbEffect effect = EffectWith(MonoIr(RandomTaps(2345, 5, decay: true)));
        IAudioEffectTail tail = effect;
        Assert.Equal(0, tail.LatencySamples);
        Assert.Equal(0, tail.TailSamples); // nothing loaded yet
        _ = Run(effect, Wet(), Impulse(100), 100);
        Assert.Equal(2345, tail.TailSamples);
        Assert.True(AudioEffectTraits.IsHeavy(EffectTypeIds.AudioConvolutionReverb));
    }

    [Fact]
    public void Reset_Clears_The_Tail()
    {
        ConvolutionReverbEffect effect = EffectWith(MonoIr(RandomTaps(3000, 9, decay: false)));
        _ = Run(effect, Wet(), Impulse(1000), 1000); // load the FDL and pending tail
        effect.Reset();
        float[] silent = Run(effect, Wet(), new float[2000 * Channels], 1000);
        Assert.All(silent, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Steady_State_Processing_Does_Not_Allocate()
    {
        ConvolutionReverbEffect effect = EffectWith(MonoIr(RandomTaps(4 * Rate, 13, decay: true))); // a 4 s hall
        ResolvedEffect p = Wet((EffectParamNames.IrLength, 0.9), (EffectParamNames.HighDamp, 0.2),
            (EffectParamNames.PreDelayMs, 10.0), (EffectParamNames.Width, 0.8));
        var block = new float[1024 * Channels];
        for (int i = 0; i < 100; i++) // warm-up: allocate the convolvers + let tiered JIT settle
            effect.Process(block, 1024, Rate, Channels, p);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
            effect.Process(block, 1024, Rate, Channels, p);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"steady-state Process allocated {allocated} bytes");
    }

    [Fact]
    public void Fft_Round_Trips_And_Matches_A_Naive_Dft()
    {
        var plan = new FftPlan(64);
        float[] re = RandomTaps(64, 31, decay: false);
        var im = new float[64];
        float[] original = (float[])re.Clone();
        plan.Forward(re, im);
        for (int k = 0; k < 64; k += 7)
        {
            double er = 0, ei = 0;
            for (int n = 0; n < 64; n++)
            {
                double a = -2 * Math.PI * k * n / 64;
                er += original[n] * Math.Cos(a);
                ei += original[n] * Math.Sin(a);
            }
            Assert.Equal(er, re[k], 1e-4);
            Assert.Equal(ei, im[k], 1e-4);
        }
        plan.Inverse(re, im);
        for (int n = 0; n < 64; n++)
            Assert.Equal(original[n], re[n], 1e-5f);
    }

    // ── WAV import + the shared cache ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Wav_Impulse_Response_Loads_Resamples_And_Normalises()
    {
        // A 44.1 kHz stereo 16-bit IR peaking at 0.5 → resampled to the 48 kHz project rate and peak-normalised.
        const int SourceRate = 44100;
        var interleaved = new float[SourceRate * 2]; // 1 s
        interleaved[0] = 0.5f;               // L: δ
        interleaved[1] = -0.25f;             // R
        interleaved[(SourceRate / 2) * 2] = 0.1f; // a late L tap
        string path = WavFixture.TempPath("ir.wav");
        WavFixture.Write(path, interleaved, 2, SourceRate);

        (float[] read, int channels, int rate) = WaveFile.Read(path);
        Assert.Equal(2, channels);
        Assert.Equal(SourceRate, rate);
        Assert.Equal(0.5f, read[0], 1e-3f);
        Assert.Equal(-0.25f, read[1], 1e-3f);

        ImpulseResponse ir = ImpulseResponseCache.Load(path, Rate);
        Assert.Equal(2, ir.Channels);
        Assert.Equal(Rate, ir.SampleRate);
        Assert.Equal(Rate, ir.Length); // 1 s at the target rate
        Assert.Equal(1f, ir.Heads[0].Max(MathF.Abs), 1e-3f); // peak-normalised
        Assert.Same(ir, ImpulseResponseCache.TryGet(path, Rate)); // cached, shared by every instance
        ImpulseResponseCache.Invalidate(path);
    }

    [Fact]
    public void Float_Wav_Reads_Exactly()
    {
        float[] samples = [0.75f, -0.5f, 0.125f, 1f];
        string path = WavFixture.TempPath("ir-f32.wav");
        WavFixture.Write(path, samples, 1, Rate, asFloat: true);
        (float[] read, int channels, int rate) = WaveFile.Read(path);
        Assert.Equal(1, channels);
        Assert.Equal(Rate, rate);
        Assert.Equal(samples, read);
    }

    [Fact]
    public async Task Missing_File_Fails_Quietly_And_The_Effect_Passes_Through()
    {
        string path = WavFixture.TempPath("does-not-exist.wav");
        Assert.Null(ImpulseResponseCache.TryGet(path, Rate)); // kicks off the background load
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ImpulseResponseCache.HasFailed(path, Rate) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(ImpulseResponseCache.HasFailed(path, Rate));

        // The production effect (default resolver = the cache) is transparent for the unavailable IR.
        var effect = new ConvolutionReverbEffect();
        float[] input = NoiseStereo(2000, 41, 0.8f);
        float[] output = (float[])input.Clone();
        effect.Process(output, 2000, Rate, Channels, new ResolvedEffect(EffectTypeIds.AudioConvolutionReverb,
            new Dictionary<string, double> { [EffectParamNames.Mix] = 1.0 },
            new Dictionary<string, string> { [EffectParamNames.ImpulseResponse] = path }));
        Assert.Equal(input, output);

        // Relink: once the file exists and the cache is invalidated, the same path loads.
        WavFixture.Write(path, Delta(100, 0), 1, Rate);
        ImpulseResponseCache.Invalidate(path);
        Assert.False(ImpulseResponseCache.HasFailed(path, Rate));
        Assert.NotNull(ImpulseResponseCache.Load(path, Rate));
        ImpulseResponseCache.Invalidate(path);
    }

    [Fact]
    public void Non_Wav_File_Is_Rejected_As_Invalid_Data()
    {
        string path = WavFixture.TempPath("not-audio.wav");
        File.WriteAllText(path, "this is not a wave file at all");
        Assert.Throws<InvalidDataException>(() => WaveFile.Read(path));
        Assert.Throws<InvalidDataException>(() => ImpulseResponseCache.Load(path, Rate));
        Assert.True(ImpulseResponseCache.HasFailed(path, Rate));
        ImpulseResponseCache.Invalidate(path);
    }
}
