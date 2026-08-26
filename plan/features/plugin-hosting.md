# Native plugin & color hosting (VST3 / AU audio, OpenColorIO / OFX video)

🟡 **Partially shipped; the native halves remain.** This file tracks the remainders of
build-order steps 31 and 33 together (they share the plugin-host foundation). Shipped
records: [step 31](../history/steps-21-40.md#step-31) (built-in audio effects, all four
chain scopes, mixer/inserts UI) and [step 33](../history/steps-21-40.md#step-33) (managed
plugin host on a collectible `AssemblyLoadContext`, `IVideoEffect`/`IAudioEffectProvider`
contracts, ACES Filmic through the registry). Tracked in [PLAN.md](../../PLAN.md) Open work.
See [COLOR_GRADING_ROADMAP.md](../../COLOR_GRADING_ROADMAP.md) for the color-parity sequence
this feeds.

## What remains

### Audio: native VST3 / AU hosting (step 31 remainder, blocked on nothing now — step 33's host exists)
- Per-format **C-ABI bridge shims** (`sprocket_vst3host` / `sprocket_auhost`) — the VST3 SDK is
  C++/COM-style and AU is Obj-C; each wraps to a flat C ABI reached by P/Invoke (the FFmpeg/Skia
  pattern; **no C++/CLI**, ARCHITECTURE §1/§13), bundled per RID by `scripts/release.ps1`
  (steps 35–36 machinery).
- Plugin **scan/instantiate off the audio thread**; processing joins the existing
  `IAudioEffect` chain seam (allocation-free per-buffer blocks).
- **Plugin editor GUI** embedding (plugin opens its own window).
- **Plugin delay compensation.**
- **Opaque state-blob persistence** (plugin id + component/controller state + automation;
  additive, schema-versioned §12) with **offline bypass** when a plugin is missing (§15).
- Parameter automation rides `AnimatableValue` keyframes (already the contract).
- **VST3 SDK licensing decision** (GPLv3 vs Steinberg dual license) before distribution —
  cf. the FFmpeg GPL note in THIRD-PARTY-NOTICES.md.

### Video/color: native hosting (step 33 remainder)
- Host a full **OpenColorIO/ACES config** (native lib via a C-ABI wrapper; per-RID bundling)
  with working-space management beyond the built-in ACES Filmic fit.
- An **OFX** C-ABI adapter surfacing hosted video effects through the existing
  `SkiaEffectPipeline.RegisterEffect` registry (plugins never touch SkiaSharp directly).
  CPU-only OFX plugins reuse the readback seam designed for frei0r in step 59
  ([frei0r-ladspa-lv2.md](frei0r-ladspa-lv2.md)).

### Split out as their own steps (2026-08-26)
- **Plugin unload/reload + the Manage Plugins panel** → step 58,
  [plugin-manager.md](plugin-manager.md) — ships on the existing managed host, independent
  of the native bridges here.
- **frei0r hosting** (with LADSPA / LV2) → step 59,
  [frei0r-ladspa-lv2.md](frei0r-ladspa-lv2.md).

## Where it lands

- `src/Sprocket.Plugins/` — host extensions (native bridge loading beside `PluginLoadContext`).
- `src/Sprocket.Core` — contracts only (`IAudioEffect`, `IAudioEffectProvider`, `IVideoEffect`
  already exist; add latency/tail metadata surface shared with convolution reverb).
- `src/Sprocket.Audio/AudioMixer.cs` — delay compensation in the chain executor.
- New native shim projects built per RID by `scripts/release.ps1` + CI matrix; version-guarded
  loads following the `FFmpegLoader` pattern.
- Heavy/non-deterministic plugins steer users to **Freeze** (step 32 render cache) rather than
  betting on realtime.

## Sequencing note

Ship the VST3 bridge first (cross-platform, one shim serves all three OSes); AU is macOS-only
polish. OCIO/OFX can proceed independently on the video side.
