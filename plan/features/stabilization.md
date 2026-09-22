# Video stabilization (adaptive smoothing, per-channel, focus-breathing lock)

✅ **Done — all 7 phases shipped 2026-09-21.** Unscheduled feature (no build-order step number);
tracked in [PLAN.md](../../PLAN.md) Open work (now `[x]`), DONE log in
[plan/history/steps-58plus.md](../history/steps-58plus.md), user docs at
`../sprocket-docs/effects-color/stabilization.md`. Relative links resolve from the repo root.

**Scope in one line:** a `Stabilization` effect (`builtin.stabilization`, short code `ST`, category
`Video`) that removes camera-shake — pan/tilt jitter, roll, and scale wobble — from motion recovered
by a background analysis pass, with the best control from the leading editors: adaptive
intent-preserving smoothing, per-channel smoothing, a Resolve-style cropping budget + auto-zoom, a
Strength blend, Camera Lock (tripod), a horizon lock, and a **Scale Lock** that fixes focus breathing
(FOV pumping from accidental autofocus).

Analysis data lives in a **per-user regenerable cache** (not the project file), Resolve/FCP style, and
**analysis starts automatically** when the effect is applied. The solve is a cheap, deterministic pure
function of (motion track, settings), so preview and export produce identical frames (ARCHITECTURE §5).

## Why / product context

Every leading editor has a marquee stabilizer, and each does one thing best. We take the best control
from each rather than cloning any single one (CLAUDE.md: prefer established behavior, note departures).

| Tool | What it does well | What we take |
|---|---|---|
| **Premiere Warp Stabilizer** | Auto-analyzes in the background the moment it is applied (banner over the monitor); re-tunes instantly without re-analysis; Method ladder Position → PSR → Perspective → Subspace Warp; "Stabilize Only" shows the raw corrected frame with borders; Show Track Points; Detailed Analysis. | Auto-analyze on apply, tune-without-reanalysis, the Method ladder, Stabilize-Only borders, track-point overlay, Detailed Analysis tier. **Not** its four-control cropping cluster (Max Scale / Action-Safe / Additional Scale / Crop-Less↔Smooth-More). |
| **DaVinci Resolve** | A single **Cropping Ratio** budget with automatic "smooth as much as the budget allows"; **Zoom** toggle (fill vs borders); **Strength** blend; **Camera Lock**; Mode Perspective / Similarity / Translation; a **camera path graph** (pan/tilt/zoom/rotation curves); stabilization data cached in the project database. | Cropping Ratio + Zoom as *the* framing controls; Strength; Camera Lock; the path graph in the Inspector (the killer diagnostic for focus breathing — the zoom curve shows the pumping). |
| **Final Cut Pro** | Enable = analyze (one checkbox); **InertiaCam** intent-preserving smoothing; **SmoothCam** with separate Translation / Rotation / Scale sliders; Tripod Mode; batch **Analyze for stabilization** from the browser; a **Background Tasks** window. | Adaptive intent-preserving smoothing as the default; **per-channel smoothing** (the natural home for a scale-only fix); batch analyze from the media bin; a generalised Background Tasks window. |

**Naming:** "Stabilization" (Resolve and FCP both use it; Warp Stabilizer is Adobe's brand). Id
`builtin.stabilization`, short code `ST`, category `Video`, menu Effects ▸ Video.

**Deliberate departures**
- **One Smoothing mode dropdown** `Smooth Camera (adaptive) | Smooth Motion (uniform) | Camera Lock`
  instead of FCP's Automatic/InertiaCam/SmoothCam + Tripod checkbox and Warp's Result dropdown.
- **Per-channel scale handling** goes further than FCP: Scale has `Smooth | Preserve | Lock`; `Lock`
  removes *all* scale change relative to a reference (`Tightest | Widest | First Frame | Median`) — the
  focus-breathing fix. Preset **"Fix Focus Breathing Only"** = position/rotation smoothing 0, Scale Lock,
  Zoom on. Plus a **Lock Horizon** toggle, cheap once rotation is tracked.
- **Framing = Zoom toggle + Cropping Ratio** (Resolve). Zoom on ⇒ auto-scale up to the budget and
  automatically relax smoothing where the budget would be exceeded (what Warp's Crop-Less↔Smooth-More
  does by hand); Zoom off ⇒ Warp's "Stabilize Only" (transparent borders). Applied zoom shown as a readout.
- **v1 methods:** Translation, Similarity (position + scale + rotation), Perspective. Subspace Warp,
  Synthesize Edges, Rolling Shutter, gyro-metadata stabilization are follow-ons.
- Analysis in a **per-user cache** (Resolve/FCP style). Missing ⇒ pass-through + banner + auto re-analyze.
- Focus breathing also changes *blur*; we correct FOV only — docs say so.

## Existing seams to build on

| Piece | Where | Use |
|---|---|---|
| Transform shader + matrix composition | [SkiaEffectPipeline.cs](../../src/Sprocket.Render/SkiaEffectPipeline.cs) `TransformSksl`, `BuildTransformShader`, `HasTransform` Decal rule | Model for `StabilizeSksl` (projective) + `BuildStabilizationShader`; extend `HasTransform` so borders read transparent. |
| Frame-context seam | [RenderPlan.cs](../../src/Sprocket.Core/Rendering/RenderPlan.cs) `ResolvedEffect`, populated in `RenderGraph.ResolveEffectsCore` ([RenderGraph.cs](../../src/Sprocket.Core/Rendering/RenderGraph.cs)) | **Additive** `ResolvedEffect.SourceTime` + `MediaRefId?` (phase 2) so Render can look up the clip's track. Media / nested / multicam paths in `ResolveClipLayer` know both. |
| Sequential decode + downscale | [MediaSource.cs](../../src/Sprocket.Media/MediaSource.cs) `SeekTo` / `TryDecodeNextFrame`; `Native/SwsScaler.cs` (internal; arbitrary dst size/format) | New public `MediaSource.TryDecodeNextGray(GrayFramePool, out GrayFrame)` with a second scaler; `SwsScaler` stays internal. |
| Background job + per-user cache + invalidation event | [ProxyService.cs](../../src/Sprocket.App/Proxy/ProxyService.cs) (worker, generation fencing, throttled progress, `ProxyPathChanged`), `Proxy/ProxyCache.cs`; [ExportQueue.cs](../../src/Sprocket.Export/ExportQueue.cs) (`IProgress<double>` + CTS) | `StabilizationService` + `AnalysisCache` copy these. Completion raises an event routed to preview repaint + render-cache invalidation — **no model mutation**, nothing to undo. |
| Background Tasks window | [ProxyStatusWindow.cs](../../src/Sprocket.App/ProxyStatusWindow.cs) (View ▸ Proxy, "modelled on FCP's Background Tasks") | Generalise into **View ▸ Background Tasks** listing proxy builds *and* stabilization analyses (phase 6). |
| Media bin context menu | [src/Sprocket.App/MediaBrowser/](../../src/Sprocket.App/MediaBrowser/) | "Analyze for Stabilization" on selected bin items (FCP), pre-warming the cache before the effect is applied. |
| Bespoke Inspector rows + graphs | [InspectorPanel.cs](../../src/Sprocket.App/Inspector/InspectorPanel.cs) `BuildEffectSection`, Color Wheels bespoke branch, `BuildPresetRow`; `Inspector/KeyframeGraphMath.cs` | `BuildStabilizationRows`: status/progress/Analyze/Cancel + Applied Zoom readout, and the **camera path graph** (raw vs smoothed pan/tilt/zoom/rotation; reuse the keyframe-graph drawing math). |
| Overlays | [MonitorOverlay.cs](../../src/Sprocket.Render/MonitorOverlay.cs) | Analyzing / needs-analysis / low-confidence banners; Show Track Points. |
| Render-cache hashing | [RenderCacheHasher.cs](../../src/Sprocket.Persistence/RenderCacheHasher.cs) | Params hashed already; solve is a pure function of (params, track). Analysis completion must invalidate segments rendered while unanalysed. |
| Export | [VideoExporter.cs](../../src/Sprocket.Export/VideoExporter.cs) + export dialog | Pre-check for unanalysed/stale stabilizations: "Analyze then export" / "Export as-is"; pause the worker during export. |
| Effect relevance / add menu | [EffectRelevance.cs](../../src/Sprocket.App/EffectRelevance.cs), `BuildAddEffectBar` | Media clips only. |
| MCP | `Sprocket.Mcp` lists `EffectCatalog.All` | Free; add `stabilization_status` / `stabilization_analyze` tools (phase 6). |

## Parameter design

Model units; Inspector order; `Kind` per `ParameterKind`. Dropdowns store their choice index; toggles
store 0/1. The dropdown choice lists live on `StabilizationSettings` (`ModeChoices`, `MethodChoices`,
`ScaleModeChoices`, `ScaleLockRefChoices`).

| Name | Display | Kind | Default | Range | Notes |
|---|---|---|---|---|---|
| `stabMode` | Mode | Dropdown | Smooth Camera | Smooth Camera / Smooth Motion / Camera Lock | Adaptive (intent-preserving) / uniform Gaussian / tripod. |
| `smoothness` | Smoothness | Continuous | 0.5 | 0–1 (%), keyframeable | Master. Window radius ≈ smoothness × 1 s of frames. |
| `strength` | Strength | Continuous | 1.0 | 0–1 (%), keyframeable | Blend between original and stabilized path (Resolve). |
| `stabMethod` | Method | Dropdown | Similarity | Translation / Similarity / Perspective | Solve-time choice; both models live in the track. |
| `positionSmooth` | Position Smoothing | Continuous | 1.0 | 0–2 (× master) | FCP SmoothCam per-channel. |
| `rotationSmooth` | Rotation Smoothing | Continuous | 1.0 | 0–2 (× master) | |
| `scaleMode` | Scale | Dropdown | Smooth | Smooth / Preserve / Lock | Lock = focus-breathing fix. |
| `scaleSmooth` | Scale Smoothing | Continuous | 1.0 | 0–2 (× master) | Used when Scale = Smooth. |
| `scaleLockRef` | Lock Reference | Dropdown | Tightest | Tightest / Widest / First Frame / Median | Used when Scale = Lock. |
| `lockRotation` | Lock Horizon | Toggle | 0 | | Rotation fully removed relative to the first frame. |
| `zoom` | Auto Zoom | Toggle | 1 | | On = auto-scale to fill; off = borders (Stabilize Only). |
| `croppingRatio` | Cropping Ratio | Continuous | 0.8 | 0.5–1 (%) | Fraction of the frame that must survive; 1 = no crop allowed. |
| `detailedAnalysis` | Detailed Analysis | Toggle | 0 | | Higher analysis res + 2× features; changes the cache key. |
| `showTrackPoints` | Show Track Points | Toggle | 0 | | Preview-only overlay. |
| `hideBanner` | Hide Warning Banner | Toggle | 0 | | |
| *(readouts)* | Applied Zoom · Analysis status | — | — | — | Bespoke status row, not params (phase 6). |

Presets (`StabilizationPresets.All`): Default, Gentle, Strong, Camera Lock / Tripod, Handheld Look
(Strength 0.6), Fix Focus Breathing Only, Horizon Lock. Presets set only *solve* parameters — never the
analysis/workflow toggles (`detailedAnalysis` / `showTrackPoints` / `hideBanner`), which stay the user's.

## Algorithm

**Analysis (once per source range, background, cancellable):**
1. Sequential decode (`MediaSource.SeekTo` + a gray decode overload), software decode forced for
   determinism, libswscale → **GRAY8 at ~480 px wide** (960 with Detailed) into pooled native buffers —
   no per-frame managed pixels (§1).
2. Shi-Tomasi corners bucketed on an 8×6 grid (~300 / ~600), re-seeded when a bucket runs dry; pyramidal
   Lucas-Kanade (4 levels, 21×21), forward-backward check ≤ 0.5 px.
3. Per frame pair fit **both** a 4-DOF similarity (tx, ty, log s, θ) and a homography (normalised DLT) with
   RANSAC; store inlier ratio + feature count as confidence; < 8 inliers ⇒ interpolate from neighbours and
   flag. Optionally store the inlier feature positions (for the overlay).
4. Write the **motion track** sidecar (binary `SPMT`, ~60 B/frame + optional points).

**Solve (cheap, deterministic, cached per (track, params)):**
1. Integrate inter-frame motion into the camera path (log-scale/angle/translation for similarity).
2. Smooth per channel with radius = master × channel multiplier: *Smooth Motion* = Gaussian low-pass;
   *Smooth Camera* = adaptive (wide-window median velocity estimates intent; the Gaussian window shrinks
   where sustained velocity indicates a deliberate pan/zoom, so the path follows intent instead of lagging
   it, InertiaCam-like); *Camera Lock* = constant path (mean). Scale `Lock` ⇒ scale replaced by the
   reference; `Preserve` ⇒ raw scale kept; `Lock Horizon` ⇒ rotation replaced by first-frame value.
3. Strength: `P_target = lerp(P_raw, P_smooth, strength)`; correction = target − raw per channel.
4. Framing: with Zoom on, a uniform zoom in [1, 1/croppingRatio] chosen minimal to cover the corrected
   frame; where the cap binds, a residual-damping λ ∈ [0,1] relaxes the correction until it covers. Zoom
   off ⇒ zoom 1 with transparent borders. Output a `StabilizationSolution` (per-frame normalised 3×3
   output→source matrices + applied zoom). L1-optimal cinematographic paths are a follow-on quality tier.

**Render:** one hard-coded pipeline case (like `Transform`) — inverse projective map in normalised layer
coords, Decal tiling. Per frame: binary search `SourceTime` in the track's pts ⇒ matrix ⇒ floats. Zero
allocation per frame once the solve is cached.

## New code layout

- **`src/Sprocket.Analysis`** (project → Core; + Media from phase 3): `Features/` (GrayImage span wrapper,
  ImagePyramid, CornerDetector, LucasKanadeTracker, RobustFit — pure managed, SIMD via `Vector<T>`, no IO),
  `Motion/` (`MotionEstimator`, `MotionTrackAnalyzer`).
- **`src/Sprocket.Core/Stabilization/`**: `FrameMotion`, `Homography` (shared motion primitives — Core owns
  the type, Analysis owns the estimation), `MotionTrack` (+ `Write/Read(Stream)`), `StabilizationSettings`,
  `StabilizationSolver` → `StabilizationSolution`, `AnalysisKey` (source identity + detailed flag + bucketed
  source range), `IMotionTrackProvider`; `StabilizationPresets` beside `EffectCatalog`.
- **`src/Sprocket.Render`**: `StabilizeSksl`, `BuildStabilizationShader`, `StabilizationSolveCache`,
  `pipeline.MotionTracks` provider property (null ⇒ pass-through).
- **`src/Sprocket.App/Stabilization/`**: `StabilizationService` (BelowNormal worker, queue, generation
  fencing, events; implements `IMotionTrackProvider`), `AnalysisCache` (`%LocalAppData%/Sprocket/analysis`,
  `SPROCKET_ANALYSIS_DIR`, Clear in Preferences), `CameraPathGraph` control, Background Tasks generalisation.
- Analysis range = clip source range ± 2 s handles, rounded out to 5 s buckets (small trims stay cached).

## Build phases

Too large for one session — **seven independently mergeable phases**, each sized for one context window.
**Start a phase in a fresh session by reading only: this section, the "Existing seams" table, the
"Parameter design" rows the phase adds, and the phase's own files.** Each phase ends with tests green,
`dotnet build Sprocket.slnx` clean, the phase box ticked with a dated one-liner, and one commit. Next
session prompt: "Implement phase N of plan/features/stabilization.md".

| Phase | Ships | Depends | Size |
|---|---|---|---|
| 1 | `Sprocket.Analysis`: tracker + RANSAC fits, headless on synthetic images | — | 1 session |
| 2 | Core: `MotionTrack`, `StabilizationSolver` (all modes), descriptor/params/presets, `ResolvedEffect` context | — | 1 session |
| 3 | Media: gray decode overload; `MotionTrackAnalyzer`; fixture integration tests | 1, 2 | ½–1 |
| 4 | Render: projective shader, solve cache, provider seam; fake-provider tests | 2 | ½–1 |
| 5 | App: service + cache + wiring + minimal Inspector row → **first end-to-end stabilized clip** | 3, 4 | 1 |
| 6 | UX: auto-analyze, stale detection, banners, Applied Zoom, **camera path graph**, track-point overlay, **Background Tasks window**, bin "Analyze", export pre-check, render-cache invalidation, Preferences, MCP | 5 | 1–1½ |
| 7 | Perspective method, Detailed Analysis tier, low-confidence handling, tuning, user docs, FEATURES/PLAN/README close-out | 5 | 1 |

- [x] **Phase 1 — Analysis library (headless)** (2026-09-21): new `src/Sprocket.Analysis` (net10.0,
  `TreatWarningsAsErrors`, → Core) added to `Sprocket.slnx`, with `tests/Sprocket.Analysis.Tests`. `Features/`
  (GrayImage, ImagePyramid, CornerDetector Shi-Tomasi 8×6 buckets, pyramidal Lucas-Kanade with
  forward-backward filter, RobustFit RANSAC similarity + normalised-DLT homography) and `Motion/MotionEstimator`
  (two grays + workspace → `FrameMotion`, re-seeds dry buckets). Tests: procedural-warp recovery within
  tolerance, outlier rejection, forward-backward drop, determinism, ≈0 managed alloc on the second call. Only
  the solution file otherwise touched. (Commit `e234ff2`.)
- [x] **Phase 2 — Core model, solver, descriptor** (2026-09-21): moved the shared `FrameMotion` / `Homography`
  motion primitives from `Sprocket.Analysis` into `Sprocket.Core.Stabilization` (Core owns the type; Analysis
  keeps the estimation). Added `Core/Stabilization/`: `MotionTrack` (binary `SPMT`, `FormatVersion = 2` with an
  optional points section; v1 still read; version/magic guard) + `Write/Read(Stream)`; `StabilizationSettings`
  (+ dropdown choice lists, clamped `FromResolvedEffect`); `StabilizationSolver` → `StabilizationSolution` (path
  integration; Gaussian / adaptive / Camera-Lock smoothing; per-channel multipliers; Scale Smooth/Preserve/Lock
  + references; Lock Horizon; Strength blend; crop-fit with minimal-zoom / λ-damping under the Cropping Ratio
  cap — Perspective solves as Similarity until phase 7); `AnalysisKey` (± 2 s handles, 5 s buckets, SHA-256
  cache file name); `IMotionTrackProvider`. `EffectTypeIds.Stabilization = "builtin.stabilization"` + 15
  `EffectParamNames`; descriptor in `EffectCatalog.BuiltIns` (category `Video`, `ShortCode = "ST"`,
  `Presets = StabilizationPresets.All`, dropdown/toggle kinds); `StabilizationPresets` (7 presets). Additive
  `ResolvedEffect.SourceTime` (`Timecode`) + `MediaRefId?`, populated in `RenderGraph.ResolveEffectsCore` for
  media / nested / multicam / adjustment layers (multicam binds the *angle's* media + synced source time; the
  public `ResolveEffects` preview path carries the same context). Tests: `Sprocket.Core.Tests/StabilizationTests`
  (catalog id/short-code/order/dropdowns/defaults round-trip/presets; `MotionTrack` round-trip ± points +
  magic/version guards; solver — Smooth Motion lowers jitter variance, adaptive tracks a pan with less lag than
  uniform, Camera Lock ⇒ constant path, Scale Lock ⇒ constant scale + untouched position at position smoothing 0,
  Lock Horizon ⇒ constant angle, Strength 0 ⇒ identity, crop-fit covers every frame within the ratio, applied
  zoom ≤ 1/croppingRatio, determinism; `AnalysisKey` bucketing; `ResolvedEffect` context population), updated
  `ParameterKindTests` allowlist/count, and a `Sprocket.Persistence.Tests` round-trip of the params. The effect
  appears in the Effects browser and renders pass-through (no provider yet). Feature-tracking docs
  (this file, PLAN.md, FEATURES.md, README.md) were created at phase-2 close-out.
- [x] **Phase 3 — Media decode driver + analyzer** (2026-09-21): `Sprocket.Media` gained `GrayFrame` /
  `GrayFramePool` (native GRAY8, caller-chosen size, `VideoFrame`/`VideoFramePool` RAII shape) and
  `MediaSource.TryDecodeNextGray(GrayFramePool, out GrayFrame)` (lazily-created second `SwsScaler`, mirrors
  `TryDecodeNextFrame` incl. seek decode-to-target discard + GPU-download fallback); `AvConst.PixFmtGray8 = 8`.
  `Sprocket.Analysis` gained the `Sprocket.Media` reference and `Motion/MotionTrackAnalyzer.Analyze(request,
  sourceIdentity, from, to, settings, IProgress<double>?, CancellationToken) → MotionTrack`: forced-software
  sequential decode, analysis width 480 px (960 with Detailed, capped at the source width so it never upscales;
  height follows the source aspect, both even), `MotionEstimator` at ~300 / ~600 features, per-frame cancellation,
  progress = fraction of the range decoded, and a post-pass that linearly interpolates the similarity channels of
  sub-8-inlier frames from their nearest reliable neighbours (left flagged, confidence 0). Frames stay in pooled
  native buffers — a `GrayImage` span wraps each without touching the managed heap (§1). Tests: `Sprocket.Media.Tests`
  `TryDecodeNextGray` (downscaled GRAY8 frame size / non-decreasing pts / seek honoured) and
  `Sprocket.Analysis.Tests` `MotionTrackAnalyzerTests` (gated on the ffmpeg CLI + FFmpeg 8 natives — new
  `UsesFFmpegNatives` + win-x64 RID — over `StabFixtures` clips that loop a static `testsrc2` still): static ⇒
  near-identity, moving-crop shake ⇒ integrated translation matches the analytic 2×24 / 2×16 px swing (detrended),
  pumping-zoom ⇒ recovered scale wobble ~0.06, cancellation stops within one frame, and the analysed track survives
  a `Write`/`Read` round-trip.
- [x] **Phase 4 — Render shader + solve cache + provider seam** (2026-09-21): `SkiaEffectPipeline` gained
  `StabilizeSksl` (a projective `float3 r0/r1/r2` output→source map — Similarity now, Perspective-ready — with
  Decal borders), a `case EffectTypeIds.Stabilization` in `BuildEffectShader`, and `HasTransform` extended so a
  stabilized layer's root image tiles Decal (off-source borders read transparent). New
  `public IMotionTrackProvider? MotionTracks` seam: `BuildStabilizationShader` pulls the frame's track (by
  `MediaRefId` + `SourceTime`), solves it through the new per-pipeline `StabilizationSolveCache`
  (`StabilizationSolveCache.cs`; key = track ref + settings snapshot + frame size, cleared past 64 entries),
  binary-searches the cached solution's `FramePts` for this frame's matrix, and folds `Ninv·M·N` so the shader
  maps output canvas coords straight to source canvas coords (the layer rect preserves the source aspect, so
  width-normalising in canvas space matches the solve). A null provider, missing `MediaRefId`, or a not-yet-analysed
  source all render pass-through. `StabilizationSolveCount` exposes the miss count for the zero-re-solve test.
  Tests (`Sprocket.Render.Tests/StabilizationRenderTests`, CPU backend, fake provider): null-provider and
  no-track-yet pass-through, identity track pass-through, Camera-Lock pan removal moves the centre marker to the
  solve-predicted pixel, Scale-Lock (first-frame ref) scales about the centre (centre marker fixed, off-centre
  marker pulled inward to the predicted pixel), and five frames of one clip solve exactly once.
- [x] **Phase 5 — App service, cache, wiring, minimal Inspector row (first end-to-end)** (2026-09-21):
  `App/Stabilization/AnalysisCache` (per-user `%LocalAppData%/Sprocket/analysis`, `SPROCKET_ANALYSIS_DIR` override,
  atomic temp+move write, content-hash `.spmt` name from `AnalysisKey`, `TryRead`/`Write`/`SizeBytes`/`DeleteAll` —
  mirrors `ProxyCache`) and `StabilizationService : IMotionTrackProvider` (a below-normal-priority worker thread over a
  FIFO queue with per-entry generation fencing, keyed by `(MediaRefId, Detailed)`; `Analyze(media, in, out, detailed)`
  adopts a cached track or enqueues, `Cancel`, `StatusOf`, lock-free `TryGetTrack`, `TrackChanged`/`ProgressChanged`
  events, `SourceIdentity` path+size+mtime helper — mirrors `ProxyService`). Analysis runs through an injected
  `IMotionAnalyzer` seam (`MediaMotionAnalyzer` wraps `MotionTrackAnalyzer.Analyze`; a fake drives the tests without
  ffmpeg). Composition root: `MediaBootstrap.Result.Stab` constructed in both factory paths, owned + disposed by `App`
  (startup + session-swap + teardown), threaded into `MainWindow`; `MotionTracks` set on the preview pipeline (new
  `PreviewSurface.MotionTracks` seam, re-applied on pipeline recreate) **and** the export/preview-cache pipelines
  (new optional `IMotionTrackProvider?` arg on `VideoExporter.Export` both overloads + `PreviewRenderer.RenderVideo`,
  passed from all four App call sites). `TrackChanged` → preview repaint + `RenderCacheService.DeleteAll` (the render
  hash doesn't yet reflect tracks, so a coarse invalidation keeps stale pass-through segments from replaying; phase 6
  refines it) — no model mutation, nothing to undo. Inspector: bespoke `BuildStabilizationStatusRow` (status text +
  progress bar + Analyze/Cancel, updated in place via `RefreshStabilizationStatus`/`_stabRefreshers` off the service
  events, no rebuild); `InspectorPanel.SetStabilizationService` injected like `SetLiveAudioMixer`. Tests
  (`Sprocket.App.Tests`, `[Collection("Stabilization analysis cache")]` to serialise the shared env var):
  `StabilizationServiceTests` (queue→ready + TrackChanged, cache adoption without re-analysis, same-range idempotency,
  cancel reverts, stale-completion generation fencing, detailed/standard tracked independently, progress throttle)
  and `AnalysisCacheTests` (round-trip, miss→null, deterministic path, bucket re-use, detailed forks the file,
  DeleteAll). First end-to-end: a clip stabilizes in preview after Analyze, and export/preview-cache pull the same
  cached solve, so they match by construction.
- [x] **Phase 6 — UX** (2026-09-21): **auto-analyze on apply + stale-on-trim** — `MainWindow` subscribes to
  `EditHistory.Changed` and sweeps every stabilized media clip (`Stabilization/StabilizationScan`), calling the
  idempotent `StabilizationService.Analyze` for each; a per-`(source, detail, bucketed-range)` dedup set fires once
  on apply / when a trim moves the used range to a new `AnalysisKey` bucket, and never re-fights an explicit Cancel.
  **Camera-path graph** (`Inspector/CameraPathGraph` + pure `CameraPathGraphMath`): four lanes (Pan/Tilt/Zoom/
  Rotation) plotting the solved raw (dim) vs smoothed (accent) path with a playhead marker, plus an **Applied Zoom**
  readout + low-confidence note; the Inspector solves via `StabilizationSolver.Solve` off the ready track (new shared
  `StabilizationSettings.FromParameters`). **Monitor banners + Show Track Points** (`MonitorOverlay.DrawBanner` /
  `DrawTrackPoints`, threaded through `PreviewSurface.DrawOp` and driven by `MainWindow.UpdateStabilizationBanner`
  off the selected clip + playhead; `hideBanner` / `showTrackPoints` toggles honored). **Placement rule** — the
  Inspector `+ Effect` bar inserts Stabilization after a Color Transform else at index 0 (`PlacementCommand`), with a
  "Transform precedes this" Inspector hint; **`EffectRelevance` media-only** (offered only for `ClipKind.Media`).
  **View ▸ Background Tasks** — `ProxyStatusWindow` generalised (renamed, menu relabeled) with a second section
  listing stabilization analyses (queued/running/ready/failed) each cancellable, fed by new
  `StabilizationService.Snapshot()`. **Media bin ▸ Analyze for Stabilization** (`MediaBrowserPanel`
  `AnalyzeForStabilizationRequested` → whole-source pre-warm). **Export pre-check** (`StabilizationExportPrecheck`)
  prompts "Analyze first / Export as-is" for unanalyzed stabilized clips and awaits them; the analysis worker is
  **paused for the duration of every export/render** (new `StabilizationService.SetPaused`, mirroring `ProxyService`,
  requeuing the in-flight item) so a second FFmpeg pipeline never races the export. **Preferences ▸ Clear analysis
  cache** (`AnalysisCache.SizeBytes`/`DeleteAll`). **MCP**: `stabilization_status` / `stabilization_analyze` tools
  (source-level, via new `IEditorApi.StabilizationInfoForClip` / `AnalyzeStabilizationForClip`). Tests:
  `CameraPathGraphMathTests` (range/polyline), `StabilizationScanTests` (enumeration, export pre-check dedupe,
  auto-enqueue + stale re-analyze, pause/resume), `StabilizationToolsTests` (MCP round-trip), updated tool-surface
  count. `LowConfidenceFrames` counts flagged frames off the ready track.
- [x] **Phase 7 — Perspective, quality tier, docs, close-out** (2026-09-21): the real
  Perspective/homography path in `StabilizationSolver` — it integrates the tracked homography's perspective row
  (re-expressed in the solve's centred coords by `CentredProjectiveRow`, `H_c = T⁻¹·H·T`) on top of the similarity
  channels, smooths + Strength-blends it, and folds its residual into `BuildMatrix`'s third row (`Covered` /
  `MinimalZoom` / `MaxLambda` now map corners through the perspective divide, so the crop budget is exact for the
  projective warp). Translation / Similarity keep the projective channels at zero ⇒ bit-for-bit the phase-2 solve.
  Low-confidence frames now interpolate their homography element-wise (`MotionTrackAnalyzer.LerpHomography`) instead
  of snapping to identity, so a Perspective solve stays continuous across a gap while the frames stay flagged.
  Detailed Analysis verified end-to-end (settings → 960 px + doubled feature cap → `MotionTrack.DetailedAnalysis` →
  distinct `AnalysisKey` / render lookup); low-confidence already surfaced (phase 6 banner + `LowConfidenceFrames`).
  User docs `../sprocket-docs/effects-color/stabilization.md` (note: the docs site groups video + colour effects in
  one `effects-color` folder — there is no `effects-video`). FEATURES ✅ + Docs path; PLAN todo `[x]`; README Features
  bullet added + Planned fragment removed; DONE log in [plan/history/steps-58plus.md](../history/steps-58plus.md).
  Tests: Core Perspective↔Similarity equivalence / projective-engaged + crop-fit + determinism / focus-breathing
  preset flattens a scale pump; Analysis ffmpeg fixtures — Detailed flag + feature count, and the focus-breathing
  preset removes the pump on the pumping-zoom clip end-to-end. Full `dotnet test` green.

## Tests

- **Analysis (`Sprocket.Analysis.Tests`):** synthetic warp recovery, outlier rejection, determinism, allocation.
- **Core (`Sprocket.Core.Tests`):** catalog id/short-code/order/defaults/presets; `MotionTrack` round-trip +
  guards; every solver mode; `AnalysisKey` bucketing; `ResolvedEffect` context population. **Persistence
  (`Sprocket.Persistence.Tests`):** params round-trip (free — id + parameter map).
- **Media/Analysis (phase 3):** static, shaking, and pumping-zoom ffmpeg fixtures.
- **Render (phase 4):** pass-through, translate, scale-lock, null provider, zero-alloc.
- **App (phases 5–6):** service, keys, auto-enqueue, export pre-check, graph math, MCP.

## Fixes after completion

- **2026-09-22 — phantom pan from uncentred similarity translation.** The estimator fits each inter-frame
  similarity about the frame's top-left origin (`dst = S·R·src + t`), but the solver integrated `Tx/Ty` as if
  they were centre-referenced pan/tilt (only the homography's perspective row was being re-centred). A zoom or
  roll about the centre therefore leaked `(S·R − I)·c` into the position channel — on a real focus-breathing
  clip about half the measured "pan" energy — and every mode with Position Smoothing > 0 (Smooth Camera,
  Tripod) then *corrected* that phantom pan, shifting the frame in step with the scale change. Symptom: a
  stabilized breathing shot still looked like it was breathing; Tripod added jitter (measured 0.0158 → 0.0262
  Σ|tx| on the user's clip; after the fix 0.0049). Fixed in `StabilizationSolver` (`CentredTranslation`,
  `t_c = t + (S·R − I)·c`) — no change to the on-disk track format, so caches stay valid. Tests: Core
  (centred zoom / roll ⇒ zero pan; true pan unchanged), Render (`CentredZoom` helper encodes the estimator's
  convention), Analysis (Camera Lock on the zoompan fixture adds no pan).

- **2026-09-22 — framing budget over the clip's used range only.** The analysed track covers the clip's range
  padded to 5 s cache buckets (+2 s handles), so it can include footage the clip trimmed away — and the framing
  solve (`Covered` / `MinimalZoom` / `MaxLambda`), the Camera Lock mean, and the scale-lock references were taken
  over the *whole* track. A settling jolt in the first second of a take therefore forced a bigger zoom (or λ < 1,
  softening the lock across the entire clip) on a clip that had cut it out. `ResolvedEffect` now carries
  `SourceIn/SourceOut` (the render graph fills them from the clip, angle-mapped for multicam), the solver takes an
  optional used range (frames from the one shown at the in-point to the one shown at the out-point — the
  renderer's own lookup), and the Render solve cache keys on it. Smoothing still sees the whole track (no edge
  effects at the cut). Inspector graph/readout use the same range. Tests: Core (jolt outside the used range costs no
  zoom; lock target is the used-range mean; null range = whole track; `ResolveEffects` carries the range).

## Follow-ons (out of scope for v1)

Subspace Warp (mesh), Synthesize Edges (temporal inpaint), Rolling Shutter (FCP-style separate effect),
L1-optimal cinematographic paths, gyro-metadata stabilization (GoPro GPMF / DJI / BMD, Resolve-style),
stabilize-on-subject via the mask tracker, import-time "excessive shake" flagging (FCP), GPU LK.

## On completion

Per phase: tick the phase box above with the date, keep the FEATURES.md tracking honest (it stays in the
Planned section until phase 5 makes the effect user-visible, then moves to the §4 matrix), and commit.
After phase 7: flip this feature's PLAN.md todo, append the DONE log to `plan/history/steps-58plus.md`,
set the FEATURES.md row ✅ with the Docs path, add the README bullet — then delete or archive the
no-longer-open parts of this file.
