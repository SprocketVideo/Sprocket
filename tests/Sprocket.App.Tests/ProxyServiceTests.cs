using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sprocket.App.Proxy;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the proxy service's <em>runtime state machine</em> (PLAN.md step 18) — the load-bearing half
/// of the View ▸ Proxy window, which is a thin view over it. The <see cref="IProxyTranscoder"/> seam is what makes
/// these testable without <c>ffmpeg</c>: the fake below can block mid-build, report progress, observe cancellation,
/// and be released late to simulate a stale completion landing after the state moved on.
/// </summary>
/// <remarks>
/// The behaviours pinned here are the ones that are easy to get subtly wrong and impossible to see by inspection:
/// per-entry (not global) stale-completion fencing, pause/resume semaphore accounting, the inventory-vs-scheduling
/// split that lets a disabled project still enumerate, and the "deletion is not persisted, so a re-enable rebuilds"
/// promise the dialog's tooltip makes. The window itself rests on manual verification (the App is a UI-bound WinExe).
/// </remarks>
public sealed class ProxyServiceTests : IDisposable
{
    private const int Timeout = 10_000;

    private readonly string _root;

    public ProxyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sprocket-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "cache"));
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        // Keep every build and delete inside this test's own throwaway cache dir.
        Environment.SetEnvironmentVariable("SPROCKET_PROXY_DIR", Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SPROCKET_PROXY_DIR", null);
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    // ── Inventory vs. scheduling ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_project_inventoried_while_disabled_still_enumerates_every_source()
    {
        // The regression this pins: Enqueue used to early-return when disabled, so the dialog had nothing to show
        // and the user couldn't tell what would be built if they switched proxies on.
        Project project = NewProject(out MediaRef heavy, out MediaRef light);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: false, ProxyTier.Half, fake);

        service.Enqueue(project);

        IReadOnlyList<ProxySnapshot> rows = service.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal(ProxyState.NotGenerated, rows.Single(r => r.Id == heavy.Id).State);
        Assert.Equal(ProxyState.NotNeeded, rows.Single(r => r.Id == light.Id).State);
        Assert.Equal(0, fake.CallCount); // …and nothing was built
    }

    [Fact]
    public void A_source_imported_while_disabled_is_inventoried_by_the_second_Enqueue_pass()
    {
        // Both call sites matter: the composition root's Enqueue at session build, and MainWindow's post-import one.
        Project project = NewProject(out MediaRef first, out _);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: false, ProxyTier.Half, fake);
        service.Enqueue(project);

        MediaRef imported = AddHeavySource(project, "imported.mp4");
        service.Enqueue(project);

        Assert.Equal(3, service.Snapshot().Count);
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(imported.Id));
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(first.Id));
    }

    [Fact]
    public void Enabling_schedules_everything_that_is_not_generated_and_builds_it()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: false, ProxyTier.Half, fake);
        service.Enqueue(project);

        service.SetEnabled(true);

        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.Equal(heavy.Id, Assert.Single(fake.Built));
        Assert.NotEqual(heavy.AbsolutePath, service.BestPath(heavy)); // the preview switched onto the proxy
    }

    // ── On / off ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Disabling_reverts_the_preview_to_originals_and_announces_every_formerly_ready_source()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        var announced = new ConcurrentBag<MediaRefId>();
        service.ProxyPathChanged += id => announced.Add(id);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);
        announced.Clear();

        service.SetEnabled(false);

        Assert.Equal(heavy.AbsolutePath, service.BestPath(heavy));
        Assert.Contains(heavy.Id, announced); // …so the engine re-opens the feed on the original
        Assert.Equal(ProxyState.Ready, service.StateOf(heavy.Id)); // the file is kept, not deleted
    }

    [Fact]
    public void Re_enabling_rebuilds_a_proxy_that_was_explicitly_deleted()
    {
        // Pinned semantics: deletion isn't persisted in the project, so any reload would rebuild it anyway —
        // pretending a delete sticks across a re-enable would just make the two paths disagree. The dialog's
        // Delete tooltip promises exactly this.
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);

        service.SetEnabled(false);
        Assert.True(service.DeleteProxy(heavy.Id));
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(heavy.Id));

        service.SetEnabled(true);

        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.Equal(2, fake.CallCount); // built, deleted, built again
    }

    [Fact]
    public void Deleting_leaves_the_entry_not_generated_and_does_not_rebuild_by_itself()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);
        string proxyPath = service.Snapshot().Single(r => r.Id == heavy.Id).ProxyPath!;

        service.DeleteProxy(heavy.Id);

        Assert.False(File.Exists(proxyPath));
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(heavy.Id));
        Assert.Equal(heavy.AbsolutePath, service.BestPath(heavy));
        // A later inventory pass must not quietly re-queue it either.
        service.Enqueue(project);
        Thread.Sleep(50);
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(heavy.Id));
        Assert.Equal(1, fake.CallCount);

        service.Generate(heavy.Id); // …but an explicit Generate does
        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public void Delete_all_empties_the_cache_and_resets_every_entry_without_rebuilding()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        MediaRef second = AddHeavySource(project, "second.mp4");
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);
        WaitForState(service, second.Id, ProxyState.Ready);

        int deleted = service.DeleteAllProxies();

        Assert.Equal(2, deleted);
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(heavy.Id));
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(second.Id));
        Thread.Sleep(50);
        Assert.Equal(2, fake.CallCount); // no auto-rebuild

        service.RebuildAll();
        WaitForState(service, heavy.Id, ProxyState.Ready);
        WaitForState(service, second.Id, ProxyState.Ready);
        Assert.Equal(4, fake.CallCount);
    }

    // ── Pause / resume ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pausing_cancels_the_active_build_and_resuming_restarts_it_from_zero()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder { BlockUntilReleased = true };
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        Assert.True(fake.WaitForStart(), "the first build never started");

        service.SetPaused(true);

        // The cancelled encode unwinds and the item goes back on the queue rather than reporting Failed.
        WaitForState(service, heavy.Id, ProxyState.Queued);
        Assert.True(service.Paused);
        Assert.Equal(1, fake.CallCount);

        fake.BlockUntilReleased = false;
        service.SetPaused(false);

        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.Equal(2, fake.CallCount); // restarted from scratch — the partial output was never promoted
    }

    [Fact]
    public void Resuming_after_a_pause_with_a_full_queue_completes_every_item()
    {
        // The semaphore-accounting case: while paused the worker swallows permits without consuming queue items,
        // so resume has to release exactly one per queued item — including the pause-cancelled build's requeue.
        Project project = NewProject(out MediaRef first, out _);
        var all = new List<MediaRef> { first };
        for (int i = 0; i < 3; i++)
            all.Add(AddHeavySource(project, $"extra{i}.mp4"));

        using var fake = new FakeTranscoder { BlockUntilReleased = true };
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        Assert.True(fake.WaitForStart(), "the first build never started");

        service.SetPaused(true);
        foreach (MediaRef media in all)
            WaitForState(service, media.Id, ProxyState.Queued);

        fake.BlockUntilReleased = false;
        service.SetPaused(false);

        foreach (MediaRef media in all)
            WaitForState(service, media.Id, ProxyState.Ready);
    }

    // ── Stale-completion fencing ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_completion_released_after_a_tier_change_cannot_overwrite_the_new_tier_state()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder { BlockUntilReleased = true, IgnoreCancellation = true };
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        Assert.True(fake.WaitForStart(), "the first build never started");
        Resolution halfTarget = service.Snapshot().Single(r => r.Id == heavy.Id).Target;

        service.SetTier(ProxyTier.Quarter);

        // Let the old-tier build "succeed" now, long after its entry was re-keyed: its result must be discarded.
        // The new-tier build (already queued behind it) then runs unblocked and is the one that lands.
        fake.BlockUntilReleased = false;
        fake.Release();
        WaitForState(service, heavy.Id, ProxyState.Ready);

        ProxySnapshot row = service.Snapshot().Single(r => r.Id == heavy.Id);
        Assert.Equal(ProxyTier.Quarter, service.Tier);
        Assert.NotEqual(halfTarget, row.Target);
        Assert.Equal(2, fake.CallCount);
        // The Ready state came from the new-tier build, not the stale one that finished after it was invalidated.
        Assert.Equal(new[] { halfTarget, row.Target }, fake.Targets.ToArray());
    }

    [Fact]
    public void Deleting_one_asset_does_not_invalidate_another_asset_s_in_flight_build()
    {
        // The headline of the per-entry generation counter: a single global counter made DeleteProxy(A) throw away
        // B's completed build, leaving B stuck on its original with no proxy and nothing scheduled.
        Project project = NewProject(out MediaRef building, out _);
        MediaRef other = AddHeavySource(project, "other.mp4");
        using var fake = new FakeTranscoder { BlockUntilReleased = true };
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);

        // Put only `building` on the timeline so it is the priority-0 item and starts first.
        service.Enqueue(project);
        Assert.True(fake.WaitForStart(), "the first build never started");
        Assert.Equal(ProxyState.Building, service.StateOf(building.Id));

        Assert.True(service.DeleteProxy(other.Id));

        fake.BlockUntilReleased = false;
        fake.Release();

        WaitForState(service, building.Id, ProxyState.Ready);
        Assert.Equal(ProxyState.NotGenerated, service.StateOf(other.Id));
    }

    [Fact]
    public void An_old_build_completing_after_a_delete_and_regenerate_is_discarded_and_the_new_one_lands()
    {
        // The ABA case: delete → Generate re-queues the same path while the old build is still running. Comparing
        // paths would see "same file, all good"; only the bumped generation catches it.
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder { BlockUntilReleased = true, IgnoreCancellation = true };
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        Assert.True(fake.WaitForStart(), "the first build never started");

        service.DeleteProxy(heavy.Id);
        service.Generate(heavy.Id);
        Assert.Equal(ProxyState.Queued, service.StateOf(heavy.Id));

        fake.Release();                       // the stale build reports success — must be discarded
        Assert.True(fake.WaitForStart(), "the re-queued build never started");
        fake.BlockUntilReleased = false;
        fake.Release();

        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.Equal(2, fake.CallCount);
    }

    // ── Progress ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void State_transitions_always_announce_even_though_progress_ticks_are_throttled()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder { ProgressTicks = 200 };
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        int events = 0;
        service.ProgressChanged += () => Interlocked.Increment(ref events);

        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);

        // The three transitions (queued / building / ready) always announce; the 200 progress reports on top of them
        // are rate-limited to ~4/s, so the subscriber sees a handful of events rather than 203 of them.
        Assert.True(events >= 3, $"expected the state transitions to announce, saw {events}");
        Assert.True(events < 20, $"progress was not throttled: {events} events for 200 reports");
    }

    [Fact]
    public void ShouldPostProgress_rate_limits_ticks_to_the_throttle_interval()
    {
        Assert.False(ProxyService.ShouldPostProgress(1_000, 1_000));
        Assert.False(ProxyService.ShouldPostProgress(1_000 + ProxyService.ProgressThrottleMs - 1, 1_000));
        Assert.True(ProxyService.ShouldPostProgress(1_000 + ProxyService.ProgressThrottleMs, 1_000));
    }

    [Fact]
    public void The_snapshot_reports_the_proxy_size_captured_at_build_time()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder { OutputBytes = 4096 };
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);

        Assert.Equal(4096, service.Snapshot().Single(r => r.Id == heavy.Id).SizeBytes);
    }

    // ── Undoable settings ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Undoing_the_enable_command_reconfigures_the_live_service()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);
        var history = new EditHistory();

        history.Execute(ProxySettingsOps.BuildEnableCommand(project.Settings, service.SetEnabled, false)!);
        Assert.False(project.Settings.UseProxies);
        Assert.False(service.Enabled);
        Assert.Equal(heavy.AbsolutePath, service.BestPath(heavy)); // preview back on the original

        history.Undo();

        Assert.True(project.Settings.UseProxies);
        Assert.True(service.Enabled);
        Assert.NotEqual(heavy.AbsolutePath, service.BestPath(heavy));
        Assert.Equal(1, history.RedoCount); // and the change is a real undo entry, so the document is dirty
    }

    [Fact]
    public void Undoing_the_tier_command_restores_the_prior_tier_and_rebuilds()
    {
        Project project = NewProject(out MediaRef heavy, out _);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);
        Resolution half = service.Snapshot().Single(r => r.Id == heavy.Id).Target;
        var history = new EditHistory();

        history.Execute(ProxySettingsOps.BuildTierCommand(project.Settings, service.SetTier, ProxyTier.Quarter)!);
        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.Equal(ProxyTier.Quarter, service.Tier);
        Assert.NotEqual(half, service.Snapshot().Single(r => r.Id == heavy.Id).Target);

        history.Undo();

        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.Equal(ProxyTier.Half, project.Settings.ProxyTier);
        Assert.Equal(ProxyTier.Half, service.Tier);
        Assert.Equal(half, service.Snapshot().Single(r => r.Id == heavy.Id).Target);
    }

    [Fact]
    public void The_settings_commands_are_null_when_nothing_changed()
    {
        var settings = new ProjectSettings();
        Assert.Null(ProxySettingsOps.BuildEnableCommand(settings, _ => { }, settings.UseProxies));
        Assert.Null(ProxySettingsOps.BuildTierCommand(settings, _ => { }, settings.ProxyTier));
    }

    // ── Format-triggered proxies (codec / bit depth at 1080p-class) ────────────────────────────────

    [Fact]
    public void A_1080p_hevc_source_queues_with_the_codec_reason_and_the_tier_target()
    {
        Project project = NewProject(out _, out _);
        MediaRef hevc = AddSource(project, "gopro.mp4", 1920, 1080, codec: "hevc");
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);

        service.Enqueue(project);

        WaitForState(service, hevc.Id, ProxyState.Ready);
        ProxySnapshot row = service.Snapshot().Single(r => r.Id == hevc.Id);
        Assert.Equal(ProxyReason.DemandingCodec, row.Reason);
        Assert.Equal(new Resolution(960, 540), row.Target); // the tier applies as-is, as in Resolve/Premiere
        Assert.Contains(new Resolution(960, 540), fake.Targets);
    }

    [Fact]
    public void The_FullHd_tier_builds_a_same_resolution_codec_conversion_proxy_for_1080p_hevc()
    {
        Project project = NewProject(out _, out _);
        MediaRef hevc = AddSource(project, "drone.mp4", 1920, 1080, codec: "hevc");
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.FullHd, fake);

        service.Enqueue(project);

        WaitForState(service, hevc.Id, ProxyState.Ready);
        Assert.Contains(new Resolution(1920, 1080), fake.Targets); // same-res: the codec conversion is the benefit
    }

    [Fact]
    public void Plain_1080p_h264_still_never_wants_a_proxy()
    {
        Project project = NewProject(out _, out MediaRef light);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);

        service.Enqueue(project);

        Assert.Equal(ProxyState.NotNeeded, service.StateOf(light.Id));
        Assert.Equal(ProxyReason.None, service.Snapshot().Single(r => r.Id == light.Id).Reason);
    }

    // ── Runtime recommendation (the playback drop monitor's entry point) ───────────────────────────

    [Fact]
    public void RecommendProxy_promotes_a_not_needed_entry_without_building_and_Generate_then_builds_it()
    {
        Project project = NewProject(out MediaRef heavy, out MediaRef light);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready); // let the static work drain first

        Assert.True(service.RecommendProxy(light.Id));

        // Recommend-only: the entry is marked, nothing is scheduled.
        ProxySnapshot row = service.Snapshot().Single(r => r.Id == light.Id);
        Assert.Equal(ProxyState.Recommended, row.State);
        Assert.Equal(ProxyReason.Performance, row.Reason);
        Assert.Equal(new Resolution(960, 540), row.Target);
        Assert.Equal(1, fake.CallCount); // only the heavy source's static build ran

        // Idempotent once it is anything but NotNeeded.
        Assert.False(service.RecommendProxy(light.Id));

        // The user's Generate click is what actually builds it.
        service.Generate(light.Id);
        WaitForState(service, light.Id, ProxyState.Ready);
        Assert.Contains(light.Id, fake.Built);
    }

    [Fact]
    public void RecommendProxy_records_the_recommendation_even_while_proxies_are_off()
    {
        // Matches Enqueue's inventory-vs-scheduling split: the dialog shows the recommendation either way.
        Project project = NewProject(out _, out MediaRef light);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: false, ProxyTier.Half, fake);
        service.Enqueue(project);

        Assert.True(service.RecommendProxy(light.Id));

        Assert.Equal(ProxyState.Recommended, service.StateOf(light.Id));
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public void No_automatic_path_builds_an_unconfirmed_recommendation()
    {
        // The whole point of the Recommended state: telemetry can misfire, so a suggestion is built only when
        // the user asks. Every automatic scheduling path keys off NotGenerated and must skip it — enabling
        // proxies, resuming, and Rebuild All included.
        Project project = NewProject(out MediaRef heavy, out MediaRef light);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: false, ProxyTier.Half, fake);
        service.Enqueue(project);
        Assert.True(service.RecommendProxy(light.Id));

        service.SetEnabled(true);
        WaitForState(service, heavy.Id, ProxyState.Ready); // the statically-wanted proxy still builds
        service.RebuildAll();
        service.SetPaused(true);
        service.SetPaused(false);
        service.Enqueue(project);                           // a later project-load pass must not sweep it up either

        Assert.Equal(ProxyState.Recommended, service.StateOf(light.Id));
        Assert.DoesNotContain(light.Id, fake.Built);
        Assert.Equal(1, fake.CallCount); // only heavy
    }

    [Fact]
    public void Deleting_proxies_leaves_an_unconfirmed_recommendation_unconfirmed()
    {
        // Delete All resets entries to NotGenerated (schedulable). A recommendation has no file to delete and
        // must not be promoted into that state as a side effect.
        Project project = NewProject(out MediaRef heavy, out MediaRef light);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: true, ProxyTier.Half, fake);
        service.Enqueue(project);
        WaitForState(service, heavy.Id, ProxyState.Ready);
        Assert.True(service.RecommendProxy(light.Id));

        Assert.False(service.DeleteProxy(light.Id));
        service.DeleteAllProxies();

        Assert.Equal(ProxyState.Recommended, service.StateOf(light.Id));
    }

    [Fact]
    public void A_performance_recommendation_survives_a_tier_change_as_a_recommendation()
    {
        Project project = NewProject(out _, out MediaRef light);
        using var fake = new FakeTranscoder();
        using var service = new ProxyService(enabled: false, ProxyTier.Half, fake);
        service.Enqueue(project);
        Assert.True(service.RecommendProxy(light.Id));

        service.SetTier(ProxyTier.FullHd);

        // The drop evidence is tier-independent and the advisor only nudges once per session, so the entry must
        // not fall back to NotNeeded — but a tier change is not the user confirming it either.
        ProxySnapshot row = service.Snapshot().Single(r => r.Id == light.Id);
        Assert.Equal(ProxyState.Recommended, row.State);
        Assert.Equal(ProxyReason.Performance, row.Reason);
        Assert.Equal(new Resolution(1920, 1080), row.Target); // re-targeted to the new tier
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A project with one 4K source on the timeline (wants a proxy) and one 1080p source (never will).</summary>
    private Project NewProject(out MediaRef heavy, out MediaRef light)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(3840, 2160), 48000));
        var track = new VideoTrack { Name = "V1" };
        project.Timeline.Tracks.Add(track);

        heavy = AddHeavySource(project, "heavy.mp4");
        track.Clips.Add(new Clip(heavy.Id, Timecode.Zero, heavy.Info.Duration, Timecode.Zero));

        light = new MediaRef(MediaRefId.New(), WriteSource("light.mp4"),
            new ProbedMediaInfo(Timecode.FromSeconds(10), true, new Rational(30, 1), 1920, 1080, false, 0, 0));
        project.MediaPool.Add(light);
        return project;
    }

    /// <summary>Adds a 4K source (over the 1080p preview ceiling, so <c>NeedsProxy</c> is true) to the media pool,
    /// backed by a real file so its cache key can be computed.</summary>
    private MediaRef AddHeavySource(Project project, string fileName)
    {
        var media = new MediaRef(MediaRefId.New(), WriteSource(fileName),
            new ProbedMediaInfo(Timecode.FromSeconds(10), true, new Rational(30, 1), 3840, 2160, false, 0, 0));
        project.MediaPool.Add(media);
        return media;
    }

    /// <summary>Adds a source with explicit format facts (codec / pixel format / bit depth) to the media pool,
    /// backed by a real file so its cache key can be computed — for the format-triggered policy tests.</summary>
    private MediaRef AddSource(
        Project project, string fileName, int width, int height, string codec = "", string pixFmt = "", int bitDepth = 8)
    {
        var media = new MediaRef(MediaRefId.New(), WriteSource(fileName),
            new ProbedMediaInfo(Timecode.FromSeconds(10), true, new Rational(30, 1), width, height, false, 0, 0,
                VideoCodec: codec, PixelFormatName: pixFmt, BitDepth: bitDepth));
        project.MediaPool.Add(media);
        return media;
    }

    private string WriteSource(string fileName)
    {
        string path = Path.Combine(_root, "media", fileName);
        File.WriteAllBytes(path, new byte[128]);
        return path;
    }

    private static void WaitForState(ProxyService service, MediaRefId id, ProxyState expected)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < Timeout)
        {
            if (service.StateOf(id) == expected)
                return;
            Thread.Sleep(5);
        }
        Assert.Equal(expected, service.StateOf(id)); // fail with the actual state in the message
    }

    /// <summary>
    /// A scriptable stand-in for the <c>ffmpeg</c> child: it can block until released (so a build can be caught
    /// mid-flight), report an arbitrary number of progress ticks, honour or deliberately ignore cancellation (the
    /// latter is how a stale completion is staged), and write an output file of a chosen size.
    /// </summary>
    private sealed class FakeTranscoder : IProxyTranscoder, IDisposable
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _release = new(0);
        private int _calls;

        /// <summary>When set, each <see cref="Generate"/> parks until <see cref="Release"/> (or cancellation).</summary>
        public volatile bool BlockUntilReleased;

        /// <summary>Runs to completion even when cancelled — used to stage a completion landing after the
        /// service has already invalidated the entry underneath it.</summary>
        public bool IgnoreCancellation { get; init; }

        /// <summary>How many progress reports to emit per build.</summary>
        public int ProgressTicks { get; init; } = 2;

        /// <summary>Size of the proxy file written on success.</summary>
        public int OutputBytes { get; init; } = 64;

        public int CallCount => Volatile.Read(ref _calls);
        public ConcurrentQueue<MediaRefId> Built { get; } = new();

        /// <summary>Every target a build was asked for, in call order — how a test tells which build's result landed.</summary>
        public ConcurrentQueue<Resolution> Targets { get; } = new();

        public bool WaitForStart(int timeoutMs = Timeout) => _started.Wait(timeoutMs);

        public void Release() => _release.Release();

        public bool Generate(
            MediaRef media, Resolution target, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Targets.Enqueue(target);
            _started.Release();

            for (int i = 1; i <= ProgressTicks; i++)
                progress?.Report((double)i / (ProgressTicks + 1));

            if (BlockUntilReleased)
            {
                while (!_release.Wait(5))
                {
                    if (cancellationToken.IsCancellationRequested && !IgnoreCancellation)
                        return false;
                }
            }

            if (cancellationToken.IsCancellationRequested && !IgnoreCancellation)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, new byte[OutputBytes]);
            Built.Enqueue(media.Id);
            return true;
        }

        public void Dispose()
        {
            _started.Dispose();
            _release.Dispose();
        }
    }
}
