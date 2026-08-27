# Open plugin standards: frei0r (video) + LADSPA / LV2 (audio) — build-order step 59

✅ **All three arms shipped** — LADSPA 2026-08-26, LV2 core subset + frei0r (with the CPU-effect readback seam)
2026-08-27; the implementation log lives in [history](../history/steps-58plus.md#step-59). This document is the
original plan, kept for the design rationale. Host the open-source plugin standards alongside the commercial ones
tracked in [plugin-hosting.md](plugin-hosting.md) (VST3/AU, OpenColorIO/OFX). All three are
**plain C ABIs** — unlike VST3 (C++/COM) they need **no bridge shim**: P/Invoke directly, per
the no-C++/CLI rule (ARCHITECTURE §1). Highest value on Linux, where these are the native
plugin ecosystems (and where Sprocket has no plugin story otherwise). Tracked in
[PLAN.md](../../PLAN.md) Open work; discovered plugins surface in the step-58
[Plugin Manager](plugin-manager.md).

## The three standards

- **LADSPA (audio)** — the simplest possible plugin ABI (one `ladspa.h`: descriptor +
  float32 control/audio ports, `run(instance, sampleCount)`). Maps almost 1:1 onto the
  existing `IAudioEffect` block-processing seam; control ports become typed
  `EffectParameterDescriptor`s (min/max/default hints come from the descriptor). Ship first.
- **LV2 (audio)** — LADSPA's successor: C ABI + Turtle/RDF metadata bundles. Host a
  **core-spec subset first** (audio + control ports only; skip atoms, worker threads, and
  plugin UIs initially — parameters render through Sprocket's own type-driven Inspector,
  which most LV2 plugins tolerate well). Metadata via a minimal managed Turtle reader or a
  bundled `lilv` (C ABI) — decide by spike; prefer managed if the subset stays small.
- **frei0r (video)** — filters/sources/mixers behind a tiny C ABI (`f0r_init`,
  `f0r_get_plugin_info`, `f0r_get_param_info`, `f0r_update`). The catch: frei0r processes
  **CPU RGBA buffers**, which collides with the GPU-only pipeline (§1). Needs a new
  **CPU-effect stage seam**: GPU readback → pooled *native* buffer (no managed pixels —
  P/Invoke writes straight into native memory) → `f0r_update` → re-upload. Per-frame readback
  is expensive: classify frei0r effects as heavy, steer users to the render cache / freeze
  (step 32), and make the seam once — CPU-only **OFX** plugins (plugin-hosting.md) reuse it.

## Sequencing

1. **LADSPA** — small, proves the native-audio-plugin path end to end (scan → descriptor →
   catalog → mixer chain → persistence by plugin id + port values).
2. **LV2 core subset** — same seam, richer discovery/metadata.
3. **frei0r** — after the CPU-stage readback seam is designed (it is this step's only render
   change; everything else rides existing seams).

## Where it lands

- `src/Sprocket.Plugins` — native scanners/hosts beside the managed `PluginHost` (per-format
  discovery: LADSPA_PATH / LV2_PATH / frei0r standard dirs + `%APPDATA%/Sprocket/Plugins`).
- `src/Sprocket.Core` — nothing new for audio (`IAudioEffectProvider` seam exists); a
  CPU-video-effect contract for the readback stage.
- `src/Sprocket.Render/SkiaEffectPipeline.cs` — the CPU-stage execution point (readback /
  upload around the existing effect-chain switch).
- `src/Sprocket.Audio` — LADSPA/LV2 instances join the mixer chain like built-ins (stateful,
  cached per `StateKey`, allocation-free per buffer).
- Persistence: plugin id + parameter values (already the `EffectInstance` shape); missing
  plugin = pass-through offline (§15). Licensing: frei0r plugins are GPL — compatible
  (Sprocket is MIT and already ships GPL FFmpeg builds; note in THIRD-PARTY-NOTICES.md that
  user-installed plugins carry their own licenses).

## Tests

- LADSPA/LV2: deterministic block tests against a known open plugin fixture (e.g. a simple
  gain/delay), port-descriptor → parameter mapping, cross-buffer state continuity,
  missing-plugin pass-through.
- frei0r: golden-frame test with a reference plugin through the readback seam; allocation
  profile proves no managed pixels (§1) and pooled buffer reuse.
- Plugin Manager rows show all three formats with per-plugin errors.

## On completion

Flip the step 59 ledger row, check the PLAN.md todo, append the DONE log to
`plan/history/` (`steps-58+.md` chunk), add FEATURES.md rows, update the README plugin
bullet.
