# Toy cassette camera look (PXL 2000–style)

✅ **Done** — all 6 phases shipped 2026-09-27; implementation log in
[plan/history/steps-58plus.md](../history/steps-58plus.md) (this file is kept as the design record; only the
"Deferred" items listed there remain open). A one-apply
look that makes footage resemble the late-1980s toy camcorder that recorded black-and-white video
onto ordinary audio cassettes — chunky low-res pixels, a low frame rate, heavy black border, smeary
highlights, and hissy band-limited mono sound. Tracked in [PLAN.md](../../PLAN.md) Open work.
Relative links resolve from the repo root.

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
cam" filters. **Take their naming for the primitives** (`Mosaic`, `Posterize Time`, `Echo`) and
offer the one-tap look as a preset on top, like our Day for Night presets.

**Naming (trademark):** follow the black-and-white film-preset rule — the effect/preset gets a
generic name (working name **"Toy Cassette Camera"**), and the brand appears only in the
description/tooltip ("Inspired by the Fisher-Price PXL 2000"). Extend the existing brand-token
guard test (`EffectCatalogTests.BlackWhite_Film_Preset_Names_Carry_No_Brand_Or_Stock_Token`) to
cover it.

## Existing seams to build on

- **Registry SkSL effects** — descriptor in `EffectCatalog.BuiltIns`
  ([EffectCatalog.cs](../../src/Sprocket.Core/Model/EffectCatalog.cs)), ids/param names in
  `EffectTypeIds` / `EffectParamNames` ([EffectInstance.cs](../../src/Sprocket.Core/Model/EffectInstance.cs)),
  shader class in `Sprocket.Render/Effects/*Effect.cs` registered in the `SkiaEffectPipeline` static
  constructor. Reserved uniforms `sprocket_time` (= `ResolvedEffect.FrameTime`, timeline time) and
  `sprocket_bounds` are bound automatically. [FlickerEffect.cs](../../src/Sprocket.Render/Effects/FlickerEffect.cs)
  is the minimal template; B&W grain's cell hash is the deterministic-noise pattern (no RNG).
  Inspector, MCP `list_effect_types`, persistence and A/V routing are descriptor-driven — no
  per-effect code.
- **Time mapping** — `Clip.MapToSource` / `SourceOffset`
  ([Clip.cs](../../src/Sprocket.Core/Model/Clip.cs), order hold ▸ ramp ▸ speed ▸ reverse) is the
  single timeline→source map. Callers: `RenderGraph.ResolveClipLayer` / `ResolveEffects` /
  `ResolveGenerator`, `PlaybackEngine.UseLayers`, `VideoTrackPlayer.PumpAsync` (live decode target),
  `TimelineControl`, `FrameHoldEdits`, MCP `SprocketTools`. The audio planner (`PlanAudioBufferCore`)
  uses it too — anything video-only must not go into the shared map.
- **Audio effects** — `IAudioEffect.Process` (in-place interleaved, allocation-free steady state,
  `Reset()` on seek), factory [BuiltInAudioEffects.cs](../../src/Sprocket.Audio/Effects/BuiltInAudioEffects.cs).
  Reusable: `BiquadBand` (RBJ TDF-II; shelf/peak only today), `DelayLine.TapFrac`, Tape Delay's inline
  wow/flutter LFO (pure function of a sample counter) and `tanh` saturation, Compressor's
  envelope follower.
- **One-click presets** — `EffectDescriptor.Presets` + `FindPreset`/`CreateInstance`; the Effects
  browser DAY FOR NIGHT group ([MediaBrowserPanel.cs](../../src/Sprocket.App/MediaBrowser/MediaBrowserPanel.cs))
  + `TimelineControl.DropEffect`; multi-command precedent `ActionVfxCatalog` (one `CompositeCommand`);
  linked A/V via `Timeline.ClipsLinkedTo`. **Looks are not used** — `LookRules.IsLookEffect` is
  Color-only and single-clip, and this look spans Video-category effects plus linked audio.
- **MCP** — `add_effect` with `preset` ([SprocketTools.cs](../../src/Sprocket.Mcp/SprocketTools.cs)).

## Design decisions (2026-09-27)

1. **Posterize Time is an effect that the Clip reads**, not a clip property and not a shader.
   `builtin.posterizetime` lives in the clip's effect chain (AE/Premiere naming, one-tap stackable,
   undoable as an ordinary `AddEffectCommand`, no DTO change). A new **video-only**
   `Clip.MapToSourceVideo(t)` quantizes *timeline* time to the rate, then runs the existing
   `MapToSource` — so it composes with speed, ramps, reverse and nested sequences, and Frame Hold
   still short-circuits. The audio path keeps plain `MapToSource` (Posterize Time never chops audio).
   - **Grid anchor: timeline time** (floor to multiples of `Δ` from sequence 0, clamped to
     `≥ TimelineStart`), like AE's composition-time behavior. A clip-local grid would shift phase on
     blade splits.
   - **Effect evaluation steps too:** effects on a posterized clip get `FrameTime` and keyframe time
     = the quantized `t` (AE: the whole layer updates at the posterized rate), so toycam noise lines
     and dropouts also step at 15 fps.
   - **The rate is static** (not keyframeable); the first enabled Posterize Time entry wins; the
     entry itself renders as a no-op (filtered out of the shader chain).
2. **The one-tap look is an Effects-browser preset group** ("TOY CASSETTE CAMERA", like DAY FOR
   NIGHT), backed by a small Core *preset stack* that adds video entries to the target's video clips
   and the Cassette entry to its linked audio clips in **one** `CompositeCommand`. The Looks model is
   untouched.
3. **True temporal smear ships last** (phase 6) via a render-plan temporal footprint + `builtin.echo`;
   phases 1–5 ship the look with a spatial smear approximation.

## Phased plan

Every phase is independently mergeable, ends green (`dotnet test Sprocket.slnx`), and **adds or
amends its own FEATURES.md row in the same change** (new rows ❌ undocumented; edit rows in place —
the Planned row at the "Toy cassette camera" entry moves into §4/§5 as its parts ship). Every new
descriptor must pass the catalog-wide tests in `EffectCatalogTests` (defaults in range, `%` ⇔
`DisplayScale`, tooltip on every parameter, preset values in range), `EffectTagsTests` (unique
`ShortCode`), and `InspectorTests` percent round-trip; each new Toggle/Integer/Dropdown needs the
hard-coded lists in `ParameterKindTests` updated.

### Phase 1 — Mosaic (`builtin.mosaic`)

Generic primitive, pure SkSL, no new seam.

- **Core:** `EffectTypeIds.Mosaic`; descriptor (Video category, `ShortCode = "MO"`), AE naming:
  **Horizontal Blocks** (Integer, default 10, 1–1920), **Vertical Blocks** (Integer, default 10,
  1–1080), **Sharp Colors** (Toggle, default off: off averages a small tap grid per block, on takes
  the block-centre sample — AE semantics), plus **Edge Softness** (0–100 %, default 0; our addition
  for the PXL's soft pixels — a smooth blend across block borders). AE defaults are kept; the toycam
  preset sets 120×90.
- **Render:** `MosaicEffect.cs`; block grid computed from `sprocket_bounds` so it tracks the layer
  (transform/crop), not the canvas. Cap the averaging taps (≤ 4×4) — each tap re-evaluates upstream.
- **Shared SkSL:** add a small internal `SkslSnippets` class (cell hash, Rec.709 luma, block-grid
  helper) for Mosaic/toycam/Echo. Existing effects keep their inline copies (no refactor churn).
- **Tests (`Sprocket.Render.Tests`, pattern of `VfxPrimitiveEffectTests`):** pixels uniform within a
  block with Sharp Colors on; block count matches params on a `HorizontalRamp`; 1×… at full
  resolution ≈ pass-through; deterministic (pure function of input).

### Phase 2 — Posterize Time (`builtin.posterizetime`)

- **Core model:** `EffectTypeIds.PosterizeTime`; descriptor (Video category, `ShortCode = "PT"`),
  one param **Frame Rate** (fps, Continuous, default 12 = AE's default, 1–60, step 0.001 so 23.976
  works). Add a descriptor flag (e.g. `IsTimeModifier`) that means "read by the planner, skipped by
  the shader pipeline, not keyframeable"; the inspector hides the keyframe control for it.
- **Clip:** `Clip.PosterizeInterval` (derived from the chain: first enabled entry → `Δ` in ticks,
  `round(TicksPerSecond / fps)`, exact for 12/15/24/30…) and
  `Clip.MapToSourceVideo(Timecode t)` = `MapToSource(QuantizeTimeline(t))`. Also expose
  `Clip.EffectEvalTime(t)` (quantized `t`, or `t` when not posterized) for `FrameTime`/keyframes.
- **Callers switched to the video map:** `RenderGraph.ResolveClipLayer` (media, multicam, nested,
  transitions reaching into handles), `ResolveEffects`/`ResolveEffectsCore` (also use
  `EffectEvalTime`, and drop time-modifier entries from the resolved list), `ResolveGenerator`
  (generators posterize too), `PlaybackEngine.UseLayers`, `VideoTrackPlayer.PumpAsync` (a quantized
  target just stops promoting → cheap hold, same path as slow motion), `FrameHoldEdits.SourceFrameSpan`
  (a hold captures the frame the user actually sees), MCP `SprocketTools` frame lookups, and the
  `TimelineControl` sites that show the displayed frame. `PlanAudioBufferCore` stays on `MapToSource`.
- **Render:** `SkiaEffectPipeline` ignores time-modifier ids (belt-and-braces if one reaches it).
- **Tests:**
  - `Sprocket.Core.Tests` (new `PosterizeTimeTests.cs`, templates in `FrameHoldTests` /
    `VariableRetimeTests`): source time is constant within each `Δ` step and advances at step
    boundaries; composes with 2× speed, a ramp, reverse; Frame Hold wins; timeline-anchored grid is
    unchanged by a blade split; disabled entry = no quantization; effect `FrameTime` is quantized;
    planner (`PlanVideoFrame`) emits identical `SourceTime` across a step; audio plan unchanged
    (`PlanAudioBuffer` spans identical with/without the effect).
  - `Sprocket.Playback.Tests`: posterized clip delivers held frames, no drop storm (pattern of
    `Slow_Motion_Clip_Holds_Are_Delivered_Not_Dropped`).
  - `Sprocket.Export.Tests`: exported frames repeat in runs of `projectRate / 15`.
  - `Sprocket.Persistence.Tests`: a chain with Posterize Time round-trips (ordinary effect entry).

### Phase 3 — Toy Cassette Camera video stage (`builtin.toycam`)

One ordered SkSL stage (cheaper than chaining Mosaic + B&W + noise, since chained taps re-evaluate
upstream): pixel grid → monochrome + low-DR tone → highlight bloom → spatial smear → noise lines +
seeded dropouts → grain shimmer → 4:3 black border.

- **Core:** `EffectTypeIds.ToyCam`; descriptor "Toy Cassette Camera" (Video category,
  `ShortCode = "TC"`, description "Inspired by the Fisher-Price PXL 2000 …"). Parameters (grouped by
  comments like B&W): **Horizontal Pixels** 120 / **Vertical Pixels** 90 (Integer), **Pixel
  Softness** %, **Contrast**, **Black Crush** %, **Highlight Bloom** %, **Smear Length** %, **Smear
  Threshold** %, **Noise Lines** %, **Dropouts** %, **Grain** %, **Border Size** % (inset of a 4:3
  window inside `sprocket_bounds`), **Border Softness** %, **Seed** (Integer 0–999, generator
  precedent). Descriptor `Presets` for single-effect use (Clean / Worn Tape / Low Light) so MCP
  `add_effect preset` works on the stage alone.
- **Render:** `ToyCamEffect.cs` using `SkslSnippets`; monochrome = luma → tone curve (own simple
  curve; users wanting B&W's channel mixer can stack `builtin.blackwhite`). **Spatial smear:** ≤ 8
  taps trailing horizontally from pixels above the threshold, `max` operator (bright-only trail).
  Noise lines/dropouts hash on (row band, `sprocket_time`, Seed) so they step with Posterize Time.
  Premultiplied output, `rgb ≤ a`.
- **Tests:** border pixels black / interior not; output is grey (r=g=b); same `FrameTime` →
  identical pixels, different → noise lines differ; Seed changes the pattern; every preset binds and
  renders (pattern of `GradingEffectTests` B&W preset test); brand-token guard extended — tokens
  "Fisher-Price", "PXL", "PixelVision" checked against toycam descriptor + preset *names* (description
  is allowed to carry the brand).

### Phase 4 — Cassette audio (`builtin.audio.cassette`)

Useful on its own for any "recorded on cassette" sound.

- **Core:** `EffectTypeIds.AudioCassette` (the `builtin.audio.` prefix routes it to the mixer);
  descriptor "Cassette" (Audio category, `ShortCode = "CS"`). Params reuse existing names where
  they exist: **Mono** (Toggle, on), **Low Cut** (Hz, 100) / **High Cut** (`HighCutHz`, 5000),
  **Hiss** (dB below full scale, e.g. −42), **Wow/Flutter** (`WowFlutterDepth`, `WowFlutterRateHz`),
  **Drive** (`Drive`), **AGC Amount** % + **AGC Release** (ms), **Mix** %. Presets mirror the video
  ones (Clean / Worn Tape / Low Light).
- **Audio:** `CassetteEffect.cs`. Add `BiquadBand.ConfigureHighPass` / `ConfigureLowPass` (RBJ, with
  Q; cascade two for 24 dB/oct). Extract Tape Delay's wow/flutter LFO into an internal
  `TapeWowFlutter` helper (counter-driven, reset in `Reset()`) used by both effects — Tape Delay tests
  guard the refactor. Pitch wobble = short `DelayLine` with modulated `TapFrac` (report the fixed
  latency via `IAudioEffectTail` if it's > 0). Hiss = counter-hashed PRNG (xorshift/PCG reset in
  `Reset()`), shaped by the same band-limit. Saturation = Tape Delay's `tanh(k·x)/k`. AGC = one-pole
  envelope follower (Compressor pattern) with slow release pulling gain toward a target → audible
  pumping. With Mono on, run the chain once on the channel average and write it to every channel.
  All buffers allocated on format change only.
- **Register** in `BuiltInAudioEffects`.
- **Tests (`Sprocket.Audio.Tests/CassetteEffectTests.cs`, helpers from `DelayEffectsTests` /
  `ShelvingEqEffectTests`):** factory `IsType`; steady state allocates 0 bytes; deterministic
  run-to-run and after `Reset()`; block-split equivalence; mono → L == R; settled sine amplitude:
  1 kHz passes, 40 Hz and 12 kHz attenuated by ≥ the expected dB; hiss present on silence at the set
  level; Mix 0 = bit-exact dry; no NaN/Inf, bounded output; `AudioEffectChainPersistenceTests`
  round-trip.

### Phase 5 — One-tap look + MCP

- **Core:** `PresetStack` record (Name, Group, Description, `VideoEntries`, `AudioEntries`, each
  `(EffectTypeId, presetName)`) and `ToyCassetteCameraStacks.All` — **Clean**, **Worn Tape**, **Low
  Light** — each = Posterize Time 15 + Toy Cassette Camera *preset* on video, Cassette *preset* on
  audio. `PresetStackApplication.Build(timeline, clip, stack)` → one
  `CompositeCommand("Apply Toy Cassette Camera ▸ Worn Tape")` of `AddEffectCommand`s: video entries
  on the target's video-track clips and audio entries on audio-track clips, over the clip plus
  `Timeline.ClipsLinkedTo(clip)`. Dropping on either half of a linked pair applies both halves; an
  unlinked clip gets only the entries matching its track kind (the result reports what was skipped,
  like `LookApplication`). Re-applying replaces an existing stack's entries rather than duplicating
  (match by effect id), so repeat drops are idempotent.
- **App:** a "TOY CASSETTE CAMERA" group in the Effects browser next to DAY FOR NIGHT (rows show the
  "Inspired by…" tooltip); double-click applies to the selected clip; drag uses a new
  `DragFormats.PresetStackName` handled in `TimelineControl.DropEffect`.
- **MCP:** `apply_preset_stack(clipId, name)` tool routed through `EditHistory` (undoable), and stack
  names listed by `list_effect_types` (or a `list_preset_stacks` tool). `add_effect preset` already
  covers each effect alone.
- **Tests:** Core — builder adds 2 video + 1 audio entries across a linked pair as **one** undo step;
  undo removes all; unlinked video clip gets video only; re-apply is idempotent; every stack names
  real descriptors + presets; brand-token guard covers stack names. App — browser group lists the
  stacks, drop applies. MCP — tool applies and undoes.
- **Docs:** README effects list gains the look + primitives (a whole feature area appeared).

### Phase 6 — Temporal footprint + Echo (`builtin.echo`) → true highlight smear

Keeps every frame a pure function of (project, time) (ARCHITECTURE §1/§8): no feedback buffer —
it would make a frame depend on playback history, so scrubbing, seeking, the render cache and
export would all disagree.

- **Core descriptor:** `builtin.echo` (Video, `ShortCode = "EC"`), AE naming/defaults: **Echo Time**
  (s, default −0.033), **Number of Echoes** (Integer 1–8, default 1), **Starting Intensity** (1.0),
  **Decay** (1.0), **Echo Operator** (Dropdown: Add, Maximum, Minimum, Screen, Composite in Back,
  Composite in Front, Blend). Deliberate addition: **Highlight Key** % (only pixels above the key
  echo — needed for PXL smear; 0 = AE behavior).
- **Core plan:** descriptors may supply a `TemporalFootprint` computed from resolved params
  (count K = Number of Echoes, spacing Δ = |Echo Time|; K capped at 8 per layer). `ResolveClipLayer`
  maps each `t + k·EchoTime` through `MapToSourceVideo` (so speed / ramp / reverse / hold / Posterize
  Time all apply) and emits them as a new additive `VideoLayer.PriorSourceTimes` (serializable,
  deterministic). Before the clip's start, handles are used when the source has them; otherwise clamp
  to the first source frame.
- **Render:** a temporal effect declares `uniform shader echo0..echo7` children; the pipeline wraps
  each prior native frame as an `SKImage` (no managed pixels) and re-applies the effects *above* Echo
  to each prior frame. The toycam stacks put Echo **first** (directly on source), so that replay
  costs nothing there.
- **Media/Export:** `ExportFrameProvider` keeps a small ring of recent decoded frames (refcounted
  native `AVFrame`s, ≤ K+1) so `t − kΔ` requests hit instead of re-seeking. **Preview:**
  `VideoTrackPlayer` keeps the same bounded history of promoted frames keyed by source time
  (nearest ≤). Budget the extra GPU/native memory; heavy stacks fall back to the step-32 render cache.
- **Toycam:** the stacks swap to Echo (Maximum, Highlight Key ~80 %, fast Decay, Echo Time −1/15,
  3–4 echoes) and set toycam's spatial Smear Length to 0; the spatial smear stays for users without
  Echo.
- **Tests:** planner emits correct prior source times (incl. at clip start with/without handles,
  with 2× speed, a ramp, reverse, Posterize Time); Echo render with a moving `CenterDot` shows the
  trail with Maximum and nothing with Number of Echoes 0 / Highlight Key 100 %; preview vs export
  produce the same pixels for the same frame; allocation check — prior frames add ~0 Gen0 per frame
  (§1 rule); export of a moving highlight re-seeks ≤ once per clip.

## Verification (end to end, per phase)

- `dotnet test Sprocket.slnx` green (ffmpeg on PATH / FFmpeg-8 natives cached, per CLAUDE.md).
- `dotnet run --project src/Sprocket.App`, open the sample project, and check by eye:
  Mosaic blocks (phase 1); Posterize Time stutters at the chosen rate during playback, scrub and
  export while linked audio plays smoothly (phase 2); toycam look (phase 3); Cassette on the sample
  audio (phase 4); drag each TOY CASSETTE CAMERA stack onto the linked clip → one Ctrl+Z removes all
  three entries (phase 5); Echo trails on a bright moving object match between preview and export
  (phase 6).
- Allocation profiler on playback of a clip carrying the full stack: ~0 Gen0 per frame (phases 3 and 6).
- MCP (Edit ▸ Preferences ▸ enable): `list_effect_types` shows the new effects/presets;
  `apply_preset_stack` applies and undoes (phase 5).

## On completion

Flip this feature's PLAN.md todo, append the DONE log to `plan/history/steps-58plus.md`, and
update FEATURES.md — then delete or archive the no-longer-open parts of this file.
