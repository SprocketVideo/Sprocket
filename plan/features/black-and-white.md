# Black & White conversion (film-emulation grade monochrome)

🟡 **In progress (phases 1–3 of 5 shipped).** Unscheduled feature (no build-order step number yet); tracked in
[PLAN.md](../../PLAN.md) Open work. Relative links resolve from the repo root.

**Scope in one line:** a dedicated `Black & White` effect (`builtin.blackwhite`, short code `BW`)
that converts footage to monochrome with granular control over *how* colour maps to tone
(per-hue channel mixer + optical colour filter), a film-style tone response, procedural grain,
toning / split-toning, vignette and a dry/wet mix — shipped with a curated preset library of
well-known B&W looks (colour filters, classic film stocks, darkroom tonings, cinematic looks).

Today the only route to B&W is `Color ▸ Saturation = 0`
([EffectCatalog.cs](../../src/Sprocket.Core/Model/EffectCatalog.cs) `builtin.color`), which
gives a flat Rec.709-luma desaturation with no control over channel weighting, no film
character and no tinting.

## Why / product context

Monochrome is one of the most-used creative treatments, and every leading tool treats it as
more than "saturation 0":

| Tool | What it offers | What we borrow |
|---|---|---|
| **Lightroom / Photoshop "Black & White"** | Eight per-hue luminance sliders (Reds, Oranges, Yellows, Greens, Aquas, Blues, Purples, Magentas), an *Auto* mix, a **Tint** (hue + saturation), and presets named for optical filters (Red / Yellow / Green / Blue Filter, High Contrast Red, Infrared, Lighter / Darker, Maximum Black / White, Neutral Density). | The **eight-hue channel mixer** as the primary granular control — far more intuitive than an RGB weight matrix — and the filter-named preset family. |
| **Nik Silver Efex Pro** (the reference B&W tool) | Brightness / Contrast / Structure, a **colour filter** (Red / Orange / Yellow / Green / Blue with hue + strength), **film types** (Ilford HP5 / Delta / Pan F / XP2, Kodak Tri-X / T-Max / Plus-X / BW400CN, Fuji Neopan Acros, Agfa APX) each combining spectral sensitivity + curve + grain, **grain** (per-pixel + hardness), **toning** (sepia, selenium, cyanotype, split, coffee…), **vignette**, burn edges, borders. | The overall module layout — *Conversion → Film → Finishing* — the colour-filter model, film-stock presets built from (sensitivity, curve, grain), toning + vignette as finishing. Borders/burn-edges are out of scope. |
| **DaVinci Resolve** | RGB Mixer **Monochrome** checkbox with per-channel weights; Film Look Creator (grain, halation, vignette); Saturation 0 on a node. | Weight-based conversion semantics (our per-hue mixer reduces to an RGB mixer for the primaries) and grain-as-part-of-the-look. |
| **Premiere Pro** | `Black & White` (no parameters) and Lumetri Saturation 0; Creative Looks include a few monochrome film LUTs. | Nothing beyond the effect name. Premiere's zero-parameter effect is the gap we fill. |
| **Final Cut Pro** | `Black & White` effect with a small preset pop-up. | Effect name; presets-in-Inspector convention (we already have this via step 41). |

**Naming:** the effect is **"Black & White"** (Premiere / FCP / Lightroom all use it). The
per-hue sliders reuse Lightroom's eight names. Filter presets use the photographic Wratten
colours (Red 25 / Orange / Yellow 8 / Green 11 / Blue). Toning presets use darkroom terms
(Sepia, Selenium, Cyanotype, Platinum).

**Film-stock names (decided 2026-09-21): generic preset names, stock in the tooltip.** Preset
names describe the *look* ("Classic 400", "Fine Grain 100", "Pushed 3200"), and the preset's
tooltip names the stock it is inspired by ("Inspired by Kodak Tri-X 400"). This avoids trading
on trademarks in the UI while keeping the reference discoverable — the approach Resolve's Film
Look Creator and most LUT packs take, versus Silver Efex / DxO FilmPack's nominative use.
Presets are *inspired by* the stock's published characteristic curve and spectral sensitivity,
not measured — the tooltip and docs say so. This needs one additive model change:
`EffectPreset` gains an optional `Description` (see the sketch) that `InspectorPanel.BuildPresetRow`
shows as the combo item's tooltip — every existing preset keeps `null`.

**Deliberate departures:**
- **Continuous filter hue/strength instead of a filter dropdown.** Silver Efex picks the filter
  from a list; we store `FilterHue` (°) + `FilterStrength` (%) so the filter is *keyframeable*
  (dropdowns are constant-only in our model, `ParameterKind.Dropdown`) and presets simply set
  the standard hues. The Inspector reads the same.
- **No "Structure"/clarity at v1.** Local contrast needs a blurred copy of the frame (a second
  pass / downsample chain); our registry effects are single-pass `src.eval` shaders. Listed as a
  follow-on below rather than faked with a fixed 5×5 kernel that would alias at 4K.
- **Grain is procedural**, not scanned. Deterministic hash noise seeded by frame time, so preview
  and export match frame-for-frame (§5) and there is no texture asset to bundle.

## Existing seams to build on

| Piece | Where | What it gives us |
|---|---|---|
| Registry shader effects (`IVideoEffect`: descriptor + SkSL + `BindUniforms`) | [IVideoEffect.cs](../../src/Sprocket.Core/Rendering/IVideoEffect.cs); built-ins in [src/Sprocket.Render/Effects/](../../src/Sprocket.Render/Effects/) — `WhiteBalanceEffect`, `ColorWheelsEffect`, `CurvesEffect`, `HslQualifierEffect`, `AcesFilmicEffect` | The exact shape this effect takes: one class, one SkSL program, registered in the `SkiaEffectPipeline` static ctor ([SkiaEffectPipeline.cs:245-253](../../src/Sprocket.Render/SkiaEffectPipeline.cs#L245-L253)). Runs identically in preview / export / thumbnails, no pixels cross to managed code (§1). |
| Descriptor + typed parameters + `Presets` | [EffectCatalog.cs](../../src/Sprocket.Core/Model/EffectCatalog.cs) (`EffectDescriptor`, `EffectParameterDescriptor`, `EffectPreset`); ids/names in [EffectInstance.cs](../../src/Sprocket.Core/Model/EffectInstance.cs) (`EffectTypeIds`, `EffectParamNames`) | The Inspector builds every slider/toggle from the descriptor; `Presets` already drives a preset picker (`InspectorPanel.BuildPresetRow`, applied as one undoable `CompositeCommand` — step 41). **No new UI code is needed for the controls or the presets.** |
| Keyframes | step 16d — any `Continuous` parameter is keyframeable | Filter hue sweeps, grain ramps, fade-to-mono via `Mix`, all free. |
| sRGB↔linear helpers | inline SkSL in `WhiteBalanceEffect` / `AcesFilmicEffect` | Copy the two functions (or hoist to a shared `SkslCommon` const — optional tidy-up). |
| Golden/centre-pixel render tests | [GradingEffectTests.cs](../../tests/Sprocket.Render.Tests/GradingEffectTests.cs), `RegisteredEffectTests` (offscreen CPU backend, `RenderCenter`) | The test harness; B&W tests follow the same pattern. |
| Persistence | `EffectDto` in [ProjectDto.cs](../../src/Sprocket.Persistence/ProjectDto.cs) serialises any effect by id + parameter map | **Zero persistence work** — new effect ids and parameter names round-trip already. |
| MCP | `Sprocket.Mcp` lists effects/parameters from `EffectCatalog.All` | AI clients get the new effect + descriptions for free. |
| Looks browser (future, tier-2 presets) | [looks-browser.md](looks-browser.md) | B&W presets are single-effect looks; when the Looks tab lands, surface the B&W preset library there too. Nothing to do now beyond keeping presets as plain `EffectPreset`s. |

### One small seam extension: per-frame context for registered effects

`BindUniforms(ResolvedEffect, IUniformWriter)` sees only the parameter map. Grain needs a
**frame seed** and vignette needs the **layer bounds**; neither is available to registry effects
today (the wipe transition gets `bounds` because it is a pipeline case, not a registry effect).

Proposed additive change (Core, no signature break for plugins):

- Add `ResolvedEffect.FrameTime` (`long` ticks, default `0`) populated by
  `RenderGraph.PlanVideoFrame` — it already has the time. Pure data; still serialisable.
- The pipeline **auto-binds two reserved uniforms if the compiled program declares them**:
  `sprocket_time` (float seconds, from `FrameTime`) and `sprocket_bounds` (`float4`
  left/top/width/height of the layer rect it already has for `BuildTransformShader`). Detect via
  `SKRuntimeEffect.Uniforms` names once per compile and cache the flags in
  `CachedRegisteredEffect`. Plugins that don't declare them are unaffected.

This is smaller than changing the `IVideoEffect` signature and benefits every future effect
(film flicker, animated noise, radial anything). Record it in ARCHITECTURE §13's SkSL contract.

## Parameter design

All stored in model units; display scaling only where noted. ~27 parameters (Curves has 20).
Order = Inspector order, grouped by comment banners in the descriptor.

**Conversion**
| Name | Display | Default | Range | Notes |
|---|---|---|---|---|
| `Mix` (reuse existing name) | Amount | 1.0 | 0–1, % | Dry/wet against the colour original — partial desaturation and fade-to-mono keyframes. |
| `FilterHue` | Filter Hue | 0 | 0–360 ° | Hue of the virtual optical filter in front of the "lens". |
| `FilterStrength` | Filter Strength | 0 | 0–1, % | 0 = no filter. Applied *before* the mixer, in linear light, luma-normalised so exposure doesn't shift. |
| `MixReds` … `MixMagentas` (8) | Reds, Oranges, Yellows, Greens, Aquas, Blues, Purples, Magentas | 0 | −100…100 | Lightroom's per-hue luminance sliders. Positive = that hue renders lighter. |

**Film (tone response)**
| Name | Display | Default | Range | Notes |
|---|---|---|---|---|
| `Exposure` (existing) | Brightness | 0 | −3…3 EV | Applied to the monochrome signal. |
| `Contrast` (existing) | Contrast | 1.0 | 0–2 | Around mid-grey, matches `Color`. |
| `Shadows` | Shadows | 0 | −1…1 | Toe lift/crush (filmic toe). |
| `Highlights` | Highlights | 0 | −1…1 | Shoulder roll-off / lift. |
| `GrainAmount` | Grain | 0 | 0–1, % | Luma-weighted (strongest in mids, like real emulsion). |
| `GrainSize` | Grain Size | 1.0 | 0.5–4 | Noise cell size in source pixels — resolution-aware so 1080p and 4K look alike. |
| `GrainSeedLock` | Static Grain | 0 | Toggle | On = one noise field for the whole clip (useful for stills / stop-motion); off = re-seeded per frame. |

**Finishing**
| Name | Display | Default | Range | Notes |
|---|---|---|---|---|
| `ToneHue` | Tone Hue | 35 | 0–360 ° | Colour of the tint (35° ≈ sepia). |
| `ToneStrength` | Tone Strength | 0 | 0–1, % | 0 = pure neutral. |
| `SplitShadowHue` / `SplitHighlightHue` | Shadow Hue / Highlight Hue | 35 / 210 | 0–360 ° | Split toning. |
| `SplitStrength` | Split Strength | 0 | 0–1, % | |
| `SplitBalance` | Split Balance | 0 | −1…1 | Where the shadow/highlight crossover sits. |
| `VignetteAmount` | Vignette | 0 | −1…1 | Negative = darken edges (classic), positive = lighten. |
| `VignetteSize` | Vignette Size | 0.7 | 0–1.5 | Radius relative to half-diagonal. |
| `VignetteSoftness` | Vignette Softness | 0.5 | 0–1 | |

**Shader order** (single pass, all inside `main`): unpremultiply → sRGB→linear → optical filter
(multiply by filter colour, luma-normalised, lerp by strength) → per-hue mixer (hue/sat of the
*original* pixel selects a blended weight across the 8 bands; weight scales luma, gated by
saturation so greys are untouched) → Rec.709 luma → exposure/contrast/toe/shoulder → linear→sRGB
→ grain (hash noise on `floor(coord / GrainSize)` + `sprocket_time` seed, luma-weighted) → toning
(single tone = lerp toward a hue at the tone's luma; split = two-hue lerp by luma with balance)
→ vignette (radial from `sprocket_bounds` centre) → `Mix` lerp against the original → clamp
`rgb ≤ a`, repremultiply.

## Preset library

Flat `EffectPreset` list; names prefixed by family so the step-41 combo groups visually
(`"Filters ▸ Red"`). Every preset leaves **Amount (`Mix`) untouched** (the Studio Reverb rule) so
switching looks keeps the user's dry/wet. Filter presets set only conversion params; film
presets set conversion + film; toning presets set only finishing; cinematic presets set all
three. That way presets from different families layer sensibly (pick a film, then a toning).

- **Neutral:** Neutral (all defaults, the reset), Flat / Low Contrast, High Contrast, High Key,
  Low Key, Maximum White, Maximum Black.
- **Filters** (Wratten-style, strength ≈ 0.6–0.8): Yellow (8) — gentle sky, Orange (16),
  Red (25) — dramatic dark skies, Deep Red (29), Green (11) — foliage & skin, Blue (47),
  Infrared — greens white, blues black, slight glow via Highlights.
- **Film stocks** (sensitivity via mixer, curve via contrast/shadows/highlights, grain via
  amount/size) — generic name → tooltip "Inspired by …":

  | Preset name | Inspired by | Character |
  |---|---|---|
  | Classic 400 | Kodak Tri-X 400 | Gritty grain, punchy contrast, the street/reportage look |
  | Modern Fine Grain 100 | Kodak T-Max 100 | Tabular grain, very smooth, neutral tones |
  | Modern 400 | Kodak T-Max 400 | Fine for its speed, slightly cool, clean shadows |
  | Pushed 3200 | Kodak T-Max P3200 | Coarse grain, lifted shadows, low-light |
  | Vintage 125 | Kodak Plus-X 125 | Soft midtones, gentle roll-off, 1950s–60s feel |
  | Chromogenic 400 (K) | Kodak BW400CN | Very fine grain, low contrast, smooth skin |
  | Traditional 400 | Ilford HP5 Plus 400 | Classic cubic grain, forgiving latitude, warm mids |
  | Traditional 125 | Ilford FP4 Plus 125 | Fine grain, rich midtones, long tonal range |
  | Tabular 100 | Ilford Delta 100 | Sharp, smooth, slightly higher contrast than Fine Grain 100 |
  | Tabular 400 | Ilford Delta 400 | Fine grain, crisp, neutral |
  | Tabular 3200 | Ilford Delta 3200 | Heavy but even grain, soft contrast |
  | Ultra Fine 50 | Ilford Pan F Plus 50 | Near-grainless, high acutance, strong contrast |
  | Chromogenic 400 (I) | Ilford XP2 Super | Smooth, low grain, slightly flat, bright highlights |
  | Orthopanchromatic 100 | Fujifilm Neopan Acros 100 | Extremely fine, reds render darker, cool clean look |
  | Reportage 400 | Fujifilm Neopan 400 | Medium grain, snappy contrast |
  | Reportage 1600 | Fujifilm Neopan 1600 | Pronounced grain, deep blacks |
  | European 100 | Agfa APX 100 | Classic grain structure, warm tonality |
  | European 400 | Agfa APX 400 | Coarser grain, gentle contrast |
  | Near-Infrared 80 | Rollei Retro 80S | Extended red sensitivity: pale skin & foliage, dark skies |

  Names must stay unique and descriptive; if two references would collapse to the same generic
  name, disambiguate by character (as with the two chromogenic entries) rather than by brand.
- **Toning** (finishing only): Sepia, Warm Sepia, Cool Sepia, Selenium (subtle purple-brown
  shadows), Cyanotype, Platinum / Palladium, Coffee / Antique, Gold Tone, Split — Warm Shadows
  / Cool Highlights, Split — Cool Shadows / Warm Highlights.
- **Cinematic:** Film Noir (red filter, high contrast, crushed shadows, strong vignette),
  Silent Era (soft contrast, heavy grain, sepia, vignette, static grain off), Newsreel
  (mid grain, slight flat), Modern Cinema (clean, high contrast, no grain), Documentary
  (neutral, light grain), Street / Pushed (Tri-X-like pushed two stops), Fashion High Key,
  Dramatic Landscape (red filter, deep shadows), Portrait (green/orange filter mix, soft
  highlights, fine grain).

~50 presets. If the combo gets unwieldy in practice, a grouped/searchable preset picker is a
generic Inspector follow-on (also wanted by the Multi-Tap Delay note), not a B&W concern.

## Build phases

The feature is too large for one session, so it ships in **five independently mergeable
phases**, each usable on its own and each sized for a single context window. **Start a phase
in a fresh session by reading only: this section, the "Existing seams" table, the "Parameter
design" rows the phase adds, and the phase's own files.** The whole-picture
[Implementation sketch](#implementation-sketch) below is reference, not a checklist. Phase
status lives in the checklist here; flip the box and add a dated one-liner when a phase lands.

| Phase | Ships | Depends on | Size |
|---|---|---|---|
| [1](#phase-1--conversion-core) | Effect exists: Amount, optical filter, 8-hue mixer, brightness/contrast/toe/shoulder. Presets: `Neutral` only. | — | ~½ session |
| [2](#phase-2--frame-context-seam-grain-vignette) | Registry-effect frame context (`sprocket_time` / `sprocket_bounds`) + grain + vignette | 1 | ~½ session |
| [3](#phase-3--toning--preset-descriptions) | Toning + split toning; `EffectPreset.Description` + Inspector tooltip | 1 | ~½ session |
| [4](#phase-4--preset-library-non-film) | Neutral / Filters / Toning / Cinematic presets (~30) | 1–3 | ~½ session |
| [5](#phase-5--film-stock-presets--docs) | 19 film-stock presets (tuning-heavy) + user docs + FEATURES/README/PLAN close-out | 1–4 | 1 session |

- [x] Phase 1 — Conversion core (2026-09-21): `builtin.blackwhite` (short code `BW`, category Color)
  with Amount, optical filter (hue + strength), the 8-hue mixer, and brightness/contrast/shadow-toe/
  highlight-shoulder; `BlackWhiteEffect` SkSL registered in the pipeline; single `Neutral` preset. Core
  catalog tests + Render centre-pixel tests (neutral = Rec.709 mono, `Mix=0` pass-through, red filter,
  `MixReds` saturation gating, tone-control monotonicity) green.
- [x] Phase 2 — Frame-context seam, grain, vignette (2026-09-21): `ResolvedEffect.FrameTime` (`long` ticks,
  default 0) populated by `RenderGraph.ResolveEffectsCore` from the frame's timeline time. Pipeline auto-binds
  the reserved `sprocket_time` (seconds) / `sprocket_bounds` (layer rect) uniforms when a compiled registry
  program declares them (detected once per compile, cached in `CachedRegisteredEffect`; `dest` threaded into
  `BuildRegisteredEffectShader`) — effects declaring neither bind unchanged. New params `GrainAmount` /
  `GrainSize` / `GrainSeedLock` (toggle) / `VignetteAmount` / `VignetteSize` / `VignetteSoftness` + descriptor
  rows + Neutral-preset defaults. `BlackWhiteEffect` SkSL extended: luma-weighted hash grain (re-seeded per
  frame from `sprocket_time` unless Static Grain) and radial vignette from `sprocket_bounds`. ARCHITECTURE §13
  documents the two reserved uniforms. Tests: Core `FrameTime` population + updated catalog/parameter-kind
  lists; Render grain determinism/per-frame variation/static-lock, vignette corner-vs-centre + pass-through,
  and pipeline auto-bind positive + plugin regression — all green.
- [x] Phase 3 — Toning + preset descriptions (2026-09-21): six new `EffectParamNames` — `ToneHue` / `ToneStrength`
  (single-tone tint) and `SplitShadowHue` / `SplitHighlightHue` / `SplitStrength` / `SplitBalance` (split toning) —
  with descriptor rows in the Finishing group (before the vignette) and Neutral-preset defaults (tone/split hues
  35 / 35 / 210, strengths 0). `EffectPreset` gains an optional `string? Description` (additive; every existing
  preset stays `null`), which `InspectorPanel.BuildPresetRow` now shows as the combo item's tooltip via a
  `FuncDataTemplate<EffectPreset>`. `BlackWhiteEffect` SkSL gains a toning stage between grain and vignette:
  single tone lerps each pixel toward its hue carried at the pixel's own luma; split toning picks the hue per
  pixel between the shadow/highlight hues by luma, with balance shifting the crossover. All 27 parameters now
  present; the effect is feature-complete (presets still just `Neutral`). Tests: Core catalog parameter list +
  `EffectPreset.Description` default/round-trip; Render single-tone hue-at-mid-grey, strength-0 neutral, and
  split shadows/highlights carrying their hues — all green.
- [ ] Phase 4 — Preset library (non-film)
- [ ] Phase 5 — Film-stock presets + docs + close-out

### Phase 1 — Conversion core

**Goal:** a working `Black & White` effect that already beats every NLE's built-in.

- **Core:** `EffectTypeIds.BlackWhite = "builtin.blackwhite"`; new `EffectParamNames`:
  `FilterHue`, `FilterStrength`, `MixReds`, `MixOranges`, `MixYellows`, `MixGreens`, `MixAquas`,
  `MixBlues`, `MixPurples`, `MixMagentas`, `Shadows`, `Highlights` (reuse `Mix`, `Exposure`,
  `Contrast`). Descriptor in `EffectCatalog.BuiltIns` after `HslQualifier` (category `Color`,
  `ShortCode = "BW"`), with the **Conversion** and **Film tone** rows from the parameter table
  (not grain — that is phase 2). Presets: a single `Neutral` (all defaults) so the preset row
  exists from day one.
- **Render:** `src/Sprocket.Render/Effects/BlackWhiteEffect.cs` — SkSL: unpremultiply →
  linear → filter → mixer → luma → exposure/contrast/toe/shoulder → sRGB → `Mix` lerp →
  repremultiply. Register in the `SkiaEffectPipeline` static ctor. The 8 mixer weights bind as
  one `float[8]` uniform — allocate it once as a field (the effect is stateless otherwise; the
  array is written then copied by Skia synchronously, so a single shared scratch is safe only if
  `BindUniforms` is never concurrent — it isn't per §13 one-pipeline-per-thread? **Verify**; if
  not, `stackalloc` into a `Span<float>` overload on `IUniformWriter` is the clean fix).
- **Tests:** Core catalog tests (id present, short code unique, `CreateInstance` neutral);
  Render tests per the [Tests](#tests) list for neutral / `Mix=0` / red filter / `MixReds` /
  ramp monotonicity.
- **Docs:** FEATURES.md row → status `🟡 partial (phase 1/5)`, source-of-truth column updated
  to the real files.
- **Exit:** effect appears in Effects browser ▸ Color, sliders work + keyframe, undo works,
  export matches preview on the sample project.

### Phase 2 — Frame-context seam, grain, vignette

**Goal:** the seam every future animated/radial effect needs, then the two consumers.

- **Core:** `ResolvedEffect.FrameTime` (`long` ticks, default 0), populated in
  `RenderGraph.PlanVideoFrame` (find where `ResolvedEffect` is constructed — also transitions'
  per-side effect lists). New `EffectParamNames`: `GrainAmount`, `GrainSize`, `GrainSeedLock`,
  `VignetteAmount`, `VignetteSize`, `VignetteSoftness`; add the descriptor rows.
- **Render:** in `BuildRegisteredEffectShader`, after compile, inspect
  `SKRuntimeEffect.Uniforms` for `sprocket_time` / `sprocket_bounds` and cache two bools in
  `CachedRegisteredEffect`; bind them when present (`bounds` = the layer rect already passed to
  `BuildTransformShader` — thread it into `BuildRegisteredEffectShader`). Extend
  `BlackWhiteEffect` SkSL: grain (hash noise on `floor(coord / GrainSize)` + time seed unless
  `GrainSeedLock`), vignette (radial from bounds centre). **Deterministic:** same
  `FrameTime` ⇒ identical pixels.
- **ARCHITECTURE §13:** add the two reserved uniforms to the SkSL contract paragraph.
- **Tests:** pipeline regression (an effect declaring neither uniform binds as before — use
  the existing `RegisteredEffectTests` fake); grain determinism / per-frame variation / lock;
  vignette corner vs centre; `RenderGraph` populates `FrameTime`.
- **Exit:** grain visibly animates in preview and is frame-identical in export; vignette
  follows the layer when transformed.

### Phase 3 — Toning + preset descriptions

- **Core:** `EffectParamNames`: `ToneHue`, `ToneStrength`, `SplitShadowHue`,
  `SplitHighlightHue`, `SplitStrength`, `SplitBalance`; descriptor rows. `EffectPreset` gains
  `string? Description { get; init; }` (additive; every existing preset stays `null`).
- **Render:** toning stage in the shader between grain and vignette (single tone = lerp toward
  hue at the pixel's luma; split = shadow/highlight hue lerp by luma with balance).
- **App:** `InspectorPanel.BuildPresetRow` sets the combo item `ToolTip` from `Description`.
- **MCP:** if `SprocketTools` lists presets by name, include `Description`; otherwise nothing.
- **Tests:** sepia hue ≈ 35° at mid-grey; strength 0 neutral; split shadows/highlights carry
  the two hues; a Core test that `Description` round-trips through the descriptor (no
  persistence — presets aren't saved).
- **Exit:** all 27 parameters present; effect is feature-complete; presets still just `Neutral`.

### Phase 4 — Preset library (non-film)

- **Core:** `src/Sprocket.Core/Model/BlackWhitePresets.cs` (static class returning the
  `IReadOnlyList<EffectPreset>`), wired into the descriptor. Families **Neutral**, **Filters**,
  **Toning**, **Cinematic** from the [Preset library](#preset-library) — ~30 presets, names
  prefixed `"Family ▸ Name"`. Every preset leaves `Mix` unset; filter presets touch only
  conversion params, toning presets only finishing params, so they layer.
- **Tests:** every preset key is a declared parameter; no preset sets `Mix`; names unique;
  every preset binds and renders without throwing; neutral-toning presets yield R≈G≈B.
- **Exit:** preset combo is populated and switching looks in the Inspector is a single undo
  step. Tune against the sample clip + a colour-checker still (add a small fixture PNG to
  `tests/Sprocket.Render.Tests` only if needed for the assertions — otherwise tune by eye and
  keep the tests structural).

### Phase 5 — Film-stock presets + docs + close-out

- **Core:** the 19 film presets from the table, generic names, `Description =
  "Inspired by <stock>. <character>."`. Add the brand-token test (names contain none of
  Kodak / Ilford / Fujifilm / Agfa / Rollei / Tri-X / T-Max / HP5 / FP4 / Delta / Pan F / XP2 /
  Neopan / Acros / APX / Retro; the reference lives only in `Description`).
- **Tuning:** the bulk of the session — per stock set mixer sensitivity (e.g. ortho-ish
  stocks: `MixReds` negative; near-IR: `MixReds`/`MixOranges` high, `MixBlues` low), curve
  (contrast/toe/shoulder) and grain amount/size scaled by ISO. Keep a short rationale comment
  per preset.
- **Docs:** `../sprocket-docs` page `effects-color/black-and-white.md` (conversion controls,
  presets by family, "inspired by" disclaimer); FEATURES.md row → `✅`, Docs column filled;
  README Features mention if the color-grading bullet enumerates effects; PLAN.md todo →
  `[x]` with date; DONE log appended to the current `plan/history/` chunk; archive this file's
  open parts per "On completion".
- **Exit:** everything in [Tests](#tests) green; docs deployed.

## Implementation sketch

Whole-picture reference; the phases above are the checklist.

1. **Core (model):** add `EffectTypeIds.BlackWhite = "builtin.blackwhite"`, the new
   `EffectParamNames` (reuse `Mix`, `Exposure`, `Contrast`), the descriptor in
   `EffectCatalog.BuiltIns` (category `Color`, `ShortCode = "BW"`, `Presets` from a
   `BlackWhitePresets` static class kept beside the catalog so the catalog file stays readable),
   `ResolvedEffect.FrameTime`, populated in `RenderGraph.PlanVideoFrame`. Pure data, no UI.
   Add `string? Description { get; init; }` to `EffectPreset` (additive; existing presets keep
   `null`) — the film presets carry "Inspired by <stock>. <character>." here, and the MCP
   effect-listing surfaces it alongside the parameter descriptions.
2. **Render:** `src/Sprocket.Render/Effects/BlackWhiteEffect.cs` (`IVideoEffect`, SkSL above),
   registered in the `SkiaEffectPipeline` static ctor. Pipeline: detect + auto-bind
   `sprocket_time` / `sprocket_bounds` for registry effects (cache the has-flags per compiled
   program). Uniforms are floats/`float[]` — no per-frame managed pixel work (§1); check the
   `float[]` for the 8 mixer weights is pooled or a fixed-size field on the effect (the effect is
   shared across pipelines, so a per-call `stackalloc`→copy or a `[ThreadStatic]` scratch; see the
   step-60 churn plan).
3. **Persistence:** none — `EffectDto` already round-trips id + parameters. Old projects load
   unchanged.
4. **UI (App):** `InspectorPanel.BuildPresetRow` sets each combo item's `ToolTip` from
   `EffectPreset.Description` when non-null (the only UI change). Verify the Effects browser
   lists the effect under Color and the preset row appears.
5. **ARCHITECTURE §13:** document the two reserved auto-bound uniforms in the SkSL contract.
6. **Docs/inventory:** the FEATURES.md row already exists (added with this plan, ❌); flip its
   Status / point Docs at `effects-color/black-and-white.md` in the shipping change. Check
   README Features for a "Black & white / film emulation" mention.

Effort: see the phase table — roughly three sessions total, the film-stock tuning being the
largest single block.

## Tests

- **Core (`Sprocket.Core.Tests`):** catalog contains `builtin.blackwhite` with unique short code;
  every preset key is a declared parameter name; no preset sets `Mix`; preset names are unique
  and no preset *name* contains a stock/brand token (Kodak, Ilford, Fujifilm, Agfa, Rollei, Tri-X,
  T-Max, HP5, Delta, Neopan, APX…) — the reference lives only in `Description`; `CreateInstance()`
  yields neutral defaults; `RenderGraph` populates `FrameTime`.
- **Render (`Sprocket.Render.Tests`, `GradingEffectTests` pattern):**
  - Neutral defaults on a saturated colour → R=G=B within ±1 and equals Rec.709 luma ±2.
  - `Mix = 0` is pass-through.
  - Red filter darkens pure blue, brightens pure red; luma of a grey card unchanged (normalisation).
  - `MixReds = +100` lightens red, leaves grey untouched (saturation gating).
  - Contrast/shadows/highlights monotonic on a 5-step ramp.
  - Grain 0 = deterministic; grain > 0 with `Static Grain` off differs between two `FrameTime`s
    and is identical for the same `FrameTime` (preview/export determinism); with `Static Grain`
    on it is identical across times.
  - Sepia toning: output hue ≈ 35° at mid-grey; strength 0 = neutral.
  - Vignette negative darkens a corner pixel more than centre; 0 = pass-through.
  - Every preset compiles/binds without throwing and yields R≈G≈B for neutral-toning presets.
- **Pipeline:** an effect that declares neither reserved uniform binds exactly as before
  (regression for plugins).
- **Persistence:** round-trip a clip with the effect + a preset applied.

## Follow-ons (out of scope for v1)

- **Structure / clarity** (local contrast) — needs a multi-pass blur seam for registry effects.
- **Halation / bloom** for the film family — same multi-pass need.
- **Grouped / searchable preset picker** in the Inspector (generic).
- **Surface B&W presets in the Looks browser** when [looks-browser.md](looks-browser.md) lands.
- **User-saved B&W presets** — arrives with the Looks browser's user store rather than a
  per-effect mechanism.

## On completion

Per phase: tick the phase box above with the date, update the FEATURES.md row status
(`🟡 partial (phase n/5)`), and commit. After phase 5: flip this feature's PLAN.md todo (and add a ledger row if it gets a step number), append the
DONE log to `plan/history/steps-58plus.md` (or the current chunk), update the FEATURES.md row
(Status ✅, Docs path) and README Features if warranted — then delete or archive the
no-longer-open parts of this file.
