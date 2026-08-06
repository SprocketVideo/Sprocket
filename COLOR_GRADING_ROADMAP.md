# Color Grading Roadmap

> Review date: 2026-08-06
>
> Goal: reach practical color-grading parity with leading editorial NLEs before pursuing distinctive post-parity features. DaVinci Resolve is the depth benchmark; Adobe Premiere Pro, Final Cut Pro, and Avid Media Composer define the initial parity bar.

## Executive summary

Sprocket already has a credible SDR correction foundation:

- Exposure, contrast, saturation, and vibrance.
- Temperature and tint white balance.
- Lift, gamma, and gain color wheels with trackball controls.
- RGB master and per-channel parametric curves.
- HSL qualification with matte preview.
- Camera-log input transforms for DJI, ARRI, Sony, Panasonic, Canon, Blackmagic, Fujifilm, and Nikon.
- An ACES-style filmic tone-map effect.
- Waveform, RGB parade, vectorscope, and histogram.
- Non-destructive, keyframeable, undoable effect stacks.
- One render graph and GPU effect path for preview and export.

The largest gap is not shader count. The current pipeline is display-referred and repeatedly clamps intermediate values to `[0, 1]`. That prevents highlight recovery across stages and does not provide the explicit input, working, display, and output transforms expected from a professional color-managed pipeline.

The roadmap therefore prioritizes color integrity first, daily grading ergonomics second, shared-shot workflow third, and advanced automation only after parity.

## Current strengths

### Architecture

- Preview and export resolve the same effect chain.
- Grading is non-destructive and routes through undo/redo.
- Adjustment layers can apply a grade over a sequence range.
- The `IVideoEffect` registry keeps grading effects on the established GPU seam.
- Scope analysis samples the actual monitor composite.
- Camera transforms are prepended before creative clip effects.
- No managed pixel allocation is introduced per frame.

### Existing tools

| Area | Current capability |
|---|---|
| Primaries | Exposure, contrast, saturation, vibrance |
| White balance | Temperature and tint |
| Wheels | Lift, gamma, gain trackballs; master and RGB channels |
| Curves | Five fixed points for RGB master and each color channel |
| Secondaries | HSL range key, softness, hue shift, saturation, exposure, matte view |
| Input transforms | Bundled DJI LUTs and mathematical camera-log curves to Rec.709 |
| Tone mapping | ACES-style fitted filmic tone curve |
| Scopes | Waveform, RGB parade, vectorscope, histogram |
| Reuse | Clip effect stacks and adjustment layers |
| Automation | Keyframing and MCP parameter control |

## Review findings

### Critical: intermediate grading stages clip image data

The color wheels, white balance, curves, qualifier, and input-transform paths clamp intermediate values to `[0, 1]`. Once a stage clips highlights or undershoot, a later stage cannot recover them.

This is acceptable for a bounded SDR effect pipeline, but not for wide-gamut, scene-linear, HDR, or robust log workflows. Decoding 10- or 12-bit media does not preserve its grading latitude if processing immediately reduces it to bounded display RGB.

### High: ACES Filmic is not ACES color management

The current effect decodes sRGB, applies exposure and an ACES fitted tone curve, then encodes sRGB. It is a useful creative tone map, but it does not provide ACES input device transforms, an ACES working space, reference rendering, or output device transforms.

Until full ACES or OpenColorIO management ships, present this feature as **ACES-style Filmic Tone Map** rather than implying an ACES project pipeline.

### High: tool labels exceed interaction depth

- Curves are fixed five-point scalar parameters, not a freeform graphical curve editor.
- The HSL qualifier lacks viewer sampling, add/subtract eyedroppers, visual H/S/L ranges, matte cleanup, and spatial masks.
- White balance lacks a neutral eyedropper and automatic balance.
- The existing `.cube` parser cannot load user creative or technical LUTs.
- Primaries lack dedicated highlights, shadows, whites, blacks, and offset controls.

### High: no explicit grading hierarchy

Sprocket has clip effects and adjustment layers, but no named grading scopes separating:

1. Input transform.
2. Shared camera or scene balance.
3. Per-shot correction.
4. Shared creative look.
5. Timeline grade.
6. Output transform.

A node graph is not required for editorial-NLE parity. Named scopes, linked grades, and clear color-space boundaries would provide most of the workflow value with less complexity.

### Medium: scopes are SDR guides rather than finishing scopes

The scopes use a downscaled RGBA8888 monitor sample and 256 signal levels. They are useful for SDR editorial grading but currently lack:

- Data-level versus video-level selection.
- Pre-transform versus post-transform taps.
- HDR nit scales and PQ/HLG interpretation.
- Skin-tone and color-target overlays.
- Chromaticity and gamut warnings.
- Brightness, persistence, zoom, and simultaneous layouts.

### Medium: tests prove mechanics, not color fidelity

Existing tests establish neutral states and representative pixel changes. They do not yet establish trustworthy colorimetry across charts, transfer functions, bit depths, ranges, or effect orderings.

## Competitive parity matrix

| Area | Sprocket today | Editorial-NLE parity | Resolve depth |
|---|---|---|---|
| SDR primaries | Good foundation | Mature tonal controls and auto balance | Extensive primary, log, and HDR palettes |
| Color wheels | Three-way LGG | Standard three-way wheels | Primary, log, and zone-based HDR wheels |
| Curves | Fixed parametric points | Freeform RGB and hue/saturation curves | Extensive curve families and color warper |
| Secondaries | Numeric HSL key | Eyedropper, masks, feathering, tracking | Qualifiers, windows, tracking, semantic masks |
| Scopes | Four basic post-display scopes | Configurable scopes and overlays | HDR, gamut, chromaticity, advanced layouts |
| LUTs and looks | Bundled camera LUTs only | User LUT import and look presets | Full technical and creative LUT workflow |
| Shot matching | None | Comparison view and match tools | Gallery, stills, lightbox, chart and shot match |
| Color management | Rec.709-oriented | Managed SDR/HDR project spaces | RCM, ACES, wide gamut, Dolby Vision/HDR |
| Grade reuse | Adjustment layers | Source/shared effects and presets | Groups, shared nodes, stills, remote grades |
| Control surfaces | None | Third-party mappings | Deep native panel integration |

## Roadmap

### Phase 1: Color pipeline foundation

**Objective:** preserve image latitude through the complete render chain and make every color-space transition explicit.

#### Deliverables

- Use floating-point render surfaces for color processing.
- Preserve extended-range values between effects; do not clamp at every stage.
- Model explicit input, working, display, and output color spaces.
- Introduce an internal wide-gamut working space suitable for SDR and HDR.
- Host OpenColorIO through the planned C ABI boundary, or implement an equivalent tested transform layer while retaining an OCIO upgrade path.
- Separate input transforms from creative effects in the model and Inspector.
- Add display-view transforms that do not alter exported scene data.
- Add output transforms and correct color metadata for Rec.709, PQ, and HLG exports.
- Handle full and limited range explicitly.
- Rename the current ACES effect to **ACES-style Filmic Tone Map** until real ACES management exists.

#### Acceptance criteria

- A value above `1.0` survives multiple neutral or lowering grading stages and can be recovered before output mapping.
- Preview and export remain visually equivalent under the same display/output transform.
- SDR, PQ, and HLG projects select appropriate working and output transforms.
- Input, working, display, and output transforms are visible and independently bypassable.
- Proxy preview differences are documented and never affect full-resolution export.
- Steady-state playback still allocates no managed pixel buffers per frame.

### Phase 2: Daily correction parity

**Objective:** make common correction tasks as direct as Premiere, Final Cut Pro, or Avid Symphony.

#### Deliverables

- Graphical freeform RGB master and per-channel curve editor.
- Histogram overlay behind the curve.
- Arbitrary point add, move, delete, reset, and channel selection.
- Highlights, shadows, whites, blacks, and offset primary controls.
- Neutral white-balance eyedropper in the Program monitor.
- Automatic white balance and conservative automatic tonal balance.
- User import of `.cube` technical and creative LUTs.
- LUT intensity/mix and domain validation.
- Saved color presets and reusable looks.
- Dedicated reset and bypass controls per grading section.

#### Acceptance criteria

- Curves can produce standard S-curves and independent channel corrections without editing scalar rows.
- White balance can be sampled directly from the displayed frame with one undoable action.
- Imported LUTs render identically in preview and export and survive project round trips.
- Invalid LUTs fail with an actionable message and do not corrupt the project.
- Auto adjustments are represented as ordinary editable parameters rather than an opaque effect.

### Phase 3: Secondaries, masks, and tracking

**Objective:** reach ordinary professional secondary-correction parity.

#### Deliverables

- Viewer eyedropper for HSL qualification.
- Add-to-sample and subtract-from-sample modes.
- Interactive hue, saturation, and luma range strips.
- Matte cleanup controls, including denoise, clean black, clean white, blur, and in/out ratio.
- Ellipse, rectangle, gradient, and freeform masks.
- Feather, expansion, opacity, and invert controls.
- Combine qualifier and spatial mask by intersection, union, or subtraction.
- Forward, backward, and bidirectional mask tracking.
- Manual keyframe correction after tracking.

#### Acceptance criteria

- A user can isolate a subject color without typing degree or range values.
- Matte preview clearly distinguishes selected, partial, and rejected pixels.
- Masks and tracking are non-destructive, undoable, persist correctly, and render identically in export.
- Tracking failure is visible and recoverable rather than silently producing a bad mask.

### Phase 4: Shot and sequence workflow

**Objective:** make matching and maintaining a sequence faster than copying independent clip stacks.

#### Deliverables

- First-class grade scopes for input, shared group, clip, timeline, and output stages.
- Camera and scene groups with shared pre-clip and post-clip grades.
- Linked grade instances that update every subscribing clip.
- Copy grade, paste grade, append grade, and paste selected corrections.
- Reference still capture and named still gallery.
- Horizontal/vertical wipe, split-screen, difference, and picture-in-picture comparison.
- Side-by-side current and reference scopes.
- Adjustment-layer creation directly from the grading workspace.
- Grade versions with quick audition and restore.

#### Acceptance criteria

- Updating a shared camera balance updates all linked shots without duplicating stacks.
- Per-shot corrections remain independent from the shared look.
- References retain the frame, grade metadata, project color context, and a useful label.
- Users can compare adjacent shots without moving or altering the timeline.

### Phase 5: Finishing and confidence

**Objective:** make Sprocket dependable for measured delivery, not only visual correction.

#### Deliverables

- Configurable waveform scale: IRE, code values, and HDR nits.
- Scope taps before and after input, creative, and output transforms.
- Simultaneous two- and four-scope layouts.
- Vectorscope skin-tone line and selectable target boxes.
- CIE chromaticity/gamut scope.
- Out-of-gamut and out-of-range overlays.
- Scope intensity, persistence, zoom, and quality settings.
- Broadcast-safe limiter as an explicit output stage.
- MIDI or established grading-panel protocol mapping where feasible.
- Color-pipeline diagnostics in Doctor and project settings.

#### Acceptance criteria

- Scope labels always identify their color space, transfer function, range, and tap point.
- HDR scopes express meaningful absolute luminance values.
- Gamut warnings agree with the selected delivery space.
- Output limiting is measurable, bypassable, and never silently applied.

## Validation strategy

Color work needs numerical and visual evidence beyond center-pixel assertions.

### Numerical fixtures

- ColorChecker patches with measured expected values and Delta E tolerances.
- Neutral grayscale ramps for monotonicity, neutrality, and clipping checks.
- Extended-range ramps below `0.0` and above `1.0`.
- Full-range and limited-range equivalence fixtures.
- 8-, 10-, and 12-bit source equivalence within documented tolerances.
- Known camera-log chart samples for every input profile.
- LUT identity, interpolation, domain, and boundary tests.

### Pipeline fixtures

- Effect-ordering tests that prove input transforms run before creative grades and output transforms run last.
- Preview/export golden-frame parity for SDR, PQ, and HLG.
- Nested sequence, transition, adjustment-layer, and proxy/full-resolution cases.
- Repeated grade round-trip tests through persistence and undo/redo.
- No-allocation profiling on the decode, grade, composite, and scope hot paths.

### Visual verification

- Calibrated chart projects covering mixed cameras and mixed transfer functions.
- Banding inspection on smooth gradients.
- Scope verification against trusted reference software for the same frames.
- Cross-platform screenshots and exported-frame comparisons on Windows, Linux, and macOS.

## Surprise and delight after parity

These features should come after the color pipeline and ordinary grading workflow are trustworthy.

### Continuity map

Display every shot as a compact fingerprint of luminance, white balance, saturation, and dominant palette. Flag shots that drift from adjacent clips and jump directly to them.

The value is not automatic grading; it is making sequence inconsistency visible at a glance.

### Explainable shot matching

Suggest exposure, temperature, contrast, saturation, and curve adjustments separately, each with a confidence value and enable toggle. Applying the suggestion creates normal editable parameters.

This avoids the opaque one-button-match problem and teaches users what changed.

### Live delta scopes

Overlay the pre-grade waveform or vectorscope as a subdued trace behind the current result. A colorist can see both the result and exactly how the correction moved the signal.

### Intent locks

Allow the user to preserve selected properties while grading:

- Neutral-pixel balance.
- Skin-tone hue.
- Black level.
- Highlight headroom.
- Average scene luminance.

The interface must show which locks are active and how strongly they constrain the operation.

### LUT decomposition

Approximate an imported creative LUT as editable wheels, curves, and hue adjustments, then show the residual error. The user receives an understandable starting grade rather than an inscrutable transform.

### Grade audition strip

Generate several controlled variations around the current grade, such as warmer/cooler, softer/harder contrast, or restrained/richer saturation. Selecting one updates ordinary parameters and remains fully undoable.

### Temporal scope trails

Show a short fading history of waveform or vectorscope traces during playback. This makes exposure flicker, white-balance drift, and intermittent gamut excursions easier to spot.

### Grade provenance

Present a compact signal-path view of camera transform, shared balance, shot correction, creative look, timeline grade, and output transform. Every stage has a clear color-space label and one-click bypass.

### Consistency-aware paste

When pasting a grade to multiple clips, optionally adapt only the balancing stage to each shot while preserving the creative look exactly. Preview the proposed per-shot deltas before committing.

### Story palette view

Extract representative colors from each scene and display how the palette evolves across the timeline. This helps editors evaluate intentional color progression without replacing human creative decisions.

## Product principle

Sprocket should not try to out-node Resolve immediately. Its opportunity is to make professional color behavior unusually understandable:

- Keep the signal path visible.
- Make automation explain itself.
- Preserve every automated result as editable parameters.
- Make sequence consistency easy to inspect.
- Never imply ACES, HDR, or colorimetric guarantees that the pipeline does not yet provide.

Resolve wins on depth. Sprocket can differentiate through clarity, deterministic preview/export behavior, and grading workflows that help editors understand what the image pipeline is doing.

## Reference products

The parity decisions in this roadmap were compared against current official product documentation as of 2026-08-06:

- [DaVinci Resolve Color](https://www.blackmagicdesign.com/products/davinciresolve/color)
- [Adobe Premiere color correction effects](https://helpx.adobe.com/premiere-pro/using/color-correction-adjustment.html)
- [Adobe Premiere color workflows](https://helpx.adobe.com/premiere-pro/using/color-workflows.html)
- [Final Cut Pro User Guide](https://support.apple.com/guide/final-cut-pro/welcome/mac)
- [Avid Media Composer](https://www.avid.com/media-composer)

## Relationship to authoritative project documents

This roadmap expands the outstanding color-management work in PLAN step 33, the completed grading foundation in step 34, and the log-media work in steps 37 and 52. It does not replace `PLAN.md`, `ARCHITECTURE.md`, or `FEATURES.md`.

When a roadmap item is scheduled or implemented:

1. Add or amend the corresponding build-order entry in `PLAN.md`.
2. Preserve the architecture constraints in `ARCHITECTURE.md`, especially the shared preview/export path and no managed per-frame pixel allocation.
3. Update only the affected rows in `FEATURES.md` when user-facing behavior changes.
4. Check whether the coarse-grained feature and roadmap sections in `README.md` need an update.
