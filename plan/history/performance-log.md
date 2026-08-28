# Playback performance log

> Moved verbatim from PLAN.md in the 2026-08-26 restructure. Append future playback-performance
> investigations here (PLAN.md links to this file).

- **Preview judder fix + diagnostics overlay (recorded 2026-06-30).** Reported stutter on a plain 1080p30
  clip (GPU `h264`/D3D11VA decode, RTX 3060). Added a **View ▸ Playback Statistics** overlay
  (`Sprocket.App/PlaybackStatsOverlay.cs` + `PlaybackEngine.GetStatistics()`/per-track drop counters +
  `GetActiveVideoDecodeInfo()`) reporting effective vs. target fps, dropped frames, decode codec + HW device,
  CPU/memory/GC. A headless real-time benchmark over the engine then pinned the cause: **not** decode (0 drops),
  GC (0 collections) or the OS timer per se, but the pump pacing — it polled at a fixed sub-frame interval
  (~27–31 ms) that **aliased** against the 33.3 ms frame grid, so frames averaged a clean 30 fps but were
  presented at uneven times (present-interval sd ≈ 9.6 ms, gaps to 63 ms, doubled frames). **Fix
  (`Sprocket.Playback/PlaybackEngine.cs` + `PlaybackTimerResolution.cs`):** pace the pump on an **absolute
  wall-clock frame schedule** with a no-overshoot sleep+spin waiter (precise regardless of OS timer
  granularity; re-anchors if >2 frames behind), keeping the existing drop/hold for A/V sync; plus a
  `timeBeginPeriod(1)` raise while playing to shrink the spin window when Windows honours it. Removed the
  obsolete fixed-pace `ComputePace`. Verified across runs: present-interval **sd 9.6 → ~3.0 ms, hitches
  12 → 0, doubled frames 18 → 0, 0 drops**; clean build + smoke launch. Next preview-perf wins remain the
  zero-copy GPU upload (step 6 deferral) and the render cache (step 32).

- **Static per-frame allocation audit (recorded 2026-08-28; no profiler run).** An external static
  review flagged managed allocations per repaint; every cited line was validated against the source.
  **Pixel rule (§1) holds** — `FramePresenter.cs:65` / `SkiaEffectPipeline.cs:449` wrap native memory via
  `SKImage.FromPixels`, no copies. **Confirmed churn**, by corrected severity: *High* — per-enabled-effect
  parameter `Dictionary` + `ResolvedEffect` + array (`RenderGraph.ResolveEffects`, called per repaint from
  `PlaybackEngine.UseLayers`); generator dictionaries + `ResolvedGenerator` with no zero-parameter fast path;
  per-effect `SKRuntimeEffectUniforms`/`Children`/`SKShader` + `float[]` uniform arrays in
  `SkiaEffectPipeline.BuildEffectShader`. *Medium* — fresh `List<PresentedVideoLayer>` per `UseLayers`
  and a capturing lambda per `PreviewSurface.Render`. *Low* — interface `foreach` enumerators, nested-
  placeholder `SKPaint`s, generator/adjustment offscreen surface + snapshot wrappers. **Downgraded:**
  `PlanVideoFrame` plan objects (export / render-cache / nested only — live preview bypasses it) and the
  pump's `async Task` methods (non-suspending completions return cached Tasks; the feed is already
  `ValueTask`; only `Task.Delay(1)` far from the deadline allocates). **Mitigations already present:**
  compiled-once `SKRuntimeEffect`s, one reused `SKPaint`, no-effects fast path, plugin effect cache,
  process-cached LUT, reused scratch lists, zero-enabled-effects early return. Allocation *rate* has never
  been measured (the 2026-06-30 run counted collections only) — step 60
  ([plan](../features/preview-allocation-churn.md)) adds a `GC.GetAllocatedBytesForCurrentThread()`
  harness first, then remediates in payoff order.
