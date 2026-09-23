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

- **Export speed phase 2 — pipelined Final Export (recorded 2026-09-23).** Throwaway headless harness over
  `VideoExporter.ExportCore` (not committed), 1080p30 testsrc2 H.264 source, software decode + CPU raster render,
  x264 encode, 24-core box → 4 render workers. *Plain* (one Brightness effect): sequential ~22 fps → pipelined
  **40–48 fps** (~2×; now encode-bound). *Heavy* (Brightness + Glow + DirectionalBlur + ColorWheels, 5 frames):
  sequential **299 s** (~60 s/frame — CPU-raster SkSL for these blurs is extremely slow) → pipelined **162 s**
  (1.85×). Summed render busy time rose 299 → 490 s, i.e. per-frame render inflates ~1.6× with 4 concurrent
  workers (memory-bandwidth contention), so raising `MaxRenderWorkers` would buy little; the earlier suspected
  "hang" was just this per-frame cost, not a deadlock. The real fix for effect-heavy exports is the GPU render path
  (phase 3 Fast Export).

- **Export speed phases 1 → 2 on `Psycho.json` (recorded 2026-09-23).** Manual acceptance in the interactive app
  (Release, same machine/range), H.264 with *Hardware (if available)*, ~819 frames, A1 muted. Stage times are busy
  time summed across workers, so after phase 2 they can exceed the total.

  | Build | Encoder | Total | Avg fps | Decode | Render | Encode | Audio |
  |---|---|---|---|---|---|---|---|
  | Phase 1 (`98a9a12`, sequential) | h264_nvenc (hw) | 56,825 ms | 14.4 | 972 ms | 51,319 ms | 4,304 ms | 0 ms |
  | Phase 2 (`ab1eafc`, 4 workers) | h264_nvenc (hw) | 15,333 ms | 53.4 | 4,293 ms | 56,111 ms | 5,047 ms | 0 ms |

  **3.7× wall-clock** — the project is render-bound, and summed render busy time rose only ~9% (51.3 → 56.1 s),
  i.e. ~3.7 of 4 workers busy on average with little contention (unlike the CPU-blur synthetic, ~1.6×). Summed
  decode rose ~4.4× (0.97 → 4.3 s) because round-robin makes each worker decode every source frame to reach its
  own; it overlaps render via prefetch so it isn't on the critical path here, but it would matter on a
  decode-heavy (4K / long-GOP) project — a candidate for later tuning (e.g. contiguous chunks per worker).
  Hardware engagement and the muted-A1 audio skip (audio 0 ms) confirmed; phase-1 manual acceptance closed.

- **Procedural generators rendered on the CPU (recorded 2026-09-23; confirmed in-app — Smoke and the Ground Explosion stack now play smoothly).** Reported:
  after inserting a Smoke generator, the preview became very laggy and the playhead wouldn't drag through the smoke
  clip (it jumped before/after it); removing the clip restored normal speed. Cause: `SkiaEffectPipeline.DrawGenerator`
  rendered every generator into a **raster** `SKSurface.Create(info)` at sequence resolution. So the atmospheric
  generators' SkSL (the Smoke/Fog clouds run ~10 value-noise samples per pixel) ran in Skia's CPU interpreter, and
  the result was uploaded to the GPU every frame. Titles and mattes were cheap enough to hide this. **Fix:** create
  the offscreen on the destination canvas's `GRRecordingContext` (budgeted, so Skia's scratch-texture cache recycles
  it), with raster kept as the fallback for a CPU canvas (headless tests, CPU export). The scrub symptom was the same
  stall: each redraw had to finish the CPU smoke frame.

- **CPU spike and UI stall on project open (recorded 2026-09-23).** Reported: the app starts quickly, but
  opening a project pegs the CPU and the UI stutters for a while. Loading the project file was cheap (JSON; the
  probe info is stored, so nothing is re-probed). The cost came from work that starts when `MainWindow` is built:
  1. **Media-bin thumbnails.** `MediaBrowserPanel.RebuildGrids` started one unthrottled `Task.Run` per tile.
     Posters decoded in software with auto threading (one FFmpeg thread per core, per job), so N items meant
     roughly N × cores threads. Every audio-bearing item also started a waveform decode (up to 4M samples) for an
     Audio-tab grid that the mixer replaces, so it was never shown. The cache was in-memory and per window, so
     every open, including reopening the same project, regenerated everything.
  2. **Idle timeline repaint.** The pump raised `PositionChanged` every ~16 ms even while paused, and
     `TimelineControl` invalidated unconditionally. Partly visible clips drew their schematic waveform and
     filmstrip across the full clip width.
  3. **Proxy builds** capped only the encoder (`-threads` sat after `-i`). On the UI thread, `oldProxy.Dispose()`
     and `StabilizationService.Dispose()` could each block for up to 5 s.

  **Fix:**
  - **Thumbnails:** a shared `SemaphoreSlim` limits generation to `clamp(cores/4, 1, 4)`. Thumbnail decoders get
    a new `MediaOpenRequest.DecoderThreads = 1`, and pending work is cancelled when the service is disposed.
  - **Thumbnail disk cache:** `ThumbnailDiskCache` (`%LocalAppData%/Sprocket/thumbs`), keyed by SHA-256 over
    kind, path, size, mtime and dimensions. Cache hits skip the semaphore. The cache is pruned above 200 MB,
    down to 150 MB.
  - **Waveforms:** the hidden audio grid is only built when it can actually be shown, and waveform peaks are
    reduced while streaming (`WaveformPeakAccumulator`) instead of buffering 4M samples.
  - **Idle repaint:** the engine raises `PositionChanged` only when the position changes or a seek/force-present
    happens, so a same-position seek still echoes. `TimelineControl` and the monitor readouts skip repeats and
    coalesce their posts, and clip decoration loops are clipped to the viewport.
  - **Proxies and teardown:** proxy ffmpeg also passes `-threads` before `-i`, and old-session proxy and
    stabilization teardown runs on a background thread. Not yet measured in the interactive app.
