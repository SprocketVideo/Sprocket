using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sprocket.Core.Model;

namespace Sprocket.App.Proxy;

/// <summary>
/// Generates and tracks lower-resolution preview proxies in the background (PLAN.md step 18). <b>Default-on and
/// transparent:</b> <em>on</em> means "preview against a proxy once one is ready, else the original" — a freshly
/// imported clip previews on its original immediately and switches to its proxy the moment it finishes building
/// (signalled via <see cref="ProxyReady"/>, which the app routes to <c>PlaybackEngine.InvalidateSource</c>).
/// Export ignores proxies entirely and re-renders full-resolution originals (ARCHITECTURE.md §17), so output
/// determinism is unaffected.
/// </summary>
/// <remarks>
/// A single bounded background worker encodes off the hot path (leaving cores for decode/render/audio). Work is
/// drawn from a priority queue: sources used on the timeline build before bin-only sources. Proxies persist in a
/// local, regenerable cache dir keyed by source identity + target size (<see cref="ProxyCache"/>), so they
/// survive restarts and a cached proxy is reused without re-encoding. Sources already light enough to preview in
/// real time are never queued (<see cref="ProxyPolicy.NeedsProxy"/>). Disposal cancels any in-flight encode.
/// </remarks>
public sealed class ProxyService : IDisposable
{
    private sealed record Entry(ProxyState State, string? ProxyPath, Resolution Target);

    private readonly record struct WorkItem(MediaRefId Id, MediaRef Media, Resolution Target, string Path, int Priority);

    private readonly bool _enabled;
    private readonly ProxyTier _tier;

    private readonly ConcurrentDictionary<MediaRefId, Entry> _entries = new();
    private readonly object _queueGate = new();
    private readonly List<WorkItem> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private bool _disposed;

    /// <summary>Raised (on the worker thread) when a source's proxy becomes available — either freshly built or
    /// found already cached. Subscribers must marshal to their own thread.</summary>
    public event Action<MediaRefId>? ProxyReady;

    public ProxyService(bool enabled, ProxyTier tier)
    {
        _enabled = enabled;
        _tier = tier;
        _worker = enabled ? Task.Run(WorkerLoopAsync) : Task.CompletedTask;
    }

    /// <summary>Whether proxies are enabled for this session (the project's <c>UseProxies</c> setting).</summary>
    public bool Enabled => _enabled;

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

    /// <summary>The current proxy state of a source (<see cref="ProxyState.NotNeeded"/> if it was never queued).</summary>
    public ProxyState StateOf(MediaRefId id) =>
        _entries.TryGetValue(id, out Entry? entry) ? entry.State : ProxyState.NotNeeded;

    /// <summary>A one-line summary of proxy progress for the status bar, or <see langword="null"/> when there is
    /// nothing to report (proxies off, or every source resolved).</summary>
    public string? StatusSummary()
    {
        if (!_enabled)
            return null;
        int building = 0, queued = 0, ready = 0;
        foreach (Entry e in _entries.Values)
        {
            switch (e.State)
            {
                case ProxyState.Building: building++; break;
                case ProxyState.Queued: queued++; break;
                case ProxyState.Ready: ready++; break;
            }
        }
        int pending = building + queued;
        if (pending == 0)
            return ready > 0 ? $"proxies ready ({ready})" : null;
        return $"building proxies… {ready} ready, {pending} pending";
    }

    /// <summary>
    /// Scans <paramref name="project"/>'s media pool and queues a proxy for every video source that needs one and
    /// isn't already tracked, prioritising sources used on the timeline. Idempotent and additive — safe to call on
    /// import, project load, and after edits; an already-tracked source is left as-is (a re-import with the same id
    /// keeps its state). A no-op when proxies are disabled.
    /// </summary>
    public void Enqueue(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!_enabled || _disposed)
            return;

        // Sources referenced by any clip on the timeline build first (priority 0); bin-only sources after (1).
        var onTimeline = new HashSet<MediaRefId>(
            project.Timeline.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaRefId));

        foreach (MediaRef media in project.MediaPool.Items)
        {
            if (_entries.ContainsKey(media.Id))
                continue;
            if (!ProxyPolicy.NeedsProxy(media.Info, _tier))
            {
                _entries[media.Id] = new Entry(ProxyState.NotNeeded, null, default);
                continue;
            }

            Resolution target = ProxyPolicy.TargetResolution(media.Info.Width, media.Info.Height, _tier);
            string? path = TryResolveCachePath(media, target);
            if (path is null)
            {
                // Source offline / unreadable identity → can't key a cache file; leave it on the original.
                _entries[media.Id] = new Entry(ProxyState.NotNeeded, null, target);
                continue;
            }

            if (File.Exists(path))
            {
                // A prior session already built it: reuse without re-encoding and switch the preview to it.
                _entries[media.Id] = new Entry(ProxyState.Ready, path, target);
                ProxyReady?.Invoke(media.Id);
                continue;
            }

            _entries[media.Id] = new Entry(ProxyState.Queued, path, target);
            int priority = onTimeline.Contains(media.Id) ? 0 : 1;
            lock (_queueGate)
                _queue.Add(new WorkItem(media.Id, media, target, path, priority));
            _signal.Release();
        }
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

            if (!TryDequeue(out WorkItem item))
                continue;

            _entries[item.Id] = new Entry(ProxyState.Building, item.Path, item.Target);
            bool ok;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                ok = ProxyTranscoder.Generate(item.Media, item.Target, item.Path, ct);
            }
            catch
            {
                ok = false; // never let one bad source kill the worker
            }

            _entries[item.Id] = ok
                ? new Entry(ProxyState.Ready, item.Path, item.Target)
                : new Entry(ProxyState.Failed, null, item.Target);

            if (ok)
                ProxyReady?.Invoke(item.Id);
        }
    }

    /// <summary>Removes and returns the highest-priority queued item (lowest priority number, then FIFO).</summary>
    private bool TryDequeue(out WorkItem item)
    {
        lock (_queueGate)
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
