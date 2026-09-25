# Toy cassette camera look (PXL 2000–style)

❌ **Not started** (planned 2026-09-25). A one-apply look that makes footage resemble the
late-1980s toy camcorder that recorded black-and-white video onto ordinary audio cassettes —
chunky low-res pixels, a low frame rate, heavy black border, smeary highlights, and hissy
band-limited mono sound. Tracked in [PLAN.md](../../PLAN.md) Open work. Relative links resolve
from the repo root.

## Why / product context

The camera (Fisher-Price PXL 2000, 1987; "PixelVision") ran a standard compact cassette at very
high speed to squeeze a video signal onto audio tape — roughly 11 minutes per side of a C90. The
result has a distinctive, widely imitated character (it became an art-film/music-video medium):

- **Resolution** — commonly cited as ~120×90 visible samples, shown as blocky, soft-edged pixels.
- **Monochrome** — black-and-white only, low dynamic range, crushed blacks, blooming whites.
- **Frame rate** — ~15 fps, so motion stutters versus the project rate.
- **Framing** — the picture sits inside a thick black border on a 4:3 TV (it does not fill the frame).
- **Sensor lag** — bright moving objects leave comet-like trails/smear; auto-exposure pumps.
- **Tape artifacts** — horizontal noise lines, occasional dropouts/tearing, grain-like shimmer.
- **Audio** — mono, narrow bandwidth, tape hiss, wow/flutter, AGC pumping on loud sounds.

No leading NLE ships this as a built-in; the equivalents are composed from primitives or plugins:
After Effects / Premiere build it from **Mosaic** + **Posterize Time** + Black & White + noise;
Resolve users stack **Mosaic** / **Blanking Fill** + a Fusion/OFX retro plugin (e.g. Red Giant
Universe's retro/VHS family); consumer apps (CapCut, Dazz Cam and similar) ship one-tap "retro
cam" filters. **Take their naming for the primitives** (`Mosaic`, `Posterize Time`) and offer the
one-tap look as a preset on top, like our Looks / Day for Night presets.

**Naming (trademark):** follow the black-and-white film-preset rule — the effect/preset gets a
generic name (working name **"Toy Cassette Camera"**), and the brand appears only in the
description/tooltip ("Inspired by the Fisher-Price PXL 2000"). Extend the existing brand-token
guard test (`EffectCatalogTests.BlackWhite_Film_Preset_Names_Carry_No_Brand_Or_Stock_Token`) to
cover it.

## Existing seams to build on

- **Registry SkSL effects** (`EffectCatalog.cs` + `Sprocket.Render/Effects/*Effect.cs`) with the
  frame-context uniforms (`sprocket_time` / `sprocket_bounds`) added by the black-and-white
  phase 2 — enough for pixelation, border, noise lines, dropouts, flicker.
- **`builtin.blackwhite`** — its channel mixer, tone response and grain can supply the monochrome
  stage rather than re-implementing it (either stack it in the preset, or share its SkSL).
- **`builtin.flicker`** (special-effects phase 1) — auto-exposure pumping.
- **Tape Delay** (`Sprocket.Audio/Effects/TapeDelayEffect.cs`) — existing wow/flutter + saturation
  code to reuse for the cassette audio stage; the built-in EQ/filters for band-limiting.
- **`EffectDescriptor.Presets`** and the **Looks / effect-preset browser groups** — the one-tap entry
  point and MCP `add_effect` `preset`.
- **Frame Hold / retime** (`RenderGraph` source-time mapping, PLAN steps 21/43) — the precedent for
  changing *which* source frame is sampled; Posterize Time belongs here, not in a shader.

## Implementation sketch

1. **Mosaic effect** (`builtin.mosaic`, generic primitive) — block size as horizontal/vertical cell
   counts (default 120×90 for this look), sharp or soft cell edges. Pure SkSL, no new seam.
2. **Posterize Time** (`builtin.posterizetime`, Frame Rate param, default 15) — quantize the
   clip-local time the render graph resolves to the effect's rate, so the *decoded source frame*
   holds. This is a Core render-graph change (pure function of time; preview = export) and the one
   real design question — decide between an effect that the planner reads before source-time
   resolution vs. a clip property alongside speed. Must compose with speed ramps / reverse.
3. **Toy Cassette Camera video effect** (`builtin.toycam`) — one ordered stage: mosaic cell grid →
   monochrome + low-DR tone curve (bloom/crush) → highlight smear → horizontal noise lines + seeded
   dropouts → black border (inset + softness). **Smear is temporal** — it needs the previous output
   frame, which the stateless pipeline doesn't have. Ship first with a spatial approximation
   (a short streak off bright pixels); phase 8 replaces it with true temporal smear.
4. **Cassette audio effect** (`builtin.audio.cassette`) — mono sum, band-limit (~100 Hz–5 kHz),
   hiss floor, wow/flutter (reuse Tape Delay's modulator), soft saturation, AGC pumping. Useful on
   its own for any "recorded on cassette" sound.
5. **One-tap look** — a preset/Look that stacks Posterize Time + Toy Cassette Camera on video and
   Cassette on the linked audio as one undo step; variants (e.g. Clean, Worn Tape, Low Light).
   MCP `add_effect` `preset` support.
6. **Persistence** — new effects are ordinary effect entries; no format change beyond Posterize
   Time if it lands as a clip property (additive nullable DTO field).
7. **Docs/inventory** — FEATURES.md row(s) move from Planned into §4/§5 (❌ undocumented); consider
   the README effects list.
8. **Temporal smear via a multi-frame source seam** (follow-on phase, independently mergeable) —
   resolves "highlight smear needs earlier frames" without breaking the render graph's
   "pure function of (project, time)" rule (ARCHITECTURE §1/§8). A feedback buffer (keep last
   frame's output) is rejected: it makes a frame depend on playback history, so scrubbing, seeking,
   the render cache and export would all disagree.
   - **Precedent:** After Effects / Premiere **Echo** (Echo Time, Number of Echoes, Starting
     Intensity, Decay, Echo Operator incl. *Maximum*). Ship it as a reusable `builtin.echo`
     primitive using that naming; the toycam smear becomes an Echo configured with the *Maximum*
     operator on a highlight key (only bright pixels trail) with fast decay.
   - **Core** — an effect may declare a **temporal footprint** in its descriptor (count *K* and
     spacing Δ, e.g. K = 4 at 1/15 s). `RenderGraph.PlanVideoFrame` then emits the extra source-frame
     requests at clip-local `t − k·Δ` for that clip, after speed / reverse / frame-hold /
     Posterize Time mapping; times before the clip's `SourceIn` use available source handles,
     otherwise clamp. The plan stays serializable and deterministic.
   - **Render/Media** — the effect receives the prior frames as extra shader children (native
     `SKImage` wrappers, no managed pixels per §1). Sequential playback mostly hits frames the
     decode ring/pool already holds; add a small recent-frames lookup so the extra requests don't
     re-seek. Cap K (≤ 8) and budget the extra GPU memory; the step-32 render cache handles heavy
     stacks.
   - **Export** — same plan, so export matches preview frame-for-frame.
   - **Tests** — planner emits the right source times (incl. at clip starts, with speed ramps /
     reverse / Posterize Time); golden frames for Echo and the smeared toycam; allocation check
     that the extra frames add no per-frame managed pixel buffers.

## Tests

- `Sprocket.Core.Tests` — descriptors/presets, brand-token guard, Posterize Time source-time
  quantization (incl. with speed ramps / reverse / frame hold).
- Render golden frames for Mosaic and Toy Cassette Camera (deterministic seeds).
- `Sprocket.Audio.Tests` — Cassette effect band-limit/mono/determinism.

## On completion

Flip this feature's PLAN.md todo, append the DONE log to `plan/history/steps-58plus.md`, and
update FEATURES.md — then delete or archive the no-longer-open parts of this file.
