using System.Diagnostics;
using Sprocket.Core.Timing;

namespace Sprocket.Playback;

/// <summary>
/// A play/pause/seekable wall-clock implementation of <see cref="IClock"/> driven by a monotonic elapsed
/// source (a <see cref="Stopwatch"/> by default). This is the slice's stand-in master clock (PLAN.md step
/// 4): <see cref="Now"/> advances in real time while running and freezes while paused. PLAN step 5 replaces
/// it with the audio device clock (the true master — ARCHITECTURE.md §8) behind the same <see cref="IClock"/>.
/// </summary>
/// <remarks>
/// <para>The position is re-anchored on every <see cref="Start"/>/<see cref="Pause"/>/<see cref="Seek"/>, so
/// time is only ever extrapolated within one continuous play span — there is no error accumulation across
/// the timeline the way summing <see cref="double"/> seconds would cause. The audio clock supersedes this
/// for sample-exact sync, which is why a stopwatch (millisecond-grained, double-derived) is acceptable here.</para>
/// <para>Thread-safe: <see cref="Now"/> is read from the render/pump threads while transport methods are
/// called from the UI thread, all guarded by one lock.</para>
/// </remarks>
public sealed class SoftwareClock : IClock
{
    private readonly Func<TimeSpan> _elapsedSource;
    private readonly object _gate = new();

    private Timecode _anchor;          // timeline position captured at the last (re)anchor
    private TimeSpan _anchorElapsed;   // _elapsedSource() value captured at that anchor
    private bool _running;
    private double _rate = 1.0;

    /// <summary>Creates a clock. <paramref name="elapsedSource"/> supplies a monotonically increasing elapsed
    /// time; when null a process-lifetime <see cref="Stopwatch"/> is used. Tests inject a controllable source.</summary>
    public SoftwareClock(Func<TimeSpan>? elapsedSource = null)
    {
        if (elapsedSource is null)
        {
            var sw = Stopwatch.StartNew();
            elapsedSource = () => sw.Elapsed;
        }
        _elapsedSource = elapsedSource;
        _anchorElapsed = _elapsedSource();
    }

    /// <inheritdoc />
    public Timecode Now
    {
        get
        {
            lock (_gate)
                return NowLocked();
        }
    }

    /// <summary>Whether the clock is currently advancing.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Playback rate multiplier (1.0 = real time). Re-anchors so the change applies from <see cref="Now"/>.</summary>
    public double Rate
    {
        get { lock (_gate) return _rate; }
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Rate must be positive.");
            lock (_gate)
            {
                ReanchorLocked();
                _rate = value;
            }
        }
    }

    /// <summary>Starts (or resumes) advancing from the current position. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;
            _anchorElapsed = _elapsedSource();
            _running = true;
        }
    }

    /// <summary>Freezes the clock at the current position. Idempotent.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_running)
                return;
            _anchor = NowLocked();   // capture where we stopped
            _running = false;
        }
    }

    /// <summary>Jumps to <paramref name="position"/>, keeping the running/paused state.</summary>
    public void Seek(Timecode position)
    {
        lock (_gate)
        {
            _anchor = position;
            _anchorElapsed = _elapsedSource();
        }
    }

    private Timecode NowLocked()
    {
        if (!_running)
            return _anchor;

        TimeSpan delta = _elapsedSource() - _anchorElapsed;
        long advanced = (long)Math.Round(delta.TotalSeconds * _rate * Timecode.TicksPerSecond);
        return new Timecode(_anchor.Ticks + advanced);
    }

    private void ReanchorLocked()
    {
        _anchor = NowLocked();
        _anchorElapsed = _elapsedSource();
    }
}
