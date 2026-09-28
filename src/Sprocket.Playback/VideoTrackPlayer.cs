using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Playback;

/// <summary>
/// Decodes and presents the frames for one <see cref="VideoTrack"/> on behalf of the <see cref="PlaybackEngine"/>
/// (PLAN.md step 14). It owns the track's <see cref="IVideoFrameFeed"/> and keeps one presented frame plus a
/// one-frame prefetch, dropping/holding frames to stay in sync with the playhead — the same per-track logic the
/// slice's single-track engine used, now one instance per track so the engine can composite N video tracks.
/// </summary>
/// <remarks>
/// Two feed-binding modes: a <b>fixed</b> feed (the slice/test path — one supplied feed, source never changes)
/// or a <b>factory</b> (the app path — the feed is created lazily for the active clip's source and rebuilt when
/// the active clip's source changes). All pumping happens on the engine's pump thread; the presented frame is
/// swapped under the engine's frame gate so the UI draw can never see a recycled buffer (ARCHITECTURE.md §1/§8).
/// <para><b>Temporal history</b> (Echo, plan/features/toy-cassette-camera.md phase 6): while the active clip carries a
/// temporal effect, frames the pump steps past are kept — pooled native frames, never copied — in a short history
/// keyed by source time, so the preview can bind the prior frames the plan asks for (<see cref="TryGetPriorFrame"/>,
/// the export rule: the latest frame at/before a forward time, strictly before a reversed one). A seek on such a clip
/// starts the decode far enough back that the history already covers the first frame's echoes, so a paused, scrubbed
/// frame shows the same trail export renders. The history keeps only frames a later frame can still need and is capped
/// at the <see cref="GopFrameWindow"/> byte budget; a footprint beyond it drops its oldest echoes in preview (export
/// stays exact) — the heavy-stack case the render cache covers.</para>
/// </remarks>
internal sealed class VideoTrackPlayer : IAsyncDisposable
{
    private readonly Func<MediaRefId, bool, IVideoFrameFeed?>? _feedFactory;
    private readonly object _frameGate;

    private IVideoFrameFeed? _feed;
    private MediaRefId? _feedSource;   // which source _feed currently decodes (factory mode)
    private bool _feedReverse;         // which direction the factory-built feed walks (a reversed clip needs a reverse feed)
    private Clip? _feedClip;           // which clip the feed is currently positioned for (so a same-source clip change re-seeks)
    private bool _feedStarted;
    private bool _needsSeek = true;    // a fresh player (or one after a seek) must seek before presenting
    private bool _atEof;               // the feed reached end-of-stream; hold (don't re-read) until a seek resumes it
    private bool _needsRebuild;        // the source's best-available file changed (e.g. a proxy became ready)

    private VideoFrame? _current;      // presented frame; guarded by _frameGate
    private VideoFrame? _next;         // pump-thread-only prefetch

    // Repeat-frame fast path (PLAN.md step 43): the source target the presented frame is known-correct for.
    // A held clip maps every timeline time to the same source time, so scrub/seek ticks inside its span keep
    // requesting the identical target — when it matches, the seek (and the re-decode behind it) is skipped and
    // the presented frame simply stays. Cleared whenever the feed or the presented frame is invalidated.
    private Timecode? _presentedTarget;

    // How far before a reversed clip's target the forward-only fallback seeks (see PumpAsync): a second covers one
    // frame interval of any source down to a 1 fps timelapse, so the promote loop finds the frame before the target,
    // while bounding the per-pump decode to about a second of frames (this path is tests / render cache only).
    private static readonly long ReverseFallbackLookbackTicks = Timecode.TicksPerSecond;

    // Temporal history (phase 6). _history is every frame shown before _current since _historyOrigin (the seek that
    // started the contiguous run), ascending PTS, trimmed to what the active clip's echoes can still need; guarded by
    // _frameGate for the UI's reads. _keepHistory/_historyFilledFor are pump-thread only.
    private readonly List<VideoFrame> _history = new();
    private readonly List<VideoFrame> _toRelease = new();  // pump-thread scratch: frames to dispose outside the gate
    private Timecode _historyOrigin;
    private bool _historyReverse;
    private bool _historyTrimmedFront;                     // forward: the run's first frame has been dropped
    private bool _historyOverBudget;                       // the footprint outgrew the byte budget: echoes may miss
    private bool _keepHistory;
    private Timecode? _historyFilledFor;                   // the target a temporal re-seek last ran for (no thrash)

    // A forward temporal seek starts this far before the earliest prior time, so the frame at/before it is decoded
    // (a seek serves the first frame at/after its target) — half a second covers any source down to 2 fps.
    private static readonly long HistorySeekLeadTicks = Timecode.TicksPerSecond / 2;

    // Same match tolerance as the export frame provider, so preview and export pick the same prior frame.
    private static readonly long MatchToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    // Decode info (codec + hw device) of the current feed, snapshotted when the feed is (re)built — it is
    // immutable for a feed's life, so caching it lets the diagnostics overlay read it from the UI thread
    // without touching native decoder state. Set on the pump thread / at construction; read cross-thread.
    private VideoDecodeInfo? _decodeInfo;

    /// <summary>Fixed-feed player (slice/test path): always decodes <paramref name="feed"/>, source never changes.</summary>
    public VideoTrackPlayer(VideoTrack track, IVideoFrameFeed feed, object frameGate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(frameGate);
        Track = track;
        _feed = feed;
        _frameGate = frameGate;
        _decodeInfo = feed.DecodeInfo;
    }

    /// <summary>Factory player (app path): creates the feed lazily for the active clip's source and playback
    /// direction — the factory receives <c>(source id, reverse)</c> and returns a forward or reverse feed
    /// (PLAN.md step 21 remainder).</summary>
    public VideoTrackPlayer(VideoTrack track, Func<MediaRefId, bool, IVideoFrameFeed?> feedFactory, object frameGate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(feedFactory);
        ArgumentNullException.ThrowIfNull(frameGate);
        Track = track;
        _feedFactory = feedFactory;
        _frameGate = frameGate;
    }

    /// <summary>The track this player renders.</summary>
    public VideoTrack Track { get; }

    /// <summary>Looks up a source's duration (the planner's upper clamp for a temporal effect's prior times), or null
    /// when unknown. Set by the engine from its project; optional.</summary>
    public Func<MediaRefId, Timecode?>? SourceDuration { get; set; }

    /// <summary>Frames currently kept in the temporal history (not counting <see cref="Current"/>). Pump-thread / test read.</summary>
    internal int HistoryCount => _history.Count;

    /// <summary>The currently-presented frame, or <c>null</c>. Read only while holding the engine's frame gate.</summary>
    public VideoFrame? Current => _current;

    /// <summary>How this player's current feed decodes (codec + hardware device), or <c>null</c> when it has no
    /// feed. A cached snapshot, safe to read from another thread (the diagnostics overlay).</summary>
    public VideoDecodeInfo? DecodeInfo => _decodeInfo;

    /// <summary>Forces the next pump to re-seek the feed (called by the engine when the playhead jumps).</summary>
    public void MarkNeedsSeek() => _needsSeek = true;

    /// <summary>Which source this player's feed currently decodes (factory mode), or <c>null</c> if it has no
    /// feed yet. Read on the pump thread.</summary>
    public MediaRefId? FeedSource => _feedSource;

    /// <summary>
    /// Requests that this player rebuild its feed on the next pump — used when a source's best-available file
    /// changes underneath it (a proxy became ready, PLAN.md step 18), so the preview transparently switches
    /// without a clip-source change. No-op in fixed-feed mode (the slice/test single feed). Called on the pump thread.
    /// </summary>
    public void RequestRebuild()
    {
        if (_feedFactory is not null)
            _needsRebuild = true;
    }

    /// <summary>
    /// Advances this track's frame toward the playhead <paramref name="pos"/>: (re)targets the feed to the
    /// active clip's source, seeks if needed, then promotes the latest frame at/just before the target while
    /// dropping intermediates. Returns whether the presented frame changed. The engine, not this player, counts
    /// dropped frames — it measures them in timeline frames off the playhead (so a clip whose source runs faster
    /// than the sequence is not charged for its correct downsampling). With no active clip (or an offline source)
    /// the track contributes nothing and its current frame is cleared.
    /// </summary>
    public async Task<bool> PumpAsync(Timecode pos, bool force, CancellationToken ct)
    {
        // Matches PlaybackEngine.ActiveVideoClip (and so RenderGraph, §5): a disabled track or a disabled clip
        // shows nothing, and must also stop decoding and release its frame so the preview can't hold it.
        Clip? clip = Track.Enabled && Track.ResolveActiveClip(pos) is { Enabled: true } active ? active : null;
        if (clip is null)
        {
            ClearCurrent();
            _feedClip = null; // re-entering any clip must re-seek the feed to that clip's in-point
            return false;
        }

        // A change of active clip breaks source-time continuity even when the next clip draws from the SAME
        // source — e.g. the same media placed twice on one track with a gap between the two clips. The feed is
        // left sitting at the previous clip's out-point (or parked at EOF), so it must seek to the new clip's
        // mapped in-point. Without this the reused feed is positioned past the new clip's source range and never
        // promotes a frame, so the track holds black. EnsureFeedFor only re-seeks when the source id changes, so
        // a same-source clip change would otherwise slip through.
        if (!ReferenceEquals(clip, _feedClip))
        {
            _needsSeek = true;
            _feedClip = clip;
        }

        if (!EnsureFeedFor(clip.MediaRefId, clip.Reverse))
        {
            ClearCurrent();
            return false;
        }

        if (!_feedStarted)
        {
            _feed!.Start(); // idempotent
            _feedStarted = true;
        }

        // The video time map (Posterize Time, plan/features/toy-cassette-camera.md phase 2): a posterized clip's
        // target stays constant within each step, so the promote walk below simply finds nothing new due and
        // holds the presented frame — the same cheap path slow motion takes — until the next step boundary.
        Timecode target = clip.MapToSourceVideo(pos);
        bool reverse = _feed!.IsReverse;

        // Temporal effects (Echo): which earlier source frames this frame's echoes read. A reversed clip on a
        // forward-only feed re-seeks for every frame (below), so it keeps no history — its echoes are export-only.
        IReadOnlyList<Timecode> priors = clip.Reverse && !reverse
            ? []
            : RenderGraph.ResolvePriorSourceTimes(clip, pos, SourceDuration?.Invoke(clip.MediaRefId));
        _keepHistory = priors.Count > 0;
        if (!_keepHistory)
        {
            _historyFilledFor = null;
            if (_history.Count > 0)
                ClearHistory();
        }
        else if (!_needsSeek && _historyFilledFor != target && !_historyOverBudget && !HistoryCovers(priors))
        {
            // The echoes reach frames the history doesn't hold (the effect was just added, or a jump the pump walked
            // instead of seeking): re-seek once for this target so the decode starts far enough back.
            _needsSeek = true;
            _presentedTarget = null;
        }
        // A reversed clip's mapped time is an *exclusive* upper bound (the clip start maps to the out-point), so the
        // frame to show is the latest strictly before it: the forward promote rule runs against target − 1 tick.
        Timecode forwardTarget = clip.Reverse ? new Timecode(target.Ticks - 1) : target;
        bool localForce = force || _needsSeek;
        if (clip.Reverse && !reverse)
        {
            // A reversed clip on a forward-only feed (the fixed-feed path: tests, the render cache) has no backward
            // decode to lean on, so every new target re-seeks the feed a little *before* the target and promotes
            // forward to the latest frame at/before it — correct output at a GOP-decode cost per frame. The factory
            // path builds a genuine reverse feed instead (ReverseRingVideoFrameFeed).
            if (_presentedTarget == target && _current is not null)
            {
                _needsSeek = false;
                return false;
            }
            _feed.RequestSeek(new Timecode(Math.Max(0, target.Ticks - ReverseFallbackLookbackTicks)));
            _next?.Dispose();
            _next = null;
            _atEof = false;
            _needsSeek = false;
            localForce = false; // the look-back frame itself must not be force-presented
        }
        else if (_needsSeek)
        {
            // Repeat-frame fast path: the presented frame is already exactly right for this source target (a
            // held clip's constant map, or repeated seeks to one spot), so don't disturb the feed at all.
            if (_presentedTarget == target && _current is not null && (!_keepHistory || HistoryCovers(priors)))
            {
                _needsSeek = false;
                return false;
            }
            // A temporal clip seeks far enough back that the walk to the target passes (and keeps) every prior frame
            // its echoes read: forward, a lead before the earliest; reversed (descending feed), from the latest.
            Timecode seekTarget = target;
            if (_keepHistory)
            {
                seekTarget = reverse
                    ? (priors[^1] > target ? priors[^1] : target)
                    : new Timecode(Math.Max(0, Math.Min(priors[0].Ticks, target.Ticks) - HistorySeekLeadTicks));
                _historyFilledFor = target;
            }
            ClearHistory();
            _historyOrigin = seekTarget;
            _historyReverse = reverse;
            _historyTrimmedFront = false;
            _historyOverBudget = false;
            _feed!.RequestSeek(seekTarget);
            _next?.Dispose();
            _next = null;
            _atEof = false; // a seek resumes the feed from the new target
            _needsSeek = false;
        }

        // The feed has been drained to end-of-stream. The decode worker parks at EOF and only resumes on a seek,
        // so reading again here would block forever. Hold the last presented frame instead. Without this, once the
        // source is exhausted while the playhead is still inside the clip's timeline span (the audio master clock
        // keeps the position just short of the end), the pump would hang on the read — freezing the playhead and
        // never reaching the end-of-timeline stop, while the independent audio clock kept running.
        if (_atEof)
            return false;

        bool promoted = false;
        _next ??= await _feed!.ReadAsync(ct).ConfigureAwait(false);

        // After a seek, present the freshly decoded frame even if its PTS sits just past the target.
        if (localForce && _next is not null)
        {
            Promote(_next);
            promoted = true;
            _next = await _feed!.ReadAsync(ct).ConfigureAwait(false);
        }

        // Advance through every frame already due, dropping intermediates, landing on the latest ≤ target. Frames
        // skipped here are NOT all "dropped" in the stutter sense: when the source runs faster than the sequence
        // rate, skipping the in-between source frames is correct downsampling. The engine measures genuine drops
        // off the playhead's timeline-frame advance instead, so that intentional downsampling isn't miscounted.
        // A reverse feed's frames descend: the next (earlier) frame becomes due once the *shown* frame's PTS has
        // risen above the target — so the walk lands on the latest frame at/before the target from above.
        while (_next is not null && (reverse
                   ? PlaybackMath.ShouldPromoteReverse(_current?.Pts, target)
                   : PlaybackMath.ShouldPromote(_next.Pts, forwardTarget, forcePresent: false)))
        {
            Promote(_next);
            promoted = true;
            _next = await _feed!.ReadAsync(ct).ConfigureAwait(false);
        }

        // Forward-only fallback for a reversed clip: a target before the source's first frame yields nothing at/before
        // it — show the first decoded frame rather than hold a stale one (mirrors the force-present convention).
        if (clip.Reverse && !reverse && !promoted && _current is null && _next is not null)
        {
            Promote(_next);
            promoted = true;
            _next = await _feed!.ReadAsync(ct).ConfigureAwait(false);
        }

        // A null prefetch is end-of-stream: latch it so the next pump holds rather than blocking on a read the
        // parked worker can't satisfy until a seek. A seek (above) clears the latch and resumes decoding.
        if (_next is null)
            _atEof = true;

        // Drop history frames no later frame's echoes can need (the walk only moves on from here).
        if (_keepHistory)
            PruneHistory(priors);

        // Remember the target the presented frame is known-correct for (the repeat-frame fast path above). Only
        // when it is the right frame for the target — at/before it forward, strictly before it for a reversed clip;
        // the force-present path can show a frame past it, and the forward-only fallback can miss.
        bool exact = _current is not null && (reverse || clip.Reverse ? _current.Pts < target : _current.Pts <= target);
        _presentedTarget = exact ? target : null;

        return promoted;
    }

    /// <summary>Ensures <see cref="_feed"/> decodes <paramref name="sourceId"/> in the requested direction,
    /// (re)building it in factory mode when the source or direction changes. Returns false when no feed is
    /// available (offline / no-video source).</summary>
    private bool EnsureFeedFor(MediaRefId sourceId, bool reverse)
    {
        if (_feedFactory is null)
            return _feed is not null; // fixed feed: source assumed constant

        if (_feed is not null && _feedSource == sourceId && _feedReverse == reverse && !_needsRebuild)
            return true;

        // Either the active clip's source changed, or the source's best-available file changed under us (a proxy
        // became ready). Tear down the old feed and build one for the (possibly same) source.
        DisposeFeed();
        ClearHistory();          // the history belongs to the old feed's walk
        _presentedTarget = null; // a new feed must decode the frame fresh — never skip its first seek
        _needsRebuild = false;
        _feed = _feedFactory(sourceId, reverse);
        _feedSource = sourceId;
        _feedReverse = reverse;
        _feedStarted = false;
        _needsSeek = true;
        // Snapshot the (immutable) decode info now, before the feed's worker starts, so the overlay never reads
        // native decoder state off the pump thread.
        _decodeInfo = _feed?.DecodeInfo;
        return _feed is not null;
    }

    private void Promote(VideoFrame frame)
    {
        VideoFrame? old;
        lock (_frameGate)
        {
            old = _current;
            _current = frame;
            if (old is not null && _keepHistory)
            {
                InsertHistory(old); // an echo may still read it
                old = null;
            }
        }
        old?.Dispose();
        DisposeReleased();
    }

    private void ClearCurrent()
    {
        _presentedTarget = null;
        ClearHistory();
        VideoFrame? old;
        lock (_frameGate)
        {
            old = _current;
            _current = null;
        }
        old?.Dispose();
    }

    /// <summary>
    /// The frame a temporal effect's prior source time <paramref name="sourceTime"/> resolves to — the same frame
    /// export serves for it: the latest frame at/before it (1 ms tolerance) for a forward feed, strictly before it
    /// for a reversed one — from the history plus the current frame, when the contiguous run since the last seek
    /// provably holds it. False when it doesn't (the echo is then left out, never faked). Call only while holding the
    /// engine's frame gate, and don't retain the frame beyond it.
    /// </summary>
    public bool TryGetPriorFrame(Timecode sourceTime, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VideoFrame? frame)
    {
        frame = null;
        if (_current is not { } current)
            return false;

        if (_historyReverse)
        {
            // Descending run from the origin: every frame below it was shown in order, so the latest one strictly
            // below a time at/below the origin is known.
            if (sourceTime.Ticks > _historyOrigin.Ticks + MatchToleranceTicks)
                return false;
            VideoFrame? best = current.Pts < sourceTime ? current : null;
            foreach (VideoFrame f in _history)
                if (f.Pts < sourceTime && (best is null || f.Pts > best.Pts))
                    best = f;
            frame = best;
            return frame is not null;
        }

        // Ascending run: every frame from the run's start to the current one is held (bar those pruned below the
        // horizon), so the latest one at/before the time is the one a sequential walk showed for it.
        if (current.Pts.Ticks <= sourceTime.Ticks + MatchToleranceTicks)
        {
            frame = current;
            return true;
        }
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Pts.Ticks <= sourceTime.Ticks + MatchToleranceTicks)
            {
                frame = _history[i];
                return true;
            }
        }
        // Before the run's first frame: at the very start of the stream that first frame is what export shows too.
        if (_historyOrigin.Ticks <= 0 && !_historyTrimmedFront)
        {
            frame = _history.Count > 0 ? _history[0] : current;
            return true;
        }
        return false;
    }

    /// <summary>Whether every one of <paramref name="priors"/> resolves from the history (pump thread).</summary>
    private bool HistoryCovers(IReadOnlyList<Timecode> priors)
    {
        lock (_frameGate)
        {
            for (int i = 0; i < priors.Count; i++)
                if (!TryGetPriorFrame(priors[i], out _))
                    return false;
            return true;
        }
    }

    /// <summary>Adds a frame the walk just stepped past to the history, in PTS order, then trims to the byte budget:
    /// the farthest-back frame (oldest forward, highest reversed) goes first. Under the frame gate.</summary>
    private void InsertHistory(VideoFrame frame)
    {
        int index = _history.Count;
        while (index > 0 && _history[index - 1].Pts > frame.Pts)
            index--;
        _history.Insert(index, frame);

        int capacity = GopFrameWindow.DefaultCapacityFor(frame.Width, frame.Height);
        while (_history.Count > capacity)
        {
            _historyOverBudget = true; // don't re-seek for the echoes that now miss — they would miss again
            if (_historyReverse)
            {
                VideoFrame top = _history[^1];
                _history.RemoveAt(_history.Count - 1);
                _historyOrigin = top.Pts; // frames at/above it are no longer held
                _toRelease.Add(top);
            }
            else
            {
                _toRelease.Add(_history[0]);
                _history.RemoveAt(0);
                _historyTrimmedFront = true;
            }
        }
    }

    /// <summary>Drops the history frames no later frame's echoes can need: forward, those older than the best match
    /// for the earliest prior time; reversed, those at/above the latest one.</summary>
    private void PruneHistory(IReadOnlyList<Timecode> priors)
    {
        if (priors.Count == 0)
            return;
        lock (_frameGate)
        {
            if (_historyReverse)
            {
                Timecode hi = priors[^1];
                while (_history.Count > 0 && _history[^1].Pts >= hi)
                {
                    _toRelease.Add(_history[^1]);
                    _history.RemoveAt(_history.Count - 1);
                }
                if (hi < _historyOrigin)
                    _historyOrigin = hi;
            }
            else if (_current is { } current)
            {
                long lo = priors[0].Ticks + MatchToleranceTicks;
                while (_history.Count > 0 && (_history.Count > 1 ? _history[1].Pts : current.Pts).Ticks <= lo)
                {
                    _toRelease.Add(_history[0]);
                    _history.RemoveAt(0);
                    _historyTrimmedFront = true;
                }
            }
        }
        DisposeReleased();
    }

    private void ClearHistory()
    {
        lock (_frameGate)
        {
            _toRelease.AddRange(_history);
            _history.Clear();
        }
        DisposeReleased();
    }

    private void DisposeReleased()
    {
        foreach (VideoFrame f in _toRelease)
            f.Dispose();
        _toRelease.Clear();
    }

    private void DisposeFeed()
    {
        _next?.Dispose();
        _next = null;
        if (_feed is not null)
        {
            // Fire-and-forget the async teardown of the old feed; we don't block the pump on it. A feed switch
            // only happens at a clip-source boundary, which is rare relative to the per-frame pump.
            IVideoFrameFeed old = _feed;
            _ = old.DisposeAsync();
            _feed = null;
        }
        _feedStarted = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_feed is not null)
            await _feed.DisposeAsync().ConfigureAwait(false);
        _feed = null;

        lock (_frameGate)
        {
            _current?.Dispose();
            _current = null;
            foreach (VideoFrame f in _history)
                f.Dispose();
            _history.Clear();
        }
        _next?.Dispose();
        _next = null;
    }
}
