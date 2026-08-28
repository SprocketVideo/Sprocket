# Sprocket — Cross-Platform Video Editor on .NET 10 — Feasibility & Vertical-Slice Plan

> See [BRIEF.md](BRIEF.md) for the feature brief, [ARCHITECTURE.md](ARCHITECTURE.md) for the
> technical design, and [UI.md](UI.md) for the target UI and the features its mockup implies.

## Context

Greenfield project (empty repo). The goal is a cross-platform (Windows 10 & 11 + Linux + macOS)
non-destructive video editor in C# / .NET 10 with multiple video & audio tracks,
hardware-accelerated decode/encode, GPU effects (brightness/color/contrast), fades,
audio volume mixing, and an eventual plugin system, leveraging OSS (FFmpeg, Skia) for
the heavy lifting.

**The gating question — "can C# deliver the performance?" — is answered: yes**, provided
C# is used purely as an *orchestrator* and pixel data never lands on the managed heap per
frame. The compute-heavy work is delegated to FFmpeg (C) and GPU shaders; C# owns the
timeline model, scheduling, render graph, UI, and A/V sync. Existence proof: FramePFX
(C#/Avalonia/FFmpeg/SkiaSharp). This is the standard "managed orchestration + native/GPU
compute" pattern.

### Decisions locked in
- **Preview:** 1080p (or proxy) real-time preview; export at full source resolution.
- **GPU stack:** SkiaSharp-first (Avalonia already renders via Skia; GPU-accelerated 2D
  compositing + shader effects). Drop to raw GPU (Silk.NET/Vulkan) later only for measured hotspots.
- **First milestone:** Vertical slice — 1 video track + 1 audio track, import, trim, one
  effect (brightness), a fade, playback, export.
- **OS-specific code** is acceptable behind a C# interface when a per-OS equivalent exists
  (mandatory for hardware accel: D3D11VA/CUDA/QSV on Windows, VAAPI/CUDA on Linux, VideoToolbox
  on macOS). **No C++/CLI** — native wrapping must be plain P/Invoke against a C ABI so one
  managed codebase serves all three OSes; only the bundled native libraries differ per RID.
- **Three target OSes: Windows 10 & 11 (floor: Windows 10 64-bit, version 1809+ — step 56), Linux, macOS**
  (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`).
  The managed assemblies are identical everywhere; FFmpeg 8 is bundled per-RID (`.dll`/`.so`/`.dylib`,
  see [ARCHITECTURE §11](ARCHITECTURE.md)) since the hand-rolled binding ships no FFmpeg runtime NuGet
  for any RID. macOS ships as a signed/notarized `.app` bundle (build order step 36).

## The non-negotiable performance rule

Pixel data must never be allocated on the managed heap per frame. Decoded frames stay in
native memory (FFmpeg `AVFrame`) → uploaded to a GPU texture → all effects/compositing run
as Skia GPU operations → presented. C# holds handles/pointers only. Use `ArrayPool`/pinned
native buffers for the few crossings that must happen (audio samples). Server/Background GC.

## Build order — status ledger

Steps 1–57 built the editor; steps 58+ are scheduled but not started (their detail lives in
`plan/features/` until they ship). Each completed step's full original spec + **✅ DONE**
implementation log was moved verbatim to `plan/history/` in the 2026-08-26 restructure (this
file had grown past 400 KB); the **Detail** column links to it. The original feasibility preamble (recommended
stack, FFmpeg-8 migration note, architecture sketch, vertical-slice definition of done) is
archived at the top of [steps-01-20.md](plan/history/steps-01-20.md). References elsewhere
(e.g. [FEATURES.md](FEATURES.md)'s "PLAN.md step N") resolve to these rows.

**Maintenance:** when a step or feature completes, flip its row here (and check off its
[Open work](#open-work) entry), then append the detailed DONE log to the matching
`plan/history/steps-*.md` chunk under its `## Step N` heading — annotated the same way the
archived steps are. A new large feature gets a `plan/features/<name>.md` (copy
[`_TEMPLATE.md`](plan/features/_TEMPLATE.md)) plus a row + todo here; small follow-ons to a
shipped step append to its history entry directly.

### Vertical slice (steps 1–9)

| # | Step | Status | Detail |
|---|---|---|---|
| 1 | Architecture spike (decode → GPU → Skia effect → present) | ✅ | [history](plan/history/steps-01-20.md#step-1) |
| 2 | Timeline data model + RenderGraph (`Sprocket.Core`) | ✅ | [history](plan/history/steps-01-20.md#step-2) |
| 3 | `MediaSource` decode + seek | ✅ | [history](plan/history/steps-01-20.md#step-3) |
| 4 | Skia preview surface + transport (software clock) | ✅ | [history](plan/history/steps-01-20.md#step-4) |
| 5 | Audio output + mixer; audio master clock | ✅ | [history](plan/history/steps-01-20.md#step-5) |
| 6 | Hardware-accel decode (`IHardwareContext`) | ✅ | [history](plan/history/steps-01-20.md#step-6) |
| 7 | Effects (brightness, fade) + audio volume/fade | ✅ | [history](plan/history/steps-01-20.md#step-7) |
| 8 | Export pipeline (full-res encode) | ✅ | [history](plan/history/steps-01-20.md#step-8) |
| 9 | Project save/load (JSON) | ✅ | [history](plan/history/steps-01-20.md#step-9) |

### Post-slice build-out (steps 10–60)

| # | Step | Status | Detail |
|---|---|---|---|
| 10 | Undo/redo command stack | ✅ | [history](plan/history/steps-01-20.md#step-10) |
| 11 | App UI shell | ✅ | [history](plan/history/steps-01-20.md#step-11) |
| 12 | Timeline control v1 | ✅ | [history](plan/history/steps-01-20.md#step-12) |
| 13 | Editing tools (Select / Blade / Slip, linked A/V) | ✅ | [history](plan/history/steps-01-20.md#step-13) |
| 14 | Multiple tracks | ✅ | [history](plan/history/steps-01-20.md#step-14) |
| 15 | Media bin & browsers | ✅ | [history](plan/history/steps-01-20.md#step-15) |
| 16 | Inspector & expanded effects | ✅ | [history](plan/history/steps-01-20.md#step-16) |
| 16b | Direct-manipulation editing & keyframe editor | ✅ | [history](plan/history/steps-01-20.md#step-16b) |
| 16c | Menu / command surface wiring | ✅ | [history](plan/history/steps-01-20.md#step-16c) |
| 16d | Keyframes | ✅ | [history](plan/history/steps-01-20.md#step-16d) |
| 16e | Cross-track clip dragging | ✅ | [history](plan/history/steps-01-20.md#step-16e) |
| 17 | Monitors (Source / Program) | ✅ | [history](plan/history/steps-01-20.md#step-17) |
| 18 | Proxy media | ✅ | [history](plan/history/steps-01-20.md#step-18) |
| 19 | Generators & adjustment layers | ✅ | [history](plan/history/steps-01-20.md#step-19) |
| 20 | Markers & comments + autosave / crash recovery | ✅ | [history](plan/history/steps-01-20.md#step-20) |
| 21 | Retime & speed controls | 🟡 constant-speed ✅; reverse ✅ + keyframed speed ramps ✅ (2026-08-27); pitch-preserving stretch / frame-interpolated slow-mo are later quality tiers → [plan](plan/features/variable-retime.md) | [history](plan/history/steps-21-40.md#step-21) |
| 22 | Ripple / roll / slide editing | ✅ | [history](plan/history/steps-21-40.md#step-22) |
| 23 | Sequences (nesting / compound clips) | ✅ | [history](plan/history/steps-21-40.md#step-23) |
| 24 | Multicam editing & clip sync | ✅ | [history](plan/history/steps-21-40.md#step-24) |
| 25 | Transitions | ✅ | [history](plan/history/steps-21-40.md#step-25) |
| 26 | Alpha-channel media compositing | ✅ | [history](plan/history/steps-21-40.md#step-26) |
| 27 | Broad media format support | ✅ | [history](plan/history/steps-21-40.md#step-27) |
| 28 | Interchange & relink workflow (EDL / XML) | ✅ | [history](plan/history/steps-21-40.md#step-28) |
| 29 | Export queue, burn-ins, handles & presets | ✅ | [history](plan/history/steps-21-40.md#step-29) |
| 30 | Audio loudness metering & normalization | ✅ | [history](plan/history/steps-21-40.md#step-30) |
| 31 | Audio effects & plugin hosting (VST3 / AU) | 🟡 built-in effects + chain UI ✅; native VST3/AU open → [plan](plan/features/plugin-hosting.md) | [history](plan/history/steps-21-40.md#step-31) |
| 32 | Preview render cache (pre-render / freeze) | ✅ | [history](plan/history/steps-21-40.md#step-32) |
| 33 | Plugins & advanced color management | 🟡 managed plugin host + ACES Filmic ✅; native OCIO/OFX open → [plan](plan/features/plugin-hosting.md) | [history](plan/history/steps-21-40.md#step-33) |
| 34 | Color grading | ✅ | [history](plan/history/steps-21-40.md#step-34) |
| 35 | Cross-platform native-lib bundling | ✅ | [history](plan/history/steps-21-40.md#step-35) |
| 36 | Packaging & distribution | 🟡 packaging + auto-update ✅; code-signing / notarization deferred → [plan](plan/features/code-signing.md) | [history](plan/history/steps-21-40.md#step-36) |
| 36a | Third-party notices | ✅ | [history](plan/history/steps-21-40.md#step-36a) |
| 37 | Log media & color management (D-Log) | ✅ | [history](plan/history/steps-21-40.md#step-37) |
| 38 | AI control via MCP server | ✅ | [history](plan/history/steps-21-40.md#step-38) |
| 39 | Fade handles & opacity rubber-band | ✅ | [history](plan/history/steps-21-40.md#step-39) |
| 40 | Rich text & titles | ✅ | [history](plan/history/steps-21-40.md#step-40) |
| 41 | Reverb quality upgrade + audio freeze | ✅ | [history](plan/history/steps-41-57.md#step-41) |
| 42 | Image-sequence & still import | ✅ | [history](plan/history/steps-41-57.md#step-42) |
| 43 | Frame hold + stop-motion frame edits | ✅ | [history](plan/history/steps-41-57.md#step-43) |
| 44 | Audio-only export delivery | ✅ | [history](plan/history/steps-41-57.md#step-44) |
| 45 | Channel-aware update checks | ✅ | [history](plan/history/steps-41-57.md#step-45) |
| 46 | Delay effects (digital / tape / multi-tap / stereo) | ✅ | [history](plan/history/steps-41-57.md#step-46) |
| 47 | Noise Gate | ✅ | [history](plan/history/steps-41-57.md#step-47) |
| 48 | Shelving EQ | ✅ | [history](plan/history/steps-41-57.md#step-48) |
| 49 | Acoustic Space (Convolution) Reverb | ✅ (user IR import; no bundled IRs) | [history](plan/history/steps-41-57.md#step-49) |
| 50 | Shimmer Reverb | ✅ | [history](plan/history/steps-41-57.md#step-50) |
| 51 | Reorder effects within an audio chain | ✅ | [history](plan/history/steps-41-57.md#step-51) |
| 52 | Additional camera log profiles (non-DJI) | ✅ | [history](plan/history/steps-41-57.md#step-52) |
| 53 | Clip right-click context menu | ✅ | [history](plan/history/steps-41-57.md#step-53) |
| 54 | Multi-clip selection | ✅ | [history](plan/history/steps-41-57.md#step-54) |
| 55 | Link clips (re-link A/V) | ✅ | [history](plan/history/steps-41-57.md#step-55) |
| 56 | Windows 10 support (verify + declare) | 🟡 declaration ✅; Win10 VM smoke pending | [history](plan/history/steps-41-57.md#step-56) |
| 57 | Linux support (verify + declare) | 🟡 phases 1–4 ✅; hardware-accel verify remaining | [history](plan/history/steps-41-57.md#step-57) |
| 58 | Plugin Manager (user-facing plugin management UI) | ✅ | [history](plan/history/steps-58plus.md#step-58) |
| 59 | Open plugin standards (frei0r / LADSPA / LV2) | ✅ | [history](plan/history/steps-58plus.md#step-59) |
| 60 | Preview allocation churn (per-frame metadata / wrapper Gen0 — measure + remediate) | ❌ → [plan](plan/features/preview-allocation-churn.md) | — |

## Open work

The actionable remainder. Each large feature has its detailed plan in `plan/features/`;
verification-only items carry their checklist in the step's history entry.

- [ ] **Grading presets / creative looks** (unscheduled feature) — a Looks browser over the
  existing tier-2 grading effects, per the [ARCHITECTURE §18](ARCHITECTURE.md) preset taxonomy →
  [plan/features/looks-browser.md](plan/features/looks-browser.md)
- [x] **Convolution reverb (Acoustic Space)** — step 49, shipped 2026-08-26 (user WAV IR import; bundled
  IR library deliberately deferred on licensing) → [history](plan/history/steps-41-57.md#step-49)
- [x] **Variable / ramped speed & reverse retime** — step 21 remainder, shipped 2026-08-27 (reverse
  playback with GOP-aware backward decode + reversed audio; keyframed speed ramps via the Inspector Speed
  lane). Pitch-preserving stretch and frame-interpolated slow motion remain later quality tiers →
  [plan/features/variable-retime.md](plan/features/variable-retime.md)
- [ ] **Native plugin & color hosting** — steps 31 + 33 remainders (VST3/AU C-ABI bridges,
  OpenColorIO config hosting, OFX adapter) →
  [plan/features/plugin-hosting.md](plan/features/plugin-hosting.md)
- [x] **Plugin Manager (user-facing)** — step 58, shipped 2026-08-26: Edit ▸ Plugins… lists every
  discovered plugin with version/status/effects, per-plugin enable/disable (live register/unregister +
  ALC unload), Install/Uninstall/Rescan/Open Folder, disabled list persisted in `UserSettings`. Ships
  over the shipped managed host, independent of the native bridges →
  [history](plan/history/steps-58plus.md#step-58)
- [x] **Open plugin standards — frei0r / LADSPA / LV2** — step 59, shipped 2026-08-26 (LADSPA) and
  2026-08-27 (LV2 core subset + frei0r over the new CPU-effect readback seam): native scan →
  descriptor/metadata → catalog → mixer chain / render pipeline → persistence by plugin id + port
  values; Plugin Manager rows for all three formats →
  [history](plan/history/steps-58plus.md#step-59)
- [ ] **Code-signing & notarization** — step 36 remainder (alpha ships unsigned; also
  `linux-arm64` AppImage + sample-export CI validation) →
  [plan/features/code-signing.md](plan/features/code-signing.md)
- [ ] **Live stop-motion capture** (unscheduled feature; deliberately deferred until packaging
  stabilizes) → [plan/features/stop-motion-capture.md](plan/features/stop-motion-capture.md)
- [ ] **Windows 10 VM smoke verification** — step 56 remainder: create the Win10 22H2 VM, run
  the manual release-smoke checklist, redeploy docs + regenerate the PDF manual, flip the
  FEATURES.md row ([checklist](plan/history/steps-41-57.md#step-56))
- [ ] **Preview allocation churn** — step 60: a 2026-08-28 static audit was validated line-by-line;
  the §1 pixel rule holds (`SKImage.FromPixels` wraps native memory, no copies), but effect/generator
  parameter dictionaries, per-effect Skia uniform/child wrappers, and the per-draw layer list + closure
  are rebuilt on every repaint, and per-frame allocation *rate* has never been measured (the 2026-06-30
  benchmark saw 0 collections but did not count bytes). Measure first, then remediate in payoff order →
  [plan/features/preview-allocation-churn.md](plan/features/preview-allocation-churn.md)
- [ ] **Linux hardware-accel verification** — step 57 Phase 5: real VAAPI + NVENC encode/decode
  on physical Intel/AMD/NVIDIA boxes, probe-order + software-fallback confirmation
  ([details](plan/history/steps-41-57.md#step-57))

Open product questions (e.g. the mockup's user-avatar / account affordance, full panel docking)
are tracked in [UI.md §5](UI.md).

## Verification

- **Performance claim:** run `Sprocket.App` under a memory profiler (dotnet-counters / dotMemory);
  assert ~0 Gen0 allocations per frame in the render loop; confirm GPU upload path (no CPU
  pixel loops). Measure sustained 1080p preview fps. (Originally measured on the step 1 spike, which
  no longer exists — the shipping app is the target now.) **Status 2026-08-28:** pixel rule verified by
  static inspection; per-frame *metadata* allocation rate still unmeasured — step 60 adds the harness.
- **Cross-platform:** CI matrix builds + runs the headless tests on windows-latest, ubuntu-latest,
  and macos-latest (the latter covers `osx-arm64`); manually run the app + export on a real Linux box,
  Win 11, and a Mac. The render path is byte-identical across OSes (verified Win↔Linux via the headless
  PNG hash; macOS to be confirmed once the dylibs are bundled, steps 35–36).
- **Correctness:** unit tests for RenderGraph (clip resolution, trim, effect-stack order,
  fade ramps) headlessly; golden-frame test comparing exported frames against expected output.
- **A/V sync:** export a clip with a known audio/video sync marker (clap/flash) and verify
  alignment; check drift over a multi-minute clip.
- **Hardware accel:** verify decode uses the GPU (nvidia-smi / vainfo / macOS `VideoToolbox` via
  GPU usage) and that software fallback engages when no device is present.

## Top risks

- Real-time A/V sync & jitter (hard in any language) — mitigate with audio master clock +
  bounded buffers + frame drop/duplicate. **(Preview judder addressed 2026-06-30 — see
  [plan/history/performance-log.md](plan/history/performance-log.md).)**
- GC in the hot path — mitigated by the no-managed-pixels rule; must be enforced/profiled early.
  **(Residual metadata/wrapper churn catalogued 2026-08-28 → step 60.)**
- FFmpeg interop surface is raw and unforgiving — wrap narrowly in `Sprocket.Media`.
- Hardware-accel fragmentation across vendors/OSes — abstract + always keep software fallback.
- FFmpeg licensing (LGPL vs GPL) — decide before distribution.

## Playback performance log

Moved to [plan/history/performance-log.md](plan/history/performance-log.md); append future
playback-performance investigations there.
