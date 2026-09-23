using System.Diagnostics;
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
/// <para>Decode runs in software (<see cref="HardwareAccelMode.Disabled"/>) for bit-deterministic output, which is
/// what makes golden-frame export testing meaningful. Not thread-safe: one provider per source, driven by the
/// single export render thread. The returned frame is owned by this provider and stays valid only until the next
/// <see cref="GetFrame"/> call (the caller composites it immediately), so callers must not hold it.</para>
/// <para><b>Decode prefetch</b> (export-speed phase 2, opt-in): after a forward request, the frame <em>after</em>
/// the look-ahead is decoded on a background task so the next request finds it ready — each source's decode
/// overlaps the render of the current frame (and other sources' decodes). The decoder still produces the identical
/// frame sequence in the identical order; only <em>when</em> it runs changes, so the output is unchanged. The
/// <see cref="MediaSource"/> is only ever touched by one thread at a time: every other operation first waits for
/// (or, ahead of a seek, discards) the in-flight prefetch.</para>
/// </remarks>
internal sealed class ExportFrameProvider : IDisposable
{
    // A frame whose PTS is within this tolerance of (or before) the request counts as "at or before" it, so
    // sub-tick rounding between the timeline clock and the source PTS never skips the correct frame.
    private static readonly long MatchToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    private readonly MediaSource _source;
    private readonly VideoFramePool _pool;
    private readonly bool _isStill;   // a single-frame still: hold the one frame for every requested time (step 42)
    private readonly bool _prefetch;  // decode the next forward frame in the background (export-speed phase 2)

    // Forward (sequential) state.
    private VideoFrame? _current;   // the frame currently "on screen" for the last request
    private VideoFrame? _pending;   // decoded look-ahead whose PTS is past the last request
    private Task<VideoFrame?>? _prefetchTask; // the frame after _pending, decoding in the background (null result = EOF)
    private bool _started;
    private bool _eof;

    // Reverse state: the GOP window holding frames before the last reverse request, in ascending order. The two
    // modes never hold frames at the same time — switching direction resets the other side.
    private GopFrameWindow? _window;
    private bool _inReverse;

    private bool _disposed;

    // Decode-time accounting (export-speed phase 1): Stopwatch ticks spent in seek / decode / GOP refill only, so a
    // look-ahead or window cache hit costs nothing. Two timestamps per decode operation — no allocation. The totals
    // are also updated by the prefetch task, hence Interlocked; the blocking share is render-thread-only.
    private long _decodeTimestampTicks;
    private long _decodeOperations;
    private long _blockingDecodeTimestampTicks;

    /// <param name="source">The full-resolution source to decode. The provider owns and disposes it.</param>
    /// <param name="isStill">Whether the source is a single still image (PLAN.md step 42): its one frame is held
    /// and returned for every requested source time, so a still clip renders identically across its whole span
    /// instead of seeking past its only frame and going black.</param>
    /// <param name="prefetch">Whether to decode the next forward frame on a background task (export-speed phase 2).
    /// Output-neutral; off by default so a directly constructed provider stays single-threaded.</param>
    public ExportFrameProvider(MediaSource source, bool isStill = false, bool prefetch = false)
    {
        _source = source;
        _isStill = isStill;
        _prefetch = prefetch && !isStill; // a still decodes once — nothing to prefetch
        _pool = new VideoFramePool(source.Info.Width, source.Info.Height);
    }

    /// <summary>Total time spent seeking / decoding the source so far (cache hits excluded), on any thread —
    /// including background prefetch that overlapped rendering.</summary>
    internal TimeSpan DecodeElapsed => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _decodeTimestampTicks));

    /// <summary>The share of decode time the calling (render) thread actually waited on: synchronous seek / decode /
    /// refill plus waits for an unfinished prefetch (which also cover the task's scheduling latency, so this can exceed
    /// the prefetch's own decode time). Equals <see cref="DecodeElapsed"/> when prefetch is off. Render-thread only.</summary>
    internal TimeSpan BlockingDecodeElapsed => Stopwatch.GetElapsedTime(0, _blockingDecodeTimestampTicks);

    /// <summary>Number of seek / decode / GOP-refill operations performed so far.</summary>
    internal long DecodeOperations => Interlocked.Read(ref _decodeOperations);

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
            Seek(sourceTime);
            _started = true;
        }
        else if (!_started)
        {
            Seek(sourceTime);
            _started = true;
        }
        else if (_current is not null && sourceTime.Ticks < _current.Pts.Ticks - MatchToleranceTicks)
        {
            // The request moved backwards (a cut to an earlier in-point); re-seek and rebuild the look-ahead.
            Reset();
            Seek(sourceTime);
        }

        while (true)
        {
            if (_pending is null && !_eof)
            {
                if (TakeNext(out VideoFrame? next))
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

        // The look-ahead is past this request, so the next request will want the frame after it: start decoding
        // that now, overlapping the caller's render of the frame returned below.
        if (_prefetch && _pending is not null && !_eof && _prefetchTask is null)
            _prefetchTask = Task.Run(() => TryDecodeNext(blocking: false, out VideoFrame? f) ? f : null);

        // Prefer the promoted current frame; fall back to the first decoded frame if the request precedes it.
        return _current ?? _pending;
    }

    /// <summary>The next frame in decode order — the prefetched one when a prefetch is in flight (waiting for it if
    /// unfinished), else a synchronous decode. False at end of stream.</summary>
    private bool TakeNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VideoFrame? frame)
    {
        if (_prefetchTask is not { } task)
            return TryDecodeNext(blocking: true, out frame);

        _prefetchTask = null;
        long start = Stopwatch.GetTimestamp();
        frame = task.GetAwaiter().GetResult(); // a decode fault surfaces here, exactly as a synchronous one would
        _blockingDecodeTimestampTicks += Stopwatch.GetTimestamp() - start;
        return frame is not null;
    }

    /// <summary>Waits out and drops an in-flight prefetch. Only called ahead of a seek / window refill (or on
    /// dispose), which repositions the decoder anyway, so the skipped frame is never missed.</summary>
    private void DiscardPrefetch()
    {
        if (_prefetchTask is not { } task)
            return;
        _prefetchTask = null;
        long start = Stopwatch.GetTimestamp();
        try { task.GetAwaiter().GetResult()?.Dispose(); }
        catch { /* the discarded frame was never needed; a persistent decode fault resurfaces on the next decode */ }
        _blockingDecodeTimestampTicks += Stopwatch.GetTimestamp() - start;
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
        if (hit is null && FillWindowBelow(exclusiveEnd) > 0)
            hit = _window.PeekBelow(exclusiveEnd);
        return hit;
    }

    private void Seek(Timecode sourceTime)
    {
        // SeekTo lands on the first frame at/after its target, but a forward request wants the latest frame at/before
        // it. Seeking one source frame (plus tolerance) early makes that frame the first one decoded, so a seek to a
        // time between two source frames serves the same frame a sequential walk would — which is what lets the
        // export's render workers each start mid-stream and still match the one-worker output (export-speed phase 2).
        Rational fps = _source.Info.FrameRate;
        long lead = fps.Num > 0 && fps.Den > 0 ? Timecode.FromFrames(1, fps).Ticks + MatchToleranceTicks : 0;
        long start = Stopwatch.GetTimestamp();
        _source.SeekTo(Timecode.FromTicks(Math.Max(0, sourceTime.Ticks - lead)));
        EndDecode(start, blocking: true);
    }

    private bool TryDecodeNext(bool blocking, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VideoFrame? frame)
    {
        long start = Stopwatch.GetTimestamp();
        bool decoded = _source.TryDecodeNextFrame(_pool, out frame);
        EndDecode(start, blocking);
        return decoded;
    }

    private int FillWindowBelow(Timecode exclusiveEnd)
    {
        long start = Stopwatch.GetTimestamp();
        int held = _window!.FillBelow(exclusiveEnd);
        EndDecode(start, blocking: true);
        return held;
    }

    private void EndDecode(long startTimestamp, bool blocking)
    {
        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        Interlocked.Add(ref _decodeTimestampTicks, elapsed);
        Interlocked.Increment(ref _decodeOperations);
        if (blocking)
            _blockingDecodeTimestampTicks += elapsed;
    }

    /// <summary>Drops the forward state, including any in-flight prefetch. Every caller follows it with a seek or a
    /// GOP window refill, which repositions the decoder.</summary>
    private void Reset()
    {
        DiscardPrefetch();
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

        DiscardPrefetch(); // the background decode must finish before the source it reads is freed
        _current?.Dispose();
        _pending?.Dispose();
        _window?.Dispose();
        _pool.Dispose();
        _source.Dispose();
    }
}
