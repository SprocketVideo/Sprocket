# Grading presets / creative looks (Looks browser)

❌ **Not started.** Unscheduled feature (no build-order step number yet); tracked in
[PLAN.md](../../PLAN.md) Open work. Relative links below resolve from the repo root.

## Original PLAN.md scope note (moved verbatim, 2026-08-26)

**Future step (unscheduled): grading presets / creative looks.** A looks browser (one-click
creative grades) must follow the **two-tier preset taxonomy** now recorded in
[ARCHITECTURE §18](ARCHITECTURE.md): **tier 1 — technical presets** are the camera-log →
working-space input transforms that already ship as step 37's `builtin.colortransform` — DJI via
LUT (step 37) and every other vendor via math curve (step 52), both addressed by the same
`ColorProfiles.All` append-only index — and are *not* part of this feature; **tier 2 — creative
looks** are what the feature adds — saved parameter bundles over the step-16/34 grading effects via
the existing `EffectDescriptor.Presets` surface (step 41), saved multi-effect *stacks* for compound
looks, and creative `.cube` LUTs sampled through step 37's packed-LUT stage (no new render
machinery). The scope guard: the looks browser lists tier 2 only — camera-conversion transforms
(D-Log→Rec.709 et al.) never appear as looks (that would duplicate steps 37/52 and invite double
application), and looks are authored/applied against normalized Rec.709 footage,
never raw log. This mirrors how leading editors separate camera-conversion transforms from creative
looks (e.g., input LUT vs. creative look, or input color space vs. node LUTs/power grades).

## Existing seams (mapped 2026-08-26 — all the machinery already exists)

| Piece | Where | What it gives us |
|---|---|---|
| `EffectPreset` (name → constant param values) + `EffectDescriptor.Presets` | `src/Sprocket.Core/Model/EffectCatalog.cs` | The tier-2 "saved parameter bundle" shape; applied over current values, omitted params untouched. Only two audio effects populate it today — no grading effect ships presets yet. |
| Preset apply pattern (one undoable `CompositeCommand` of `SetEffectParameterCommand`s) | `src/Sprocket.App/Inspector/InspectorPanel.cs` `BuildPresetRow` | The exact apply mechanic a look reuses. |
| Grading effects (Color, White Balance, Color Wheels, Curves, HSL Qualifier, ACES Filmic) | descriptors in `EffectCatalog.cs`; shaders in `src/Sprocket.Render/Effects/` | The tier-2 effect set looks are authored over. `builtin.colortransform` (tier 1) is **excluded**. |
| `.cube` parser + GPU packing | `src/Sprocket.Render/CubeLut.cs` (`Parse`, `ToPackedImage`) | Creative-LUT machinery; currently fed only from embedded resources by `src/Sprocket.Render/ColorLuts.cs`. |
| Effect-stack serialization DTOs | `src/Sprocket.Persistence/ProjectDto.cs` (`EffectDto`, `AnimatableValueDto`) | The shape a saved multi-effect stack serializes as. |
| Preset-store pattern (built-ins + user JSON, best-effort IO) | `src/Sprocket.Export/ExportPresetStore.cs` + `src/Sprocket.App/UserExportPresets.cs` | Template for a `LooksStore` + `%AppData%/Sprocket/looks.json` wrapper. |
| Tabbed Project-panel browser | `src/Sprocket.App/MediaBrowser/MediaBrowserPanel.cs` (`Tab` enum, `BuildEffects`, `ApplyEffect`) | Where the **Looks** tab slots in, cloning the Effects-tab structure (rows, double-click apply, drag). |
| Command surface | `src/Sprocket.Core/Commands/ModelCommands.cs` (`AddEffectCommand`, `InsertEffectAtCommand`, `SetEffectParameterCommand`) | Applying a look = one `CompositeCommand`; undoable by construction (step 10). |

## Implementation sketch

1. **Model (Core):** a `Look` = name + ordered list of (tier-2 effect type id → constant
   parameter values) — i.e. a saved stack; a single-effect look is the one-entry case. A
   creative-LUT look references a `.cube` asset. Add a `LooksCatalog` (mirroring
   `TransitionCatalog`/`GeneratorCatalog`) of curated built-in looks over
   Color/WhiteBalance/ColorWheels/Curves — **never** `builtin.colortransform`.
2. **Creative LUT effect (Core + Render):** a distinct effect id (e.g. `builtin.lut.creative`)
   so tier-1/tier-2 stay separate types — same packed-LUT SkSL stage as
   `EffectTypeIds.ColorTransform` but loading from a file path (reusing `CubeLut.Parse` +
   `ToPackedImage`; a file-aware loader beside `ColorLuts`), with intensity (mix) param. The
   IR/LUT path persists via the step-28 media-link/relink pattern; a missing LUT passes through
   (§15).
3. **Persistence:** `LooksStore` (serialize looks with `EffectDto`-shaped entries; built-ins
   first, then user looks) + `UserLooks` app wrapper at `%AppData%/Sprocket/looks.json`,
   mirroring `ExportPresetStore`/`UserExportPresets`.
4. **UI (App):** a **Looks** tab in `MediaBrowserPanel` (add `Tab.Looks`, `BuildLooks()`,
   `SelectTab` case) listing built-in + user looks; double-click / drag applies to the selected
   clip as a `CompositeCommand` (insert after any tier-1 transform, which sits at index 0);
   **Save Look…** captures the selected clip's current tier-2 grading stack. Thumbnail swatches
   (the look applied to a neutral gradient/frame) can come later.
5. **Docs/inventory:** add the FEATURES.md row (starts ❌) in the shipping change; consider a
   README Features mention when it lands.

**Comparable-editor behavior:** Premiere's Lumetri Creative Look / Resolve's PowerGrades +
LUT browser / FCP's effect presets — one-click apply, user-saveable, listed separately from
input/camera transforms. Follow their naming ("Looks") and drag-to-clip behavior.

## Tests

- Core: look → command expansion (insert position after tier-1, params applied, undo restores);
  catalog excludes tier-1 ids by construction.
- Persistence: look round-trip; built-in/user merge order; unknown effect id in a user look
  degrades to skip-with-warning, not a crash.
- Render: creative-LUT effect golden-frame vs a reference (reuse the step-37 LUT test pattern);
  missing-LUT pass-through.
- App: Save Look captures only tier-2 grading effects from a mixed stack.
