using System.Collections.Concurrent;

namespace Sprocket.Audio.Effects;

/// <summary>
/// A loaded, preprocessed impulse response for the Convolution Reverb (PLAN.md step 49): the captured space's
/// samples resampled to the project rate, peak-normalised, capped at <see cref="MaxSeconds"/>, and split into
/// uniform <see cref="BlockSize"/>-sample partitions with every partition after the first already transformed
/// to the frequency domain (the expensive part of convolution setup). Built <em>off</em> the audio thread —
/// by <see cref="ImpulseResponseCache"/> in the background, or directly via <see cref="FromSamples"/> in
/// tests — so the live per-buffer work is only the multiply-accumulate. Immutable once built and shared by
/// every convolver instance using the same IR.
/// </summary>
public sealed class ImpulseResponse
{
    /// <summary>Uniform partition length B in samples. Partition 0 runs as a direct time-domain convolution so
    /// the effect has zero latency; partitions 1..P−1 run through <see cref="FftSize"/>-point overlap-save FFTs.
    /// 512 balances the O(B) per-sample head cost against the O(P·2B / B) per-sample FFT tail cost.</summary>
    public const int BlockSize = 512;

    /// <summary>FFT length N = 2B for overlap-save with B-tap partitions.</summary>
    public const int FftSize = BlockSize * 2;

    /// <summary>Longest IR kept, in seconds (a cathedral tail is ~8 s; longer files are trimmed).</summary>
    public const double MaxSeconds = 10.0;

    /// <summary>Plausibility bounds shared with <see cref="WaveFile"/>: more channels than this is not an IR
    /// (the convolver only ever uses <c>channel % Channels</c>), and rates outside the range would let a hostile
    /// header request absurd resampling work.</summary>
    public const int MaxChannels = 8;
    private const int MinRate = 8000;
    private const int MaxRate = 384000;
    private const int ResampleLobes = 8; // Lanczos a = 8

    /// <summary>The single shared FFT plan for <see cref="FftSize"/> — read-only tables, safe across threads.</summary>
    internal static readonly FftPlan Plan = new(FftSize);

    private ImpulseResponse(int sampleRate, int channels, int length, float[][] heads, float[][][] re, float[][][] im)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Length = length;
        Heads = heads;
        SpectraRe = re;
        SpectraIm = im;
    }

    /// <summary>The rate the IR was resampled to (the project/mixer rate).</summary>
    public int SampleRate { get; }

    /// <summary>IR channel count (1 = mono applied to every output channel; 2 = a stereo pair).</summary>
    public int Channels { get; }

    /// <summary>IR length per channel in samples, after resampling and the <see cref="MaxSeconds"/> cap.</summary>
    public int Length { get; }

    /// <summary>Partition count P (≥ 1): the head plus P−1 frequency-domain partitions.</summary>
    public int PartitionCount => 1 + SpectraRe[0].Length;

    /// <summary>Partition 0 per channel: the first <see cref="BlockSize"/> taps, zero-padded.</summary>
    internal float[][] Heads { get; }

    /// <summary>Real parts of partitions 1..P−1 per channel: <c>[channel][partition − 1][FftSize]</c>.</summary>
    internal float[][][] SpectraRe { get; }

    /// <summary>Imaginary parts, same layout as <see cref="SpectraRe"/>.</summary>
    internal float[][][] SpectraIm { get; }

    /// <summary>Loads a WAV impulse response for use at <paramref name="targetRate"/> (peak-normalised).</summary>
    /// <exception cref="InvalidDataException">The file is not a readable PCM/float WAVE.</exception>
    /// <exception cref="IOException">The file cannot be read (missing, locked).</exception>
    public static ImpulseResponse Load(string path, int targetRate)
    {
        (float[] samples, int channels, int rate) = WaveFile.Read(path);
        return FromSamples(samples, channels, rate, targetRate, normalize: true);
    }

    /// <summary>
    /// Builds an IR from interleaved samples at <paramref name="sourceRate"/>, resampling to
    /// <paramref name="targetRate"/> (windowed-sinc) when they differ. <paramref name="normalize"/> scales the
    /// peak to 1.0 (what every DAW convolution plugin does on import, so IRs of different capture levels sit at
    /// comparable loudness); tests pass <see langword="false"/> to convolve with exactly the given taps.
    /// </summary>
    public static ImpulseResponse FromSamples(
        ReadOnlySpan<float> interleaved, int channels, int sourceRate, int targetRate, bool normalize)
    {
        if (channels is <= 0 or > MaxChannels) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sourceRate is < MinRate or > MaxRate) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate is < MinRate or > MaxRate) throw new ArgumentOutOfRangeException(nameof(targetRate));
        int frames = interleaved.Length / channels;
        if (frames == 0)
            throw new ArgumentException("An impulse response needs at least one sample.", nameof(interleaved));

        // De-interleave, resample per channel, cap the length. The cap is applied in SOURCE samples first so
        // the resampler never runs over more input than can survive it (bounded work + allocation).
        int maxLength = (int)(MaxSeconds * targetRate);
        int maxSourceFrames = (int)Math.Min(frames, (long)maxLength * sourceRate / targetRate + ResampleLobes);
        var perChannel = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            var mono = new float[maxSourceFrames];
            for (int f = 0; f < maxSourceFrames; f++)
                mono[f] = interleaved[f * channels + ch];
            if (sourceRate != targetRate)
                mono = Resample(mono, sourceRate, targetRate);
            if (mono.Length > maxLength)
                Array.Resize(ref mono, maxLength);
            perChannel[ch] = mono;
        }
        int length = perChannel[0].Length;

        if (normalize)
        {
            float peak = 0;
            foreach (float[] c in perChannel)
                foreach (float s in c)
                    peak = Math.Max(peak, Math.Abs(s));
            if (peak > 0 && peak != 1f)
            {
                float scale = 1f / peak;
                foreach (float[] c in perChannel)
                    for (int i = 0; i < c.Length; i++)
                        c[i] *= scale;
            }
        }

        // Partition: head = taps [0, B); partition k = taps [kB, (k+1)B) zero-padded into an N-point frame and
        // transformed (overlap-save's filter placement — the last B outputs of each circular block are linear).
        int partitions = Math.Max(1, (length + BlockSize - 1) / BlockSize);
        var heads = new float[channels][];
        var re = new float[channels][][];
        var im = new float[channels][][];
        for (int ch = 0; ch < channels; ch++)
        {
            float[] h = perChannel[ch];
            heads[ch] = new float[BlockSize];
            h.AsSpan(0, Math.Min(BlockSize, h.Length)).CopyTo(heads[ch]);
            re[ch] = new float[partitions - 1][];
            im[ch] = new float[partitions - 1][];
            for (int k = 1; k < partitions; k++)
            {
                var pr = new float[FftSize];
                var pi = new float[FftSize];
                int start = k * BlockSize;
                int count = Math.Min(BlockSize, h.Length - start);
                h.AsSpan(start, count).CopyTo(pr);
                Plan.Forward(pr, pi);
                re[ch][k - 1] = pr;
                im[ch][k - 1] = pi;
            }
        }
        return new ImpulseResponse(targetRate, channels, length, heads, re, im);
    }

    /// <summary>Windowed-sinc (Lanczos, a = 8) resampler — offline quality for a one-shot IR conversion, no
    /// dependency on the Media layer's libswresample.</summary>
    private static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        const int Lobes = ResampleLobes;
        long outLength = Math.Min((long)input.Length * targetRate / sourceRate, (long)(MaxSeconds * targetRate));
        var output = new float[Math.Max(1, (int)outLength)];
        double ratio = (double)sourceRate / targetRate;
        for (int i = 0; i < output.Length; i++)
        {
            double center = i * ratio;
            int c = (int)Math.Floor(center);
            double sum = 0;
            for (int j = c - Lobes + 1; j <= c + Lobes; j++)
            {
                if (j < 0 || j >= input.Length)
                    continue;
                sum += input[j] * Lanczos(center - j, Lobes);
            }
            output[i] = (float)sum;
        }
        return output;
    }

    private static double Lanczos(double x, int a)
    {
        if (x == 0)
            return 1;
        if (Math.Abs(x) >= a)
            return 0;
        double px = Math.PI * x;
        return a * Math.Sin(px) * Math.Sin(px / a) / (px * px);
    }
}

/// <summary>
/// Process-wide cache of loaded <see cref="ImpulseResponse"/>s keyed by (path, sample rate), with background
/// loading (PLAN.md step 49): the effect's per-buffer <see cref="TryGet"/> never blocks — the first request for
/// an unseen IR kicks off a <see cref="Task"/> to read, resample and partition it, and returns
/// <see langword="null"/> (the effect passes the dry signal through) until it lands. A file that fails to load
/// (missing after a project move, not a WAV) is remembered as failed so the audio thread doesn't retry on
/// every buffer; <see cref="Invalidate"/> clears that once the user re-picks / relinks it. Every convolver
/// instance using the same IR shares one preprocessed copy.
/// </summary>
public static class ImpulseResponseCache
{
    private sealed class Entry
    {
        public volatile ImpulseResponse? Ir;
        public volatile bool Failed;
        public volatile string? Error; // the failure's message, for Load's exception / UI status
    }

    private static readonly ConcurrentDictionary<(string Path, int Rate), Entry> Entries = new();

    /// <summary>Retained-entry bound: a 10 s stereo IR's spectra are ~15 MB, so an unbounded cache would grow by
    /// that much for every IR a user auditions. Beyond this many entries the oldest others are dropped; a live
    /// effect keeps its own reference to the IR it is using, so eviction never disturbs playback.</summary>
    public const int MaxEntries = 16;

    /// <summary>The loaded IR, or <see langword="null"/> while it is still loading (a background load is started
    /// on the first call) or after loading failed. Allocation-free once the entry exists — safe per buffer.</summary>
    public static ImpulseResponse? TryGet(string path, int sampleRate)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (Entries.TryGetValue((path, sampleRate), out Entry? entry))
            return entry.Ir;

        // Single atomic insert: no indexer re-read that a concurrent Invalidate could turn into a throw on
        // the audio thread. Only the thread whose entry won starts the load.
        var fresh = new Entry();
        entry = Entries.GetOrAdd((path, sampleRate), fresh);
        if (!ReferenceEquals(entry, fresh))
            return entry.Ir;
        Trim((path, sampleRate));
        _ = Task.Run(() => Populate(fresh, path, sampleRate));
        return null;
    }

    /// <summary>Loads (or returns the cached) IR synchronously — for tests and eager preloads.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable WAVE.</exception>
    public static ImpulseResponse Load(string path, int sampleRate)
    {
        Entry entry = Entries.GetOrAdd((path, sampleRate), _ => new Entry());
        Trim((path, sampleRate));
        if (entry.Ir is { } loaded)
            return loaded;
        Populate(entry, path, sampleRate);
        return entry.Ir ?? throw new InvalidDataException($"Could not load impulse response '{path}': {entry.Error}");
    }

    /// <summary>Drops arbitrary entries other than <paramref name="keep"/> until the cache is within
    /// <see cref="MaxEntries"/> (entries are cheap to reload; only the bound matters).</summary>
    private static void Trim((string Path, int Rate) keep)
    {
        if (Entries.Count <= MaxEntries)
            return;
        foreach ((string Path, int Rate) key in Entries.Keys)
        {
            if (Entries.Count <= MaxEntries)
                break;
            if (key != keep)
                Entries.TryRemove(key, out _);
        }
    }

    /// <summary>Whether loading <paramref name="path"/> at <paramref name="sampleRate"/> has failed (for UI status).</summary>
    public static bool HasFailed(string path, int sampleRate) =>
        Entries.TryGetValue((path, sampleRate), out Entry? entry) && entry.Failed;

    /// <summary>Forgets every cached copy of <paramref name="path"/> (all rates) so the next request reloads it —
    /// call after the user relinks / replaces the file.</summary>
    public static void Invalidate(string path)
    {
        foreach ((string Path, int Rate) key in Entries.Keys)
        {
            if (string.Equals(key.Path, path, StringComparison.Ordinal))
                Entries.TryRemove(key, out _);
        }
    }

    /// <summary>Drops every cached IR (tests).</summary>
    public static void Clear() => Entries.Clear();

    private static void Populate(Entry entry, string path, int sampleRate)
    {
        try
        {
            entry.Ir = ImpulseResponse.Load(path, sampleRate);
        }
        catch (Exception ex)
        {
            // Unavailable/unreadable IR → pass-through, never a crash (§15's unknown-effect convention). This
            // runs on a threadpool task with a "never fail" contract, so every exception type — IO, format,
            // access, unseekable device paths (NotSupportedException), even OOM from an absurd file — lands
            // here and marks the entry failed so the UI can say so and the audio thread stops asking.
            entry.Error = ex.Message;
            entry.Failed = true;
        }
    }
}
