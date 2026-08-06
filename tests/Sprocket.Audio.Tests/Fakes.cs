using Sprocket.Core.Audio;
using Sprocket.Core.Timing;

namespace Sprocket.Audio.Tests;

/// <summary>
/// A deterministic <see cref="IAudioOutput"/> for tests: <see cref="PlayedFrames"/> is set by the test (no
/// real device, no real time), enqueued buffers are captured for assertions, and <see cref="FreeFrames"/>
/// models a bounded queue so the engine's feeder fills then idles rather than spinning.
/// </summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    private readonly object _gate = new();
    private long _played;
    private long _totalEnqueued;       // sample-frames ever enqueued
    private readonly int _budgetFrames;

    public FakeAudioOutput(int budgetFrames = 8192) => _budgetFrames = budgetFrames;

    public int Channels { get; private set; } = 2;
    public int SampleRate { get; private set; } = 48000;
    public bool Playing { get; private set; }
    private readonly List<float[]> _enqueued = new();

    /// <summary>A thread-safe copy of every buffer enqueued so far (the feeder writes from its own thread).</summary>
    public float[][] EnqueuedSnapshot() { lock (_gate) return _enqueued.ToArray(); }

    public void Configure(int sampleRate, int channels, string? deviceSpecifier = null)
    {
        SampleRate = sampleRate;
        Channels = channels;
        LastReopenSpecifier = deviceSpecifier;
    }

    public long PlayedFrames { get { lock (_gate) return _played; } }

    /// <summary>Test hook: pretend the device has played out to <paramref name="frames"/> total.</summary>
    public void SetPlayedFrames(long frames) { lock (_gate) _played = frames; }

    public int FreeFrames
    {
        get
        {
            lock (_gate)
            {
                long outstanding = _totalEnqueued - _played;
                long free = _budgetFrames - outstanding;
                return free <= 0 ? 0 : (int)free;
            }
        }
    }

    public void Enqueue(ReadOnlySpan<float> interleaved)
    {
        lock (_gate)
        {
            _enqueued.Add(interleaved.ToArray());
            _totalEnqueued += interleaved.Length / Channels;
        }
    }

    public void Play() { lock (_gate) Playing = true; }
    public void Pause() { lock (_gate) Playing = false; }

    public void Flush()
    {
        lock (_gate) _totalEnqueued = _played; // drop queued-but-unplayed
    }

    // --- Device-loss recovery hooks (ARCHITECTURE.md §8) --------------------------------------------------------

    private bool _connected = true;
    private Func<bool>? _reopenResult; // null → default clean reconnect

    /// <inheritdoc />
    public bool IsConnected { get { lock (_gate) return _connected; } }

    /// <summary>Number of <see cref="TryReopenDefaultDevice"/> calls — asserts the engine attempts recovery once.</summary>
    public int ReopenCalls { get; private set; }

    /// <summary>Test hook: simulate the device dropping (or coming back) out from under the engine.</summary>
    public void SetConnected(bool connected) { lock (_gate) _connected = connected; }

    /// <summary>Script the outcome of the next reopen(s): the callback's return value is the effective result, and
    /// the fake sets <see cref="IsConnected"/> to it (a live device iff the reopen succeeded). Unset = clean
    /// reconnect. Model "reopen returned true but still disconnected" simply as a <c>false</c> result — the real
    /// <see cref="OpenAlAudioOutput"/> collapses that native case to false before the engine ever sees it.</summary>
    public void SetReopenResult(Func<bool> result) { lock (_gate) _reopenResult = result; }

    /// <summary>The device specifier passed to the most recent <see cref="Configure"/> / <see cref="TryReopenDevice"/>.</summary>
    public string? LastReopenSpecifier { get; private set; }

    /// <inheritdoc />
    public bool TryReopenDefaultDevice() => TryReopenDevice(null);

    /// <inheritdoc />
    public bool TryReopenDevice(string? deviceSpecifier)
    {
        lock (_gate)
        {
            ReopenCalls++;
            LastReopenSpecifier = deviceSpecifier;
            bool ok = _reopenResult?.Invoke() ?? true;
            // A successful reopen yields a live device; a failed one leaves the current device untouched (OpenAL
            // Soft keeps the original device open when alcReopenDeviceSOFT fails), so don't force it disconnected.
            if (ok)
                _connected = true;
            return ok;
        }
    }

    public void Dispose() { }
}

/// <summary>
/// A deterministic <see cref="ISoftwareTimeSource"/> tests advance by hand. The engine reads it for both the
/// device-clock smoothing estimator and the software-fallback clock, so injecting it makes <c>AudioEngine.Now</c>
/// fully scripted: raw played frames via <see cref="FakeAudioOutput.SetPlayedFrames"/>, elapsed time via
/// <see cref="Advance"/> — no wall-clock flakiness in either direction.
/// </summary>
internal sealed class FakeTimeSource : ISoftwareTimeSource
{
    private readonly object _gate = new();
    private TimeSpan _elapsed;
    public TimeSpan Elapsed { get { lock (_gate) return _elapsed; } }
    public void Advance(TimeSpan by) { lock (_gate) _elapsed += by; }
}

/// <summary>
/// A synthetic <see cref="IPcmReader"/> that returns a constant value (or a 440 Hz-ish ramp) so the mixer can
/// be tested without FFmpeg. Records its <see cref="SeekTo"/> calls so seek-on-jump behaviour is observable.
/// </summary>
internal sealed class FakePcmReader : IPcmReader
{
    private readonly float _value;
    private long _frameCursor;

    public FakePcmReader(int sampleRate, int channels, float value)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _value = value;
    }

    public int Channels { get; }
    public int SampleRate { get; }
    public List<Timecode> Seeks { get; } = new();
    public bool Disposed { get; private set; }

    public int Read(Span<float> destinationInterleaved)
    {
        int frames = destinationInterleaved.Length / Channels;
        destinationInterleaved.Fill(_value);
        _frameCursor += frames;
        return frames;
    }

    public void SeekTo(Timecode sourceTime)
    {
        Seeks.Add(sourceTime);
        _frameCursor = sourceTime.ToSampleIndex(SampleRate);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// A synthetic <see cref="IPcmReader"/> whose sample value is a known linear ramp of the absolute source frame
/// index (× <see cref="_scale"/>, equal on every channel), so the retime resampler (PLAN.md step 21) can be
/// verified: an output sample produced from source position <c>p</c> reads back as <c>p × scale</c>. Records
/// seeks so streaming (seek-free) playback is observable.
/// </summary>
internal sealed class RampPcmReader : IPcmReader
{
    private readonly float _scale;
    private long _frame;

    public RampPcmReader(int sampleRate, int channels, float scale)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _scale = scale;
    }

    public int Channels { get; }
    public int SampleRate { get; }
    public List<Timecode> Seeks { get; } = new();

    public int Read(Span<float> destinationInterleaved)
    {
        int frames = destinationInterleaved.Length / Channels;
        for (int f = 0; f < frames; f++)
        {
            float v = (_frame + f) * _scale;
            for (int ch = 0; ch < Channels; ch++)
                destinationInterleaved[f * Channels + ch] = v;
        }
        _frame += frames;
        return frames;
    }

    public void SeekTo(Timecode sourceTime)
    {
        Seeks.Add(sourceTime);
        _frame = sourceTime.ToSampleIndex(SampleRate);
    }

    public void Dispose() { }
}

/// <summary>
/// A synthetic <see cref="IPcmReader"/> that generates an endless sine of a given frequency and amplitude on every
/// channel — a realistic loudness test signal (unlike the DC-like <see cref="FakePcmReader"/>, which the
/// K-weighting high-pass would treat as silence). Used to exercise <c>LoudnessAnalyzer</c> without FFmpeg.
/// </summary>
internal sealed class SinePcmReader : IPcmReader
{
    private readonly double _freq;
    private readonly double _amp;
    private long _frame;

    public SinePcmReader(int sampleRate, int channels, double freq, double amp)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _freq = freq;
        _amp = amp;
    }

    public int Channels { get; }
    public int SampleRate { get; }

    public int Read(Span<float> destinationInterleaved)
    {
        int frames = destinationInterleaved.Length / Channels;
        for (int f = 0; f < frames; f++)
        {
            double s = _amp * Math.Sin(2.0 * Math.PI * _freq * _frame / SampleRate);
            for (int ch = 0; ch < Channels; ch++)
                destinationInterleaved[f * Channels + ch] = (float)s;
            _frame++;
        }
        return frames;
    }

    public void SeekTo(Timecode sourceTime) => _frame = sourceTime.ToSampleIndex(SampleRate);

    public void Dispose() { }
}
