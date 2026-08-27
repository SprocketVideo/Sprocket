using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Export;

/// <summary>
/// Supplies the decoded full-resolution frame for one source media at a requested source time during export.
/// Export pulls frames at the <b>full source resolution</b> (never a proxy, ARCHITECTURE.md §17) and walks the
/// timeline forward, so a forward clip decodes sequentially — keeping a one-frame look-ahead and only seeking
/// backward if a request ever moves back (a cut to an earlier in-point). A <em>reversed</em> clip (PLAN.md step 21
/// remainder) asks explicitly for the latest frame strictly before its mapped time; those requests are served from
/// a GOP-aware <see cref="GopFrameWindow"/>, which decodes each GOP tail once and hands out the preceding frames,
/// so a reverse clip exports at roughly one decode pass per GOP rather than one per frame.
/// </summary>
/// <remarks>
/// Decode runs in software (<see cref="HardwareAccelMode.Disabled"/>) for bit-deterministic output, which is
/// what makes golden-frame export testing meaningful. Not thread-safe: one provider per source, driven by the
/// single export thread. The returned frame is owned by this provider and stays valid only until the next
/// <see cref="GetFrame"/> call (the caller composites it immediately), so callers must not hold it.
/// </remarks>
internal sealed class ExportFrameProvider : IDisposable
{
    // A frame whose PTS is within this tolerance of (or before) the request counts as "at or before" it, so
    // sub-tick rounding between the timeline clock and the source PTS never skips the correct frame.
    private static readonly long MatchToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    private readonly MediaSource _source;
    private readonly VideoFramePool _pool;
    private readonly bool _isStill;   // a single-frame still: hold the one frame for every requested time (step 42)

    // Forward (sequential) state.
    private VideoFrame? _current;   // the frame currently "on screen" for the last request
    private VideoFrame? _pending;   // decoded look-ahead whose PTS is past the last request
    private bool _started;
    private bool _eof;

    // Reverse state: the GOP window holding frames before the last reverse request, in ascending order. The two
    // modes never hold frames at the same time — switching direction resets the other side.
    private GopFrameWindow? _window;
    private bool _inReverse;

    private bool _disposed;

    /// <param name="source">The full-resolution source to decode. The provider owns and disposes it.</param>
    /// <param name="isStill">Whether the source is a single still image (PLAN.md step 42): its one frame is held
    /// and returned for every requested source time, so a still clip renders identically across its whole span
    /// instead of seeking past its only frame and going black.</param>
    public ExportFrameProvider(MediaSource source, bool isStill = false)
    {
        _source = source;
        _isStill = isStill;
        _pool = new VideoFramePool(source.Info.Width, source.Info.Height);
    }

    /// <summary>Source frame width in pixels.</summary>
    public int Width => _source.Info.Width;

    /// <summary>Source frame height in pixels.</summary>
    public int Height => _source.Info.Height;

    /// <summary>
    /// Returns the source frame to display at <paramref name="sourceTime"/>, advancing the decoder as needed, or
    /// <see langword="null"/> only if the source yields no usable frame. Forward (<paramref name="reverse"/> false):
    /// the latest decoded frame whose PTS is at or before the time. Reverse: the latest frame strictly
    /// <em>before</em> it — a reversed clip's mapped time is an exclusive upper bound (<see cref="Core.Rendering.VideoLayer.Reverse"/>).
    /// The result is valid until the next call.
    /// </summary>
    public VideoFrame? GetFrame(Timecode sourceTime, bool reverse = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A still has one frame at (about) time zero; every timeline time inside the clip maps to it, so pin the
        // request to zero instead of seeking to the mapped source time (which would run off the single frame).
        if (_isStill)
        {
            sourceTime = Timecode.Zero;
            reverse = false;
        }

        return reverse ? GetReverse(sourceTime) : GetForward(sourceTime);
    }

    private VideoFrame? GetForward(Timecode sourceTime)
    {
        if (_inReverse)
        {
            // Leaving reverse mode: drop the window and rebuild the sequential look-ahead from the request.
            _window?.Clear();
            _inReverse = false;
            Reset();
            _source.SeekTo(sourceTime);
            _started = true;
        }
        else if (!_started)
        {
            _source.SeekTo(sourceTime);
            _started = true;
        }
        else if (_current is not null && sourceTime.Ticks < _current.Pts.Ticks - MatchToleranceTicks)
        {
            // The request moved backwards (a cut to an earlier in-point); re-seek and rebuild the look-ahead.
            Reset();
            _source.SeekTo(sourceTime);
        }

        while (true)
        {
            if (_pending is null && !_eof)
            {
                if (_source.TryDecodeNextFrame(_pool, out VideoFrame? next))
                    _pending = next;
                else
                    _eof = true;
            }

            // Promote the look-ahead to current while it is still at or before the requested time.
            if (_pending is not null && _pending.Pts.Ticks <= sourceTime.Ticks + MatchToleranceTicks)
            {
                _current?.Dispose();
                _current = _pending;
                _pending = null;
                continue;
            }

            break;
        }

        // Prefer the promoted current frame; fall back to the first decoded frame if the request precedes it.
        return _current ?? _pending;
    }

    private VideoFrame? GetReverse(Timecode exclusiveEnd)
    {
        if (!_inReverse)
        {
            // Entering reverse mode: the sequential look-ahead is useless below the request; the window takes over.
            Reset();
            _started = false;
            _inReverse = true;
        }
        _window ??= new GopFrameWindow(_source, _pool);

        // Serve from the window while the walk stays inside it; refill below it when it runs dry. (A request that
        // turns around — later than the window's top — also refills, since PeekBelow can only shed frames.)
        VideoFrame? hit = _window.Count > 0 && (_window.LastPts is { } top && top.Ticks < exclusiveEnd.Ticks - MatchToleranceTicks
                                                 || _window.FirstPts is { } first && first.Ticks < exclusiveEnd.Ticks)
            ? _window.PeekBelow(exclusiveEnd)
            : null;
        if (hit is null && _window.FillBelow(exclusiveEnd) > 0)
            hit = _window.PeekBelow(exclusiveEnd);
        return hit;
    }

    private void Reset()
    {
        _current?.Dispose();
        _current = null;
        _pending?.Dispose();
        _pending = null;
        _eof = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _current?.Dispose();
        _pending?.Dispose();
        _window?.Dispose();
        _pool.Dispose();
        _source.Dispose();
    }
}
