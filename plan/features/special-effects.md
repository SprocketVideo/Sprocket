# Action VFX and day-for-night

❌ **Not started.** Unscheduled feature; tracked in [PLAN.md](../../PLAN.md) Open work. Relative
links resolve from the repo root.

**Scope in one line:** add a practical special-effects roadmap centered on three editorially useful
families rather than physics simulation: **action composites** (fire, explosions, muzzle flashes,
shockwaves), **atmospherics** (smoke, embers, dust, heat haze), and a guided **day-for-night**
toolkit for turning daytime footage into believable night exteriors.

## Why / product context

Editors usually do not expect a desktop NLE to ship a full fluid or rigid-body simulator. The common
professional workflow is closer to Premiere Pro, Final Cut Pro, and Resolve Fusion-lite usage: stack
alpha elements, drive them with blend/distortion/light effects, and add just enough masking/tracking
to seat them into the plate. Day-for-night is similar: the useful version is not one slider, but a
disciplined grading and masking workflow that gets believable results quickly.

What we should copy from leading editors:

| Area | Established behavior | What Sprocket should do |
|---|---|---|
| Fire / explosions | Built from layers: flash, flame, smoke, debris, distortion, shake, glow, sound | Ship coordinated presets over reusable primitives rather than one opaque mega-effect |
| Atmospheric effects | Reusable overlays or generators, blendable and maskable | Treat smoke / embers / dust / leaks as ordinary clips or generators plus a small effect stack |
| Day-for-night | Guided grade with protected skin, sky control, highlight rolloff, practical-light enhancement | Make a purpose-built preset stack with a small bespoke control surface over the existing grading stack |
| Attachment / occlusion | Good results require tracking, masks, and compositing order | Start with manual placement; add tracked placement and occlusion only after the motion/mask seams are ready |

**Deliberate departures**

- **No full simulation in v1.** No volumetric fire solver, no particle-physics explosion sim, no 3D lighting.
- **No separate compositor.** Everything stays on the existing clip / adjustment-layer / effect-stack seams.
- **Day-for-night before deep VFX.** It solves a common real edit problem and leverages the current color system better than a flashy but shallow fire effect would.

## Existing seams to build on

| Piece | Where | What it gives us |
|---|---|---|
| `IVideoEffect`, descriptors, presets, reserved per-frame uniforms | `src/Sprocket.Core/Rendering/IVideoEffect.cs`, `src/Sprocket.Core/Model/EffectCatalog.cs`, [ARCHITECTURE §7](../../ARCHITECTURE.md) / [§13](../../ARCHITECTURE.md) | The main seam for glow, heat distortion, chromatic aberration, shockwave, flicker, and day-for-night grading helpers |
| Existing grading stack | `EffectCatalog.cs`, `src/Sprocket.Render/Effects/`, [COLOR_GRADING_ROADMAP.md](../../COLOR_GRADING_ROADMAP.md) | Exposure, wheels, curves, qualifiers, ACES-style filmic, and presets that day-for-night can build on rather than replacing |
| Adjustment layers | `src/Sprocket.Core/Model/GeneratorCatalog.cs`, render graph notes in [UI.md](../../UI.md) / [ARCHITECTURE §5](../../ARCHITECTURE.md) | A natural place for whole-shot atmospherics or a sequence-wide day-for-night pass |
| Generator clips | `src/Sprocket.Core/Model/GeneratorCatalog.cs`, title / matte precedent | A seam for procedural smoke, dust, embers, light leaks, and muzzle-flash plates without importing stock media |
| Alpha media import and compositing | shipped step 26; tracked in [FEATURES.md](../../FEATURES.md) | Lets stock fire, smoke, debris, and explosion elements work immediately as upper-track composites |
| Frame-context uniforms (`frameTime`, `layerRect`) | plugin/effect registry path in [ARCHITECTURE §13](../../ARCHITECTURE.md) | Deterministic animated noise, flicker, vignette, radial shockwaves, and layer-aware distortion |
| Stabilization motion-analysis work | [stabilization.md](stabilization.md) | Future seam for tracked placement, camera-motion-aware shake compensation, and basic attachment to footage |
| Effect-browser / Inspector preset UI | `src/Sprocket.App/MediaBrowser/MediaBrowserPanel.cs`, `src/Sprocket.App/Inspector/InspectorPanel.cs` | Existing UI surfaces for discoverability, parameter editing, and one-click preset application |

## Product shape

The feature should ship as three connected layers, in this order:

1. **Primitive image effects**
   Effects that are broadly reusable outside VFX: glow/bloom, directional blur, zoom blur, heat haze,
   shockwave distortion, chromatic aberration, flicker, and impact shake.
2. **Action and atmospheric presets**
   Fire, explosion, muzzle-flash, smoke, ember, dust, and light-leak presets built by combining the
   primitive effects with overlays or generators.
3. **Day-for-night toolkit**
   A guided correction stack over the grading system with controls for sky darkening, highlight
   suppression, cooler moonlight tint, shadow shaping, saturation restraint, and practical-light lift.

This ordering matters: the primitive layer has broad value, the preset layer makes the feature feel
finished, and day-for-night becomes more credible once the grading-preset surface is already in place.

## Implementation sketch

1. **Core descriptors and preset model:** add built-in effect ids for the primitive image effects and
   a curated preset catalog for action VFX and day-for-night. Reuse `EffectDescriptor.Presets`; avoid a
   separate preset system unless a multi-effect look/action bundle proves necessary.
2. **Render primitives:** implement the reusable GPU stages first: glow/bloom, directional/zoom blur,
   heat distortion, radial shockwave, channel offset, vignette-light wrap, animated flicker, and a
   lightweight shake transform. Keep them deterministic and preview/export identical.
3. **Generators and overlays:** add a small generator family for smoke, embers, dust, and light leaks.
   Treat imported alpha stock elements as first-class workflow, not a fallback. The point is practical
   compositing, not procedural purity.
4. **Action presets:** ship presets like `Muzzle Flash`, `Small Explosion`, `Fuel Fire`, `Dust Hit`,
   and `Aftershock` that coordinate multiple existing effects and generator settings. These should read
   like editorial building blocks, not VFX-lab internals.
5. **Day-for-night preset stack:** add a dedicated entry point that applies a curated grading stack in
   the correct order. The initial UI can be a preset plus a few high-value controls: `Night Strength`,
   `Sky`, `Highlights`, `Shadow Floor`, `Moonlight Tint`, `Practical Lights`, and `Protect Skin`.
6. **Masking and tracking follow-on:** once the stabilization and future mask-tracking seams exist,
   allow selected effects or presets to attach to motion, inherit position, and respect occlusion. Do
   not block phase 1 on this.
7. **Docs and samples:** add a sample project or stock-free demo stack showing one daytime street shot
   converted to night, plus one controlled action composite using the same primitives.

## Proposed phases

| Phase | Ships | Depends | Notes |
|---|---|---|---|
| 1 | Primitive image effects ✅ (2026-09-22) | existing effect seam | Glow/bloom, heat haze, shockwave, directional blur, zoom blur, flicker, chromatic aberration, impact shake |
| 2 | Atmospheric generators | 1 | Smoke, embers, dust, light leaks; usable as standalone mood layers |
| 3 | Action VFX presets | 1–2 | Fire / explosion / muzzle-flash preset stacks over overlays and generators |
| 4 | Day-for-night toolkit | grading + looks work | Guided preset stack over the existing grading system; likely the highest practical user value |
| 5 | Tracking / masking integration | stabilization + future masks | Motion attachment, better placement, sky/subject isolation, occlusion |
| 6 | Polish / docs / samples | 1–5 | Tutorials, sample assets policy, FEATURES/README close-out if shipped |

## Candidate effect list

### Primitive effects

- `Glow / Bloom`
- `Directional Blur`
- `Zoom Blur`
- `Heat Distortion`
- `Shockwave`
- `Chromatic Aberration`
- `Impact Shake`
- `Flicker / Exposure Pulse`
- `Light Wrap`
- `Lens Dirt / Edge Burn`

### Atmospheric generators or overlays

- `Smoke`
- `Dust`
- `Embers`
- `Sparks`
- `Fog / Haze`
- `Light Leak`

### Guided presets

- `Muzzle Flash`
- `Small Fire Burst`
- `Ground Explosion`
- `Explosion Aftermath`
- `Burning Edge`
- `Day for Night - Exterior Wide`
- `Day for Night - Street Scene`
- `Day for Night - Blue Moon`


## Phase 1 — ✅ DONE (2026-09-22)

Eight registry SkSL effects on the existing `IVideoEffect` seam (`Sprocket.Render/Effects/*Effect.cs`,
registered in `SkiaEffectPipeline`'s static constructor next to the grading toolset), their descriptors in
`EffectCatalog.BuiltIns` under `EffectCategory.Video`, and their ids/parameter names in `EffectTypeIds` /
`EffectParamNames`. No new seam, no pipeline special-case, no App change — `EffectRelevance` offers every
Video-category descriptor to video-track clips already, and the Inspector builds each control from the
descriptor, so the eight appear in the Effects browser, the Effects menu, the Inspector, and MCP with no
per-effect UI code.

**Shipped:** Glow (`GL`), Directional Blur (`DB`), Zoom Blur (`ZB`), Heat Distortion (`HD`), Shockwave
(`SW`), Chromatic Aberration (`CA`), Impact Shake (`IS`), Flicker (`FL`).

**Decisions worth keeping:**

- **Everything spatial is a fraction of the layer rect**, read from the reserved `sprocket_bounds` uniform,
  never a pixel count. This is what makes a preview at one resolution and an export at another produce the
  same picture (§5); `VfxPrimitiveCatalogTests` guards it at the descriptor level.
- **Time-driven effects read the reserved `sprocket_time` uniform** (Heat Distortion, Impact Shake,
  Flicker) and use hashed value noise rather than a texture or any hidden state, so each is a pure function
  of (project, time) — asserted by the "is a pure function of frame time" render tests.
- **Shockwave's ring position is an ordinary keyframeable `Radius`**, not an internal clock. The user owns
  the timing (After Effects' CC Ripple / Resolve's Ripple convention), the wave retimes with the clip, and
  the render stays deterministic. Same reasoning puts the decay of Impact Shake on a keyframed `Amount`.
- **Impact Shake's `Amount` is a master intensity** that scales the roll as well as the throw, so one
  keyframe lane decays the whole hit — the After Effects wiggle-amplitude / FCP Earthquake behaviour.
  `Overscan` (default 105%) keeps the shake off the frame edge.
- **The keyframe-driven three default to identity** (Zoom Blur / Chromatic Aberration `Amount` 0, Shockwave
  `Radius` 0) so dropping one on a clip never causes a visible jump; the always-on ones (Glow, Heat
  Distortion, Flicker, Impact Shake) default to a usable look.
- **Blur cost is honest and bounded.** Each tap re-evaluates the upstream chain (the shader-graph model,
  §7), so Glow is 25 taps and the two blurs 13–15 — enough that a small highlight does not ring, few enough
  to stay a single pass. A true separable/downsampled bloom would need an offscreen round-trip and belongs
  with the phase-5 work if it is ever wanted.
- **Premultiplied-alpha discipline throughout.** Glow raises alpha along with the added light so a bloom
  can spill past an alpha layer's edge; Chromatic Aberration recombines its three samples unpremultiplied
  so channels taken at different alphas do not contaminate each other; Flicker clamps back to the pixel's
  own alpha.

**Tests:** `tests/Sprocket.Render.Tests/VfxPrimitiveEffectTests.cs` (27 offscreen render tests — a
pass-through assertion at each effect's neutral setting, a behavioural assertion for what it claims to do,
and frame-time determinism for the three animated ones) and
`tests/Sprocket.Core.Tests/VfxPrimitiveCatalogTests.cs` (registration, category, parameter sets,
identity defaults, resolution-independence, no presets yet).

**Not in phase 1:** Light Wrap and Lens Dirt / Edge Burn — see the candidate list above for why each needs
a seam phase 1 does not have.

## UI notes

- Put the reusable effects in the normal Effects browser under `Video`.
- Put the preset workflows in a visible subgroup such as `Action VFX` and `Day for Night` so users do
  not need to assemble them from scratch.
- Day-for-night likely deserves one bespoke Inspector row group rather than exposing every underlying
  grading parameter all at once.
- Fire and explosion presets should show a placement hint: best on an upper track, optionally with a
  matching sound effect and blend recommendation.

## Tests

- **Core (`Sprocket.Core.Tests`):** descriptor registration, preset coverage, parameter defaults,
  preset-application command expansion.
- **Render (`Sprocket.Render.Tests`):** golden-frame tests for each primitive effect; determinism for
  animated effects using `frameTime`; transparent-border behavior where relevant.
- **Persistence (`Sprocket.Persistence.Tests`):** round-trip for any new parameters, presets, or
  generator payloads.
- **App (`Sprocket.App.Tests`):** preset application order, Inspector grouping, and any bespoke
  day-for-night control mapping.

## Scope guards

- Not a 3D compositor.
- Not a physics simulator.
- Not dependent on OFX or external plugin standards.
- No managed pixel-buffer allocations per frame; every new effect must stay on the established GPU seam.
- Day-for-night should not claim true relighting or full semantic segmentation until mask/tracking work exists.

## On completion

If this graduates from roadmap to scheduled work, add a ledger row or expand the existing Open work note
in [PLAN.md](../../PLAN.md), then update [FEATURES.md](../../FEATURES.md) when any user-visible effect ships.
If a subset ships first, split the remaining scope into smaller feature files rather than letting this one
turn into a grab bag.