using System.Diagnostics;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;

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
/// <para>Decode normally runs in software (<see cref="HardwareAccelMode.Disabled"/>) for bit-deterministic output,
/// which is what makes golden-frame export testing meaningful; only Fast Export's GPU-decode opt-in opens a GPU
/// decoder (see below). Not thread-safe: one provider per source, driven by the
/// single export render thread. The returned frame is owned by this provider and stays valid only until the next
/// <see cref="GetFrame"/> call (the caller composites it immediately), so callers must not hold it.</para>
/// <para><b>Decode prefetch</b> (export-speed phase 2, opt-in): after a forward request, the frame <em>after</em>
/// the look-ahead is decoded on a background task so the next request finds it ready — each source's decode
/// overlaps the render of the current frame (and other sources' decodes). The decoder still produces the identical
/// frame sequence in the identical order; only <em>when</em> it runs changes, so the output is unchanged. The
/// <see cref="MediaSource"/> is only ever touched by one thread at a time: every other operation first waits for
/// (or, ahead of a seek, discards) the in-flight prefetch.</para>
/// <para><b>GPU decode</b> (Fast Export, export-speed phase 3): a source opened with hardware decode that then fails
/// <em>during</em> a seek / decode / GOP refill is reopened once in software and the operation retried, resuming after
/// the last frame it produced — the same one-shot fallback the preview decode rings apply (ARCHITECTURE.md §11).
/// <see cref="FellBackToSoftware"/> records it for the export summary. A software source's faults propagate as
/// before.</para>
/// <para><b>Temporal effects</b> (Echo, plan/features/toy-cassette-camera.md phase 6): <see cref="GetFrames"/> serves a
/// layer's frame <em>and</em> its prior frames in one call. Forward, the frames the walk has already passed are kept
/// in a small history (pooled native frames — no copies, ARCHITECTURE.md §1) instead of being recycled, so each
/// frame's <c>t − kΔ</c> requests hit it rather than re-seeking: a clip's echoes cost one seek at its start, then the
/// same single sequential decode as without them. The history holds only what the next request can still need
/// (frames at or after the earliest prior time) and is capped at the <see cref="GopFrameWindow"/> byte budget; past
/// the cap the oldest frames drop and a miss falls back to a (correct, slower) re-seek. Reversed, the prior frames lie
/// <em>above</em> the frame being shown, so each temporal request turns the GOP walk around and refills the window —
/// correct, at about one GOP decode per frame. Every frame handed out by one call stays valid until the next call,
/// even if the walk inside the call drops it (it is retired, not recycled, until then).</para>
/// </remarks>
internal sealed class ExportFrameProvider : IDisposable
{
    // A frame whose PTS is within this tolerance of (or before) the request counts as "at or before" it, so
    // sub-tick rounding between the timeline clock and the source PTS never skips the correct frame.
    private static readonly long MatchToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    private MediaSource _source;    // swapped once for a software reopen if a GPU decoder fails mid-export
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

    // Temporal fetch state (phase 6): the forward history (frames decoded before _current since the last seek,
    // ascending), whether the walk keeps it, and the frames the fetch in progress has handed out (a frame dropped
    // while leased is retired — released at the next call — instead of recycled under the caller).
    private readonly List<VideoFrame> _history = new();
    private readonly int _historyCapacity;
    private bool _keepHistory;
    private readonly List<VideoFrame> _leased = new();
    private readonly List<VideoFrame> _retired = new();
    private bool _fetching;
    private Timecode? _reverseKnownTop; // reverse: the window answers only requests at/below this (frames above were shed)
    private int _seekCount;

    private bool _disposed;

    // GPU decode facts (export-speed phase 3), captured at open so they stay readable after dispose.
    private readonly string? _hardwareDevice;
    private bool _fellBackToSoftware; // the one-shot mid-export software reopen happened

    // Where the forward walk stands, so a software reopen resumes exactly there: the last seek target, and the last
    // frame decoded since it (null right after a seek). The source's own LastDecodedPts can predate the latest seek.
    private Timecode _walkSeekTarget;
    private Timecode? _walkLastPts;

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
        _hardwareDevice = source.HardwareDeviceName;
        _historyCapacity = GopFrameWindow.DefaultCapacityFor(source.Info.Width, source.Info.Height);
    }

    /// <summary>The GPU device the source opened on (e.g. <c>d3d11va</c>), or <see langword="null"/> when it opened in
    /// software. Unchanged by a later fallback — see <see cref="FellBackToSoftware"/>.</summary>
    internal string? HardwareDevice => _hardwareDevice;

    /// <summary>Whether the source is a single still image (held as one frame).</summary>
    internal bool IsStill => _isStill;

    /// <summary>Whether the source opened on a GPU decoder, failed mid-export, and was reopened in software.</summary>
    internal bool FellBackToSoftware => _fellBackToSoftware;

    /// <summary>Whether the source decoded on the GPU for the whole export so far.</summary>
    internal bool DecodedOnHardware => _hardwareDevice is not null && !_fellBackToSoftware;

    /// <summary>Test seam: runs before every seek / decode / GOP refill on the current source; throwing simulates a
    /// GPU decode fault. Set <see cref="AssumeHardwareForTests"/> too so the fault is treated as a hardware one.</summary>
    internal Action? DecodeFaultForTests { get; set; }

    /// <summary>Test seam: treat the (software) source as GPU-decoding (CI has no GPU), so an injected fault exercises
    /// the software fallback.</summary>
    internal bool AssumeHardwareForTests { get; set; }

    private bool IsHardwareDecoding =>
        !_fellBackToSoftware && (AssumeHardwareForTests || _source.DecodeInfo.IsHardwareAccelerated);

    /// <summary>Total time spent seeking / decoding the source so far (cache hits excluded), on any thread —
    /// including background prefetch that overlapped rendering.</summary>
    internal TimeSpan DecodeElapsed => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _decodeTimestampTicks));

    /// <summary>The share of decode time the calling (render) thread actually waited on: synchronous seek / decode /
    /// refill plus waits for an unfinished prefetch (which also cover the task's scheduling latency, so this can exceed
    /// the prefetch's own decode time). Equals <see cref="DecodeElapsed"/> when prefetch is off. Render-thread only.</summary>
    internal TimeSpan BlockingDecodeElapsed => Stopwatch.GetElapsedTime(0, _blockingDecodeTimestampTicks);

    /// <summary>Number of seek / decode / GOP-refill operations performed so far.</summary>
    internal long DecodeOperations => Interlocked.Read(ref _decodeOperations);

    /// <summary>Number of times the decoder was repositioned — forward seeks plus reverse GOP-window refills. A forward
    /// clip with echoes should cost one per clip (the temporal history serves every <c>t − kΔ</c> request).</summary>
    internal int SeekCount => _seekCount;

    /// <summary>Frames currently held in the forward temporal history (not counting the current frame).</summary>
    internal int HistoryCount => _history.Count;

    /// <summary>The most frames the forward temporal history holds — the <see cref="GopFrameWindow"/> byte budget for
    /// the source's frame size.</summary>
    internal int HistoryCapacity => _historyCapacity;

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
        ReleaseRetired();

        // A plain (non-temporal) request: the walk no longer needs its history.
        if (_keepHistory)
        {
            _keepHistory = false;
            ClearHistory();
        }

        // A still has one frame at (about) time zero; every timeline time inside the clip maps to it, so pin the
        // request to zero instead of seeking to the mapped source time (which would run off the single frame).
        if (_isStill)
        {
            sourceTime = Timecode.Zero;
            reverse = false;
        }

        return reverse ? GetReverse(sourceTime) : GetForward(sourceTime);
    }

    /// <summary>
    /// Returns the frame to display at <paramref name="sourceTime"/> (as <see cref="GetFrame"/>) and appends to
    /// <paramref name="priorFrames"/> the frame each of <paramref name="priorTimes"/> resolves to (a layer's
    /// <see cref="Core.Rendering.VideoLayer.PriorSourceTimes"/>, ascending), keyed by the requested time — the
    /// temporal-effect fetch (plan/features/toy-cassette-camera.md phase 6). Requests run in source order (ascending
    /// forward, descending reversed) so the walk never backs up; forward, the frames passed are kept in the history for
    /// the next frame's echoes. A time that yields no frame is simply left out. Every returned frame stays valid until
    /// the next call on this provider.
    /// </summary>
    public VideoFrame? GetFrames(Timecode sourceTime, bool reverse, IReadOnlyList<Timecode> priorTimes, List<PriorFrame> priorFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(priorTimes);
        ArgumentNullException.ThrowIfNull(priorFrames);
        ReleaseRetired();

        if (_isStill)
        {
            // A still's one frame answers every time (GetFrame pins the request to zero).
            VideoFrame? still = GetFrame(sourceTime);
            if (still is not null)
                for (int i = 0; i < priorTimes.Count; i++)
                    priorFrames.Add(ToPrior(priorTimes[i], still));
            return still;
        }

        _fetching = true;
        try
        {
            if (!reverse)
            {
                _keepHistory = true;
                // Frames older than the best match for the earliest time asked for can no longer be needed (the walk
                // only moves forward from here), so drop them before walking.
                Timecode horizon = priorTimes.Count > 0 && priorTimes[0] < sourceTime ? priorTimes[0] : sourceTime;
                if (!_inReverse && _current is not null)
                    PruneHistory(horizon);
                for (int i = 0; i < priorTimes.Count; i++) // index loop: no enumerator allocation per frame
                    AddPrior(priorTimes[i], GetForward(priorTimes[i]), priorFrames);
                return Lease(GetForward(sourceTime));
            }

            if (_keepHistory)
            {
                _keepHistory = false;
                ClearHistory();
            }
            for (int i = priorTimes.Count - 1; i >= 0; i--)
                AddPrior(priorTimes[i], GetReverse(priorTimes[i]), priorFrames);
            return Lease(GetReverse(sourceTime));
        }
        finally
        {
            _fetching = false;
        }
    }

    private void AddPrior(Timecode requested, VideoFrame? frame, List<PriorFrame> priorFrames)
    {
        if (Lease(frame) is { } f)
            priorFrames.Add(ToPrior(requested, f));
    }

    private static PriorFrame ToPrior(Timecode requested, VideoFrame frame) =>
        new(requested, frame.Pixels, frame.RowBytes, frame.Width, frame.Height, frame.HasAlpha);

    /// <summary>Marks <paramref name="frame"/> as handed out by the fetch in progress (see <see cref="Release"/>).</summary>
    private VideoFrame? Lease(VideoFrame? frame)
    {
        if (frame is not null && _fetching && !_leased.Contains(frame))
            _leased.Add(frame);
        return frame;
    }

    /// <summary>Lets go of a frame the walk no longer holds: recycled to the pool — unless the temporal fetch in progress
    /// has handed it out, in which case it is retired until the next call so the caller's draw can still read it.</summary>
    private void Release(VideoFrame frame)
    {
        if (_fetching && _leased.Contains(frame))
            _retired.Add(frame);
        else
            frame.Dispose();
    }

    /// <summary>Recycles the frames the previous fetch retired (its caller has drawn them by now) and forgets its leases.</summary>
    private void ReleaseRetired()
    {
        foreach (VideoFrame frame in _retired)
            frame.Dispose();
        _retired.Clear();
        _leased.Clear();
    }

    /// <summary>Keeps the frame the walk just stepped past for later prior-frame requests, trimming to the budget.</summary>
    private void AddHistory(VideoFrame frame)
    {
        _history.Add(frame);
        while (_history.Count > _historyCapacity)
        {
            Release(_history[0]);
            _history.RemoveAt(0);
        }
    }

    /// <summary>Drops history frames that cannot answer any request at or after <paramref name="horizon"/>: those whose
    /// successor (the next history frame, or the current frame) is already at or before it.</summary>
    private void PruneHistory(Timecode horizon)
    {
        while (_history.Count > 0)
        {
            Timecode next = _history.Count > 1 ? _history[1].Pts : _current!.Pts;
            if (next.Ticks > horizon.Ticks + MatchToleranceTicks)
                break;
            Release(_history[0]);
            _history.RemoveAt(0);
        }
    }

    private void ClearHistory()
    {
        foreach (VideoFrame frame in _history)
            Release(frame);
        _history.Clear();
    }

    /// <summary>The latest history frame at or before <paramref name="sourceTime"/> — the frame a sequential walk showed
    /// for it, since the history is every frame decoded between the last seek and the current frame — or null.</summary>
    private VideoFrame? FindInHistory(Timecode sourceTime)
    {
        for (int i = _history.Count - 1; i >= 0; i--)
            if (_history[i].Pts.Ticks <= sourceTime.Ticks + MatchToleranceTicks)
                return _history[i];
        return null;
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
            // An earlier frame a temporal effect asks for is still in the history: serve it without moving the walk.
            if (_keepHistory && FindInHistory(sourceTime) is { } kept)
                return kept;

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
                if (_current is { } passed)
                {
                    if (_keepHistory)
                        AddHistory(passed); // a later request's echo may still need it
                    else
                        Release(passed);
                }
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
        try { task.GetAwaiter().GetResult()?.Dispose(); } // never handed out, so never leased
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
            _reverseKnownTop = null;
        }
        _window ??= new GopFrameWindow(_source, _pool, release: Release);

        // Serve from the window while the walk stays inside it; refill below it when it runs dry. A request that turns
        // around — above the highest bound the window still answers for, because PeekBelow sheds the frames above each
        // request (a reversed clip's prior frames, phase 6) — also refills rather than serve a frame from below the gap.
        bool inside = _reverseKnownTop is { } known && exclusiveEnd.Ticks <= known.Ticks + MatchToleranceTicks;
        VideoFrame? hit = inside && _window.Count > 0
                          && (_window.LastPts is { } top && top.Ticks < exclusiveEnd.Ticks - MatchToleranceTicks
                              || _window.FirstPts is { } first && first.Ticks < exclusiveEnd.Ticks)
            ? _window.PeekBelow(exclusiveEnd)
            : null;
        if (hit is null && FillWindowBelow(exclusiveEnd) > 0)
            hit = _window.PeekBelow(exclusiveEnd);
        if (hit is not null)
            _reverseKnownTop = _reverseKnownTop is { } k && k < exclusiveEnd ? k : exclusiveEnd;
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
        Timecode target = Timecode.FromTicks(Math.Max(0, sourceTime.Ticks - lead));
        long start = Stopwatch.GetTimestamp();
        try
        {
            DecodeFaultForTests?.Invoke();
            _source.SeekTo(target);
        }
        catch when (IsHardwareDecoding)
        {
            if (!TryReopenInSoftware())
                throw;
            _source.SeekTo(target); // a fresh decoder: redo the seek from scratch
        }
        _seekCount++;
        _walkSeekTarget = target;
        _walkLastPts = null;
        EndDecode(start, blocking: true);
    }

    private bool TryDecodeNext(bool blocking, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VideoFrame? frame)
    {
        long start = Stopwatch.GetTimestamp();
        bool decoded;
        try
        {
            DecodeFaultForTests?.Invoke();
            decoded = _source.TryDecodeNextFrame(_pool, out frame);
        }
        catch when (IsHardwareDecoding)
        {
            if (!TryReopenInSoftware())
                throw;
            decoded = ResumeWalk(out frame);
        }
        if (decoded)
            _walkLastPts = frame!.Pts;
        EndDecode(start, blocking);
        return decoded;
    }

    /// <summary>After a software reopen mid-walk: seek back to where the walk stands and decode forward — past the
    /// last frame already produced (dropping it and anything before it), or from the last seek target when nothing
    /// has been decoded since — so the walk continues with the next frame, no repeat and no gap.</summary>
    private bool ResumeWalk([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VideoFrame? frame)
    {
        _source.SeekTo(_walkLastPts ?? _walkSeekTarget);
        while (_source.TryDecodeNextFrame(_pool, out frame))
        {
            if (_walkLastPts is not { } last || frame.Pts.Ticks > last.Ticks + MatchToleranceTicks)
                return true;
            frame.Dispose(); // already produced before the failure
        }
        return false;
    }

    private int FillWindowBelow(Timecode exclusiveEnd)
    {
        long start = Stopwatch.GetTimestamp();
        int held;
        try
        {
            DecodeFaultForTests?.Invoke();
            held = _window!.FillBelow(exclusiveEnd);
        }
        catch when (IsHardwareDecoding)
        {
            if (!TryReopenInSoftware())
                throw;
            // The reopen dropped the window over the failed source: rebuild it over the software one and refill
            // (each fill re-seeks).
            _window = new GopFrameWindow(_source, _pool, release: Release);
            held = _window.FillBelow(exclusiveEnd);
        }
        _seekCount++;
        _reverseKnownTop = exclusiveEnd; // the fill holds every frame below the bound it was asked for
        EndDecode(start, blocking: true);
        return held;
    }

    /// <summary>
    /// One-shot runtime fallback for a GPU decoder that failed during the export: reopen the same media in software
    /// and swap it in, disposing the failed source. Returns <see langword="false"/> — leaving the original fault to
    /// propagate — when the software reopen itself fails. Runs on whichever thread holds the source (the render
    /// thread, or the prefetch task every other operation waits out), so the swap is never raced.
    /// </summary>
    private bool TryReopenInSoftware()
    {
        MediaSource software;
        try
        {
            software = _source.ReopenInSoftware();
        }
        catch
        {
            return false;
        }
        _source.Dispose();
        _source = software;
        // Any GOP window reads the failed source: drop it so the next reverse request rebuilds it over the software
        // one. Safe to free its frames — a fallback mid-refill discards them anyway, and in forward mode the window
        // was already cleared on leaving reverse, so no frame it holds is still being served.
        _window?.Dispose();
        _window = null;
        _fellBackToSoftware = true;
        DecodeFaultForTests = null; // an injected fault targets the GPU decoder, which is gone
        return true;
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
        if (_current is not null)
            Release(_current);
        _current = null;
        if (_pending is not null)
            Release(_pending);
        _pending = null;
        ClearHistory();
        _eof = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DiscardPrefetch(); // the background decode must finish before the source it reads is freed
        _fetching = false;
        _current?.Dispose();
        _pending?.Dispose();
        foreach (VideoFrame frame in _history)
            frame.Dispose();
        _history.Clear();
        _window?.Dispose();
        foreach (VideoFrame frame in _retired)
            frame.Dispose();
        _retired.Clear();
        _pool.Dispose();
        _source.Dispose();
    }
}
