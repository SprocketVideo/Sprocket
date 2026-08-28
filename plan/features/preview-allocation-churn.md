# Preview allocation churn (per-frame metadata / wrapper Gen0)

❌ **Not started.** Measure, then remove, the remaining per-frame *managed metadata and wrapper*
allocations in the live-preview path so playback approaches the "~0 Gen0 per frame" target. Tracked
in [PLAN.md](../../PLAN.md) as step 60 / Open work. Relative links resolve from the repo root.

## Why / context

A static audit (2026-08-28, no profiler run) claimed the preview and render paths still allocate
managed objects every frame. Every cited line was validated against the source; the verdicts:

| Finding | Verdict | Severity (corrected) | Notes |
|---|---|---|---|
| Effect parameter `Dictionary` + `ResolvedEffect` + array per **enabled** effect per repaint — `RenderGraph.cs:542/547/549`, `RenderPlan.cs:12`; called from `PlaybackEngine.UseLayers:565` and `UseCurrentFrame:628` | Confirmed | **High** (effected clips only) | Clips with no enabled effects allocate nothing (`RenderGraph.cs:534-547`, lazy `??=`). |
| Generator numeric + string `Dictionary` + `ResolvedGenerator` per repaint — `RenderGraph.cs:524/528/529`, `RenderPlan.cs:37`; called from `UseLayers:571` | Confirmed | **High** | **No zero-parameter fast path** — both dictionaries are built even for parameter-less generators. |
| `PlanVideoFrame` builds `List<VideoLayer>` + record-class plan per call — `RenderGraph.cs:49/73`, `RenderPlan.cs:99/123/144` | Confirmed | **Low** (audit said High) | Live preview never calls it: callers are `VideoExporter.cs:485`, nested recursion `RenderGraph.cs:176`, and the render cache via `PreviewRenderer.cs:56 → VideoExporter.Export`. Those are throughput-bound, not frame-deadline-bound. Only matters if preview is ever unified onto the planner. |
| Skia wrappers per layer/effect per draw — `SKImage.FromPixels` (`SkiaEffectPipeline.cs:449`), chain root shader (`:559/:566`), `SKRuntimeEffectUniforms` + `Children` + `SKShader` per effect (`:769/780/787/834/866/886/929`), `float[]` uniforms for wipe/transform (`:623/931/932`) | Confirmed | **High** (scales layers × effects) | Missed mitigations: the 10 `SKRuntimeEffect` programs are compiled once (`:333-342`); single reused `SKPaint` (`:343`, `ResetPaint:743`); no-effects fast path skips the chain (`:478-485`); plugin effects cached in `_registeredCache` (`:826-832`); LUT image process-cached; scratch lists reused (`:344-345`). The unmodified single-clip path allocates only the `SKImage` wrapper. |
| Pump `async Task` methods per frame / per track — `PlaybackEngine.cs:643/714/773`, `VideoTrackPlayer.cs:113` | Partially confirmed | **Low** (audit said Medium) | An `async Task` that completes without suspending returns a cached completed `Task` (and `Task<bool>` true/false are cached) — no state-machine box. `WaitForNextFrameAsync` only awaits `Task.Delay(1)` far from the deadline (`:734-740`) and spins synchronously near it (`:745`); `PumpOnceAsync` awaits `ReconcilePlayersAsync` only when `_reconcile` is set (`:786-787`); the feed is already `ValueTask` (`IVideoFrameFeed.cs:22`). Residual: `Task.Delay(1)` allocates a timer + Task when taken. |
| `UseLayers` fresh `List<PresentedVideoLayer>` per draw (`PlaybackEngine.cs:556`) + capturing lambda per `Render` (`PreviewSurface.cs:170`) | Confirmed | **Medium** | `PresentedVideoLayer` is a `readonly record struct` (`PlaybackEngine.cs:44`): one backing array + one closure per frame. The render-cache fast path (`:548-553`) skips the list but allocates a one-element array literal. |
| Interface `foreach` heap-allocates enumerators — `PreviewSurface.cs:203`, `SkiaEffectPipeline.cs:571` | Confirmed | **Low–Medium** | Both static types are `IReadOnlyList<T>`. `HasTransform` (`SkiaEffectPipeline.cs:750`) already uses the allocation-free index-`for` pattern to copy. |
| Synthetic layers — nested-placeholder `SKPaint`s (`PreviewSurface.cs:268/270`), generator offscreen `SKSurface` + `Snapshot` (`SkiaEffectPipeline.cs:671/677`), adjustment `Snapshot` (`:710`) | Confirmed | **Low** | Per nested/generator/adjustment layer, not every layer. Surfaces are GPU-side; the managed wrapper is the churn. |
| No managed pixel copy — `FramePresenter.cs:65`, `SkiaEffectPipeline.cs:449` | Confirmed good | — | `SKImage.FromPixels` references native memory. The [ARCHITECTURE §1](../../ARCHITECTURE.md) rule holds. |

**Assessment.** The §1 pixel rule is intact. What remains is *metadata/wrapper* churn that is near
zero on the plain single-clip path (one `SKImage` wrapper, one list, one closure) and grows with
effected / generator / adjustment layers. It has **never been measured**: no allocation test exists
under `tests/`, and the only recorded measurement ([performance-log](../history/performance-log.md),
2026-06-30) saw 0 GC collections on a plain clip but did not measure allocation *rate*. The ranking
above is therefore by code shape — measurement is deliverable zero.

## Existing seams to build on

- `RenderGraph.ResolveEffects` / `ResolveGenerator` (`src/Sprocket.Core/Rendering/RenderGraph.cs`) —
  pure functions of (clip, time); any cache must live in Playback, not Core, to keep the graph pure.
- `PlaybackEngine.UseLayers` (`src/Sprocket.Playback/PlaybackEngine.cs:538`) — the single per-repaint
  assembly point under `_frameGate`.
- `SkiaEffectPipeline.BuildChainShader` / `BuildEffectShader`, the `_scratch` lists,
  `_registeredCache`, and the `HasTransform` index-loop pattern (`src/Sprocket.Render/SkiaEffectPipeline.cs`).
- `PlaybackStatsOverlay` + `PlaybackEngine.GetStatistics()` — already report GC counts; add bytes/frame.

## Implementation sketch (ordered by payoff / risk)

0. **Measure first.** Headless xUnit test (Playback.Tests or Render.Tests) driving `UseLayers` /
   `SkiaEffectPipeline.DrawLayer` for N frames over fixtures with 0, 1, and 3 effects plus a generator,
   bracketed by `GC.GetAllocatedBytesForCurrentThread()`, asserting a per-frame byte ceiling (loose at
   first, tightened as items land). Add an allocation-rate (bytes/frame) line to the Playback
   Statistics overlay. Record the baseline in `plan/history/performance-log.md`.
1. **Stop rebuilding effect/generator parameter state per repaint** (High). Cache `ResolvedEffect[]` /
   `ResolvedGenerator` per clip in the Playback layer, keyed on clip version + (for keyframed params)
   evaluated time; non-keyframed resolution is time-invariant so it hits every frame. Add the missing
   zero-parameter fast path to `ResolveGeneratorCore`.
2. **Reduce per-effect Skia wrapper churn** (High). Reuse `SKRuntimeEffectUniforms` / `Children` per
   compiled effect (mutate in place); replace `new float[]` uniform arrays with reused fields; convert
   `SkiaEffectPipeline.cs:571` and `PreviewSurface.cs:203` to index-`for`. Keep `SKShader` per draw
   (cheap, disposed via `_scratch`) unless measurement disagrees.
3. **`UseLayers` scratch buffer + non-capturing callback** (Medium). Engine-owned reusable list cleared
   per call; a `UseLayers<TState>(TState, Action<TState, ReadOnlySpan<PresentedVideoLayer>>)` overload
   so `PreviewSurface` passes canvas/bounds without a closure.
4. **Pump async shape** (Low, measure-gated). Only if step 0 shows Task churn: `ValueTask` on
   `PumpOnceAsync` / `VideoTrackPlayer.PumpAsync`; a reusable timer instead of `Task.Delay(1)`.
5. **Synthetic-layer wrappers** (Low). Cache placeholder `SKPaint`s as fields; consider a pooled
   offscreen `SKSurface` sized to the sequence resolution for generator/adjustment layers.
6. **`PlanVideoFrame` churn — explicitly deferred.** Revisit only if preview is unified onto the planner.

**Non-goal:** no change to §1 pixel handling — it is already correct.

## Tests

The step-0 allocation test is the regression gate; existing golden-frame tests
(`tests/Sprocket.Render.Tests`, export goldens) guard that caching changes no pixels.

## On completion

Flip the step 60 ledger row / check the todo in PLAN.md, append the DONE log under `## Step 60` in
`plan/history/steps-58plus.md`, record measured before/after bytes-per-frame in
`plan/history/performance-log.md`, and archive this file. No FEATURES.md row (internal perf work).
