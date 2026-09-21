using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Sprocket.Core.Model;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.App.Stabilization;

/// <summary>Where one source's motion analysis sits in its lifecycle, for the Inspector status row.</summary>
public enum AnalysisState
{
    /// <summary>Never analysed (or the analysis was cancelled with no earlier result) — the effect renders
    /// pass-through until an analysis lands.</summary>
    NotAnalyzed,

    /// <summary>Queued behind the worker, not started yet.</summary>
    Queued,

    /// <summary>Being analysed now (see <see cref="AnalysisStatus.Progress"/>).</summary>
    Analyzing,

    /// <summary>A track is available and the effect stabilizes.</summary>
    Ready,

    /// <summary>Analysis failed (unreadable source, no video stream) — the effect stays pass-through.</summary>
    Failed,
}

/// <summary>An analysis lifecycle snapshot for one (source, detail) pair, as read by the Inspector.</summary>
/// <param name="State">Where the analysis sits.</param>
/// <param name="Progress">Completion 0..1 — meaningful while <see cref="AnalysisState.Analyzing"/>.</param>
public readonly record struct AnalysisStatus(AnalysisState State, double Progress);

/// <summary>
/// Runs and tracks background motion analyses for stabilized clips, and serves the recovered tracks to the render
/// pipeline as its <see cref="IMotionTrackProvider"/> (plan/features/stabilization.md phase 5). Modelled on
/// <see cref="Proxy.ProxyService"/>: a single below-normal-priority worker draws from a queue, generation counters
/// fence off stale completions, and completions raise events the app routes to a preview repaint + render-cache
/// invalidation — <b>no model mutation</b>, so nothing is undoable and analysis never dirties the document.
/// </summary>
/// <remarks>
/// <para><b>Analysis is a local, regenerable artifact.</b> Results persist in the per-user
/// <see cref="AnalysisCache"/> (never the project file), keyed by source identity + Detailed flag + bucketed
/// source range (<see cref="AnalysisKey"/>), so re-opening a project or using the same footage in another project
/// re-uses a cached track without re-analysing. Only the (cheap, deterministic) solve depends on the smoothing /
/// framing parameters, so tuning never re-analyses.</para>
/// <para><b>Threading.</b> <see cref="TryGetTrack"/> is called from the render thread and is lock-free and
/// non-blocking: it reads a <see cref="ConcurrentDictionary{TKey,TValue}"/> of ready tracks and returns
/// <see langword="null"/> on a miss (the effect renders pass-through). Every queue / state / generation mutation
/// happens under <see cref="_gate"/> so a transition and its queue effect are atomic. Events fire on the worker or
/// calling thread — subscribers marshal to their own.</para>
/// <para><b>Phase 5 scope.</b> Analysis is started explicitly (the Inspector's Analyze button); auto-analyze on
/// apply, stale-on-trim detection, and banners are phase 6. A track is keyed for lookup by (media id, Detailed)
/// only — the solver clamps a request outside the analysed range — with per-range re-use handled by the cache key.</para>
/// </remarks>
public sealed class StabilizationService : IMotionTrackProvider, IDisposable
{
    /// <summary>How often at most a bare <em>progress</em> tick raises <see cref="ProgressChanged"/> (4 Hz); state
    /// transitions are exempt and always fire.</summary>
    internal const int ProgressThrottleMs = 250;

    /// <summary>Identity of one tracked analysis: a source at a given analysis detail. A track is looked up by this
    /// on the render thread; a re-analyse over a different range replaces the same entry.</summary>
    private readonly record struct Key(MediaRefId Media, bool Detailed);

    private sealed record Entry(
        AnalysisState State, double Progress, int Generation, AnalysisKey? Key, MotionTrack? Track);

    private readonly record struct WorkItem(
        Key Key, MediaOpenRequest Request, string SourceIdentity, Timecode From, Timecode To,
        StabilizationSettings Settings, AnalysisKey AnalysisKey, int Generation);

    private readonly IMotionAnalyzer _analyzer;

    // Lock-free read for the render thread: the current ready track per key.
    private readonly ConcurrentDictionary<Key, MotionTrack> _tracks = new();

    // Lifecycle state, all mutated under _gate.
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly List<WorkItem> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;

    // The in-flight analysis's cancellation, linked to _cts; guarded by _gate.
    private CancellationTokenSource? _activeBuildCts;
    private Key? _activeKey;

    private long _lastProgressPost;
    private volatile bool _disposed;

    /// <summary>Creates the service and starts its worker. <paramref name="analyzer"/> defaults to the production
    /// <see cref="MediaMotionAnalyzer"/> (FFmpeg gray decode); tests inject a fake.</summary>
    public StabilizationService(IMotionAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new MediaMotionAnalyzer();
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Stabilization analysis",
            Priority = ThreadPriority.BelowNormal, // stay off the hot path — decode/render/audio come first
        };
        _worker.Start();
    }

    /// <summary>
    /// Raised (on the worker or calling thread) when a source's available track changed — a fresh analysis landed,
    /// or a cached one was adopted. The app routes this to a preview repaint and a render-cache invalidation (the
    /// cached segments rendered pass-through while the source was unanalysed are now stale). Subscribers marshal.
    /// </summary>
    public event Action<MediaRefId>? TrackChanged;

    /// <summary>Raised when the aggregate <see cref="StatusOf"/> picture changes — queued, started, progressed,
    /// finished (including failure). Progress ticks are throttled to <see cref="ProgressThrottleMs"/>; state
    /// transitions always fire. Raised on the enqueueing or worker thread — subscribers marshal.</summary>
    public event Action? ProgressChanged;

    /// <inheritdoc />
    /// <remarks>Lock-free and non-blocking. Keyed by (<paramref name="mediaRefId"/>, <paramref name="detailed"/>);
    /// <paramref name="sourceTime"/> is not used to select a track in phase 5 — the solver clamps a request that
    /// falls outside the analysed range.</remarks>
    public MotionTrack? TryGetTrack(MediaRefId mediaRefId, Timecode sourceTime, bool detailed)
    {
        _ = sourceTime;
        return _tracks.GetValueOrDefault(new Key(mediaRefId, detailed));
    }

    /// <summary>The analysis lifecycle of one (<paramref name="media"/>, <paramref name="detailed"/>) pair, for the
    /// Inspector's status row (<see cref="AnalysisState.NotAnalyzed"/> when it was never tracked).</summary>
    public AnalysisStatus StatusOf(MediaRefId media, bool detailed) =>
        _entries.TryGetValue(new Key(media, detailed), out Entry? e)
            ? new AnalysisStatus(e.State, e.Progress)
            : new AnalysisStatus(AnalysisState.NotAnalyzed, 0);

    /// <summary>
    /// Ensures an analysis exists for the clip's used source range [<paramref name="sourceIn"/>,
    /// <paramref name="sourceOut"/>] at the requested detail: adopts an already-cached track (raising
    /// <see cref="TrackChanged"/> at once), or queues a background analysis. Idempotent — a call that matches an
    /// entry already Ready / Queued / Analyzing for the same bucketed range is a no-op; a different range
    /// supersedes any in-flight work (generation-fenced). Call on the UI thread (it holds the <see cref="MediaRef"/>).
    /// </summary>
    public void Analyze(MediaRef media, Timecode sourceIn, Timecode sourceOut, bool detailed)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_disposed)
            return;

        var key = new Key(media.Id, detailed);
        string identity = SourceIdentity.For(media);
        AnalysisKey analysisKey = AnalysisKey.ForClipRange(identity, detailed, sourceIn, sourceOut);
        (Timecode from, Timecode to) = AnalysisKey.BucketRange(sourceIn, sourceOut);

        bool adopted = false, enqueued = false;
        lock (_gate)
        {
            _entries.TryGetValue(key, out Entry? existing);
            if (existing is { } e && e.Key == analysisKey
                && e.State is AnalysisState.Ready or AnalysisState.Queued or AnalysisState.Analyzing)
            {
                return; // already have exactly this analysis (or it's on its way)
            }

            int generation = (existing?.Generation ?? 0) + 1;

            MotionTrack? cached = AnalysisCache.TryRead(analysisKey);
            if (cached is not null && cached.FrameCount > 0)
            {
                _entries[key] = new Entry(AnalysisState.Ready, 1, generation, analysisKey, cached);
                _tracks[key] = cached;
                adopted = true;
            }
            else
            {
                _entries[key] = new Entry(AnalysisState.Queued, 0, generation, analysisKey, null);
                _queue.RemoveAll(w => w.Key.Equals(key)); // a re-analyse supersedes any queued item for this key
                StabilizationSettings settings = StabilizationSettings.Default with { DetailedAnalysis = detailed };
                _queue.Add(new WorkItem(key, MediaOpenRequest.FromMediaRef(media), identity, from, to, settings, analysisKey, generation));
                enqueued = true;
            }
        }

        if (adopted)
            TrackChanged?.Invoke(media.Id);
        if (enqueued)
            ReleaseWorker(1);
        RaiseProgress();
    }

    /// <summary>
    /// Cancels the queued or in-flight analysis for (<paramref name="media"/>, <paramref name="detailed"/>),
    /// reverting to the previously-available track if there was one (else <see cref="AnalysisState.NotAnalyzed"/>).
    /// A no-op when nothing is queued/running for that key.
    /// </summary>
    public void Cancel(MediaRefId media, bool detailed)
    {
        if (_disposed)
            return;

        var key = new Key(media, detailed);
        bool changed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? e)
                && e.State is AnalysisState.Queued or AnalysisState.Analyzing)
            {
                _queue.RemoveAll(w => w.Key.Equals(key));
                MotionTrack? prior = _tracks.GetValueOrDefault(key);
                _entries[key] = e with
                {
                    State = prior is not null ? AnalysisState.Ready : AnalysisState.NotAnalyzed,
                    Progress = prior is not null ? 1 : 0,
                    Generation = e.Generation + 1, // fence off the in-flight completion
                    Track = prior,
                    Key = prior is not null ? e.Key : null,
                };
                CancelActiveLocked(key);
                changed = true;
            }
        }
        if (changed)
            RaiseProgress();
    }

    // ── Worker ─────────────────────────────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { _signal.Wait(ct); }
            catch (OperationCanceledException) { break; }

            if (!TryStartNext(out WorkItem item, out CancellationTokenSource buildCts))
                continue;

            RaiseProgress(); // Analyzing is a state transition — never throttled

            MotionTrack? track = null;
            bool cancelled = false, failed = false;
            try
            {
                track = _analyzer.Analyze(
                    item.Request, item.SourceIdentity, item.From, item.To, item.Settings, BuildProgress(item), buildCts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch
            {
                failed = true; // never let one bad source kill the worker
            }

            if (buildCts.IsCancellationRequested)
                cancelled = true;

            lock (_gate)
            {
                if (ReferenceEquals(_activeBuildCts, buildCts))
                {
                    _activeBuildCts = null;
                    _activeKey = null;
                }
            }
            buildCts.Dispose();

            if (ct.IsCancellationRequested)
                break; // session teardown — leave state as it stands

            Finish(item, track, cancelled, failed);
        }
    }

    private bool TryStartNext(out WorkItem item, out CancellationTokenSource buildCts)
    {
        lock (_gate)
        {
            while (TryDequeueLocked(out item))
            {
                if (!_entries.TryGetValue(item.Key, out Entry? e) || e.Generation != item.Generation)
                    continue; // superseded between queueing and now — drop it
                _entries[item.Key] = e with { State = AnalysisState.Analyzing, Progress = 0 };
                buildCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _activeBuildCts = buildCts;
                _activeKey = item.Key;
                return true;
            }
        }
        buildCts = null!;
        return false;
    }

    private bool TryDequeueLocked(out WorkItem item)
    {
        if (_queue.Count == 0)
        {
            item = default;
            return false;
        }
        item = _queue[0];
        _queue.RemoveAt(0); // FIFO
        return true;
    }

    /// <summary>
    /// Lands (or discards) a finished analysis. The generation comparison is the stale fence: if the entry moved on
    /// while the analysis ran (re-analysed, cancelled), the result is dropped rather than overwriting the new state.
    /// </summary>
    private void Finish(WorkItem item, MotionTrack? track, bool cancelled, bool failed)
    {
        bool trackChanged = false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(item.Key, out Entry? e) || e.Generation != item.Generation)
            {
                // Stale — superseded; say nothing about it.
            }
            else if (cancelled)
            {
                // A still-current cancel already reset the entry (Cancel bumps the generation), so this is unusual;
                // leave the entry as it stands.
            }
            else if (failed || track is null || track.FrameCount == 0)
            {
                _entries[item.Key] = e with { State = AnalysisState.Failed, Progress = 0, Track = null };
            }
            else
            {
                _entries[item.Key] = e with { State = AnalysisState.Ready, Progress = 1, Track = track };
                _tracks[item.Key] = track;
                trackChanged = true;
            }
        }

        if (trackChanged)
        {
            AnalysisCache.Write(item.AnalysisKey, track!); // disk IO outside the lock
            TrackChanged?.Invoke(item.Key.Media);
        }
        RaiseProgress(); // also on failure, so the status never strands a stale "analyzing" readout
    }

    /// <summary>The progress sink for one analysis — deliberately not <see cref="Progress{T}"/> (which would marshal
    /// off the worker), so it stays on the worker thread.</summary>
    private IProgress<double> BuildProgress(WorkItem item) => new WorkerProgress(fraction =>
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(item.Key, out Entry? e)
                || e.Generation != item.Generation || e.State != AnalysisState.Analyzing)
            {
                return;
            }
            _entries[item.Key] = e with { Progress = Math.Clamp(fraction, 0, 1) };
        }
        RaiseProgress(throttled: true);
    });

    private sealed class WorkerProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private void CancelActiveLocked(Key key)
    {
        if (_activeBuildCts is null || _activeKey != key)
            return;
        try { _activeBuildCts.Cancel(); }
        catch (ObjectDisposedException) { /* the analysis already unwound */ }
    }

    private void ReleaseWorker(int count)
    {
        if (count <= 0 || _disposed)
            return;
        try { _signal.Release(count); }
        catch (ObjectDisposedException) { /* disposed underneath us */ }
    }

    /// <summary>Raises <see cref="ProgressChanged"/>. State transitions pass <c>throttled: false</c> and always
    /// fire; bare progress ticks are rate-limited to <see cref="ProgressThrottleMs"/>.</summary>
    private void RaiseProgress(bool throttled = false)
    {
        long now = Environment.TickCount64;
        if (throttled && !ShouldPostProgress(now, Interlocked.Read(ref _lastProgressPost)))
            return;
        Interlocked.Exchange(ref _lastProgressPost, now);
        ProgressChanged?.Invoke();
    }

    /// <summary>Whether a bare progress tick may raise <see cref="ProgressChanged"/> yet — pure, so testable.</summary>
    internal static bool ShouldPostProgress(long nowMs, long lastPostMs) => nowMs - lastPostMs >= ProgressThrottleMs;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        try { _worker.Join(TimeSpan.FromSeconds(5)); }
        catch { /* worker is cancelled / faulted; best-effort */ }

        _cts.Dispose();
        _signal.Dispose();
    }
}

/// <summary>Builds the stable source identity (path + size + mtime) the analysis cache keys by — the same shape
/// the render-cache hasher uses (ARCHITECTURE.md §20), so a replaced or edited source re-analyses.</summary>
internal static class SourceIdentity
{
    public static string For(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        string path = media.AbsolutePath ?? string.Empty;
        long length = 0, mtime = 0;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                length = fi.Length;
                mtime = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Offline / unreadable — the (empty-ish) identity still keys deterministically.
        }
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"{normalized}|{length}|{mtime}");
    }
}
