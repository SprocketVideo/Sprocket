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
| 1 | Primitive image effects | existing effect seam | Glow/bloom, heat haze, shockwave, directional blur, flicker, chromatic aberration, impact shake |
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