# Export speed / throughput

🟡 **Phase 1 done (2026-09-22)**; phases 2–5 open. Improve export throughput in two deliberate tiers: keep a deterministic
**Final Export** path for golden-frame parity, and add an explicitly speed-first **Fast Export**
mode that may trade determinism for wall-clock speed. Tracked in [PLAN.md](../../PLAN.md) Open work.
Relative links resolve from the repo root.

## Why / product context

Export currently under-delivers on user expectations for a modern editor: choosing hardware encoding
does not reliably translate into visibly faster exports, and Task Manager often shows both CPU and GPU
mostly idle while a short sequence takes minutes. Leading editors separate these concerns more clearly:

| Editor | What it does well | What we take |
|---|---|---|
| **Premiere Pro** | Exposes software vs hardware encode clearly, reports the actual encoder path, and offers preview-file / smart-render workflows for faster drafts. | Surface the **actual** encoder engaged, not just the requested mode; allow cache reuse only in explicitly draft/fast workflows. |
| **DaVinci Resolve** | Treats export speed as a pipeline problem (decode, effects, render, encode) rather than just an encode setting; separates quality/fidelity choices from fast review outputs. | Split our plan into **Final Export** vs **Fast Export** rather than weakening the current deterministic path. |
| **Final Cut Pro** | Makes background render / cached media materially useful to downstream delivery and keeps performance tooling understandable. | Reuse valid cached ranges only in an opt-in fast/draft mode, and add stage-level timing/diagnostics so users can see what is slow. |

**Deliberate product stance**
- **Final Export** remains the reference output: deterministic, full-resolution, software-decode-backed,
  and suitable for golden-frame tests.
- **Fast Export** is an explicit speed mode: hardware decode/render/encode and cached ranges are allowed,
  with documented caveats that tiny output differences vs Final Export are acceptable.
- The UI must stop implying that "Hardware (if available)" accelerates the whole export when, today,
  it only swaps the encoder backend and may silently fall back.

## Existing seams to build on

| Piece | Where | Use |
|---|---|---|
| Offline export orchestrator | [src/Sprocket.Export/VideoExporter.cs](../../src/Sprocket.Export/VideoExporter.cs) | Main control point for progress, cancellation, range handling, audio/video interleave, and export-mode branching. |
| Video encoder facts | [src/Sprocket.Media/MediaEncoder.cs](../../src/Sprocket.Media/MediaEncoder.cs) | Already exposes `IsHardwareVideo` and `VideoEncoderName`; use these to surface actual encoder engagement and software fallback. |
| Hardware encoder selection policy | [src/Sprocket.Export/ExportFormat.cs](../../src/Sprocket.Export/ExportFormat.cs) | Existing `HardwareEncoderCandidates(...)` / platform vendor order define the current encode probe chain; diagnostics should report which candidate won or whether all fell back. |
| Full-res export decode seam | [src/Sprocket.Export/ExportFrameProvider.cs](../../src/Sprocket.Export/ExportFrameProvider.cs) | Current deterministic full-res, software-decode provider; preserve this for Final Export and introduce a speed-first alternative for Fast Export. |
| Render/effect pipeline | [src/Sprocket.Render/SkiaEffectPipeline.cs](../../src/Sprocket.Render/SkiaEffectPipeline.cs) | Instrument effect/render timing here and decide how many independent pipeline instances a pipelined exporter can own safely. |
| Render cache seams | [src/Sprocket.Core/Rendering/RenderCacheSeams.cs](../../src/Sprocket.Core/Rendering/RenderCacheSeams.cs) | Export currently ignores these by design; Fast Export / Draft Export can opt in without weakening Final Export. |
| Export UI + queue | [src/Sprocket.App/Dialogs.cs](../../src/Sprocket.App/Dialogs.cs), [src/Sprocket.Export/ExportQueue.cs](../../src/Sprocket.Export/ExportQueue.cs), [src/Sprocket.App/ExportQueueWindow.cs](../../src/Sprocket.App/ExportQueueWindow.cs) | Add mode selection, actual encoder/timing summaries, and queue-visible diagnostics. |
| Audio mix gate | [src/Sprocket.Export/VideoExporter.cs](../../src/Sprocket.Export/VideoExporter.cs) `HasAudibleAudio(...)` | Tighten "needs audio work" logic so muted/solo-excluded states do not trigger unnecessary export mixing. |

## Implementation sketch

1. **Observability first.** Add an export diagnostics object that records requested mode, actual encoder,
   hardware fallback, and stage timings (decode / render / encode / audio / total). Wire it through the
   export queue and completion UI before changing throughput behavior.
2. **Cheap correctness/perf wins.** Fix audio gating so muted-only or solo-excluded timelines do not pay
   unnecessary audio mix/encode costs; expose these decisions in diagnostics so perf measurements are clean.
3. **Final Export pipelining.** Refactor the current one-frame-at-a-time loop into a bounded staged pipeline
   that overlaps decode, render, and encode while preserving deterministic output ordering. Keep the current
   software-decode provider and full-res originals here.
4. **Fast Export mode.** Add a separate export-mode switch that allows hardware decode, GPU-backed rendering,
   and non-deterministic vendor paths. Hardware failure must degrade cleanly with user-visible reporting.
5. **Cache-aware draft delivery.** Let Fast Export / Draft Export reuse valid preview-render-cache and audio
   cache ranges where coverage is exact. Final Export continues to ignore caches.
6. **Cost attribution.** Add optional per-effect / per-clip timing so support and future tuning can identify
   clips dominated by stabilization, grain, or decode rather than hand-waving at "hardware encoding".
7. **Docs + inventory.** Because Fast Export is user-visible, shipping it should add or amend the relevant
   [FEATURES.md](../../FEATURES.md) row and check whether [README.md](../../README.md)'s coarse export feature
   summary needs the same update. Final Export pipelining alone is internal perf work and needs no row.

## Build phases

Too large for one session — **five independently mergeable phases**, ordered so we measure before we optimize
and keep the riskier behavior changes behind instrumentation.

| Phase | Ships | Depends | Size |
|---|---|---|---|
| 1 | Export diagnostics + actual encoder reporting + audio-gating cleanup | — | 1 session |
| 2 | Deterministic Final Export pipeline overlap (decode/render/encode) | 1 | 1–1.5 |
| 3 | Fast Export mode surface + hardware-decode/render path + fallback reporting | 1 | 1–1.5 |
| 4 | Cache-aware Fast/Draft Export reuse | 2, 3 | 1 |
| 5 | Per-effect cost attribution + docs / FEATURES / README close-out | 1–4 | 0.5–1 |

- [x] **Phase 1 — Observability and cheap wins.** ✅ 2026-09-22 — DONE log in
  [plan/history/steps-58plus.md](../history/steps-58plus.md#export-speed--phase-1-unscheduled-feature-2026-09-22--done); the manual `Psycho.json`
  baseline for performance-log.md is still to be recorded. Add an `ExportDiagnostics` / `ExportRunSummary` shape in
  `Sprocket.Export`, timestamp the current stages in `VideoExporter`, surface `MediaEncoder.IsHardwareVideo`
  + `VideoEncoderName` on completion, and plumb the summary through the queue/UI. Tighten audio gating so muted
  or solo-excluded timelines skip audio work. Acceptance: the export completion path can tell the user
  "requested hardware, actual encoder = X, hardware engaged = yes/no, total/decode/render/encode/audio ms = ...".
- [ ] **Phase 2 — Deterministic Final Export pipelining.** Replace the single loop in `VideoExporter.Export(...)`
  with a bounded staged pipeline that overlaps decode, render, and encode while preserving output order and
  cancellation semantics. Start conservatively: one render worker, overlapped with prefetch/decode and encode,
  then widen only if measurements justify it. Acceptance: golden-frame parity with today's Final Export and a
  measurable wall-clock improvement on 1080p effect-heavy fixtures.
- [ ] **Phase 3 — Fast Export mode.** Add a user-facing export mode selector in the export dialog and queue model:
  `Final Export` vs `Fast Export`. Fast Export may use hardware decode and a GPU-backed render surface and may
  produce small vendor-dependent differences, but it must report the actual path used and fall back cleanly when
  hardware init fails. Acceptance: clear UI copy, no silent degradations, and tests that prove fallback behaves.
- [ ] **Phase 4 — Cache-aware draft delivery.** Teach Fast Export / Draft Export to consult
  `IVideoRenderCache` / `IAudioRenderCache` for exact-coverage ranges and splice cached ranges into the output.
  Final Export remains cache-blind. Acceptance: exact-coverage only, no stale partial reuse, and a clear user
  distinction between cache-assisted draft speed and full re-rendered final output.
- [ ] **Phase 5 — Cost attribution and close-out.** Add optional effect-level timing hooks in
  `SkiaEffectPipeline` and export summaries so "stabilization vs grain vs decode" costs are visible. Ship docs,
  update [FEATURES.md](../../FEATURES.md) for Fast Export, check [README.md](../../README.md), and append the DONE
  log to `plan/history/steps-58plus.md` or a future numbered step history if this work is later promoted.

## Phase 1 code-level task list

Phase 1 changes contracts but not export pixels or scheduling. Keep every current `void Export(...)` overload as a
compatibility wrapper; the new result-returning entry point owns the implementation so existing preview-cache and
call-site behavior remains unchanged.

### 1. Add the result model (`Sprocket.Export`)

Add `src/Sprocket.Export/ExportRunSummary.cs`:

```csharp
public readonly record struct ExportStageTimings(
    TimeSpan VideoDecode,
    TimeSpan VideoRender,
    TimeSpan VideoEncode,
    TimeSpan AudioMix,
    TimeSpan AudioEncode,
    TimeSpan Total);

public sealed record ExportRunSummary(
    ExportAcceleration RequestedAcceleration,
    string RequestedVideoEncoder,
    string ActualVideoEncoder,
    bool HardwareVideoEngaged,
    long VideoFrames,
    long AudioSampleFrames,
    ExportStageTimings Timings)
{
    public bool FellBackToSoftware =>
        RequestedAcceleration == ExportAcceleration.Hardware && !HardwareVideoEngaged;
}
```

Rules:
- `RequestedVideoEncoder` is the software-family selection from `VideoCodecInfo.EncoderName` (for example
  `libx264`); requested acceleration remains a separate fact because the hardware probe is an ordered candidate
  chain, not one guaranteed encoder name.
- `ActualVideoEncoder` comes only from `MediaEncoder.VideoEncoderName` after open.
- `HardwareVideoEngaged` comes only from `MediaEncoder.IsHardwareVideo`; never infer it from a name suffix.
- Audio-only exports use `ActualVideoEncoder = ""`, `HardwareVideoEngaged = false`, `VideoFrames = 0`, and zero
  video timings.
- Use elapsed monotonic time (`Stopwatch.GetTimestamp` / `Stopwatch.GetElapsedTime`), never wall-clock timestamps.

### 2. Add a measured export entry point (`VideoExporter`)

Keep the two current public `void Export(...)` overloads and make them discard the measured result. Add:

```csharp
public static ExportRunSummary ExportWithSummary(
    Project project,
    string outputPath,
    ExportOptions options,
    SequenceId? sequenceId,
    ExportRange? range,
    IProgress<double>? progress = null,
    CancellationToken cancellationToken = default,
    IMotionTrackProvider? motionTracks = null);
```

Optionally add the active-sequence convenience overload only if a real caller needs it; do not multiply overloads
speculatively. `ExportWithSummary(...)` becomes the implementation body now held by the long `Export(...)`
overload. Return the summary only after `encoder.Finish()` succeeds; cancellation/failure continues to throw and
delete the partial output, so there is no misleading "completed run" summary for a failed file.

Timing boundaries inside `VideoExporter`:
- **Total:** immediately after argument validation / sequence resolution through successful `Finish()`; document
  whether setup is included (recommended: yes, because device/encoder open latency is user-visible).
- **Video decode:** accumulated by `ExportFrameProvider` around only `MediaSource.SeekTo`,
  `TryDecodeNextFrame`, and reverse-window fill work.
- **Video render:** elapsed around `RenderVideoFrame(...)` minus the providers' decode-time delta for that call;
  includes render-graph resolution, effect setup, raster compositing, burn-ins, and flush.
- **Video encode:** `surface.PeekPixels()` plus `MediaEncoder.WriteVideoFrame(...)`; include RGBA-to-encoder-format
  conversion and any device upload because both are part of the encode handoff.
- **Audio mix:** `AudioMixer.MixInto(...)` only.
- **Audio encode:** `MediaEncoder.WriteAudioFrame(...)` only.
- `Finish()` / mux trailer time is included in `Total` but not falsely assigned to a per-frame stage in Phase 1.
  A separate mux/finalize field can be added later if measurement shows it matters.

Add internal measurement to `ExportFrameProvider` without putting timing on the hot path when diagnostics are not
requested (all Phase-1 production exports request it, but tests may construct the provider directly):

```csharp
internal TimeSpan DecodeElapsed { get; private set; }
internal long DecodeOperations { get; private set; }
```

Use one private helper around the actual decode/seek operation rather than timing the whole `GetFrame(...)` call;
cache hits must not be reported as decode time. Reverse GOP refill counts as decode. Do not add per-frame managed
collections or pixel copies.

Change the private audio-only implementation to return `ExportRunSummary`:

```csharp
private static ExportRunSummary ExportAudioOnly(...);
```

### 3. Make audio gating match render-graph audibility

Replace `VideoExporter`'s current source-presence-only `HasAudibleAudio(...)` recursion with one shared pure helper
on the render-graph authority:

```csharp
public static bool HasAudibleAudio(Project project, Sequence sequence);
```

Add it to `RenderGraph` beside `PlanAudioBuffer(...)`. Its recursive traversal must match planner admission rules:
- disabled, muted, and non-solo tracks are excluded (`anySolo` follows `PlanAudioBufferCore` exactly);
- disabled clips are excluded;
- ordinary media requires `MediaRef.Info.HasAudio`;
- nested-sequence clips recurse with the existing cycle/depth guards;
- multicam clips inspect the active angle's effective audio media reference;
- an enabled audible track with no admitted clips is false.

Then change the export gate to:

```csharp
bool wantAudio = !options.VideoOnly && RenderGraph.HasAudibleAudio(project, sequence);
```

This helper answers whether the sequence contains any audible material over its timeline; it does not scan every
export-range instant or instantiate effects. Range-specific pruning is deferred unless profiling shows meaningful
audio cost for ranges containing no active audio.

### 4. Carry summaries through the export queue

Change the runner contract in `ExportQueue.cs`:

```csharp
public delegate ExportRunSummary ExportJobRunner(
    ExportJob job,
    IProgress<double> progress,
    CancellationToken cancellationToken);
```

Add to `ExportJob`:

```csharp
public ExportRunSummary? Summary { get; internal set; }
```

Queue lifecycle rules:
- clear `Summary` when a queued job enters `Running`;
- assign `_runner(...)`'s return value before setting `Succeeded`;
- leave `Summary` null for `Queued`, `Running`, `Cancelled`, and `Failed` jobs;
- mutate `Summary`, `Status`, `Progress`, and `Error` under `_gate` as one terminal transition so UI snapshots do
  not observe `Succeeded` without its summary.

Update `MainWindow.EnsureExportQueue()` to return the measured result:

```csharp
_exportQueue ??= new ExportQueue((job, progress, ct) =>
    VideoExporter.ExportWithSummary(
        _project!, job.OutputPath, job.Options, job.SequenceId, job.Range, progress, ct, _stab));
```

Fake queue runners in tests return a small deterministic summary from a shared test helper; do not make
`ExportRunSummary` nullable merely to save test syntax.

### 5. Surface the result in App entry points

In `MainWindow.ExportAsync()`, capture the result from `Task.Run`:

```csharp
ExportRunSummary? summary = null;
summary = await Task.Run(() => VideoExporter.ExportWithSummary(...));
```

On success:
- status bar: `Exported with h264_nvenc in 00:42 -> path` (actual encoder; audio-only omits it);
- completion dialog: append a compact diagnostics block containing actual encoder, hardware/software, elapsed,
  and average export fps (`VideoFrames / Timings.Total.TotalSeconds` when video frames > 0).

Add one App-only pure formatter, rather than embedding formatting in controls:

```csharp
internal static class ExportSummaryText
{
    public static string Compact(ExportRunSummary summary);
    public static string CompletionDetails(ExportRunSummary summary);
}
```

Use invariant/stable formatting in the helper so `Sprocket.App.Tests` can assert exact text. Queue rows call
`ExportSummaryText.Compact(job.Summary)` after success and retain the existing status text for all other states.

In `RunMcpExportAsync(...)`, call `ExportWithSummary(...)` and retain the summary in a new nullable field so a
later MCP payload extension does not require re-running exports. Extending the public MCP response is optional in
Phase 1; if done, make fields additive.

`PreviewRenderer.RenderVideo(...)` continues to call the compatibility `void Export(...)` wrapper: preview-cache
generation does not need user-facing delivery diagnostics in Phase 1.

### 6. Tests and validation

Add/update these focused tests:

**`Sprocket.Core.Tests`**
- `HasAudibleAudio_MutedOnly_ReturnsFalse`
- `HasAudibleAudio_SoloExcludesOtherwiseAudibleTrack`
- `HasAudibleAudio_DisabledClip_ReturnsFalse`
- `HasAudibleAudio_NestedSequence_Recurses`
- `HasAudibleAudio_Multicam_UsesActiveAngleAudio`

**`Sprocket.Export.Tests`**
- queue success stores the runner's exact summary before raising the final `Changed` state;
- failed/cancelled jobs have null summaries;
- existing ordering/progress/cancellation tests updated for the result-returning runner;
- audio-only measured export returns zero video facts/timings (environment-gated with existing FFmpeg fixture
  conventions);
- one video integration test asserts `ActualVideoEncoder` is non-empty and frame/sample counts are plausible;
  do not assert hardware engagement on CI.

**`Sprocket.App.Tests`**
- `ExportSummaryText` formats hardware engagement, software fallback, software-requested, and audio-only cases;
- queue-row text can be covered through the formatter without UI automation.

Validation commands after each implementation slice:

```powershell
dotnet test tests/Sprocket.Core.Tests/Sprocket.Core.Tests.csproj --filter "FullyQualifiedName~HasAudibleAudio"
dotnet test tests/Sprocket.Export.Tests/Sprocket.Export.Tests.csproj --filter "FullyQualifiedName~ExportQueue"
dotnet test tests/Sprocket.App.Tests/Sprocket.App.Tests.csproj --filter "FullyQualifiedName~ExportSummaryText"
dotnet test Sprocket.slnx
```

Manual acceptance on the attached `Psycho.json` project:
1. Export H.264 with `Hardware (if available)` and record total/decode/render/encode times.
2. Confirm the completion dialog names the actual encoder (`h264_nvenc`, `h264_qsv`, `h264_amf`, or `libx264`).
3. Confirm a software fallback is stated explicitly rather than implied to be hardware.
4. Confirm the muted A1 track produces `AudioSampleFrames = 0` and zero audio stage timings.
5. Record the Phase-1 baseline in [plan/history/performance-log.md](../history/performance-log.md); Phase 2 uses
   this exact project/result shape to prove pipeline overlap improved wall-clock time.

## Tests

- `tests/Sprocket.Export.Tests`: export diagnostics population, audio-skip gating, cancellation cleanup,
  frame-order correctness under pipelining, and final-output reopen/smoke assertions.
- `tests/Sprocket.Media.Tests`: hardware-encoder fallback reporting and any new speed-first decode-provider
  behavior behind fakes or environment-gated integration tests.
- `tests/Sprocket.Render.Tests`: render/effect timing hooks do not change pixels; effect-heavy fixtures still
  match goldens on Final Export.
- `tests/Sprocket.App.Tests`: export-dialog mode selection and queue-summary formatting where practical.

Measurement gate before widening scope:
- record baseline and after-each-phase wall-clock timings in [plan/history/performance-log.md](../history/performance-log.md)
  for at least one 1080p stabilization-heavy project and one grain/effect-heavy project.

## On completion

Check the PLAN.md todo, append the DONE log to `plan/history/steps-58plus.md` (or the matching numbered-step
history file if this later gains a ledger row), update [FEATURES.md](../../FEATURES.md) for the shipped
Fast Export user-facing capability, and then archive or trim the no-longer-open parts of this file.