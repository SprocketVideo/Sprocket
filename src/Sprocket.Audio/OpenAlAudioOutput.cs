using Silk.NET.OpenAL;

namespace Sprocket.Audio;

/// <summary>
/// The real <see cref="IAudioOutput"/>: a streaming OpenAL (Silk.NET.OpenAL / OpenAL Soft) source fed by a
/// rotating pool of device buffers (ARCHITECTURE.md §6, §14). Mixed float32 frames are converted to 16-bit PCM
/// and queued; processed buffers are recycled, and the count of frames in recycled buffers plus the current
/// play offset gives <see cref="PlayedFrames"/> — the master clock's time source.
/// </summary>
/// <remarks>
/// This is device-bound: like the windowed GPU preview it rests on manual verification, not headless tests
/// (the mixer and master clock are tested against a fake output). <see cref="Configure"/> throws if no audio
/// device is available, and the playback bootstrap then falls back to a software clock. All OpenAL calls are
/// serialised by an internal lock because the OpenAL context is process-global, not thread-safe.
/// </remarks>
public sealed unsafe class OpenAlAudioOutput : IAudioOutput
{
    private const int BufferCount = 8;

    private readonly object _al = new();
    private AL _api = null!;
    private ALContext _alc = null!;
    private Device* _device;
    private Context* _context;

    private uint _source;
    private readonly Queue<uint> _free = new();
    private readonly Dictionary<uint, int> _queuedFrames = new(); // buffer id → frames it holds
    private BufferFormat _format;
    private short[] _pcm = [];
    private long _playedBase;       // frames in buffers already recycled (fully played)
    private int _framesPerBuffer;   // learned from the first enqueue; sizes FreeFrames
    private bool _playing;
    private bool _configured;
    private bool _disposed;

    /// <inheritdoc />
    public int Channels { get; private set; }

    /// <inheritdoc />
    public int SampleRate { get; private set; }

    /// <inheritdoc />
    public void Configure(int sampleRate, int channels)
    {
        if (_configured)
            throw new InvalidOperationException("Output already configured.");
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "OpenAL output supports mono or stereo.");

        SampleRate = sampleRate;
        Channels = channels;
        _format = channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;

        try
        {
            _alc = ALContext.GetApi();
            _api = AL.GetApi();
            _device = _alc.OpenDevice("");
            if (_device is null)
                throw new InvalidOperationException("No OpenAL output device available.");

            _context = _alc.CreateContext(_device, null);
            if (_context is null || !_alc.MakeContextCurrent(_context))
                throw new InvalidOperationException("Failed to create/activate the OpenAL context.");

            _source = _api.GenSource();
            foreach (uint buffer in _api.GenBuffers(BufferCount))
                _free.Enqueue(buffer);
            _configured = true;
        }
        catch
        {
            DisposeDevice();
            throw;
        }
    }

    /// <inheritdoc />
    public long PlayedFrames
    {
        get
        {
            lock (_al)
            {
                if (!_configured)
                    return 0;
                RecycleProcessedLocked();
                _api.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int offset);
                return _playedBase + offset;
            }
        }
    }

    /// <inheritdoc />
    public int FreeFrames
    {
        get
        {
            lock (_al)
            {
                if (!_configured)
                    return 0;
                RecycleProcessedLocked();
                return _framesPerBuffer == 0 ? int.MaxValue : _free.Count * _framesPerBuffer;
            }
        }
    }

    /// <inheritdoc />
    public void Enqueue(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / Channels;
        if (frames == 0)
            return;

        lock (_al)
        {
            if (!_configured)
                return;
            RecycleProcessedLocked();
            if (_free.Count == 0)
                return; // no headroom; caller checks FreeFrames first, this is a safety net

            _framesPerBuffer = frames;
            uint buffer = _free.Dequeue();

            EnsurePcm(interleaved.Length);
            for (int i = 0; i < interleaved.Length; i++)
                _pcm[i] = (short)(Math.Clamp(interleaved[i], -1f, 1f) * short.MaxValue);

            fixed (short* p = _pcm)
                _api.BufferData(buffer, _format, p, interleaved.Length * sizeof(short), SampleRate);

            _api.SourceQueueBuffers(_source, [buffer]);
            _queuedFrames[buffer] = frames;

            // Restart the source if it underran while we were behind.
            if (_playing)
                EnsurePlayingLocked();
        }
    }

    /// <inheritdoc />
    public void Play()
    {
        lock (_al)
        {
            if (!_configured)
                return;
            _playing = true;
            EnsurePlayingLocked();
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_al)
        {
            if (!_configured)
                return;
            _playing = false;
            _api.SourcePause(_source);
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (_al)
        {
            if (!_configured)
                return;

            _api.SourceStop(_source);
            // Stopping marks every queued buffer processed; recycle them all back to the free pool. We do NOT
            // credit their frames to _playedBase — discarded audio was not heard — so the clock re-anchors
            // cleanly after the seek without jumping forward.
            _api.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            if (queued > 0)
            {
                var ids = new uint[queued];
                _api.SourceUnqueueBuffers(_source, ids);
                foreach (uint id in ids)
                {
                    _queuedFrames.Remove(id);
                    _free.Enqueue(id);
                }
            }
            // A stopped source reports SampleOffset 0, so PlayedFrames == _playedBase from here.
        }
    }

    private void RecycleProcessedLocked()
    {
        _api.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        if (processed <= 0)
            return;

        var ids = new uint[processed];
        _api.SourceUnqueueBuffers(_source, ids);
        foreach (uint id in ids)
        {
            if (_queuedFrames.Remove(id, out int frames))
                _playedBase += frames;
            _free.Enqueue(id);
        }
    }

    private void EnsurePlayingLocked()
    {
        _api.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if ((SourceState)state != SourceState.Playing)
            _api.SourcePlay(_source);
    }

    private void EnsurePcm(int length)
    {
        if (_pcm.Length < length)
            _pcm = new short[length];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeDevice();
    }

    private void DisposeDevice()
    {
        lock (_al)
        {
            try
            {
                if (_configured)
                {
                    _api.SourceStop(_source);
                    _api.DeleteSource(_source);
                    if (_queuedFrames.Count > 0 || _free.Count > 0)
                    {
                        var all = new List<uint>(_free);
                        all.AddRange(_queuedFrames.Keys);
                        _api.DeleteBuffers(all.ToArray());
                    }
                }
            }
            catch { /* tearing down a device that may already be gone */ }

            if (_context is not null) { _alc.DestroyContext(_context); _context = null; }
            if (_device is not null) { _alc.CloseDevice(_device); _device = null; }
            _configured = false;
        }
    }
}
