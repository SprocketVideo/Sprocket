using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sprocket.Core.Model;

namespace Sprocket.App.Proxy;

/// <summary>One tracked source's proxy state, as handed to the UI by <see cref="ProxyService.Snapshot"/>.</summary>
/// <param name="Id">The media pool id the row describes.</param>
/// <param name="State">Where the source sits in the proxy lifecycle.</param>
/// <param name="Target">The proxy's target resolution at the current tier ((0,0) when no proxy is wanted).</param>
/// <param name="ProxyPath">The cache file the proxy lives (or will live) at, or <see langword="null"/>.</param>
/// <param name="Progress">Build completion 0..1 — meaningful while <see cref="ProxyState.Building"/>.</param>
/// <param name="SizeBytes">The proxy file's size, captured by the service when the entry became
/// <see cref="ProxyState.Ready"/>. Cached deliberately: a per-refresh <c>FileInfo</c> read on the UI thread stalls
/// the dialog when the cache sits on a network share.</param>
public readonly record struct ProxySnapshot(
    MediaRefId Id, ProxyState State, Resolution Target, string? ProxyPath, double Progress, long SizeBytes);

/// <summary>
/// Generates and tracks lower-resolution preview proxies in the background (PLAN.md step 18). <b>Default-on and
/// transparent:</b> <em>on</em> means "preview against a proxy once one is ready, else the original" — a freshly
/// imported clip previews on its original immediately and switches to its proxy the moment it finishes building
/// (signalled via <see cref="ProxyPathChanged"/>, which the app routes to <c>PlaybackEngine.InvalidateSource</c>).
/// Export ignores proxies entirely and re-renders full-resolution originals (ARCHITECTURE.md §17), so output
/// determinism is unaffected.
/// </summary>
/// <remarks>
/// <para>A single bounded background worker encodes off the hot path (leaving cores for decode/render/audio). Work
/// is drawn from a priority queue: sources used on the timeline build before bin-only sources. Proxies persist in a
/// local, regenerable cache dir keyed by source identity + target size (<see cref="ProxyCache"/>), so they survive
/// restarts and a cached proxy is reused without re-encoding. Sources already light enough to preview in real time
/// are never queued (<see cref="ProxyPolicy.NeedsProxy"/>). Disposal cancels any in-flight encode.</para>
/// <para><b>The whole feature is live-controllable</b> (the View ▸ Proxy dialog, modelled on Final Cut Pro's
/// Background Tasks window): on/off, pause/resume, and the resolution tier all take effect immediately rather than
/// only at session construction. Two consequences shape the design:</para>
/// <list type="bullet">
/// <item><description><b>The worker always runs.</b> A disabled or paused service parks on the semaphore; enabling
/// or resuming just signals it. (It used to not exist at all when constructed disabled, which is why nothing could
/// be turned on mid-session.)</description></item>
/// <item><description><b>Inventory is separate from scheduling.</b> <see cref="Enqueue"/> records an entry for
/// every source regardless of whether proxies are on — so the dialog can show what <em>would</em> be built while
/// the feature is off — and only the scheduling half is gated on <see cref="Enabled"/>/<see cref="Paused"/>.</description></item>
/// </list>
/// <para><b>Stale completions are fenced off per entry.</b> Every entry carries a generation counter that any
/// invalidating action (delete, tier change, disable, requeue) bumps for the <em>affected entries only</em>; each
/// queued <see cref="WorkItem"/> remembers the generation it was queued at, and a finished build whose generation
/// has moved on is discarded instead of overwriting the new state. Per entry rather than a single global counter so
/// deleting one asset's proxy never throws away another asset's in-flight build.</para>
/// <para>Concurrency: <see cref="_entries"/> is a <see cref="ConcurrentDictionary{TKey,TValue}"/>, and <b>every</b>
/// state/queue/generation mutation happens under <see cref="_queueGate"/> so a transition and its queue effect are
/// atomic together. Events may fire on the worker thread — subscribers marshal to their own.</para>
/// </remarks>
public sealed class ProxyService : IDisposable
{
    /// <summary>How often at most a mere <em>progress</em> tick raises <see cref="ProgressChanged"/> (4 Hz).
    /// ffmpeg reports far more often than that, and each event posts to the UI thread; state transitions are
    /// exempt and always fire.</summary>
    internal const int ProgressThrottleMs = 250;

    /// <param name="Media">The source; kept so a tier change / re-enable can reschedule without a
    /// <see cref="Project"/> in hand.</param>
    /// <param name="Priority">Timeline sources (0) build before bin-only ones (1).</param>
    private sealed record Entry(
        MediaRef Media, ProxyState State, string? ProxyPath, Resolution Target, int Generation, int Priority,
        double Progress = 0, long SizeBytes = 0);

    private readonly record struct WorkItem(
        MediaRefId Id, MediaRef Media, Resolution Target, string Path, int Priority, int Generation);

    private readonly IProxyTranscoder _transcoder;

    // Runtime state, all mutated under _queueGate and read (unlocked) from BestPath / StatusSummary on the UI thread.
    private volatile bool _enabled;
    private volatile bool _paused;
    private volatile ProxyTier _tier;

    private readonly ConcurrentDictionary<MediaRefId, Entry> _entries = new();
    private readonly List<MediaRefId> _order = [];   // inventory order, so the dialog's rows are stable
    private readonly object _queueGate = new();
    private readonly List<WorkItem> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    // The in-flight build's cancellation, linked to _cts: an individual encode can be cancelled (pause, delete,
    // tier change, disable) without tearing the service down. Guarded by _queueGate.
    private CancellationTokenSource? _activeBuildCts;
    private MediaRefId? _activeId;

    private long _lastProgressPost;
    private volatile bool _disposed;

    /// <summary>
    /// Raised (on the worker or calling thread) when the answer <see cref="BestPath"/> gives for a source may have
    /// changed — in <b>either</b> direction: its proxy became available (freshly built or found already cached),
    /// <em>or</em> its proxy stopped applying (proxies disabled, tier changed, proxy deleted). The app routes this
    /// to <c>PlaybackEngine.InvalidateSource</c>, which re-opens the feed through the factory and so picks up the
    /// new best path without any further engine API. Subscribers must marshal to their own thread.
    /// </summary>
    public event Action<MediaRefId>? ProxyPathChanged;

    /// <summary>
    /// Raised whenever the aggregate picture <see cref="StatusSummary"/> / <see cref="Snapshot"/> reports changes —
    /// work queued, a build started, progressed, or finished (including a failure), or the feature was
    /// enabled/paused/re-tiered. <see cref="ProxyPathChanged"/> alone is not enough to drive a progress readout: it
    /// fires only when a path changes, so a build that is merely <em>starting</em> would go unannounced and a run
    /// whose last source failed would leave a stale "pending" message on screen. Progress ticks are throttled to
    /// <see cref="ProgressThrottleMs"/>; state transitions always fire. Raised on the enqueueing or worker thread —
    /// subscribers must marshal to their own.
    /// </summary>
    public event Action? ProgressChanged;

    /// <summary>
    /// Creates the service with its initial runtime state (the project's <c>UseProxies</c> / <c>ProxyTier</c>) and
    /// the transcoder builds go through — the production <see cref="FfmpegProxyTranscoder"/> unless a test
    /// substitutes a fake. The worker starts either way: a disabled service parks until something enables it.
    /// </summary>
    public ProxyService(bool enabled, ProxyTier tier, IProxyTranscoder? transcoder = null)
    {
        _enabled = enabled;
        _tier = tier;
        _transcoder = transcoder ?? new FfmpegProxyTranscoder();
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>Whether proxies currently apply (the project's <c>UseProxies</c> setting, live-toggled through
    /// <see cref="SetEnabled"/>).</summary>
    public bool Enabled => _enabled;

    /// <summary>Whether generation is suspended. Orthogonal to <see cref="Enabled"/>: a paused service still
    /// previews against the proxies it already has.</summary>
    public bool Paused => _paused;

    /// <summary>The resolution tier proxies are built at, live-changed through <see cref="SetTier"/>.</summary>
    public ProxyTier Tier => _tier;

    /// <summary>
    /// The path the preview should open for <paramref name="media"/>: the ready proxy when one exists on disk,
    /// otherwise the original (so playback is never blocked on a build). Always the original when proxies are off.
    /// </summary>
    public string BestPath(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_enabled
            && _entries.TryGetValue(media.Id, out Entry? entry)
            && entry.State == ProxyState.Ready
            && entry.ProxyPath is { } path
            && File.Exists(path))
        {
            return path;
        }
        return media.AbsolutePath;
    }

    /// <summary>The current proxy state of a source (<see cref="ProxyState.NotNeeded"/> if it was never inventoried).</summary>
    public ProxyState StateOf(MediaRefId id) =>
        _entries.TryGetValue(id, out Entry? entry) ? entry.State : ProxyState.NotNeeded;

    /// <summary>
    /// Every inventoried source's proxy state, in inventory order, for the Proxy dialog. Pure in-memory — the
    /// on-disk size was captured when the entry became <see cref="ProxyState.Ready"/>, so refreshing the dialog
    /// never touches the file system. The caller joins against the media pool for display names.
    /// </summary>
    public IReadOnlyList<ProxySnapshot> Snapshot()
    {
        lock (_queueGate)
        {
            var rows = new List<ProxySnapshot>(_order.Count);
            foreach (MediaRefId id in _order)
            {
                if (_entries.TryGetValue(id, out Entry? e))
                    rows.Add(new ProxySnapshot(id, e.State, e.Target, e.ProxyPath, e.Progress, e.SizeBytes));
            }
            return rows;
        }
    }

    /// <summary>A one-line summary of proxy progress for the status bar, or <see langword="null"/> when there is
    /// nothing to report (proxies off, or no source ever needed one).</summary>
    public string? StatusSummary()
    {
        if (!_enabled)
            return null;
        int building = 0, queued = 0, ready = 0, failed = 0;
        foreach (Entry e in _entries.Values)
        {
            switch (e.State)
            {
                case ProxyState.Building: building++; break;
                case ProxyState.Queued: queued++; break;
                case ProxyState.Ready: ready++; break;
                case ProxyState.Failed: failed++; break;
            }
        }
        return FormatSummary(ready, building + queued, failed);
    }

    /// <summary>
    /// The pure status-bar wording for a proxy tally. Returns <see langword="null"/> only when there is genuinely
    /// nothing to say — no proxy was ever wanted. Failures are always surfaced: a source that stays on its original
    /// because <c>ffmpeg</c> isn't on PATH (§15) previews slower than the user expects, and silence would leave
    /// that unexplained.
    /// </summary>
    internal static string? FormatSummary(int ready, int pending, int failed)
    {
        string failedSuffix = failed > 0 ? $", {failed} failed" : "";
        if (pending > 0)
            return $"building proxies… {ready} ready, {pending} pending{failedSuffix}";
        if (ready > 0)
            return $"proxies ready ({ready}){failedSuffix}";
        if (failed > 0)
            return $"proxy generation failed ({failed}) — previewing originals";
        return null;
    }

    /// <summary>
    /// Whether a bare progress tick may raise <see cref="ProgressChanged"/> yet, given the monotonic clock now and
    /// at the last post. Pure so the throttle is testable without a build; state transitions bypass it.
    /// </summary>
    internal static bool ShouldPostProgress(long nowMs, long lastPostMs) => nowMs - lastPostMs >= ProgressThrottleMs;

    // ── Inventory + scheduling ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="project"/>'s media pool and records every source that isn't tracked yet, then queues a
    /// build for those that need one — prioritising sources used on the timeline. Idempotent and additive, so it is
    /// safe on import, project load, and after edits; an already-tracked source is left as-is (a re-import with the
    /// same id keeps its state).
    /// </summary>
    /// <remarks>
    /// <b>Inventory runs even when proxies are off</b> — only the queueing half is gated. That is what lets the
    /// Proxy dialog list which sources <em>would</em> be proxied while the feature is disabled, and it is why both
    /// call sites (the composition root at session build, and the post-import call in <c>MainWindow</c>) get the
    /// behaviour without changing: the disabled early-return moved inside.
    /// </remarks>
    public void Enqueue(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_disposed)
            return;

        // Sources referenced by any clip on the timeline build first (priority 0); bin-only sources after (1).
        var onTimeline = new HashSet<MediaRefId>(
            project.Timeline.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaRefId));

        var becameReady = new List<MediaRefId>();
        bool changed = false;
        int queued = 0;
        lock (_queueGate)
        {
            foreach (MediaRef media in project.MediaPool.Items)
            {
                if (_entries.ContainsKey(media.Id))
                    continue;
                changed = true;
                _order.Add(media.Id);

                // Proxies apply to ordinary files only; the first cut skips image sequences and stills (PLAN.md
                // step 42) — a still is one held frame, and a sequence's still frames are already cheap to decode.
                bool wants = media.Kind == MediaKind.File && ProxyPolicy.NeedsProxy(media.Info, _tier);
                _entries[media.Id] = new Entry(
                    media,
                    wants ? ProxyState.NotGenerated : ProxyState.NotNeeded,
                    ProxyPath: null,
                    Target: wants ? ProxyPolicy.TargetResolution(media.Info.Width, media.Info.Height, _tier) : default,
                    Generation: 0,
                    Priority: onTimeline.Contains(media.Id) ? 0 : 1);

                if (wants && ResolveOrQueueLocked(media.Id, becameReady, schedule: _enabled && !_paused))
                    queued++;
            }
        }

        ReleaseWorker(queued);
        foreach (MediaRefId id in becameReady)
            ProxyPathChanged?.Invoke(id);
        // Announce the new backlog before any of it finishes — a first 4K build can take minutes, and until this
        // fires the user sees nothing at all.
        if (changed)
            RaiseProgress();
    }

    /// <summary>
    /// Settles one <see cref="ProxyState.NotGenerated"/> entry: adopts an already-cached proxy file (→
    /// <see cref="ProxyState.Ready"/>, id appended to <paramref name="becameReady"/>), drops it to
    /// <see cref="ProxyState.NotNeeded"/> when the source can't be keyed at all, or — when
    /// <paramref name="schedule"/> — queues a build (→ <see cref="ProxyState.Queued"/>, returning
    /// <see langword="true"/> so the caller owes one semaphore release). Caller holds <see cref="_queueGate"/>.
    /// </summary>
    /// <remarks>Adoption of a cached file happens regardless of <paramref name="schedule"/>: a proxy that is
    /// already on disk is <em>ready</em> whether or not the feature is currently on, and reporting it as such is
    /// what lets re-enabling switch the preview straight over with no rebuild.</remarks>
    private bool ResolveOrQueueLocked(MediaRefId id, List<MediaRefId> becameReady, bool schedule)
    {
        if (!_entries.TryGetValue(id, out Entry? e) || e.State != ProxyState.NotGenerated)
            return false;

        Resolution target = ProxyPolicy.TargetResolution(e.Media.Info.Width, e.Media.Info.Height, _tier);
        string? path = TryResolveCachePath(e.Media, target);
        if (path is null)
        {
            // Source offline / unreadable identity → can't key a cache file; leave it on the original.
            _entries[id] = e with { State = ProxyState.NotNeeded, ProxyPath = null, Target = target };
            return false;
        }

        if (File.Exists(path))
        {
            // A prior session (or a prior stint at this tier) already built it: reuse without re-encoding.
            _entries[id] = e with
            {
                State = ProxyState.Ready, ProxyPath = path, Target = target, Progress = 1, SizeBytes = FileLength(path),
            };
            becameReady.Add(id);
            return false;
        }

        if (!schedule)
        {
            _entries[id] = e with { ProxyPath = null, Target = target };
            return false;
        }

        int generation = e.Generation + 1;
        _entries[id] = e with
        {
            State = ProxyState.Queued, ProxyPath = path, Target = target, Generation = generation,
            Progress = 0, SizeBytes = 0,
        };
        _queue.Add(new WorkItem(id, e.Media, target, path, e.Priority, generation));
        return true;
    }

    /// <summary>Queues every <see cref="ProxyState.NotGenerated"/> entry (when enabled), returning the ids that
    /// turned out to be cached already. Caller must not hold <see cref="_queueGate"/>.</summary>
    private List<MediaRefId> ScheduleAllNotGenerated()
    {
        var becameReady = new List<MediaRefId>();
        int queued = 0;
        lock (_queueGate)
        {
            bool schedule = _enabled && !_paused;
            foreach (MediaRefId id in _order.ToArray())
            {
                if (_entries.TryGetValue(id, out Entry? e) && e.State == ProxyState.NotGenerated
                    && ResolveOrQueueLocked(id, becameReady, schedule))
                {
                    queued++;
                }
            }
        }
        ReleaseWorker(queued);
        return becameReady;
    }

    private static string? TryResolveCachePath(MediaRef media, Resolution target)
    {
        try
        {
            var fi = new FileInfo(media.AbsolutePath);
            if (!fi.Exists)
                return null;
            string name = ProxyCache.KeyFileName(media.AbsolutePath, fi.Length, fi.LastWriteTimeUtc.Ticks, target.Width, target.Height);
            return Path.Combine(ProxyCache.Directory(), name);
        }
        catch
        {
            return null;
        }
    }

    // ── Live transitions (the Proxy dialog's controls) ──────────────────────────────────────────────

    /// <summary>
    /// Turns proxies on or off, effective immediately. <b>On:</b> every source that wants a proxy is queued (or
    /// adopted from cache) and previously-ready ones apply to the preview again. <b>Off:</b> queued/building work is
    /// dropped and the active encode cancelled, the preview reverts to originals, but <em>proxy files are kept</em>
    /// — <see cref="BestPath"/> already returns the original while off, so deleting them would only cost a rebuild.
    /// </summary>
    /// <remarks>
    /// Re-enabling <b>rebuilds proxies that were explicitly deleted</b>, because deletion is not persisted: any
    /// project reload would rebuild them anyway, so pretending otherwise would only make the two paths disagree.
    /// The dialog's Delete tooltip says so.
    /// </remarks>
    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled)
            return;

        if (enabled)
        {
            var readyAgain = new List<MediaRefId>();
            lock (_queueGate)
            {
                _enabled = true;
                foreach (Entry e in _entries.Values)
                    if (e.State == ProxyState.Ready)
                        readyAgain.Add(e.Media.Id);
            }
            // Cached-on-disk adoptions come back from the scheduling pass; already-Ready entries need the same
            // signal so the preview switches back onto them.
            readyAgain.AddRange(ScheduleAllNotGenerated());
            foreach (MediaRefId id in readyAgain)
                ProxyPathChanged?.Invoke(id);
            RaiseProgress();
            return;
        }

        var reverted = new List<MediaRefId>();
        lock (_queueGate)
        {
            _enabled = false;
            foreach (MediaRefId id in _order.ToArray())
            {
                if (!_entries.TryGetValue(id, out Entry? e))
                    continue;
                // Ready files stay on disk; Building/Queued work is abandoned back to NotGenerated.
                if (e.State is ProxyState.Ready or ProxyState.Building)
                    reverted.Add(id);
                if (e.State is ProxyState.Queued or ProxyState.Building)
                    _entries[id] = e with { State = ProxyState.NotGenerated, Generation = e.Generation + 1, Progress = 0 };
            }
            _queue.Clear();
            CancelActiveLocked();
        }
        foreach (MediaRefId id in reverted)
            ProxyPathChanged?.Invoke(id);
        RaiseProgress();
    }

    /// <summary>
    /// Suspends or resumes generation, effective immediately: pausing <b>cancels the encode in flight</b> and
    /// requeues it, so the machine goes quiet at once rather than after the current (possibly multi-minute) source.
    /// The partial output is discarded — <see cref="ProxyTranscoder"/> only promotes on a clean exit — so a resumed
    /// build restarts from zero. Suspending the child process instead (SIGSTOP / cross-platform equivalents) is
    /// deliberately not attempted. Proxies already built keep applying to the preview while paused.
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (_disposed || _paused == paused)
            return;

        int release = 0;
        lock (_queueGate)
        {
            _paused = paused;
            if (paused)
            {
                CancelActiveLocked();
            }
            else
            {
                // Resume: one permit per item now waiting. While paused the worker swallowed permits without
                // consuming queue items (see WorkerLoopAsync), and a pause-cancelled build is requeued without a
                // permit of its own — this single accounting point restores exactly one wakeup per queued item.
                release = _queue.Count;
            }
        }
        ReleaseWorker(release);
        RaiseProgress();
    }

    /// <summary>
    /// Changes the resolution tier, effective immediately. The tier is part of the cache key, so every wanted proxy
    /// is invalidated and rebuilt against the new target (files at the old tier stay in the cache and are re-adopted
    /// if the user switches back). The active encode is cancelled; the preview reverts to originals until the new
    /// proxies land.
    /// </summary>
    public void SetTier(ProxyTier tier)
    {
        if (_disposed || _tier == tier)
            return;

        var reverted = new List<MediaRefId>();
        lock (_queueGate)
        {
            _tier = tier;
            foreach (MediaRefId id in _order.ToArray())
            {
                if (!_entries.TryGetValue(id, out Entry? e))
                    continue;
                if (e.State == ProxyState.Ready)
                    reverted.Add(id);

                // Re-evaluate from scratch: a different tier can change both the target and whether a proxy is
                // worth building at all.
                bool wants = e.Media.Kind == MediaKind.File && ProxyPolicy.NeedsProxy(e.Media.Info, tier);
                _entries[id] = e with
                {
                    State = wants ? ProxyState.NotGenerated : ProxyState.NotNeeded,
                    ProxyPath = null,
                    Target = wants ? ProxyPolicy.TargetResolution(e.Media.Info.Width, e.Media.Info.Height, tier) : default,
                    Generation = e.Generation + 1,
                    Progress = 0,
                    SizeBytes = 0,
                };
            }
            _queue.Clear();
            CancelActiveLocked();
        }

        reverted.AddRange(ScheduleAllNotGenerated());
        foreach (MediaRefId id in reverted)
            ProxyPathChanged?.Invoke(id);
        RaiseProgress();
    }

    /// <summary>
    /// Explicitly schedules one source's proxy — the way back to building after a
    /// <see cref="DeleteProxy"/> or a failure. A no-op while proxies are off (nothing would use the result); while
    /// paused it queues and waits for resume.
    /// </summary>
    public void Generate(MediaRefId id)
    {
        if (_disposed || !_enabled)
            return;

        var becameReady = new List<MediaRefId>();
        int queued = 0;
        lock (_queueGate)
        {
            if (_entries.TryGetValue(id, out Entry? e) && e.State is ProxyState.NotGenerated or ProxyState.Failed)
            {
                if (e.State == ProxyState.Failed)
                    _entries[id] = e with { State = ProxyState.NotGenerated, Generation = e.Generation + 1 };
                if (ResolveOrQueueLocked(id, becameReady, schedule: !_paused))
                    queued++;
            }
        }
        ReleaseWorker(queued);
        foreach (MediaRefId readyId in becameReady)
            ProxyPathChanged?.Invoke(readyId);
        RaiseProgress();
    }

    /// <summary>
    /// Schedules every proxy that is missing or failed. Sources that are already <see cref="ProxyState.Ready"/> are
    /// left alone — use <see cref="DeleteAllProxies"/> first to force a re-encode of those. A no-op while proxies
    /// are off.
    /// </summary>
    public void RebuildAll()
    {
        if (_disposed || !_enabled)
            return;

        lock (_queueGate)
        {
            foreach (MediaRefId id in _order.ToArray())
                if (_entries.TryGetValue(id, out Entry? e) && e.State == ProxyState.Failed)
                    _entries[id] = e with { State = ProxyState.NotGenerated, Generation = e.Generation + 1 };
        }

        foreach (MediaRefId id in ScheduleAllNotGenerated())
            ProxyPathChanged?.Invoke(id);
        RaiseProgress();
    }

    // ── Deletion ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes one source's proxy file and leaves the entry <see cref="ProxyState.NotGenerated"/> — it is
    /// <b>not</b> auto-rebuilt (that would make the button pointless); <see cref="Generate"/>, a re-enable, or the
    /// next project load builds it again. Cancels the build if that source is the one in flight; other sources'
    /// builds are untouched. Returns whether the source was tracked as wanting a proxy at all.
    /// </summary>
    public bool DeleteProxy(MediaRefId id)
    {
        if (_disposed)
            return false;

        string? path;
        bool wasReady;
        lock (_queueGate)
        {
            if (!_entries.TryGetValue(id, out Entry? e) || e.State == ProxyState.NotNeeded)
                return false;
            path = e.ProxyPath;
            wasReady = e.State == ProxyState.Ready;
            _entries[id] = e with
            {
                State = ProxyState.NotGenerated, ProxyPath = null, Generation = e.Generation + 1,
                Progress = 0, SizeBytes = 0,
            };
            _queue.RemoveAll(w => w.Id == id);
            CancelActiveLocked(id);
        }

        if (path is not null)
            TryDeleteFile(path);
        if (wasReady)
            ProxyPathChanged?.Invoke(id); // the preview must come off a file that no longer exists
        RaiseProgress();
        return true;
    }

    /// <summary>
    /// Empties the proxy cache and resets every entry to <see cref="ProxyState.NotGenerated"/> (no auto-rebuild —
    /// see <see cref="DeleteProxy"/>). Returns the number of files deleted. <b>This is the single deletion path:</b>
    /// the Preferences "Clear proxy cache" action routes through it rather than calling
    /// <see cref="ProxyCache.DeleteAll"/> directly, so no surface is left reporting a <see cref="ProxyState.Ready"/>
    /// proxy whose file is gone.
    /// </summary>
    public int DeleteAllProxies()
    {
        if (_disposed)
            return ProxyCache.DeleteAll();

        var reverted = new List<MediaRefId>();
        lock (_queueGate)
        {
            foreach (MediaRefId id in _order.ToArray())
            {
                if (!_entries.TryGetValue(id, out Entry? e) || e.State == ProxyState.NotNeeded)
                    continue;
                if (e.State == ProxyState.Ready)
                    reverted.Add(id);
                _entries[id] = e with
                {
                    State = ProxyState.NotGenerated, ProxyPath = null, Generation = e.Generation + 1,
                    Progress = 0, SizeBytes = 0,
                };
            }
            _queue.Clear();
            CancelActiveLocked();
        }

        int deleted = ProxyCache.DeleteAll();
        foreach (MediaRefId id in reverted)
            ProxyPathChanged?.Invoke(id);
        RaiseProgress();
        return deleted;
    }

    // ── Worker ─────────────────────────────────────────────────────────────────────────────────────

    private async Task WorkerLoopAsync()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Paused / disabled: park without consuming a queue item. The swallowed permit is replaced wholesale
            // when SetPaused(false) releases one per queued item, so nothing is stranded.
            if (_paused || !_enabled)
                continue;

            if (!TryStartNextBuild(out WorkItem item, out CancellationTokenSource buildCts))
                continue;

            RaiseProgress(); // Building is a state transition — never throttled
            bool ok;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                ok = _transcoder.Generate(item.Media, item.Target, item.Path, BuildProgress(item), buildCts.Token);
            }
            catch
            {
                ok = false; // never let one bad source kill the worker
            }

            bool cancelled = buildCts.IsCancellationRequested;
            lock (_queueGate)
            {
                if (ReferenceEquals(_activeBuildCts, buildCts))
                {
                    _activeBuildCts = null;
                    _activeId = null;
                }
            }
            buildCts.Dispose();

            if (ct.IsCancellationRequested)
                break; // session teardown, not an invalidation — leave the state as it stands

            FinishBuild(item, ok, cancelled);
        }
    }

    /// <summary>Dequeues the next item and marks it <see cref="ProxyState.Building"/>, skipping items whose entry
    /// moved on while they sat in the queue. Returns false when there was nothing (still) to do.</summary>
    private bool TryStartNextBuild(out WorkItem item, out CancellationTokenSource buildCts)
    {
        lock (_queueGate)
        {
            while (TryDequeueLocked(out item))
            {
                if (!_entries.TryGetValue(item.Id, out Entry? e) || e.Generation != item.Generation)
                    continue; // invalidated between queueing and now — drop it
                _entries[item.Id] = e with { State = ProxyState.Building, Progress = 0 };
                buildCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _activeBuildCts = buildCts;
                _activeId = item.Id;
                return true;
            }
        }
        buildCts = null!;
        return false;
    }

    /// <summary>
    /// Lands (or discards) a finished build. The generation comparison is the stale-completion fence: if the
    /// entry's generation moved while the encode ran — deleted, re-tiered, disabled, requeued — the result is
    /// dropped rather than overwriting the new state with a spurious Ready/Failed. A cancellation of a
    /// <em>still-current</em> item is a pause, so it goes back on the queue.
    /// </summary>
    private void FinishBuild(WorkItem item, bool ok, bool cancelled)
    {
        bool pathChanged = false, release = false;
        lock (_queueGate)
        {
            if (!_entries.TryGetValue(item.Id, out Entry? e) || e.Generation != item.Generation)
            {
                // Stale — this build no longer describes the entry. Say nothing about it.
            }
            else if (cancelled)
            {
                _entries[item.Id] = e with { State = ProxyState.Queued, Progress = 0 };
                _queue.Add(item);
                // Normally the pending resume's Release(_queue.Count) covers this. If the resume already happened
                // (it races the cancelled encode's unwind), give the item its own wakeup instead.
                release = _enabled && !_paused;
            }
            else if (ok)
            {
                _entries[item.Id] = e with
                {
                    State = ProxyState.Ready, ProxyPath = item.Path, Progress = 1, SizeBytes = FileLength(item.Path),
                };
                pathChanged = true;
            }
            else
            {
                _entries[item.Id] = e with { State = ProxyState.Failed, ProxyPath = null, Progress = 0, SizeBytes = 0 };
            }
        }

        if (release)
            ReleaseWorker(1);
        if (pathChanged)
            ProxyPathChanged?.Invoke(item.Id);
        RaiseProgress(); // also on failure, so the tally never strands a stale "pending" readout
    }

    /// <summary>The progress sink for one build. Deliberately not <see cref="Progress{T}"/> — that marshals to the
    /// captured synchronization context, and this must stay on the worker.</summary>
    private IProgress<double> BuildProgress(WorkItem item) => new WorkerProgress(fraction =>
    {
        lock (_queueGate)
        {
            if (!_entries.TryGetValue(item.Id, out Entry? e)
                || e.Generation != item.Generation || e.State != ProxyState.Building)
            {
                return;
            }
            _entries[item.Id] = e with { Progress = Math.Clamp(fraction, 0, 1) };
        }
        RaiseProgress(throttled: true);
    });

    private sealed class WorkerProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Removes and returns the highest-priority queued item (lowest priority number, then FIFO).</summary>
    private bool TryDequeueLocked(out WorkItem item)
    {
        if (_queue.Count == 0)
        {
            item = default;
            return false;
        }
        int best = 0;
        for (int i = 1; i < _queue.Count; i++)
        {
            if (_queue[i].Priority < _queue[best].Priority)
                best = i;
        }
        item = _queue[best];
        _queue.RemoveAt(best);
        return true;
    }

    /// <summary>Cancels the in-flight build, optionally only when it is <paramref name="id"/>'s. Caller holds
    /// <see cref="_queueGate"/>; the worker never holds the gate while encoding, so this cannot deadlock.</summary>
    private void CancelActiveLocked(MediaRefId? id = null)
    {
        if (_activeBuildCts is null || (id is { } only && _activeId != only))
            return;
        try { _activeBuildCts.Cancel(); }
        catch (ObjectDisposedException) { /* the build already unwound */ }
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

    private static long FileLength(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (e.g. the preview still has it open) or locked — leave it; the cache ages out naturally.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* worker is cancelled / faulted; best-effort */ }

        _cts.Dispose();
        _signal.Dispose();
    }
}
