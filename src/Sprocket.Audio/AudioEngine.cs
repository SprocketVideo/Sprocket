using Sprocket.Audio.Loudness;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;

namespace Sprocket.Audio;

/// <summary>
/// The audio master clock (ARCHITECTURE.md §8): a transport-capable <see cref="IMasterClock"/> whose
/// <see cref="Now"/> is derived from the count of sample-frames the device has <em>played</em> — smoothed over
/// the device's update quantum by a bounded monotonic estimator (see the estimator field block), because every
/// real backend reports that count in coarse device-period steps — so audio is the heartbeat and video follows
/// it. A background feeder keeps the device queue topped up by mixing the timeline through an
/// <see cref="AudioMixer"/> for the advancing write cursor.
/// </summary>
/// <remarks>
/// <para>This is what <c>PlaybackEngine</c> receives as its clock when the project has audio; the engine's
/// pump reads <see cref="Now"/> and issues <see cref="Start"/>/<see cref="Pause"/>/<see cref="Seek"/> exactly
/// as it would to the software clock. Seeks bump a generation so an in-flight mix for a superseded position is
/// dropped rather than enqueued (the same discipline the video decode ring uses).</para>
/// <para><see cref="Now"/>/<see cref="IsRunning"/> are safe to read from any thread; the transport methods are
/// called from the UI thread; the feeder owns all mixing. The engine takes ownership of the output and mixer
/// and disposes them.</para>
/// </remarks>
public sealed class AudioEngine : IMasterClock, IAsyncDisposable
{
    /// <summary>Frames per mixed/enqueued buffer (~43 ms at 48 kHz) — small enough for responsive sync, large
    /// enough to keep mixing overhead low.</summary>
    public const int DefaultBufferFrames = 2048;

    private readonly IAudioOutput _output;
    private readonly AudioMixer _mixer;
    private readonly Project _project;
    private readonly int _sampleRate;
    private readonly int _bufferFrames;
    private readonly float[] _mixBuffer;
    private readonly LoudnessMeter _meter;
    private readonly ISoftwareTimeSource _timeSource;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _feeder;

    private bool _running;
    private Timecode _anchorTimeline;     // timeline position captured at the last (re)anchor
    private long _anchorPlayedFrames;     // smoothed played-frame count captured at that anchor

    // Device-clock smoothing (ARCHITECTURE.md §8). Every real backend reports PlayedFrames in device-update
    // steps, not per sample — OpenAL Soft advances AL_SAMPLE_OFFSET (and its AL_SOFT_source_latency /
    // ALC_SOFT_device_clock counters) only once per mixer update, measured at 960 frames = 20 ms; the legacy
    // Creative router steps coarser (~25 ms) and can transiently regress at an underrun. A clock that jumps by
    // most of a video frame at a time aliases the pump's frame-boundary pacing into spurious hold/skip pairs, so
    // Now smooths the raw counter with the monotonic time source, under an explicit contract:
    //   - the estimate free-runs at the time source's rate from a stable anchor (slope 1 — never re-anchored on
    //     an ordinary read, so per-read rounding cannot accumulate into drift);
    //   - it is capped at _maxLeadFrames (one mix buffer) past the highest raw reading seen, so during a genuine
    //     device stall video may lead the last-reported audio position by at most one buffer (~43 ms at the
    //     default), then the clock holds until the device reports progress again;
    //   - a raw reading ahead of the estimate snaps it forward (the device clock outran the time source);
    //   - raw readings that repeat or regress (a backend quirk at underrun) never move it backward — the
    //     estimate is monotonic between re-anchor points;
    //   - all state resets (estimate := raw, zero lead) at every re-anchor point: Start, Seek, device switch,
    //     and recovery success (ResetDeviceClockLocked).
    // The estimate may therefore lead *actually played* audio, by up to one mix buffer at worst — the
    // deliberate, documented trade that keeps video pacing smooth (see IAudioOutput.PlayedFrames).
    private long _rawMaxFrames;           // highest raw _output.PlayedFrames seen since the last reset
    private long _estAnchorFrames;        // estimate anchor: value ...
    private TimeSpan _estAnchorElapsed;   // ... at this time-source instant (re-set only on snap/cap/reset)
    private long _smoothedPlayedFrames;   // last value SmoothedPlayedFramesLocked returned (monotonic guard)
    private readonly long _maxLeadFrames; // estimate bound past the newest raw reading (one mix buffer)
    private Timecode _pausedAt;           // position to report while paused
    private Timecode _writeCursor;        // next timeline position the feeder will mix from
    private long _generation;             // bumped by Seek; a mix tagged with a stale generation is dropped
    private bool _disposed;

    // Device-loss recovery (ARCHITECTURE.md §8). The engine is the installed IMasterClock and cannot be swapped
    // (PlaybackEngine holds it in a readonly field), so recovery happens in place: on disconnect the feeder freezes
    // the clock (Recovering), attempts an in-place reopen, and either re-anchors onto the device (back to Device) or
    // switches Now to the injected monotonic time source (Software — terminal for the session, audio goes silent).
    private enum Mode { Device, Recovering, Software }
    private Mode _mode = Mode.Device;
    private Timecode _frozenPos;              // position Now holds at while Recovering (captured before the mode flip)
    private Timecode _fallbackAnchorTimeline; // timeline position when software fallback engaged
    private TimeSpan _fallbackAnchorElapsed;  // _timeSource.Elapsed at that moment

    /// <summary>Creates the engine over an already-<see cref="IAudioOutput.Configure">configured</see> output and
    /// a mixer built for the same format. Starts the (idle-until-playing) feeder. <paramref name="timeSource"/> backs
    /// the device-clock smoothing estimator and the software-fallback clock used if the device is lost
    /// unrecoverably (defaults to a <see cref="Stopwatch"/>).</summary>
    public AudioEngine(IAudioOutput output, AudioMixer mixer, Project project, int? bufferFrames = null,
        ISoftwareTimeSource? timeSource = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(project);

        _output = output;
        _mixer = mixer;
        _project = project;
        _sampleRate = output.SampleRate;
        _bufferFrames = bufferFrames ?? DefaultBufferFrames;
        _maxLeadFrames = _bufferFrames;
        _mixBuffer = new float[_bufferFrames * output.Channels];
        _meter = new LoudnessMeter(output.SampleRate, output.Channels);
        _timeSource = timeSource ?? new StopwatchTimeSource();
        _feeder = Task.Run(() => FeedLoopAsync(_stop.Token));
    }

    /// <summary>The lifecycle of the output device during playback, surfaced to the UI so device loss is visible
    /// rather than a silent freeze.</summary>
    public enum OutputStatus
    {
        /// <summary>The device dropped; an in-place reopen is being attempted (the clock is briefly frozen).</summary>
        Recovering,
        /// <summary>The device was reopened and playback resumed on it.</summary>
        Recovered,
        /// <summary>The device could not be recovered; the clock switched to software timing and audio is silent
        /// for the rest of the session.</summary>
        SoftwareFallback,
    }

    /// <summary>Raised when the output device is lost, recovered, or given up on (see <see cref="OutputStatus"/>).
    /// Fires on the feeder thread <em>after</em> the engine's internal locks are released, so a handler may touch
    /// the engine or marshal to the UI thread without risk of deadlock. Each transition fires once.</summary>
    public event Action<OutputStatus>? OutputStatusChanged;

    /// <summary>
    /// The current EBU R128 / BS.1770 loudness read-out of the mixed program (what the device plays). Safe to read
    /// from any thread; updates ~10× per second while playing and freezes when paused (PLAN.md step 30). The
    /// integrated measurement restarts on <see cref="Seek"/>.
    /// </summary>
    public LoudnessSnapshot CurrentLoudness => _meter.TakeSnapshot();

    /// <summary>
    /// The live mixer driving playback, for effect-specific UI metering (e.g. the Compressor's gain-reduction
    /// readout, PLAN.md step 31) via <see cref="AudioMixer.TryPeekEffect"/>. The mixer's own state is
    /// feeder-thread-confined; <see cref="AudioMixer.TryPeekEffect"/> is the one method safe to call from the
    /// UI thread.
    /// </summary>
    public AudioMixer Mixer => _mixer;

    /// <summary>
    /// The preview render cache's audio side (ARCHITECTURE.md §20, PLAN.md step 32), or <see langword="null"/>
    /// (the default) to always mix live. When set, the feeder asks it for cached master-mix PCM first and only
    /// mixes when the buffer's span isn't fully covered by a valid cached range — replaying a pre-rendered
    /// ("frozen") range instead of recomputing it every pass. Settable at any time from the UI thread (the
    /// composition root wires it after the session is built); the feeder reads it per buffer.
    /// </summary>
    public IAudioRenderCache? RenderCache { get; set; }

    /// <summary>Raised when a feeder iteration throws (a decode/device hiccup). The feeder swallows it and
    /// keeps running so audio recovers on the next buffer — mirroring <c>PlaybackEngine.PumpError</c> — rather
    /// than faulting the task (which would rethrow at <see cref="DisposeAsync"/> and, awaited from an async-void
    /// app handler, crash the process). Fires on the feeder thread; subscribers may surface it.</summary>
    public event Action<Exception>? FeedError;

    /// <inheritdoc />
    public Timecode Now
    {
        get { lock (_gate) return NowLocked(); }
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_running)
                return;
            _anchorTimeline = _pausedAt;
            if (_mode == Mode.Software)
            {
                _fallbackAnchorTimeline = _pausedAt;
                _fallbackAnchorElapsed = _timeSource.Elapsed;
            }
            else if (_mode == Mode.Device)
            {
                ResetDeviceClockLocked();
                _anchorPlayedFrames = _smoothedPlayedFrames;
                _output.Play();
            }
            // Recovering: just mark running; recovery completion re-anchors onto whichever mode it lands in.
            _running = true;
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (!_running)
                return;
            _pausedAt = NowLocked();
            if (_mode == Mode.Device)
                _output.Pause();
            _running = false;
        }
    }

    /// <inheritdoc />
    public void Seek(Timecode position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _writeCursor = position;
            _anchorTimeline = position;
            _pausedAt = position;
            _frozenPos = position;      // if a seek lands mid-recovery, recovery re-anchors to this position
            if (_mode == Mode.Device)
            {
                _output.Flush();
                ResetDeviceClockLocked();
                _anchorPlayedFrames = _smoothedPlayedFrames;
            }
            else if (_mode == Mode.Software)
            {
                _fallbackAnchorTimeline = position;
                _fallbackAnchorElapsed = _timeSource.Elapsed;
            }
            _generation++;
            _meter.RequestReset(); // restart the integrated measurement from the new position
        }
    }

    /// <summary>
    /// Repoints the running clock at a different output device in place (the Preferences device picker) —
    /// <paramref name="deviceSpecifier"/> null/"" = system default, otherwise a name from
    /// <see cref="OpenAlAudioOutput.EnumerateOutputDevices"/>. The playhead is preserved: it re-anchors at the
    /// current position exactly as loss-recovery does, so playback continues seamlessly. Returns
    /// <see langword="false"/> (a no-op) when the engine is mid-recovery or in software fallback, or when the
    /// reopen fails — in which case the current device keeps playing. Safe to call from the UI thread; device
    /// access is serialised by the output's own lock (lock order <c>_gate → output</c>, matching the feeder).
    /// </summary>
    public bool SwitchOutputDevice(string? deviceSpecifier)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_mode != Mode.Device)
                return false;
            Timecode pos = NowLocked();
            if (!_output.TryReopenDevice(deviceSpecifier))
                return false;
            _output.Flush();
            _writeCursor = pos;
            _anchorTimeline = pos;
            ResetDeviceClockLocked();
            _anchorPlayedFrames = _smoothedPlayedFrames;
            _pausedAt = pos;
            _generation++;      // drop any in-flight mix aimed at the old device
            _meter.RequestReset();
            return true;
        }
    }

    private Timecode NowLocked()
    {
        switch (_mode)
        {
            case Mode.Recovering:
                // Device is gone and being reopened — hold at the position captured when the drop was detected.
                return _frozenPos;
            case Mode.Software:
                if (!_running)
                    return _pausedAt;
                long elapsedFrames = (long)((_timeSource.Elapsed - _fallbackAnchorElapsed).TotalSeconds * _sampleRate);
                if (elapsedFrames < 0)
                    elapsedFrames = 0;
                return _fallbackAnchorTimeline + Timecode.FromSamples(elapsedFrames, _sampleRate);
            default:
                if (!_running)
                    return _pausedAt;
                long played = SmoothedPlayedFramesLocked() - _anchorPlayedFrames;
                if (played < 0)
                    played = 0;
                return _anchorTimeline + Timecode.FromSamples(played, _sampleRate);
        }
    }

    /// <summary>
    /// The device's played-frame count smoothed over its update quantum — see the estimator contract on the
    /// field block above. Callers hold <see cref="_gate"/>; no allocation.
    /// </summary>
    private long SmoothedPlayedFramesLocked()
    {
        long raw = _output.PlayedFrames;
        TimeSpan now = _timeSource.Elapsed;
        if (raw > _rawMaxFrames)
            _rawMaxFrames = raw; // repeats/regressions (underrun quirk) never lower the high-water mark

        double sinceSec = (now - _estAnchorElapsed).TotalSeconds;
        long estimate = _estAnchorFrames + (sinceSec > 0 ? (long)(sinceSec * _sampleRate) : 0);

        long cap = _rawMaxFrames + _maxLeadFrames;
        if (estimate > cap)
        {
            // Device stalled: hold at the bound rather than run video ahead of unheard audio. Re-anchor at the
            // cap so progress resumes at slope 1 from here once the device reports again.
            estimate = cap;
            _estAnchorFrames = cap;
            _estAnchorElapsed = now;
        }
        else if (estimate < _rawMaxFrames)
        {
            // The device outran the time source (or a fresh reading arrived after a hold): snap forward.
            estimate = _rawMaxFrames;
            _estAnchorFrames = estimate;
            _estAnchorElapsed = now;
        }

        if (estimate < _smoothedPlayedFrames)
            estimate = _smoothedPlayedFrames; // never rewind a value the pump has already seen
        _smoothedPlayedFrames = estimate;
        return estimate;
    }

    /// <summary>Re-syncs the smoothing estimator to the device's current raw reading (zero interpolation lead).
    /// Must run at every re-anchor point — Start, Seek, device switch, recovery success — always immediately
    /// before <see cref="_anchorPlayedFrames"/> is captured, so position math restarts from a clean anchor.
    /// Callers hold <see cref="_gate"/>.</summary>
    private void ResetDeviceClockLocked()
    {
        long raw = _output.PlayedFrames;
        _rawMaxFrames = raw;
        _estAnchorFrames = raw;
        _estAnchorElapsed = _timeSource.Elapsed;
        _smoothedPlayedFrames = raw;
    }

    private async Task FeedLoopAsync(CancellationToken ct)
    {
        Timecode advance = Timecode.FromSamples(_bufferFrames, _sampleRate);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Device-loss detection + recovery: only while actively playing a real device. On a drop, freeze
                // the clock, attempt an in-place reopen off the lock, then re-anchor onto the device or fall back to
                // software timing. This is the only place _mode leaves Device, so recovery is single-flighted.
                bool recover = false;
                lock (_gate)
                {
                    if (_mode == Mode.Device && _running && !_output.IsConnected)
                    {
                        _frozenPos = NowLocked(); // still Device mode → the real last-heard position
                        _mode = Mode.Recovering;
                        recover = true;
                    }
                }
                if (recover)
                {
                    RecoverFromDeviceLoss();
                    continue;
                }

                Timecode pos;
                long gen;
                lock (_gate)
                {
                    if (_mode != Mode.Device || !_running || _output.FreeFrames < _bufferFrames)
                    {
                        // Paused, recovering, in software fallback, or the device queue is full — nothing to mix.
                        pos = default;
                        gen = -1;
                    }
                    else
                    {
                        pos = _writeCursor;
                        gen = _generation;
                    }
                }

                if (gen < 0)
                {
                    await Task.Delay(5, ct).ConfigureAwait(false);
                    continue;
                }

                // Replay the pre-rendered master mix when a valid cached range covers this whole buffer
                // (ARCHITECTURE.md §20); otherwise mix live. Mixing/decoding happens off the lock.
                if (RenderCache is not { } cache || !cache.TryRead(pos, _mixBuffer))
                    _mixer.MixInto(_mixBuffer, pos, _project);

                bool enqueued;
                lock (_gate)
                {
                    enqueued = gen == _generation && _running;
                    if (enqueued)
                    {
                        _output.Enqueue(_mixBuffer);
                        _writeCursor = pos + advance;
                    }
                    // else: a seek superseded this buffer — drop it; the next tick mixes the new position.
                }

                // Meter only what was actually queued for playback, and off the lock (the K-weighting/true-peak
                // DSP must not stall Now/transport). RequestReset (from Seek) is honoured inside Process.
                if (enqueued)
                    _meter.Process(_mixBuffer);
            }
            catch (OperationCanceledException)
            {
                break; // cancellation is teardown — leave the loop
            }
            catch (Exception ex)
            {
                // A mix/enqueue/decode/device hiccup must NOT fault the feeder task: a faulted task rethrows at
                // DisposeAsync (awaited from an async-void app handler → process crash) and permanently kills
                // audio. Surface it and keep feeding so the next buffer recovers (cf. PlaybackEngine.PumpError).
                FeedError?.Invoke(ex);
                try { await Task.Delay(20, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Runs on the feeder thread once a device drop is detected (<see cref="Mode.Recovering"/> already latched under
    /// the lock). Attempts an in-place reopen off the lock, then atomically re-anchors: on success back onto the
    /// device from the frozen position; on failure into software timing (terminal). The status event is raised only
    /// after the lock is released.
    /// </summary>
    private void RecoverFromDeviceLoss()
    {
        OutputStatusChanged?.Invoke(OutputStatus.Recovering);

        bool reopened = _output.TryReopenDefaultDevice();

        lock (_gate)
        {
            // _frozenPos may have advanced to a seek target set while the (off-lock) reopen ran — re-anchor to it.
            if (reopened)
            {
                _output.Flush();
                _writeCursor = _frozenPos;
                _anchorTimeline = _frozenPos;
                ResetDeviceClockLocked();
                _anchorPlayedFrames = _smoothedPlayedFrames;
                _pausedAt = _frozenPos;
                _mode = Mode.Device;
            }
            else
            {
                _fallbackAnchorTimeline = _frozenPos;
                _fallbackAnchorElapsed = _timeSource.Elapsed;
                _pausedAt = _frozenPos;
                _mode = Mode.Software;
            }
            _generation++; // drop any in-flight mix from before the drop
            _meter.RequestReset();
        }

        OutputStatusChanged?.Invoke(reopened ? OutputStatus.Recovered : OutputStatus.SoftwareFallback);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _stop.Cancel();
        try { await _feeder.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _mixer.Dispose();
        _output.Dispose();
        _stop.Dispose();
    }
}
