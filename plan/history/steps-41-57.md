# Build-order history: steps 41-57 (audio effects, stop motion, platform support)

> Build-order step details (steps 41-57) moved **verbatim** out of [PLAN.md](../../PLAN.md) in the
> 2026-08-26 restructure. PLAN.md keeps the status ledger + open todos; this archive preserves
> each step's original spec and `✅ DONE` implementation log. Anchors are stable: `#step-N`
> (e.g. `#step-16b`). Sibling files: [steps-01-20.md](steps-01-20.md),
> [steps-21-40.md](steps-21-40.md), [steps-41-57.md](steps-41-57.md),
> [performance-log.md](performance-log.md).
> Relative links inside the moved content are as originally written — relative to the
> **repo root** (e.g. `ARCHITECTURE.md`, `UI.md`, `BRIEF.md` live at the root), not to this folder.

## Step 41

41. **Reverb quality upgrade (studio / convolution / creative reverbs + audio freeze).** **Note:**
    the Convolution Reverb and Creative-Reverb-shimmer tiers sketched below have since been promoted
    to their own dedicated built-in effects — see step 49 (Acoustic Space / Convolution Reverb) and
    step 50 (Shimmer Reverb) — following the step-46 convention of separate purpose-built effects
    over mode switches. This step now covers **Studio Reverb** (the realtime high-quality
    algorithmic tier) and the shared **audio freeze/pre-render cache** infrastructure both new
    reverbs depend on; treat the Convolution/Creative bullets below as superseded by steps 49/50.
    The current `ReverbEffect` (`src/Sprocket.Audio/Effects/ReverbEffect.cs`) is a Freeverb-style Schroeder/Moorer
    network — 8 damped combs into 4 allpasses per channel with fixed tunings. It's a good **low-CPU
    editorial ambience** effect (allocation-free steady state, linear per-sample work, honours the
    `IAudioEffect` contract), but its parameter surface (Room Size / Damping / Mix only, see
    `EffectCatalog`) and its algorithm ceiling — no early reflections, predelay, modulation, diffusion
    control, shimmer, or impulse-response realism — can't produce convincing rooms/halls/plates or lush
    BigSky-class creative spaces. The path is **hybrid**: keep the current reverb as the cheap realtime
    option and add higher tiers, with heavy tails pushed through the step-32 audio render cache
    ("freeze") so they don't run on every playback pass. **Sequencing: deliberately deferred until after
    step 37 at minimum**; depends on the step-31 audio effect-chain scopes (done) and the step-32
    preview-render-cache architecture (the audio/`IPcmReader` side of which this step is the first real
    consumer of).
    - **Quality tiers (following the DAW convention — cheap algorithmic / quality algorithmic /
      convolution):** keep the existing effect as **Reverb (Lite)**; add **Studio Reverb** (realtime
      high-quality algorithmic — a modern FDN or Dattorro-style plate/hall with modulated delay lines,
      early reflections, damping filters, stereo decorrelation, bounded internal buffers), **Convolution
      Reverb** (realistic captured spaces — **partitioned convolution** so long IRs are practical, IR
      load off the audio thread, latency/tail metadata exposed; managed deterministic implementation
      first, a small C-ABI FFT helper only if profiling demands it — consistent with the no-C++/CLI
      rule), and **Creative Reverb** (shimmer / cloud / bloom / nonlinear modes — original algorithms
      covering the familiar sonic categories, **no proprietary-algorithm cloning**).
    - **Core descriptors & presets first (everything else hangs off this).** Extend `EffectTypeIds` /
      `EffectParamNames` / `EffectCatalog` with typed descriptors: predelay, decay time, size, diffusion,
      modulation depth/rate, early/late balance, width, low/high damping, tone, shimmer pitch/mix,
      freeze/hold, wet/dry. Presets: room, chamber, plate, hall, cathedral, ambient bloom, shimmer,
      cloud, nonlinear/soft-reverse. The typed-descriptor → Inspector pipeline (step 16/33) means the UI
      falls out for free. New DSP registers via `BuiltInAudioEffects`.
    - **CPU controls.** Per-effect quality modes (Draft / Realtime / High / Offline), preview tail-length
      caps, oversampling only offline/frozen, and a chain CPU-cost indicator (an optional metadata
      surface beside `IAudioEffect` for latency/tail/cost). Studio Reverb must hold a reliable realtime
      mode; convolution and creative modes strongly steer users to freeze for long tails.
    - **Audio freeze / pre-render via the step-32 cache.** Mirror the `ProxyService`/`ProxyCache`
      background-worker pattern: an audio-cache service keyed by a **content hash** of the frozen
      clip/track/range effect state + time range + sample rate + channel layout + quality mode, writing
      local discardable PCM/WAV and exposing it back as an **`IPcmReader`** during preview — exactly the
      step-32 seam, no new render-graph machinery. **Non-destructive and undoable:** cache identity is
      recorded without ever treating the cache as source media; *Render Audio Effects / Freeze / Unfreeze /
      Delete Render Files* are step-10 commands; edits mark the cache **dirty** rather than silently
      playing stale audio. UI: context-menu freeze commands, render-bar state on the timeline, Inspector
      badges for frozen chains, warnings on likely-CPU-heavy chains.
    - **Export determinism preserved (§17).** Export re-renders from originals and live effects,
      **ignoring the preview freeze/cache by default**; opting into a full-quality cache is a later,
      explicit add gated on the cache's quality settings + content hash matching export settings.
    - **Testing & verification.** Deterministic DSP tests + preset output snapshots; freeze-equivalence
      tests (live chain vs frozen cache match within tolerance for deterministic built-ins); cache
      invalidation tests (param edits, trims, source path/mtime, sample rate, channel layout, quality
      mode ⇒ new key or dirty); persistence/undo round-trips; benchmark/stress tests mixing several
      long-tail reverbs at 48 kHz stereo at the production buffer size asserting **no steady-state
      allocations on the audio thread**; manual audition of representative presets comparing live vs
      frozen CPU. Existing `AudioEffectsTests` / `AudioMixerChainTests` cover only pass-through/tail/reset
      basics — this step adds the perceptual/budget layer.
    - **Further considerations:** IR licensing (bundled impulse responses need clear redistribution
      rights, or user IR import only); once VST3/AU hosting lands (steps 31/33), the same freeze system
      should cover third-party and non-deterministic plugin chains.
    - **✅ DONE — Studio Reverb + audio freeze surface (`Sprocket.Core/{Model/EffectInstance,Model/EffectCatalog,
      Audio/AudioEffectTraits}` + `Sprocket.Audio/Effects/{StudioReverbEffect,BuiltInAudioEffects}` +
      `Sprocket.App/{RenderCache/RenderCacheService,MainWindow,Inspector/InspectorPanel}`; 24 new tests — Core +8,
      Audio +11, Export +1, App +4; full suite **1168 green** (Core 325, Media 39, Render 123, Audio 81,
      Playback 56, Export 91, Persistence 109, Plugins 10, Mcp 64, App 270); clean build (0 warnings), smoke
      launch OK.)** Scoped per the superseding note above: Convolution/Creative tiers belong to steps 49/50.
      - **Studio Reverb (`builtin.audio.reverb.studio`).** A Dattorro-plate tank (JAES 1997): predelay →
        four input-diffusion allpasses → two cross-coupled branches, each a *modulated* decay allpass →
        delay → high/low tail damping → decay gain → second allpass → delay, wet L/R from Dattorro's
        decorrelated output-tap table, plus a small tapped early-reflection line. Full step-41 parameter
        surface (predelay, RT60-style decay seconds, size, diffusion, mod depth/rate, early/late balance,
        width as wet mid/side, low/high damping, mix); the existing Freeverb effect is relabelled
        **Reverb (Lite)**. Size scales every length inside max-size buffers allocated once, so parameter
        changes never allocate (steady-state allocation-free, asserted by test); decay maps to
        `0.001^(loop/t60)` — always < 1, so the tank can't run away (bounded-at-max-settings test); the tank
        LFOs are sine functions of a sample counter (no RNG), so output is deterministic run-to-run —
        the property freeze-equivalence rests on (bit-identical repeated `PreviewRenderer.RenderAudio`
        proven in Export tests). Realtime-safe by construction, so the step's "reliable realtime mode"
        holds without a quality-mode ladder.
      - **Presets (Core + Inspector).** `EffectDescriptor.Presets` (`EffectPreset` name → parameter values) —
        the step's "descriptors & presets" surface, usable by any effect. Studio Reverb ships Room / Chamber /
        Plate / Hall / Cathedral / Ambient Bloom (shimmer/cloud/nonlinear are steps 49–50's). The Inspector
        shows a **Preset** picker for any descriptor carrying presets; applying one is a single undoable
        `CompositeCommand` of parameter edits, and presets deliberately omit Mix so switching character keeps
        the user's wet/dry blend.
      - **Audio freeze (App, on the step-32 seam).** New Sequence-menu commands: **Freeze Clip Audio**
        (pre-renders the selected clip's range through the existing audio render cache — master-mix float32
        WAV via `PreviewRenderer.RenderAudio`, replayed by the `IAudioRenderCache` feeder splice) and
        **Unfreeze Clip Audio** (`RenderCacheService.RemoveAudioSegments` forgets intersecting audio segments
        + sweeps their files; enabled only when a valid segment covers the clip,
        `IsAudioRangeFrozen`). Invalidation/dirty-marking, render-bar state, hashing, and
        export-ignores-the-cache all inherit from step 32 unchanged. **Deliberate departure from the step
        text:** freeze/unfreeze are *not* `EditHistory` commands — the cache is a derived local artifact, not
        model state, and step 32's shipped render commands set that convention (undoing an *edit* already
        re-validates the cache for free).
      - **CPU-cost surface.** `AudioEffectTraits` (Core): `IsHeavy`/`HasHeavyEffect` flag long-tailed DSP
        (Studio Reverb today; steps 49/50 join it), and the Inspector shows a "CPU-heavy tail — Sequence ▸
        Freeze Clip Audio pre-renders it" hint on heavy effect sections — the step's warning affordance.
      - **Deferred (documented):** per-effect quality modes (Draft/Realtime/High/Offline), preview tail-length
        caps, and oversampling — they matter for convolution/shimmer (steps 49/50) where realtime genuinely
        strains, not for the realtime-safe Studio Reverb; a numeric chain CPU-cost meter (the boolean
        heavy-hint shipped); track-scope freeze (clip-scope shipped — track chains gained an editing UI in the
        step-31 follow-on, but freezing a track chain's output is still future work); Inspector frozen-state
        badges (the render bar already shows frozen
        ranges); and the opt-in export reuse of a full-quality cache (unchanged from step 32).

## Step 42

42. **Image-sequence & still import (stop-motion tier 1).** Import a folder of numbered stills as one
    video clip at a user-chosen frame rate, plus single-still import with a configurable default
    duration — the stop-motion on-ramp leading editors provide (and the same feature unlocks time-lapse and
    VFX frame-run workflows). Lands on the existing import → probe → `MediaRef` → feed-factory seams
    (§11, §17); the render graph and export need no new machinery because a sequence decodes like any
    other video once FFmpeg's `image2` demuxer opens it.
    - **Model (Core, §4).** `MediaRef` gains a `MediaKind Kind` (`File` default / `ImageSequence` /
      `Still`) and, for sequences, a nullable `SequencePattern` (printf-style `%0Nd` absolute path),
      `SequenceStartNumber`, and `SequenceFrameCount`. The intrinsic fps stays in `Info.FrameRate`
      (single source of truth — no second fps field) and `Info.Duration = FrameCount / fps`. A still
      probes as `Kind = Still` with `Info.Duration` set from the new **still-image default duration**
      preference at import (5 s, a common default in leading editors), but its media headroom is **unbounded**: the
      trim/drop clamps that read `media.Info.Duration` (`TimelineControl`, `TimelineMath`) treat still
      media as infinite, so a still clip extends freely like a step-19 generator. Core stays pure data —
      pattern strings and counts only, no IO.
    - **Media (§11) + binding.** `MediaSource.Open`/`ProbeInfo` grow an overload taking an open request
      derived from the `MediaRef` (Media already references Core): input-format name + an options
      dictionary. New binding surface (recorded in `Native/FUTURE_BINDINGS.md`):
      `av_find_input_format("image2")` and an `avformat_open_input` overload marshalling a `ref IntPtr`
      options dict (`av_dict_set` is already bound) carrying `framerate`, `start_number`, and
      `pattern_type sequence`. Frames then flow through the existing decoder/scaler/pool — no new pixel
      path, no managed pixels (§1). Stills get a dedicated `StillFrameFeed : IVideoFrameFeed` that
      decodes **once** and serves the held native frame for any requested time (no decode ring, no
      per-frame work). **Sweep every `MediaSource.Open(path)` call site** through the new request
      mapper, keyed off `MediaRef.Kind` — the preview feed factory (`MediaBootstrap`),
      `PlaybackEngine`'s nested-feed opener, `VideoExporter` (so preview = export), and
      `ThumbnailService`.
    - **Import & UI.** The picker gains an **Image** filter (`*.png *.jpg *.jpeg *.tif *.tiff *.bmp
      *.tga *.webp *.dpx`); `MediaImport` grows a pure, headless-tested **sequence-detection helper**
      (given one picked file, find the contiguous numbered sibling run → pattern + start + count,
      refusing gaps/mixed padding). When a run is detected, an import dialog offers **Import as image
      sequence** (a checkbox convention seen in leading editors; a deliberate departure from other editors' silent
      auto-grouping so a deliberate single-still import stays one click) with an fps combo
      (12 / 15 / 24 stop-motion presets + the project rate; **default = project timeline fps** —
      departs from the global "Indeterminate Media Timebase"-style preference some leading editors use, in favour of a
      per-import choice). Preferences gains **Still image default duration** (5 s). Media-bin badges
      for sequences ("SEQ · 240 frames @ 12") and stills.
    - **Interpret Footage (fps reassignment).** A `ReinterpretFootageCommand` (step 10) rewrites the
      media's `Info.FrameRate`/`Duration` **and rescales every referencing clip's
      `SourceIn`/`SourceOut` by oldFps/newFps** (exact `Timecode.Scale(Rational)`, Int128 math) so the
      same *frames* stay selected and clip timeline durations stretch accordingly — consistent with
      leading editors' "Interpret Footage ▸ Assume this frame rate". One undo entry (clip rescales + media rewrite in a
      `CompositeCommand`); entry points: media-bin context menu + Clip menu. Works on any video media,
      not just sequences (it is also the whole-clip "shoot on twos" lever for step 43).
    - **Persistence (§12).** Additive nullable `MediaRefDto` fields (`kind`, `sequencePattern`,
      `sequenceStartNumber`, `sequenceFrameCount`) with `WhenWritingNull` — ordinary files write none
      and serialize byte-identically, **no schema bump**. The step-28 media-link sidecar and batch
      relink must accept a pattern path (relinking a moved folder rewrites the pattern's directory).
      The proxy service (step 18) skips sequence/still media in its first cut (`ProxyPolicy`).
    - **Tests.** Media — extend the `TestVideo.cs` fixture generator to emit a numbered PNG run via the
      `ffmpeg` CLI; probe it at a chosen fps (duration/fps/count exact), decode frame N at t = N/fps
      (golden hash), a single PNG probes as `Still` with alpha intact. Core — reinterpret rescale
      exactness (12→24 fps keeps frame indices; undo restores byte-exact ticks), sequence duration
      math on NTSC rates. Persistence — round-trip of the new fields + absence-is-byte-identical +
      pattern-path relink. App — sequence-detection helper (runs, gaps, mixed padding, start ≠ 0,
      single file) and the unbounded-still trim clamp.
    - **Risks.** `image2` `pattern_type`/`start_number` quirks (non-contiguous numbering must be
      rejected at detection time, not discovered mid-decode); very large stills (decode-once bounds the
      cost, but one 100 MP still is a large native allocation — surface dimensions in the import
      dialog); decoder coverage in the BtbN gpl natives varies for exotic formats (DPX; keep EXR out of
      the default filter) — per-file graceful import failure already exists; audit everywhere
      `Info.Duration` is assumed finite (trim clamps, drop-duration, slip bounds).
    - **✅ DONE** (`Sprocket.Core/Model/MediaRef` + `Sprocket.Media/{Native/LibAv,Native/Handles,MediaOpenRequest,
      MediaSource,StillFrameSource}` + `Sprocket.Playback/StillFrameFeed` + `Sprocket.Export/{ExportFrameProvider,
      VideoExporter}` + `Sprocket.Core/Commands/ModelCommands` (`ReinterpretFootageCommand`) + `Sprocket.App/
      {ImageSequenceDetection,MediaImport,ImageSequenceImportDialog,InterpretFootageDialog,MainWindow,MediaBootstrap,
      Proxy/ProxyService,PreferencesDialog,UserSettingsStore,MediaBrowser/{MediaBadges,MediaBrowserPanel},
      Timeline/TimelineControl}` + `Sprocket.Persistence/{ProjectDto,ProjectSerializer,MediaRelink}`; 24 new tests —
      Media +5, Core +4, App +15 (`ImageSequenceDetectionTests` +8, `MediaBadgesSequenceTests` +3, plus the existing
      suite), Persistence +4; full suite **1196 green**, clean Release build (0 warnings), smoke `--version` + `--probe`
      a still exit 0). Delivered:
      - **Model (Core, §4):** `MediaKind` (`File` / `ImageSequence` / `Still`) + `MediaRef.{SequencePattern,
        SequenceStartNumber,SequenceFrameCount}` and a `HasUnboundedDuration` (stills). `Info.Duration = count/fps`
        via exact `Timecode.FromFrames`; Core stays pure data (pattern strings only, no IO).
      - **Media (§11) + binding:** bound `av_find_input_format` + a `ref`-options `avformat_open_input` overload
        (`FUTURE_BINDINGS.md` item consumed); one `FormatContextHandle.OpenInput(path, format, options)` choke point;
        `MediaOpenRequest.FromMediaRef` maps a `MediaRef` → open request (image sequence → `image2` with
        `framerate`/`start_number`/`pattern_type sequence`); `MediaSource.Open`/`ProbeInfo` grew request overloads
        (string overloads unchanged). Stills decode **once** via `StillFrameSource` (held master frame, pooled
        copies) — no managed pixels (§1).
      - **Feed sweep:** every `MediaSource.Open` call site now routes through the request mapper keyed off
        `MediaRef.Kind` — preview feed factory (`MediaBootstrap`, stills → `StillFrameFeed`), `VideoExporter`
        (`ExportFrameProvider` pins a still to one held frame so preview == export), and `ThumbnailService` (poster
        opens sequences via `image2`, never seeks past a still's one frame).
      - **Import & UI:** picker **Images** filter (`png/jpg/jpeg/tif/tiff/bmp/tga/webp/dpx`; EXR excluded); pure
        headless `ImageSequenceDetection` (contiguous run → pattern/start/count, refusing gaps + mixed padding, last
        digit-run = frame number); an **Import as image sequence** dialog (the convention in leading editors) with an fps combo
        (12/15/24 + project rate, default = project rate); stills import at the **Still image default duration**
        preference (5 s, unbounded headroom like a generator); media-bin badges (`SEQ · N frames @ fps` / `STILL`).
      - **Interpret Footage (§10 command):** `ReinterpretFootageCommand.ForMedia` rewrites the media rate/duration
        and rescales every referencing clip's `SourceIn/Out` by `oldFps/newFps` (same frames stay selected), one
        undo entry; entry points on the media-bin tile context menu **and** Clip ▸ Interpret Footage. Works on any
        video media (also the whole-clip "shoot on twos" lever for step 43).
      - **Persistence (§12):** additive nullable `MediaRefDto` fields (`kind`/`sequencePattern`/`sequenceStartNumber`/
        `sequenceFrameCount`, `WhenWritingNull`) — ordinary files serialize byte-identically, no schema bump; batch
        relink + sidecar rebase a moved sequence's pattern directory onto the relinked first frame. The proxy service
        skips sequences/stills (`ProxyService`).
      - **Still outstanding:** none for this tier — frame-hold / stop-motion frame edits are the separate step 43
        (which builds on this step's `StillFrameSource` and `ReinterpretFootageCommand`).

## Step 43

43. **Frame-level retime tools: frame hold + stop-motion frame edits (tier 2).** Freeze-frame and
    per-frame timing surgery ("on twos", duplicate/remove a frame) on the existing clip/command/render
    seams. **This lifts exactly one item from step 21's deferral — freeze frame — and models it as a
    hold field, not speed 0**, so the `SpeedRatio > 0` invariant, the derived-duration formula, and the
    audio resampler's positive-factor assumption are all untouched (reverse and keyframed ramps remain
    deferred on step 21's seam).
    - **Model (Core, §4).** `Clip` gains a nullable `HoldFrameAt` (a source-time `Timecode`) and a
      `HoldDuration` (`Timecode`). When held: `Duration => HoldDuration` (the independent timeline
      duration the step-21 deferral note called for) and `MapToSource(t) => HoldFrameAt` — a constant
      map, so preview and export render the identical frame across the span with zero new render-graph
      plumbing (§5). `SourceIn/Out` and `SpeedRatio` are retained untouched for exact **un-hold**.
      Precedence is defined: a held clip ignores its speed ratio. Blade split copies the hold
      (`CloneContentForSpan`); trimming a held clip edits `HoldDuration` with no media clamp (like
      generators/stills); slip on a held clip moves `HoldFrameAt`. Video holds only — linked audio
      keeps playing normally, matching leading editors.
    - **Commands & UI.** `SetClipHoldCommand` (apply/revert/coalesce, one undo entry). Menu surface
      follows leading-editor naming: **Clip ▸ Frame Hold Options…** (hold the whole clip at In Point /
      Playhead / a source timecode), **Add Frame Hold** (split at the playhead; the right-hand part
      becomes a freeze of the playhead frame), and **Insert Frame Hold Segment** (insert a 2 s freeze
      at the playhead and ripple downstream) — the latter two are `CompositeCommand`s over the existing
      blade (step 13) + ripple (step 22) primitives. Inspector shows a Hold row (frame + duration) on
      held clips; the clip body gets a hold badge.
    - **Stop-motion frame edits.** On any clip (image sequences especially), **Duplicate Frame** —
      split at the source-frame boundary under the playhead, insert a one-frame hold of that frame,
      ripple downstream (+1 frame) — and **Remove Frame** — extract that frame's timeline span and
      ripple-close (−1 frame). Both are pure `CompositeCommand`s snapped to the exact source-frame
      grid via `Rational` math (correct on NTSC rates). Repeated Duplicate Frame *is* per-frame "on
      twos/threes"; **whole-clip on-twos = Interpret Footage at half rate (step 42)** — a deliberate
      departure from Dragonframe-style X-sheet editing, which stays out of scope (a run of hold
      segments covers the same ground inside an editor).
    - **Playback/decode.** The frame feeds already serve arbitrary source times; add a **repeat-frame
      fast path** so a held span doesn't re-seek/re-decode the same frame every pump tick (the
      `StillFrameFeed` from step 42 is the model; the ring feed can short-circuit an identical
      source-frame request).
    - **Persistence (§12).** Additive nullable `ClipDto` fields `holdAtTicks` / `holdDurationTicks`
      (`WhenWritingNull`) — unheld clips serialize byte-identically, no schema bump.
    - **Tests.** Core — held `Duration`/`MapToSource` constancy, hold-ignores-speed precedence,
      split-copies-hold, un-hold restores the derived duration exactly, command undo/coalesce,
      duplicate/remove-frame composites (downstream shift exactness and frame-grid snap at 12, 24,
      30000/1001). Persistence — hold round-trip + absence-is-byte-identical. Render/Export —
      golden-frame: a held clip renders the identical hash across its span and equals the unheld
      frame at `HoldFrameAt`; export a sequence after duplicate+remove edits and hash the frame run.
      App — hold trim clamp (no media limit), playhead→source-frame-boundary math.
    - **Risks.** Interaction matrix with existing per-clip features (transitions overlapping a held
      edge; markers positioned in source time on a constant map); linked-A/V ripple correctness when
      Insert Frame Hold Segment pushes downstream video but audio must follow (reuse the step-22
      companion composites); decode-ring thrash without the repeat-frame fast path; UX ambiguity
      between Remove Frame (this step, frame-grid) and ripple delete (step 22, span) — keep distinct
      shortcuts.
    - **✅ DONE** (`Sprocket.Core/Model/Clip` + `Sprocket.Core/Commands/{ModelCommands,FrameHoldEdits}` +
      `Sprocket.Playback/VideoTrackPlayer` + `Sprocket.Persistence/{ProjectDto,ProjectSerializer}` +
      `Sprocket.App/{MainWindow,FrameHoldOptionsDialog,ClipboardOps,Inspector/InspectorPanel,Timeline/TimelineControl}`;
      21 new tests — Core +17 (`FrameHoldTests`), Persistence +2, App +1 (`ClipboardOpsTests`), plus 1 amended;
      full suite **1231 green**, clean Release build (0 warnings). Delivered:
      - **Model (Core, §4):** `Clip.HoldFrameAt` (nullable source `Timecode`) + `Clip.HoldDuration` + `IsHeld`.
        Held: `Duration => HoldDuration` and `MapToSource(t) => HoldFrameAt` (a constant map — preview and
        export render the identical frame with zero new render-graph plumbing, verified by a plan-constancy
        test). `SourceIn/Out` + `SpeedRatio` retained untouched, so un-hold restores the derived duration
        exactly; the defined precedence is hold-ignores-speed. `CloneContentForSpan` copies the hold;
        `SplitClipCommand` grew hold-awareness (a held clip splits by timeline span — both halves keep the full
        retained source span + frozen frame, only their `HoldDuration`s partition).
      - **Commands (§ step 10):** `SetClipHoldCommand` (hold/un-hold/retarget, coalescing),
        `TrimHeldClipCommand` (start+duration, no media clamp, coalescing), `ShiftClipsCommand` (the pure
        downstream-move half of a ripple), and the `FrameHoldEdits` builders — `AddFrameHold` (split at
        playhead, freeze the right half, no ripple), `InsertFrameHoldSegment` (2 s freeze + ripple, the
        leading-editor default), `DuplicateFrame` / `RemoveFrame` (exact source-frame grid via `Rational`/`Int128` math,
        `SourceFrameSpan`, ±1-frame ripple; tested at 12, 24, and 30000/1001 fps) — each one
        `CompositeCommand` = one undo entry, downstream sets captured by the caller like `RippleTrimCommand`.
      - **UI (App):** Clip ▸ **Frame Hold Options…** (hold at In Point / Playhead / source time, or release —
        `FrameHoldOptionsDialog`), **Add Frame Hold**, **Insert Frame Hold Segment**, **Duplicate Frame**,
        **Remove Frame** (no default shortcuts — leading editors ship none; Remove Frame stays distinct from
        Shift+Delete ripple delete). Video-track clips with frame content only (media/nested/multicam;
        generators animate by local progress); linked audio keeps playing, matching leading editors — Add/Insert split
        companions spanning the cut, frame-edit ripples shift **every** track downstream so A/V sync holds.
        Held-clip gestures: trim edits `HoldDuration` (no media clamp, like generators/stills), slip moves
        `HoldFrameAt`, ripple/roll/slide abort on held edges (their source-trim math doesn't apply); clip body
        gets a HOLD pill badge, Inspector a Hold row; `ClipboardOps` now clones via `CloneContentForSpan` so
        copy/paste/Alt-drag keep kind/speed/gain/hold.
      - **Playback:** repeat-frame fast path in `VideoTrackPlayer` — a seek whose target equals the
        already-presented source time (a held span's constant map, scrub ticks in a freeze) skips the
        re-seek/re-decode entirely; the presented-target latch is invalidated on feed rebuild/clear.
      - **Persistence (§12):** additive nullable `ClipDto.holdAtTicks`/`holdDurationTicks`
        (`WhenWritingNull`) — unheld clips serialize byte-identically, no schema bump; EDL/XML interchange
        counts a "frame hold dropped/not exported" warning (their events derive source span from record span).
      - **Deliberate departures:** whole-clip on-twos stays Interpret Footage at half rate (step 42), not
        Dragonframe X-sheet editing; Duplicate/Remove Frame are disabled on an already-held clip (no frame grid
        on a constant map).

## Step 44

44. **Audio-only export delivery.** Add an explicit user-facing **audio-only** export mode for writing
    the active sequence's master mix without rendering or muxing a video stream. This is a later
    delivery feature, not part of the completed step-27 container/video/audio matrix: that step writes
    audio codecs inside movie containers, while this one exports sound as the product. Follow the
    convention in leading editors — an **Audio only** export format or mode with common
    audio targets such as **WAV/PCM, FLAC, MP3, AAC/M4A, and Opus** where the bundled FFmpeg build
    supports them — rather than forcing users through a dummy video export.
    - **Export model.** Extend `ExportOptions` / `ExportFormat` with a delivery kind or equivalent
      shape that can represent normal A/V export, existing **video-only** export, and the new
      **audio-only** export without changing `default(ExportOptions)` (still MP4/H.264/AAC). Audio-only
      formats should validate against an audio-container/codec matrix, choose the correct extension and
      save-dialog filter, and reject incompatible combinations up front.
    - **Rendering path.** Reuse `RenderGraph.PlanAudioBuffer` and the existing `AudioMixer` / chain
      execution so the output is the same master mix heard in playback and measured by loudness tools;
      do **not** render video frames, open video encoders, or require a video stream. Range exports,
      in/out marks, handles, sample rate, channel count, metadata, progress, cancellation, failure
      cleanup, and reveal-in-folder should match the existing export job plumbing.
    - **UI / presets / automation.** Surface the mode in `ExportSettingsDialog` with video controls
      hidden or disabled when audio-only is selected; allow built-in and user presets to capture it;
      add an MCP/API path alongside `export_video` when the tool surface is ready, using the same
      background-export status/cancel model.
    - **Tests.** Export tests should round-trip representative audio-only formats and assert there is no
      video stream, audio duration/rate/channels match the selected range, muted/soloed tracks and
      master/track/clip effects are reflected, cancellation removes partial files, and the default
      MP4/H.264/AAC behaviour remains byte-for-byte compatible where already asserted.
    - **✅ DONE** (`Sprocket.Media/MediaEncoder` (audio-only path) + `Sprocket.Export/{ExportFormat,VideoExporter,
      ExportPresetStore}` + `Sprocket.App/{Dialogs,MainWindow}` + `Sprocket.Mcp/{IEditorSession,SprocketTools.Session}`
      + `Sprocket.App/{McpEditorSession,MainWindow}`; 12 new tests — Export +10 (`AudioOnlyExportTests`), Mcp +1
      (`ExportAudio_Starts…`) & the tool-surface + `export_audio` count). Delivered:
      - **Model / matrix:** new `ExportAudioFormat` (`WavPcm`/`Flac`/`Mp3`/`Aac`/`Opus`) + `AudioFormatInfo`
        table in `ExportCodecs` (muxer/extension/MIME/encoder/lossless per target — WAV→`wav`/`pcm_s16le`,
        FLAC→`flac`, MP3→`mp3`/`libmp3lame`, AAC→`ipod`/`.m4a`/`aac`, Opus→`opus`/`libopus`). `ExportOptions`
        gained an additive nullable `AudioFormat` (set → audio-only); `default(ExportOptions)` is unchanged
        (MP4/H.264/AAC). Each audio format is self-consistent, so there is no invalid-combination matrix to guard.
      - **Encoder:** `MediaEncoder` video stream made optional (nullable video fields + `HasVideo`) with a new
        `CreateAudioOnly(path, AudioEncoderSettings, muxer, metadata)` factory — no video stream is opened.
      - **Render path:** `VideoExporter.ExportAudioOnly` reuses the same `AudioMixer` (→ `RenderGraph.PlanAudioBuffer`)
        the preview / A/V export use, so the file is the exact master mix (muted/disabled tracks + clip/track/master
        effects reflected). Honours the range + handles, reports progress, observes cancellation, and deletes the
        partial file on failure — the existing job plumbing. A lossless target ignores the target bit rate.
      - **UI:** `ExportSettingsDialog`'s Format list now lists the audio-only targets after the video containers;
        selecting one hides every video-side row (codecs, quality/encoding, resolution/frame-rate, burn-ins, color)
        and keeps metadata. `ExportPreset` gained a nullable `AudioFormat` so built-in/user presets capture audio-only
        (additive `PresetDto` field — older files omit it). The save-dialog extension/type filter follows the audio
        format (`.wav`/`.flac`/`.mp3`/`.m4a`/`.opus`); Export Queue jobs carry it too.
      - **MCP/API:** new `export_audio(outputPath, format, rangeIn?, rangeOut?)` tool alongside `export_video`, on the
        same `IEditorSession` marshal + background status/cancel model (`StartAudioExport`); `format` is
        `wav`/`flac`/`mp3`/`aac`/`opus` (case-insensitive), unknown → error.
      - **Also (user request):** a **media-created project now always starts at 48 kHz** (`MediaBootstrap.
        BuildProjectFromMedia`) instead of inheriting the source rate — imported audio at any rate is resampled to
        the project rate transparently by `AudioSource`, keeping the mix sample-accurate to the master clock.

## Step 45

45. **Channel-aware update checks (notify + direct download link, not self-update).** Tell the user when a
    newer Sprocket build is available without changing the current GitHub release semantics. **Keep alpha /
    beta / rc builds published as GitHub prereleases** (the current `scripts/gh-release.ps1` default) and make
    the app's discovery logic understand release channels instead of pretending GitHub has only one "latest"
    build. The first shipping slice is **notification + deep-link to the correct download asset** only; it does
    **not** download, replace binaries, or run installers. Sequence this **after packaging/distribution is stable
    enough that the published assets are trustworthy install targets** (step 36), but before any future
    auto-update story.
    - **Release-source policy (product rule first).** The app must not use GitHub's `releases/latest` endpoint for
      prerelease-aware discovery because GitHub defines "latest" as the newest **non-prerelease** release.
      Instead, query the **releases list** and filter locally. Recommended default policy: **match current
      channel** — a stable build notifies only about newer stable releases; an alpha/beta/rc build may notify
      about newer prereleases. This preserves the current release flow while avoiding false "up to date"
      results during alpha.
    - **Version model (App, pure).** Add a tiny SemVer-ish parser/comparer in `Sprocket.App` over the existing
      `Program.AppVersion` surface: understand `0.2.0`, `0.2.0-alpha.1`, `0.2.0-beta.2`, optional leading `v`,
      and ignore build metadata (`+sha`). Numeric parts compare first; prerelease labels compare only when both
      sides are prereleases. The parser should reject malformed tags cleanly so a bad GitHub release does not
      break startup.
    - **Background service (App).** Add an `UpdateCheckService` following the pattern of other long-lived
      app-level services (`AutosaveService`, `McpServerService`): fire asynchronously after the main window is
      ready, never block startup, cache the last successful check/result, rate-limit checks across launches, and
      expose an immutable state/result to the UI. Public GitHub API only; no credentials, no MCP involvement.
    - **Release filtering + asset targeting.** The service reads release metadata (`tag_name`, `prerelease`,
      `draft`, `html_url`, `assets[]`) and picks the newest acceptable release by policy. For a hit, resolve the
      best matching published asset for the current platform/RID (`win-x64`, `win-arm64`, `linux-x64`,
      `linux-arm64`, `osx-x64`, `osx-arm64`) from the step-36/`dist` naming scheme; if no exact asset exists,
      fall back to the release page instead of hiding the update.
    - **User settings & preferences.** Extend the user-scoped settings with: update checks enabled, release
      channel policy (`StableOnly` / `MatchCurrentChannel` / optional explicit prerelease opt-in), and any cached
      last-check metadata needed for throttling. Surface those controls in Preferences with conservative wording:
      stable users are not opted into prereleases accidentally; alpha users can stay on the alpha track.
    - **UI affordance.** Surface availability in a lightweight, non-modal way: status bar, Help/About, or a small
      banner/button in the shell. The first cut should say **what version is available** and offer **Download** /
      **View release notes** actions. Do not interrupt editing with modal prompts on launch, and do not nag on
      every startup once a result has already been dismissed for the same version.
    - **Scope guardrails.** Explicitly out of scope here: background downloading, silent patching, replacing the
      running app, installer orchestration, code-signing trust flow, rollback, delta updates, or modifying the
      release pipeline. Those belong to a later packaging/update step once each OS has a first-class install path.
    - **Tests.** Headless tests for version parsing and ordering (`0.2.0-alpha.1 < 0.2.0-alpha.2 < 0.2.0 <
      0.2.1-alpha.1`), channel filtering (stable ignores prereleases; alpha sees newer prereleases), asset
      selection per RID, settings round-trip with additive fields, throttling/dismissal logic, and malformed API
      payload handling. Manual verification should mock both a stable-only and prerelease-heavy release list and
      confirm startup remains non-blocking.
    - **✅ DONE (`src/Sprocket.App`: `UpdateVersion.cs` + `UpdateCheck.cs` + `UpdateCheckService.cs` +
      `UpdateDialogs.cs`, settings/Preferences/shell wiring; 54 new tests in
      `tests/Sprocket.App.Tests/UpdateCheckTests.cs`).** Shipped exactly the notify + deep-link slice:
      `UpdateVersion` is the pure SemVer-ish comparer (leading `v`, prerelease labels, `+meta` ignored,
      malformed tags rejected); `UpdateCheck` holds the pure policy logic — GitHub **releases-list**
      parsing (never `releases/latest`, which hides prereleases), channel filtering
      (`StableOnly` / `MatchCurrentChannel` default / `IncludePrereleases`), per-RID asset targeting
      against the `release.ps1` `Sprocket-<version>-<rid>.zip` scheme with release-page fallback, a
      20-hour cross-launch throttle, and per-version dismissal. `UpdateCheckService` is app-scoped like
      `McpServerService` (anonymous `HttpClient`, fired after the shell is up, result cached in the
      additive `UserSettings` fields so throttled launches stay informed offline; failures degrade
      silently). UI: a click-to-open status-bar badge (hidden once a version is dismissed),
      Help ▸ Check for Updates… (bypasses switch + throttle, always answers), an Update Available
      dialog whose Download/Release Notes buttons open the browser — nothing is downloaded, installed,
      or replaced by the app — and an Updates section in Preferences with conservative wording.
      Sequencing note: shipped ahead of finished installers (step 36) deliberately — the per-RID zips
      the checker targets are already the published install artifacts, and a missing asset falls back
      to the release page.
    - **♻️ SUPERSEDED by step 36's Velopack auto-update.** When packaging landed, the notify-only stack
      (`UpdateVersion.cs`, `UpdateCheck.cs`, `UpdateCheckService.cs`, its channel-policy setting and
      tests) was replaced by `UpdateService.cs` on Velopack's `UpdateManager`: installed builds
      (Setup/AppImage/.app) check the per-RID channel feed on GitHub releases and **download + apply
      updates in-app** (Install & Restart, delta when possible); portable/dev builds get a
      releases-page pointer instead. Kept from this step: the startup check + enable setting, the
      non-modal status-bar badge, Help ▸ Check for Updates…, and per-version "Skip This Version"
      dismissal. Velopack's own version/channel logic replaced the hand-rolled SemVer parser, GitHub
      list parsing, per-RID asset targeting, and the channel-policy preference (the channel is baked
      into each install at pack time).
    - **✅ Notification UX refinement (later pass).** The non-modal notification was made *discoverable
      without nagging* (the status-bar badge alone was too easy to miss): a **Help-menu dot** mirrors the
      badge (Chrome/VS Code pattern), the badge **escalates colour by age** (muted → accent → amber
      "Update recommended", from a persisted first-seen timestamp — `UpdateEscalation.cs`, pure/tested),
      a **one-time first-run toast** fires **once per version** (persisted `UpdateToastShownTag`, so future
      launches stay quiet — the user chose "persistent badge only" cadence), and the Update dialog shows
      **inline "what's new"** notes (`UpdateService.AvailableNotes` from Velopack's feed, populated by
      `release.ps1 --releaseNotes`; falls back to the GitHub "Full Release Notes" link when absent). Still
      no launch-time modal. Additive `UserSettings` fields; 15 new tests.
    - **✅ Inline notes fixed to carry the actual changelog (later pass).** The feed was being packed with
      `RELEASE_NOTES.md` — the *evergreen GitHub preamble* — so the dialog showed install boilerplate and a
      pointer to a "What's changed" section that only ever existed in the GitHub release body (the change
      overview is generated later, in the CI release job). `release.ps1`'s `Get-FeedReleaseNotes` now runs
      `changelog.ps1` at pack time and ships **only** the generated overview plus a link line (build jobs
      checkout at `fetch-depth: 0`); no overview ⇒ notes-less pack and the dialog's existing collapse
      path. "Full Release Notes" now deep-links to `/releases/tag/v<version>`
      (`UpdateService.ReleaseUrlFor`) instead of the releases index.

## Step 46

46. **Delay effects (tape / digital / multi-tap / stereo).** Four new built-in `IAudioEffect`
    implementations in `Sprocket.Audio/Effects` alongside the existing `ReverbEffect` /
    `CompressorEffect` / `ParametricEqEffect` / `GainPanEffect`, registered through the same
    step-31 audio effect-chain scopes (clip / track / timeline bus / master) — no render-graph or
    persistence redesign, following the DAW convention of shipping delay as **separate, purpose-built
    effects** (Ableton Simple/Ping Pong/Echo, Logic Tape/Delay Designer, Pro Tools Mod/Long/Slap
    Delay) rather than one mega-delay with mode switches:
    - **Digital Delay.** The clean baseline: a single feedback delay line (sample-accurate delay
      time in ms or, when a project/track tempo/time-signature surface exists, note-synced
      divisions — otherwise ms only, since Sprocket has no tempo model yet), feedback amount,
      wet/dry mix, and a high-cut in the feedback path (the standard "analog-ish" damping tone
      control even on a digital delay). Implemented as a fixed-size ring buffer per channel —
      allocation-free steady state, matching the `IAudioEffect` contract's no-per-sample-alloc rule
      already honoured by `ReverbEffect`.
    - **Tape Delay.** Same feedback-delay core plus the tape-emulation coloration users expect from
      this name: soft saturation/tone-shaping in the feedback path, subtle **wow & flutter**
      (low-frequency + higher-frequency delay-time modulation, deterministic LFOs — no per-instance
      RNG so renders stay reproducible), and a gentle low-pass in the repeats so the tail darkens
      like successive tape generations. Parameters: delay time, feedback, wow/flutter depth+rate,
      saturation amount, mix.
    - **Multi-Tap Delay.** N independent taps (a small fixed cap, e.g. 8, matching typical DAW
      multi-tap plugins) each with its own delay time, level, and pan, summed into the output —
      lets a single instance build rhythmic/echo patterns without stacking multiple effect
      instances. Parameters: per-tap enable/time/level/pan arrays (typed descriptors following the
      step-16/31 pattern so the Inspector's generated property editor renders them; a tap count
      beyond a couple of entries may need a small custom Inspector section rather than the generic
      one-row-per-parameter layout — flag this as a UI follow-up, not a blocker).
    - **Stereo Delay.** Independent left/right delay times and feedback (the classic dual-mono
      "ping-pong-capable" stereo delay) plus a **Ping Pong** mode toggle that cross-feeds each
      channel's repeats into the opposite channel, matching the Ableton/Logic ping-pong convention.
      Parameters: left time, right time, feedback, ping-pong on/off, cross-feed amount, mix.
    - **Core & catalog.** New `EffectTypeIds.Delay.{Digital,Tape,MultiTap,Stereo}` (or a `builtin.audio.delay.*`
      family) with typed `EffectParamNames`/descriptors registered in `EffectCatalog` under
      `EffectCategory.Audio`, so they appear in the Effects browser/menu and the audio chain
      add-effect flow exactly like the step-31/41 audio effects, and serialize via the existing
      `EffectInstance` JSON (additive, no schema bump). Stateful per-effect ring buffers are cached
      per `StateKey` and rebuilt only when the chain's ids/params change, mirroring `AudioMixer`'s
      existing state-caching for `ReverbEffect`.
    - **Tests.** Deterministic DSP tests per effect (impulse response shows taps/echoes at the
      expected sample offsets and levels; feedback decay converges and never blows up at feedback
      values near 1.0; wow/flutter modulation is bounded and reproducible run-to-run; ping-pong
      cross-feed lands in the correct channel). Persistence round-trip for each new effect id.
      Extend `AudioEffectsTests`/`AudioMixerChainTests` (steady-state allocation-free assertion,
      pass-through/bypass, chain ordering with existing effects) the same way step-31's four
      built-ins were covered.
    - **✅ DONE (`Sprocket.Core/Model/{EffectInstance,EffectCatalog}` + `Sprocket.Audio/Effects/{DelayLine,
      DigitalDelayEffect,TapeDelayEffect,MultiTapDelayEffect,StereoDelayEffect,BuiltInAudioEffects}`;
      30 new tests — Audio +24 (`DelayEffectsTests` + 2 `AudioMixerChainTests`), Core +5
      (`EffectCatalogTests`), Persistence +1; full suite **1261 green** (Core 355, Media 43, Render 123,
      Audio 105, Playback 56, Export 101, Persistence 116, Plugins 10, Mcp 65, App 287); clean build
      (0 warnings).)** Four ids under `builtin.audio.delay.{digital,tape,multitap,stereo}`, registered in
      `EffectCatalog` under `EffectCategory.Audio` — they appear in the Effects browser / audio add-effect
      flow and serialize through the existing `EffectInstance` JSON (additive, no schema bump) with **zero
      App/mixer changes** (the chain executor and catalog-driven UI are generic over ids). All four share a
      once-allocated `DelayLine` ring buffer (2 s ceiling) and the `Mix = 0` exact-pass-through convention;
      feedback clamps to 0.98 so the loop always decays. Delay time is **ms only** per the step's note
      (no tempo model yet). Digital: high-cut one-pole in the feedback path, bypassed at the 20 kHz
      ceiling for bit-clean repeats. Tape: fixed 5 kHz repeat low-pass + `tanh(kx)/k` soft saturation
      (unity small-signal gain) in the feedback path; wow (≤4 ms) + flutter (×6.3 rate, ⅛ depth) as
      deterministic sine LFOs of a sample counter — no RNG, bit-reproducible run-to-run. Multi-Tap:
      8 taps (enable/time/level/pan × 8 + mix = 33 typed descriptors; the generic Inspector renders all
      rows — the compact custom tap-grid section is the **flagged UI follow-up** the step anticipated),
      mono-summed line, constant-power per-tap pan, no feedback. Stereo: independent L/R lines, shared
      feedback, Ping Pong toggle cross-feeding repeats into the opposite channel by Cross-Feed (1 = the
      classic full bounce; off = fully independent dual mono).

## Step 47

47. **Noise Gate audio effect.** A new built-in `IAudioEffect` in `Sprocket.Audio/Effects` alongside
    `CompressorEffect`/`ReverbEffect`/the step-46 delays, registered through the same step-31 audio
    effect-chain scopes (clip / track / timeline bus / master) — no render-graph or persistence
    redesign. Follows the standard DAW gate design (Ableton Gate, Logic Noise Gate, Pro Tools
    Dyn3 Expander/Gate): an envelope follower on the input drives a gain that opens above a
    **threshold** and closes below it, shaped by **attack**, **hold**, and **release** times so it
    doesn't chatter on transients or clip off decaying tails, plus a **range** parameter (how far
    the gain closes — full mute vs. a partial attenuation floor, matching Pro Tools/Logic's "Range"
    rather than a hard on/off) and a **look-ahead**-free, causal design consistent with the other
    built-ins (real-time-safe, no future-sample buffering). A **ratio/hysteresis** control
    (separate open/close thresholds) avoids rapid re-triggering right at the threshold, the same
    problem `CompressorEffect` already solves for gain reduction on the other side of the transfer
    curve — reuse its envelope-follower/detector code where practical instead of re-deriving it.
    - **Core & catalog.** New `EffectTypeIds.Audio.NoiseGate` (`builtin.audio.noisegate`) with typed
      `EffectParamNames`/descriptors (threshold, attack, hold, release, range, hysteresis) registered
      in `EffectCatalog` under `EffectCategory.Audio`, so it appears in the Effects browser/menu and
      the audio chain add-effect flow like the other built-ins, and serializes via the existing
      `EffectInstance` JSON (additive, no schema bump). Per-instance envelope/gain state cached per
      `StateKey` and rebuilt only when the chain's ids/params change, mirroring the existing
      `AudioMixer` state-caching.
    - **Tests.** Deterministic DSP tests (steady tone above threshold passes at unity, steady signal
      below threshold attenuates to the configured range, attack/release timing matches expected
      sample counts, hold prevents premature closing on a brief dip, hysteresis prevents chatter at
      threshold-straddling input). Persistence round-trip. Extend
      `AudioEffectsTests`/`AudioMixerChainTests` (steady-state allocation-free assertion,
      pass-through/bypass, chain ordering) the same way the step-31 built-ins and step-46 delays
      were covered.
    - **✅ DONE (`Sprocket.Core/Model/{EffectInstance,EffectCatalog}` + `Sprocket.Audio/Effects/{NoiseGateEffect,
      BuiltInAudioEffects}`; 16 new tests — Audio +13 (`NoiseGateEffectTests` + 2 `AudioMixerChainTests`),
      Core +2 (`EffectCatalogTests`), Persistence +1; full suite **1277 green** (Core 357, Media 43,
      Render 123, Audio 118, Playback 56, Export 101, Persistence 117, Plugins 10, Mcp 65, App 287);
      clean build (0 warnings).)** One id, `builtin.audio.noisegate`, registered in `EffectCatalog` under
      `EffectCategory.Audio` — it appears in the Effects browser / audio add-effect flow and serializes
      through the existing `EffectInstance` JSON (additive, no schema bump) with **zero App/mixer changes**
      (the chain executor and catalog-driven UI are generic over ids). Typed descriptors: threshold
      (−40 dB default), attack (1 ms), hold (50 ms), release (100 ms), range (−80 dB floor ≈ full mute;
      the Pro Tools/Logic "Range" convention), hysteresis (3 dB). DSP reuses `CompressorEffect`'s
      detector shape (per-frame cross-channel peak, one-pole smoothing — instant rise + fixed 10 ms fall)
      driving a stereo-linked gain state machine: open at ≥ threshold, close only after the envelope has
      sat below threshold − hysteresis for the hold time, gain ramping between unity and the range floor
      through one-pole attack/release. Causal (no look-ahead), state carries across buffers, steady-state
      allocation-free; `RangeDb = 0` is an exact pass-through (the `Mix = 0` convention's analogue).
      Reuses the existing `thresholdDb`/`attackMs`/`releaseMs` param names; adds `holdMs`/`rangeDb`/
      `hysteresisDb`.

## Step 48

48. **Shelving EQ audio effect.** A new built-in `IAudioEffect` in `Sprocket.Audio/Effects`
    alongside `ParametricEqEffect`/`CompressorEffect`/the step-46/47 effects, registered through
    the same step-31 audio effect-chain scopes — no render-graph or persistence redesign. **Note:**
    `ParametricEqEffect` (step 31) already contains low- and high-shelf RBJ biquads as two of its
    three bands (`ConfigureShelf` in `ParametricEqEffect.cs`), so the DSP math for a shelf already
    exists and ships today for anyone adding the 3-band parametric. This step packages **standalone
    low-shelf and high-shelf controls as their own dedicated effect** — the DAW convention
    (Ableton EQ Three/Channel EQ, Logic Channel EQ's shelf-only mode, most console-strip plugins
    also expose bare shelves) for the common quick-tone-shaping case (a tilt/warmth/air pass) where
    dragging in a full 3-band parametric is heavier than needed and the mid peaking band is unused
    dead weight in the chain. Two shelves (low + high), each with **frequency**, **gain**, and a
    **slope/Q**-style shape control (RBJ shelf slope `S`, generalizing the step-31 fixed `S = 1`),
    and independent enable so either shelf can run alone (e.g. a low-cut-style low shelf with a
    single instance) — reuse `ConfigureShelf`'s biquad derivation (extended to accept `S` instead
    of assuming 1) rather than re-deriving the coefficients.
    - **Core & catalog.** New `EffectTypeIds.Audio.ShelvingEq` (`builtin.audio.shelvingeq`) with
      typed `EffectParamNames`/descriptors (low shelf freq/gain/slope/enable, high shelf
      freq/gain/slope/enable) registered in `EffectCatalog` under `EffectCategory.Audio`, appearing
      in the Effects browser/menu and audio chain add-effect flow like the other built-ins, and
      serializing via the existing `EffectInstance` JSON (additive, no schema bump). A default
      instance (both shelves at 0 dB) is an exact pass-through, matching `ParametricEqEffect`'s
      bypass-at-0dB behavior. Per-band biquad state cached per `StateKey`, rebuilt only on
      parameter/format change, same pattern as `ParametricEqEffect`.
    - **Tests.** Deterministic DSP tests (0 dB is bit-exact pass-through; boost/cut at a known
      frequency matches the expected shelf magnitude response at DC/Nyquist and at the corner
      frequency; slope/`S` changes the transition steepness monotonically; independent
      enable/disable per shelf). Persistence round-trip. Extend
      `AudioEffectsTests`/`AudioMixerChainTests` the same way the step-31 built-ins and steps 46/47
      were covered.
    - **✅ DONE (`Sprocket.Core/Model/{EffectInstance,EffectCatalog}` + `Sprocket.Audio/Effects/{BiquadBand,
      ShelvingEqEffect,ParametricEqEffect,BuiltInAudioEffects}`; 15 new tests — Audio +12
      (`ShelvingEqEffectTests` + 1 `AudioMixerChainTests`), Core +2 (`EffectCatalogTests`), Persistence +1;
      full suite **1292 green** (Core 359, Media 43, Render 123, Audio 130, Playback 56, Export 101,
      Persistence 118, Plugins 10, Mcp 65, App 287); clean build (0 warnings).)** One id,
      `builtin.audio.shelvingeq`, registered in `EffectCatalog` under `EffectCategory.Audio` — it appears in
      the Effects browser / audio add-effect flow and serializes through the existing `EffectInstance` JSON
      (additive, no schema bump) with **zero App/mixer changes**. Typed descriptors in shelf order: low
      freq (100 Hz) / gain (0 dB) / slope (1.0) / enable (on), high freq (8 kHz) / gain (0 dB) / slope (1.0) /
      enable (on). As planned, the DSP was **not re-derived**: `ParametricEqEffect`'s private per-band biquad
      machinery (RBJ coefficient derivation + transposed-direct-form-II runner) was extracted into a shared
      internal `BiquadBand` struct whose `ConfigureShelf` now takes the RBJ shelf slope `S` as a parameter
      (`alpha = sin/2·√((A+1/A)(1/S−1)+2)`, the step-31 `√2` being the exact `S = 1` case, so the parametric
      EQ is bit-identical through the refactor); `ShelvingEqEffect` is two such bands with per-shelf
      enable-and-0dB bypass. Coefficients rebuild only on parameter/format change (the `ParametricEqEffect`
      caching pattern); a default instance is an exact bit-identical pass-through; steady-state processing is
      allocation-free. Reuses the existing `lowFreq`/`lowGainDb`/`highFreq`/`highGainDb` param names; adds
      `lowSlope`/`lowEnable`/`highSlope`/`highEnable`. Tests cover the shelf magnitude response at DC /
      Nyquist / the corner (the RBJ dB midpoint, 10^(g/40)), monotonic slope steepening, independent
      enables, cut and boost, cross-block state, and the mixer-chain + persistence round-trips.

## Step 49

49. **Acoustic Space (Convolution) Reverb.** Spec (verbatim) in
    [`plan/features/convolution-reverb.md`](../features/convolution-reverb.md) — a dedicated
    `IAudioEffect` convolving the signal with a captured impulse response through partitioned
    convolution, with IR selection as an asset/file reference, predelay, IR-length trim, low/high
    damping, width, mix, latency/tail metadata, freeze steering, and graceful missing-IR behaviour.
    - **✅ DONE (2026-08-26; `Sprocket.Core/{Model/EffectInstance,Model/EffectCatalog,Rendering/RenderPlan,
      Rendering/RenderGraph,Commands/ModelCommands,Audio/AudioEffectTraits,Audio/IAudioEffectTail}` +
      `Sprocket.Audio/Effects/{ConvolutionReverbEffect,PartitionedConvolver,ImpulseResponse,FftPlan,WaveFile,
      BuiltInAudioEffects}` + `Sprocket.Persistence/{ProjectDto,ProjectSerializer}` +
      `Sprocket.App/Inspector/InspectorPanel` + `Sprocket.Mcp/{SprocketTools,StateFormatter}`; 36 new tests —
      Audio +24 (`ConvolutionReverbEffectTests` 22, `ConvolutionReverbChainTests` 2), Core +10
      (`ConvolutionReverbCatalogTests` 9, `ParameterKindTests` 1), Persistence +1, Mcp +1 (extended); full
      suite **2052 green** (Core 502, Media 66, Render 151, Audio 192, Playback 107, Export 141, Persistence 130,
      Plugins 10, Mcp 73, App 680); clean build, 0 warnings.)**
    - **DSP.** `PartitionedConvolver` is **zero-latency uniformly partitioned convolution** (Gardner): partition 0
      (B = 512 taps) runs as a direct time-domain convolution per sample — two contiguous `Vector<float>` dot
      products over a reversed head — while partitions 1..P−1 run as N = 1024-point overlap-save FFT blocks
      whose spectra are multiply-accumulated (vector-widened complex MAC, per-partition gain) into the tail for
      the *next* block each time an input block completes; later partitions only need input ≥ one block old,
      which is what lets the FFT half be scheduled a block ahead with no latency. The FFT is a managed radix-2
      `FftPlan` with precomputed tables (no native helper needed — see the perf note). IRs are capped at 10 s,
      resampled to the project rate with a Lanczos-8 windowed sinc, peak-normalised on import (DAW convention),
      and pre-partitioned/pre-transformed **off the audio thread** by `ImpulseResponseCache` (background
      `Task`, keyed by (path, rate), shared by every instance; failed loads are remembered until `Invalidate`).
      Around the convolver: pre-delay line (≤ 200 ms), **IR-length trim** implemented as a raised-cosine ramp on
      the per-partition gains (live, no re-transform; Length = 1 is bit-exact unity), one-pole HighDamp
      low-pass (20 kHz → 1 kHz) / LowDamp high-pass (20 Hz → 500 Hz) on the wet path (exact bypass at 0), and
      mid/side Width on a stereo wet pair. Mono IRs feed every channel; stereo IRs map L→L / R→R. Steady state
      is allocation-free; buffers reallocate only on rate / channel / IR-identity change. Output is bit-identical
      regardless of host buffer framing — the freeze-equivalence property, tested with 4800- vs 331-frame
      buffers over every control. Deterministic run-to-run.
    - **Perf (managed, no C-ABI FFT).** Release, x64 AVX2 (`Vector<float>.Count = 8`), 10 s of stereo 48 kHz
      audio in 512-frame buffers through a *stereo* IR: **1 s IR → 45× real time, 4 s → 18×, 10 s (the cap)
      → 9.8×** — comfortable headroom on one core even at the longest IR, so the spec's fallback (a small
      C-ABI FFT helper) was not needed. The head cost is fixed (2 × 512 MACs/sample/channel); the tail cost
      scales with IR length (P·N complex MACs per block).
    - **Hardening (from the ship-time code + security reviews).** `WaveFile` rejects implausible headers up
      front (channels > 8, rate outside 8–384 kHz, odd bit depths, `data` before `fmt`, unseekable device
      paths) and reads at most the 10 s it can keep, so a sparse multi-GB or hostile file costs nothing;
      `ImpulseResponse` trims in *source* samples before resampling and clamps the resampler's output length;
      the background loader catches every exception (never an unobserved fault, always `HasFailed` + message);
      the cache is bounded at 16 entries (~15 MB each worst case) and its insert is a single `GetOrAdd` (no
      indexer re-read a concurrent `Invalidate` could turn into a throw on the audio thread). The Inspector
      probes `File.Exists` once per distinct path (not per playhead move), also flags *(unreadable)* from the
      loader's verdict, only invalidates a cached *failure* on re-browse, and wraps its async picker handler.
      MCP gained `set_chain_effect_asset` (track / sequence / master scope) and every numeric-parameter tool
      rejects an Asset-kind name with guidance; `list_effect_types` omits the meaningless numeric range for
      asset descriptors.
    - **Core & catalog.** `EffectTypeIds.AudioConvolutionReverb` (`builtin.audio.reverb.convolution`, short code
      `IR`) registered under `EffectCategory.Audio`. **New descriptor kind `ParameterKind.Asset`** — a
      file/asset reference, not a number: it lives in a new `EffectInstance.Assets` string map (constant-only,
      copied by `Clone`/`CloneShifted`, fluent `SetAsset`), `EffectDescriptor.CreateInstance` leaves it unset,
      `ResolvedEffect` gained an optional `Assets` map + `GetAsset` that `RenderGraph.ResolveAudioChain` fills,
      and the undoable `SetEffectAssetCommand` is the string counterpart of `SetEffectParameterCommand`.
      Parameters: `impulseResponse` (asset), `preDelayMs`, `irLength` (new, 0.05–1), `lowDamp`, `highDamp`,
      `width`, `mix`. No factory presets (with user IRs the IR *is* the preset). `AudioEffectTraits.IsHeavy`
      flags it (Inspector freeze hint); the new optional `IAudioEffectTail` interface (Core.Audio) reports
      per-instance latency (0) and tail (= loaded IR length) — the step-41 metadata surface.
    - **Persistence & cache.** `EffectDto.Assets` (nullable, `WhenWritingNull`) — additive, no schema bump;
      asset-less effects and pre-49 files serialize byte-identically. Because `RenderCacheHasher` hashes the same
      DTOs, changing the IR invalidates freeze/render-cache segments like any parameter edit.
    - **UI & MCP.** The Inspector dispatches `ParameterKind.Asset` to a new file-picker row (file name /
      *None*, **Browse…** with a WAV filter, clear ×); a path whose file no longer exists shows *(missing)* in the
      warn colour with a relink tooltip, and re-browsing invalidates the cached failure. MCP: `set_effect_asset`
      tool (clip scope; rejects numeric parameters with guidance), and `get_clip` effect detail carries an
      `assets` object when present.
    - **IR licensing.** Ships with **no bundled IRs** (the spec's licensing-safe option) — the engine leads with
      user import. A curated CC0 / Sprocket-recorded IR library is a follow-on.
    - **Tests.** Impulse-in reproduces a 3000-tap synthetic IR exactly (< 2e-4) across the direct head and FFT
      partitions at non-aligned buffer framings; partitioned output matches a double-precision direct
      convolution on dense noise (< 1e-3 of peak); stereo L/R mapping and mono fan-out; pre-delay timing;
      length-trim partition gains (unity / mid-fade 0.5 / zero) and bit-exact recovery at Length = 1; damping
      attenuation with exact bypass at 0; width-0 mono collapse; framing independence (freeze equivalence);
      tail metadata; reset; zero-allocation steady state; FFT vs naive DFT; 16-bit + float32 WAV import with
      resampling/normalisation; missing-file → quiet failure → pass-through → relink; non-WAV rejection;
      mixer-chain echo timing across contiguous buffers via the real factory + cache; missing IR on a chain is
      a pass-through; catalog/kind/CreateInstance/traits; asset set/clear/clone; undo/redo of the asset
      command; `ResolvedEffect.GetAsset` and the audio plan carrying the asset; persistence round-trip with
      the `assets` field written only for the IR effect; MCP set/get/clear/reject.

## Step 50

50. **Shimmer Reverb.** A new built-in `IAudioEffect`, `src/Sprocket.Audio/Effects/ShimmerReverbEffect.cs`,
    split out as its **own dedicated effect** rather than a mode of `ReverbEffect` — same rationale
    as step 49, and matching how shimmer ships as a distinct plugin/mode selection in every DAW that
    offers it (Ableton Shimmer *device* wraps a delay+reverb+pitch chain, Valhalla Shimmer, Logic's
    ChromaVerb Shimmer *mode* is at least a dedicated preset family, not a parameter on the plate/hall
    algorithm). **This is the "Creative Reverb ▸ shimmer" tier sketched under step 41**, promoted to
    its own item so it ships and tests independently. An octave-shifted (typically +12 semitones,
    the canonical shimmer interval; an interval control covers +5th/+octave variants) feedback path
    layered under a conventional reverb tail, producing the ethereal, pitched-up wash the effect is
    known for (an ambient/post/scoring tool, not a dialogue/room tool).
    - **DSP.** A reverb tail (reuse the existing `ReverbEffect` Freeverb-style network, or the
      step-41 Studio Reverb once it exists, as the base wet signal) feeding a **pitch-shifted
      feedback loop**: pitch shift via a granular/overlap-add pitch shifter (allocation-free steady
      state — small fixed-size grain buffers allocated once, not per block) shifting the tail by the
      configured interval before feeding it back into the reverb input, so the shimmer builds and
      sustains under continued feedback. Parameters: shimmer amount/feedback level, pitch interval
      (+octave default, selectable), base reverb size/decay/damping (the underlying tail controls),
      wet/dry mix. Feedback gain must be clamped below unity-with-margin so the shimmer path can't
      runaway/blow up regardless of parameter combination — a correctness requirement, not just a
      quality one, since this sits in a real-time audio chain.
    - **CPU.** Pitch-shifting adds real DSP cost on top of the reverb network; follows step 41's
      per-effect quality-mode convention (Draft/Realtime/High/Offline) so a heavy shimmer setting
      can still be auditioned at reduced quality in realtime and rendered clean via Freeze
      (step 32) or export.
    - **Core & catalog.** `EffectTypeIds.Audio.ShimmerReverb` (`builtin.audio.reverb.shimmer`)
      registered in `EffectCatalog` under `EffectCategory.Audio` with typed descriptors (shimmer
      amount, pitch interval, base reverb size/decay/damping, mix), serializing via the existing
      `EffectInstance` JSON (additive, no schema bump). Presets: classic shimmer, dark shimmer
      (damped/low interval), fifth shimmer, drone/infinite (near-unity feedback, deliberately
      sustained wash).
    - **Tests.** Deterministic tests (pitch-shifted feedback lands at the expected interval on a
      pure test tone within pitch-detection tolerance; feedback never diverges — RMS output stays
      bounded over an extended run at max feedback setting; 0 shimmer-amount collapses to the base
      reverb's existing behavior/tests); persistence round-trip; extend
      `AudioEffectsTests`/`AudioMixerChainTests` for allocation-free steady state and chain
      ordering, the same way prior audio-effect steps were covered.
    - **✅ DONE (`Sprocket.Core/{Model/EffectInstance,Model/EffectCatalog,Audio/AudioEffectTraits}` +
      `Sprocket.Audio/Effects/{ShimmerReverbEffect,BuiltInAudioEffects}`; 13 new tests — Audio +10,
      Core +3, Persistence +1; full suite green.)** `builtin.audio.reverb.shimmer` as its own
      `IAudioEffect`, reusing the `ReverbEffect` Freeverb comb/allpass network (Size scales line
      lengths, Decay maps to per-comb RT60 feedback `0.001^(combSeconds/decay)`, clamped below unity)
      as the base tail, with the wet mono sum fed through a granular (dual-tap, sine-crossfaded delay
      line) pitch shifter and re-injected into the comb inputs at `ShimmerAmount`. **Unconditionally
      bounded regardless of parameter combination:** the re-injection gain is normalized by the comb
      bank's frequency-averaged gain so full Shimmer targets a mean loop gain below unity, the return
      path is high-passed (keeps DC/rumble intermodulation from accumulating), and the return
      additionally passes through `tanh` as a hard ±1 backstop — verified by a 10 s sustained-loud-input
      test at maximum Decay/Size/Shimmer with zero damping. `ShimmerInterval` (1–12 semitones, default
      +12) sets the shifter ratio `2^(interval/12)`; pitch-landing tests use a Goertzel band-power scan
      to confirm the +12 st and +7 st presets land distinctly (within the granular shifter's splice-
      sideband tolerance) rather than a single-bin FFT. All buffers allocate once at Size = 1 lengths, so
      steady-state `Process` is allocation-free like the other reverb tiers; Shimmer = 0 is bit-identical
      across any `ShimmerInterval` (the pitch path contributes nothing) and Mix = 0 is an exact pass-
      through. Catalog ships four presets (Classic/Dark/Fifth/Drone-Infinite, all leaving Mix untouched)
      and joins `AudioEffectTraits.IsHeavy` alongside Studio Reverb as a freeze candidate. No Inspector/UI
      or MCP work was needed — the typed-descriptor pipeline and existing `move_effect`/chain tooling
      already cover any new `EffectTypeId`.

## Step 51

51. **Reorder effects within an audio chain.** No command exists today to move an `EffectInstance`
    within a chain **in place** — `Sprocket.Core/Commands/ModelCommands.cs` has `AddEffectCommand`/
    `InsertEffectAtCommand`/`RemoveEffectCommand` (clip scope) and `AddChainEffectCommand`/
    `RemoveChainEffectCommand` (step-31 track/bus/master scopes), but reordering an existing stack
    means remove-then-reinsert, which is not undoable as one step and (worse) drops any per-effect
    UI/selection state keyed by identity across the two commands. Since **stack order is processing
    order** (§5d/step 31 doc comments), and the new delay/gate/EQ/reverb effects from steps 46–50
    make multi-effect audio chains routine (e.g. gate → EQ → compressor → delay → reverb), users need
    to fix ordering mistakes and experiment with signal-flow order without rebuilding the chain.
    - **Core.** A new `MoveEffectCommand(IList<EffectInstance> chain, EffectInstance effect, int newIndex)`
      (or a dedicated `MoveChainEffectCommand` mirroring the existing Add/Remove split between clip
      scope and the step-31 chain scopes, if the two need different edge-case handling) that captures
      the original index, moves the instance in one atomic step, and reverts by moving it back — one
      undo entry, matching the coalescing conventions already used for slider-drag edits
      (`SetEffectParameter`) and the position-preserving undo `AddChainEffectCommand`/
      `RemoveChainEffectCommand` established. Works uniformly across all four chain scopes (clip via
      `Clip.Effects`, track via `AudioTrack.Effects`, sequence bus via `Timeline.AudioEffects`, master
      via `ProjectSettings.MasterAudioEffects`) since they're all plain `EffectInstance` lists.
    - **UI (`Sprocket.App/Inspector/InspectorPanel.cs`).** Today each effect renders as its own
      collapsible section per step 16's Inspector; add **drag-to-reorder** on the section headers
      (matching the existing drag-effect-from-browser gesture the Inspector already hosts, step 16b)
      plus a keyboard/menu-accessible **move up / move down** affordance as a discoverable fallback
      for users who don't drag. Clip-scope chains are the first UI target since clip-scope editing is
      the only chain scope with a live Inspector surface today (step 31's track/bus/master chains are
      still model+command+persistence-complete but UI-less); extend to the future mixer-panel insert
      UI (noted as outstanding in step 31) when that ships.
    - **MCP.** `Sprocket.Mcp/SprocketTools.Clips.cs` already exposes `add_effect`/`remove_effect`/
      `set_effect_parameter`; add a `move_effect` (or `reorder_effect`) tool alongside them so
      AI-driven edits can reorder a chain the same way a user can, staying on the existing
      `EditHistory`-routed marshal point so it's undoable by construction (per the Mcp architecture
      note).
    - **Tests.** Core — move-to-various-indices (start/end/no-op/out-of-range clamp) preserves list
      identity and content, undo restores the exact original index (not just "somewhere"), redo
      reapplies; a move interacting with a concurrent add/remove elsewhere in the same undo
      transaction if the two are ever composed. Persistence — order round-trips unchanged (already
      covered indirectly by existing chain-order tests, but add an explicit reorder-then-save case).
      App — Inspector drag-to-reorder headless interaction test, move up/down affordance at the
      first/last position (clamped, not wrapping).
    - **✅ DONE (`Sprocket.Core/Commands/ModelCommands.cs` `MoveChainEffectCommand` + `Sprocket.App/
      Inspector/{EffectReorder,InspectorPanel}` + `Sprocket.Mcp/SprocketTools` `move_effect`; 16 new
      tests — Core +6, App +8 `EffectReorderTests`, Mcp +1 & tool-surface, Persistence +1).** One
      uniform `MoveChainEffectCommand(IList<EffectInstance>, effect, newIndex)` covers all four chain
      scopes (the option the step left open — no separate clip-scope command needed, since `Clip.Effects`
      is the same list shape): captures the original index, clamps the target, applies atomically, and
      reverts to the exact original position; a same-index move applies as a no-op and callers skip
      executing one so history stays clean. Inspector (clip scope, the only chain scope with a live UI):
      **drag a section header** onto another effect section — top half inserts before it, bottom half
      after (the slot math is the pure, headlessly-tested `EffectReorder.DropIndex`; the drag arms on the
      header and starts past a 4px threshold so a plain click still toggles the section, with the move
      handler on the panel because the Expander header's ToggleButton captures the pointer) — plus
      **Move Up / Move Down** on the header's context menu as the non-drag fallback, boundary items
      disabled (clamped, not wrapping). MCP `move_effect(clip_id, effect_index, new_index)` rides the
      same command through `EditHistory`, clamping and reporting the resulting index. Alongside, a batch
      of Inspector polish from user feedback: effect-header eye/× glyphs dropped to a new
      `IconSizes.Dense` tier + the Expander chevron resource 16→13; plain-string section headers (Clip /
      Multicam / TEXT) normalized to the effect headers' 12px semibold; the `+ Effect` menu at 13px;
      pane-header **Expand All / Collapse All** buttons (Feather chevrons-down/up, tooltipped) acting on
      every section; and a 192px `MinWidth` floor on the Inspector column (lifted to 0 while the pane is
      hidden so View ▸ Inspector Panel can still collapse it).
## Step 52

52. **Additional camera log profiles (non-DJI).** Extend step 37's input color transform
    (`builtin.colortransform`) to the other camera manufacturers' log profiles, landing entirely on
    the existing seam — `ColorProfiles.All` grows, nothing else changes about the UI, persistence,
    export bake/pass-through, or undo/redo ([ARCHITECTURE §18](ARCHITECTURE.md)). Scope, decided with
    the user up front: **ARRI LogC3, ARRI LogC4, Sony S-Log3 (S-Gamut3.Cine), Panasonic V-Log
    (V-Gamut — the same curve Leica licenses for L-Log), Canon C-Log3 (Canon Cinema Gamut),
    Blackmagic Film Generation 5 (Blackmagic Wide Gamut Gen 5), Fujifilm F-Log2, and Nikon N-Log**.
    Unlike DJI, every one of these vendors publishes its curve's exact transfer-function formula and
    gamut primaries in its own color-science whitepaper — so, per the user's decision, these convert
    via **closed-form math (SkSL), not a bundled vendor LUT file**. That sidesteps the DJI path's
    real non-technical cost (each vendor's `.cube` is still their copyrighted asset, tracked via
    `Luts/NOTICE.md` — eight more would mean eight more redistribution-license reviews) and is more
    precise than a LUT's discrete sampling. Deliberately excluded, mirroring how step 37 already
    recorded D-Log 2 as excluded: **GoPro GP-Log/Protune** and **Insta360 Log** (neither vendor
    publishes a formula or an official redistributable LUT — nothing public to implement against);
    **legacy Sony S-Log2, Canon C-Log/C-Log2** (superseded by S-Log3/C-Log3 on current lineups; can
    be added later as pure data, same mechanism, if requested); **RED Log3G10** (RED's IPP2 pipeline
    is a fuller color-science system than a curve+matrix — getting it subtly wrong with no reference
    footage to validate against is a real risk, left for a dedicated follow-up).
    - **Core (`Sprocket.Core/Model/ColorProfiles.cs`, new `ColorProfileCurves.cs`).** Eight new
      profile ids appended to `ColorProfiles.All`/`DisplayNames` (DJI keeps indices 0/1 — persisted
      project indices don't shift); a new `ColorProfileKind { Lut, Curve }` + `KindOf` so Render
      knows which path a profile takes; `ColorProfileCurves.Decode(profileId, v)` — each vendor's
      decode transcribed directly from their own spec (cited per profile in the doc comments) — and
      `ColorProfileCurves.GamutOf(profileId)` — matrices computed from each vendor's published CIE xy
      primaries via the standard primaries→RGB-to-XYZ method, independently cross-checked against
      every vendor that also publishes a pre-computed matrix (ARRI, Panasonic, Blackmagic — all
      matched to 6+ decimal places). `EffectCatalog`'s `SourceProfile` parameter `Max` changed from a
      literal `1.0` to `ColorProfiles.All.Count - 1` (was silently stale — a real latent bug once
      more than 2 profiles existed, since the generic slider-clamp path would have clipped any new
      index back down to 1).
    - **Render (`SkiaEffectPipeline.cs`).** One new shader, `ColorTransformCurveSksl` — no texture
      child, only uniforms (a `kind` discriminant + 7 constants + a 9-float native-gamut→Rec.709
      matrix) — with a `decode1` function branching on `kind`: several vendors happen to publish the
      identical "linear toe below a cut, log10 body above" shape (kind 0: LogC3/V-Log/F-Log2, sharing
      one branch via their own constants), the rest are each a distinct shape (LogC4: log2 body;
      S-Log3: literal 10-bit-code-value formula; C-Log3: symmetric three-piece; Blackmagic: natural-exp
      body; N-Log: cubic toe + natural-exp body) — one branch apiece, still a single compiled program.
      `BuildColorTransformShader` now branches on `ColorProfiles.KindOf`: LUT profiles take the
      unchanged step-37 texture-child path; curve profiles build this uniforms-only shader instead.
      Final encode matches every other grading effect's Rec.709-working-space convention (sRGB
      transfer, `AcesFilmicEffect`'s own convention) rather than the legacy broadcast OETF.
    - **Media (`ColorProfiles.DetectLogProfile`).** Generalizes `DetectDjiLog` into a dispatcher that
      tries DJI's own heuristic first, then a conservative substring match per new vendor
      ("logc3"/"logc4"/"slog3"/"vlog"/"clog3"/"flog2"/"nlog"/"blackmagicfilm"); `MediaSource.Probe`'s
      one call site swapped from `DetectDjiLog` to the dispatcher.
    - **Tests.** The critical risk isn't the plumbing (all reused) — it's transcribing each vendor's
      published constants correctly with no camera footage to validate against in CI. Core: per
      profile, continuity at the vendor's own published piecewise threshold (catches a
      transcription slip at a segment boundary — this is what actually caught a real bug, see
      below), monotonicity across the full range, and round-trip against an independently
      transcribed encode formula written fresh in the test file from the same vendor spec; gamut
      matrices checked for white preservation (a valid conversion matrix's rows sum to ~1). Render:
      the compiled SkSL shader checked against the same Core reference math at representative code
      values (mid-gray, near-black, near-white), monotonicity on the GPU path, alpha preservation
      and grade-chaining. Media: one new tagged fixture (S-Log3) proving the dispatcher is wired
      through the real probe (the dispatcher's own string logic is unit-tested without FFmpeg in
      Core). Export: one new profile (LogC3) proving the bake/pass-through toggle works for a curve
      profile too (`StripEffects` is already generic over the effect type id, not the profile, so one
      is enough).
    - **✅ DONE (Core +2 files, Render shader + dispatch, Media dispatcher, Export/tests; 87 new tests
      — Core +60, Render +25, Media +1, Export +1; full suite 1427 green).** Every formula was
      transcribed from an official vendor document (ARRI's own PDFs for LogC3/LogC4, Panasonic's
      V-Log/V-Gamut manual, Nikon's N-Log specification, Fujifilm's F-Log2 data sheet) or
      cross-checked against the independent, widely-used `colour-science` Python library (Sony
      S-Log3, Blackmagic Film Gen 5) — not taken as-is from a single secondary source. That
      cross-checking caught one real transcription error before it shipped: an AI-summarized reading
      of Fujifilm's F-Log2 formula had the `a` constant off by 10× (`0.555556` vs. the official
      `5.555556`), silently breaking continuity at the curve's own published threshold — caught by
      the Core continuity test, fixed against the official Fujifilm PDF, confirmed by re-running the
      round-trip test. ARRI LogC4 and Canon C-Log3's constants are validated by the same
      continuity/monotonicity/round-trip suite but were not independently cross-checked against a
      second source the way the other six were — a residual gap worth closing in a follow-up if
      real-world footage surfaces a discrepancy. Leica L-Log is served by the same `PanasonicVLog`
      profile entry (display name notes the alias) rather than a separate id, since Leica licenses
      the identical curve.
## Step 53

53. **Clip right-click context menu (+ Split at Playhead, Duplicate, Enable/Disable clip).** Every
    leading editor puts the standard clip operations on a right-click menu
    on the clip itself; Sprocket's are reachable only from the menu bar and keyboard (step 16c).
    Nearly everything the menu needs **already exists** as an undoable public `TimelineControl`
    method — `CutSelected`/`CopySelected`/`PasteAtPlayhead`, `DeleteSelected`,
    `RippleDeleteSelected`, `UnlinkSelected`, `SetSelectedClipSpeed` (step 21), the frame-hold
    family (step 43), `NestSelection` (step 23), `SwitchSelectedAngle` (step 24), Normalize Audio
    (step 30), Interpret Footage (step 42) — so most of this step is wiring, plus three genuinely
    new operations. Menu contents mirror the grouping used by leading editors: Cut / Copy / Paste / **Duplicate** ·
    Delete / Ripple Delete · **Split at Playhead** (`Ctrl+K`, the Add Edit shortcut used in leading editors) ·
    **Enable** (checkable, `Shift+E`, the convention in leading editors) / Unlink / Link (stays visibly
    disabled until step 55, per the step-16c "wired or visibly disabled, never silently dead"
    rule) · Speed/Duration… / Frame Hold ▸ · Nest / Normalize Audio / Interpret Footage… /
    Multicam ▸. Deliberate departures: Nudge stays keyboard/menu-bar-only (leading editors don't put
    nudge in the context menu); Rename / label color omitted (no model support yet).
    - **Core (`Sprocket.Core/Model/Clip.cs`, `Rendering/RenderGraph.cs`).** The step's only model
      change: `Clip.Enabled { get; set; } = true` — a disabled clip renders nothing and
      contributes no audio, like the Enable toggle in leading editors — copied by `CloneContentForSpan` so
      blade/duplicate/paste preserve it. Toggled through the existing generic
      `SetPropertyCommand<bool>` (step 10; no new command class — the same pattern `UnlinkSelected`
      uses for `LinkGroupId`). `RenderGraph` skips disabled clips in `ResolveClipLayer` (which
      also covers a transition side resolving to a disabled clip — it falls back like a missing
      clip, §15) and in the audio planner's per-track clip resolution. `Track.ResolveActiveClip`
      is deliberately **unchanged** so trim/snap/edit logic still sees disabled clips.
    - **Persistence.** A `bool? Enabled = null` clip-DTO field (absent ⇒ true — the same
      absent-means-default idiom as `GainDb`, so pre-53 files load unchanged); include the field in
      `RenderCacheHasher` so toggling invalidates cached renders (step 32).
    - **UI (`Sprocket.App/Timeline/TimelineControl.cs`, `MainWindow.axaml{,.cs}`).** A
      right-button branch in `OnPointerPressed` before the left-button guard: hit-test with the
      existing `TryHitClip`, `Select(clip)`, then raise a `ClipContextMenuRequested` event (the
      `TitleEditRequested` pattern — the custom-drawn control can't host child controls, and the
      dialog-backed items live in MainWindow). MainWindow builds the `ContextMenu` imperatively
      per the established `MediaBrowserPanel` idiom, reusing the menu-bar items' handlers and the
      `RefreshClipMenu`/`RefreshEditMenu` enablement predicates at build time (the menu is rebuilt
      per open, so no refresh plumbing). New `TimelineControl` methods: `SplitAtPlayhead()` —
      extract the split core (`SplitAt(track, clip, at)`: companion collection, fresh right-hand
      link group, one `CompositeCommand`, select the right half) out of the pointer-driven
      `BladeClip`, which keeps only the cursor/snap math; `DuplicateSelected()` — a copy placed at
      the original's `TimelineEnd` on the same track, linked companions duplicated together under
      a fresh `LinkGroupId`, one undo entry; `ToggleSelectedEnabled()` — linked companions toggle
      together when Linked is on, matching leading editors. Disabled clips draw dimmed in `DrawClips`.
      Wire the stubbed `ClipEnableMenuItem` (checkable, reflecting `SelectedIsEnabled` in
      `RefreshClipMenu`); add **Clip ▸ Split at Playhead** and **Clip ▸ Duplicate** so every
      context item has a menu-bar home; `Ctrl+K` / `Shift+E` land in the central `OnKeyDown`
      (menu `InputGesture` strings are display-only, per step 16c).
    - **MCP.** `split_clip(clip_id, at)`, `duplicate_clip(clip_id)`,
      `set_clip_enabled(clip_id, enabled)` in `Sprocket.Mcp/SprocketTools.Clips.cs`, routed
      through `EditHistory` like the existing clip tools so AI edits stay undoable by
      construction.
    - **Tests.** Core — `Enabled` defaults true, `CloneContentForSpan` copies it,
      `PlanVideoFrame` yields no layer for a disabled clip (and a transition with a disabled side
      falls back per §15), `PlanAudioBuffer` skips it, the toggle undoes/redoes. Persistence —
      `Enabled=false` round-trips, a pre-53 file (field absent) loads enabled, the render-cache
      hash changes on toggle. App (headless) — `SplitAtPlayhead` splits at the playhead and
      no-ops at an edge/outside the clip, linked companions split together into a fresh
      right-hand group; `DuplicateSelected` places at `TimelineEnd`, duplicates a linked pair
      under a fresh group, one undo entry; `ToggleSelectedEnabled` toggles linked pairs together.
      Mcp — tool-surface + one behavioral test per new tool. On completion, add FEATURES.md rows
      (❌ undocumented) for the context menu, Split at Playhead, Duplicate, and Enable/Disable.
    - **✅ DONE (Core `Model/Clip` + `Rendering/RenderGraph`; Persistence DTO field; App
      `ClipEdits.cs` + `TimelineControl` + `MainWindow`; Mcp `set_clip_enabled`; 21 new tests —
      Core +6, Persistence +3, App +9, Mcp +2 (tool-surface entry + behavioral), plus the
      Enable/Disable render-cache-hash test; full suite 1514 green).** Landed as planned with two
      notes. (1) The split core was extracted to a pure `ClipEdits` static class (the
      `ClipboardOps` idiom) rather than a private `TimelineControl` method, so the
      companion-collection / fresh-right-group / one-composite logic is headlessly testable;
      `BladeClip` and `SplitAtPlayhead` both call it, and `DuplicateSelected` /
      `ToggleSelectedEnabled` follow the same pattern. (2) Duplicate places the copies shifted by
      the whole linked group's extent (the MCP `duplicate_clip` convention) — identical to "at the
      original's `TimelineEnd`" for the normal equal-span A/V pair, but overlap-free when a linked
      companion is longer than the addressed clip. The MCP tools `split_clip`, `duplicate_clip`,
      and `link_clips` already existed from step 38, so only `set_clip_enabled` was added (and
      `list_clips`/`get_clip` now report `enabled: false` on disabled clips). The context menu also
      lists a multicam clip's angles (active one checked) under Multicam ▸, since the menu is
      rebuilt per open anyway. Enable state is hashed for the render cache for free — the hash
      already covers the clip DTO, which gained the `Enabled` field. The menu is shaped by the
      clip's lane kind, as leading editors shape their clip menus (the event carries the hit clip's
      `Track`): video-only items (Frame Hold ▸, Interpret Footage…, Multicam ▸) appear only on a
      video-track clip, Normalize Audio only on an audio-track clip — on a linked pair the video
      clip contributes no audio (its companion does), so Normalize on it would silently set a
      `GainDb` the audio planner never reads. Linkedness itself never changes the item set, only
      what the operations act on.
## Step 54

54. **Multi-clip selection.** Selection is a single field today (`TimelineControl._selected`), which
    blocks Link (step 55), keeps **Edit ▸ Select All** deliberately disabled (step 16c), and rules
    out the batch operations every leading editor supports (delete/copy/nudge several clips at
    once). The selection model becomes a **set with a primary clip**: the existing single-clip
    `SelectedClip` surface is preserved as the primary, so the Inspector, Source monitor, and
    keyframe navigation (steps 16/16d/17) — all inherently single-clip — keep working unchanged.
    - **Selection gestures.** Ctrl-click toggles membership; Shift-click extends; a **rubber-band
      marquee** drag on empty lane area selects every clip it touches (the band-drag state machine
      already exists as `DragKind.Band` for the opacity rubber-band — the lane-area marquee is a
      new drag kind with pure hit math in `TimelineMath`, headlessly testable). Plain click keeps
      today's behavior (select one, clear the rest). Mixed video+audio selections are allowed
      (as in leading editors). Multi-selected clips all draw with the selection treatment; the primary is
      visually distinct.
    - **Operation surface.** The `SelectedXxx` predicate surface generalizes: batch-capable
      operations — Delete, Ripple Delete, Cut/Copy, Nudge, Enable/Disable (step 53) — act on the
      whole set as one `CompositeCommand` (one undo entry); dialog-backed operations
      (Speed/Duration, Interpret Footage) act on the primary. Multi-clip **move drag** moves the
      set rigidly (group-clamped like linked nudge, step 16c). Enable **Select All**.
    - **Tests.** Selection-set semantics (toggle/extend/clear, primary tracking); marquee hit math
      in `TimelineMath` (pure); batch delete/copy/nudge/enable as a single undo entry; Select All;
      multi-clip drag placement.
    - **✅ DONE (App `Timeline/ClipSelection.cs` (new, the set + primary) + `TimelineMath` marquee hit
      math + `ClipEdits` batch builders + `ClipboardOps.PasteAll` + `TimelineControl` gestures/drawing;
      MainWindow Select All wiring, menu + Ctrl+A below the text-box guard; 21 new App tests; full suite
      1535 green).** Landed as planned with three notes. (1) The selection set is a pure `ClipSelection`
      class (ordered members + primary, with the toggle/extend/prune primary hand-off rules), and the
      batch operations are pure `ClipEdits` builders (`ExpandWithLinked` dedups selection ∪ linked
      companions; `DeleteAll` / `RippleDeleteAll` / `NudgeAll` / `ToggleEnabledAll` / `MoveSet`, one
      composite each) plus `ClipboardOps.PasteAll` — the `ClipboardOps` idiom, so all of it is headlessly
      tested. Batch ripple delete gives each survivor its TOTAL shift across every removed upstream clip
      on its track (per-removal absolute placements would clobber one another when two selected clips
      share a track). (2) Gesture details: Ctrl+click on the opacity rubber-band keeps its step-39 meaning
      (add a fade point) — the fade-gesture hit test runs before the membership toggle; a plain press on a
      multi-selected member keeps the set, re-anchors the primary, and collapses to just that clip on a
      release without movement (the rule in leading editors); the lane-area marquee replaces drag-scrub for the
      Select tool only (a sub-threshold click still clears the selection and moves the playhead; other
      tools keep the old behavior; Ctrl/Shift makes the marquee additive), matching the lane-area
      marquee convention in leading editors. (3) A multi-clip move shifts every member rigidly in time on its own track while the
      dragged clip may change lanes — the convention linked companions already used — and Alt-copy still
      duplicates just the primary. Cut/Copy snapshots exactly the selected clips (linked companions are
      not implicitly copied — unchanged from 16c) and pasted clips stay link-free (the step-13/16c
      convention); the pasted set becomes the selection, with the clipboard ordered primary-first so the
      paste re-anchors on the copy of the clip the user had anchored (the primary need not be first in
      insertion order after a re-anchor). Enable/Disable converges a mixed selection on the primary's
      new state (the step-53 linked-pair rule, applied set-wide). Nest — enabled by the now-wider
      `HasSelection` — was extended to nest the whole selection set (not just the primary), since a
      menu item enabled by a multi-selection acting on one member would silently drop the rest;
      Speed/Duration and the other dialog-backed operations stay primary-only per this step's spec.
      Selection membership and the batch expansions use hash-set dedup so a Select All-sized set and the
      per-pointer-move marquee rebuild stay O(n) on clip-heavy timelines.
## Step 55

55. **Link clips (re-link A/V).** Unlink shipped with linked A/V (step 13/16c) but re-linking never
    did — `ClipLinkMenuItem` has sat stubbed-disabled since 16c, because linking has no natural
    single-clip trigger: in every leading editor you select a video clip and an audio clip, then
    Link. **Depends on step 54 (multi-select).** With a multi-selection of ≥2 clips spanning at
    least one video and one audio clip (the eligibility rule used in leading editors), set a fresh shared
    `LinkGroupId` on every selected clip via the existing `SetPropertyCommand<Guid?>` under one
    `CompositeCommand` — the exact mirror of `UnlinkSelected`; **no Core change** (the model,
    persistence, and every linked-aware edit already key off `LinkGroupId`, step 13). Wire
    `ClipLinkMenuItem` and the step-53 context-menu item; `Ctrl+L` (the shortcut used by leading editors)
    toggles Link/Unlink by selection state. MCP: `link_clips(clip_ids)` alongside the existing
    unlink surface.
    - **Tests.** Link sets one shared group across the selection in a single undo entry (undo
      restores each clip's prior group, including previously-linked members re-linked into a new
      pair); eligibility rules (rejects all-video / all-audio / single-clip selections); `Ctrl+L`
      toggle behavior; persistence round-trip is already covered by existing `LinkGroupId` tests.
    - **✅ DONE (App `ClipEdits` link builders (`CanLink` / `LinkAll` / `Unlink` / `ToggleLink`, the
      step-53/54 pure-builder idiom) + `TimelineControl.CanLinkSelection`/`LinkSelected`/`ToggleLinkSelected`;
      MainWindow `ClipLinkMenuItem` + context-menu item + `Ctrl+L` (⌘L on macOS) below the text-box guard;
      6 new App tests; full suite 1541 green).** Landed as planned — the MCP `link_clips` tool had
      already shipped with the step-38 tool surface, so this step was UI-only — with two notes.
      (1) Eligibility got one refinement beyond the spec: a selection that is exactly one whole link
      group is *not* linkable (re-linking it would be a no-op that pollutes undo), which is what makes
      `Ctrl+L` a strict toggle on a linked pair — link an eligible V+A selection, press again to unlink;
      a selected *subset* of a larger group stays linkable, since re-pointing it at its own fresh group
      is a real edit. Linking re-points only the selected clips: an unselected companion of a
      previously-linked member keeps its old group (and undo restores each clip's prior group
      individually, per the SetProperty mirror of Unlink). (2) `TimelineControl.UnlinkSelected` was
      refactored onto the new pure `ClipEdits.Unlink` (behavior unchanged) so Link and Unlink share one
      headlessly-tested home; both menu items display the shared `Ctrl+L` shortcut since the key
      dispatches by selection state.

## Step 56

56. **Windows 10 support (verify + declare).** Promote Windows 10 to a fully supported platform
    alongside Windows 11, with a support floor of **Windows 10 64-bit, version 1809 or later**
    (x64 / arm64) — .NET 10's oldest supported Windows 10 baseline (covers LTSC 2019), and in line
    with leading editors, whose Windows requirements range from Win10 1903 through 22H2. Deliberate nuance:
    consumer Windows 10 editions are past Microsoft end-of-support (Oct 2025), so Sprocket supports
    the OS on a "runs and is tested" basis, not an implied-OS-security basis. **No code changes are
    expected** — an audit (recorded here so nobody hunts for a gate later) found nothing that blocks
    Windows 10: both `app.manifest` files carry only DPI settings (no `<supportedOS>` GUIDs), the
    TFM is plain `net10.0` (no `SupportedOSPlatformVersion`), release RIDs are architecture-only,
    and no code path checks the Windows version (`OperatingSystem.Is*` calls branch on OS family,
    never version). Sprocket ships self-contained, so no .NET install is needed on the target. The
    work is verification + declaration:
    - **Verification prerequisite — a Windows 10 VM** (none exists today): create a Windows 10
      22H2 64-bit VM (Hyper-V + Microsoft's Windows 10 evaluation / media-creation ISO). Manual
      smoke checklist on a release build: install via `Setup.exe`; open the sample project;
      playback with clean A/V sync; hardware decode active (or clean software fallback); MP4
      export; in-app auto-update from a prior version; uninstall. Also launch from the portable
      zip. GitHub Actions has no Windows 10 runners, so CI stays on `windows-latest`; Windows 10
      coverage is this manual checklist, re-run per release-worthy change.
    - **Copy updates in this repo** ("Windows 11" → "Windows 10 & 11", with the 1809+ floor
      wherever requirements are stated): `README.md` intro + requirements-table row;
      `RELEASE_NOTES.md` header / bug-report OS line / testing note (keep "primary testing is on
      Windows 11", add "smoke-tested on Windows 10"); the target-OS statements in `BRIEF.md`,
      `ARCHITECTURE.md` §1, `CLAUDE.md`, and this file's Context/Decisions/Verification sections;
      the generated release-body download table in `.github/workflows/release.yml`
      ("**Windows 11** (most PCs)" → "**Windows 10 / 11** (most PCs)"); and a `FEATURES.md`
      platform-support row amended in place (or added, starting ❌ undocumented).
    - **Sibling repos:** `../sprocket-website/prototype/index.html` — OS-grid card label
      "Windows 11" → "Windows 10 & 11" (subtitle stays "x64 · arm64"; the `detectOS()` script has
      no version gate and needs no change). `../sprocket-docs` — no OS-requirements copy exists
      today; add a short "System requirements" subsection to the getting-started page listing
      Windows 10 (64-bit, 1809+) & 11 / Linux / macOS, then redeploy (`deploy.ps1`) and regenerate
      the PDF manual.
    - **Tests.** No automated tests (no code change). Acceptance = the Windows 10 VM smoke
      checklist passes on a release build, and `grep -ri "windows 11"` across the three repos
      shows only intentional remnants (e.g. "primary testing is on Windows 11").
    - **🟡 DECLARATION DONE (2026-07-11); VM smoke verification pending.** The full copy sweep
      landed across all three repos: this repo (`README.md` intro + platform table + Planned/Roadmap,
      `RELEASE_NOTES.md` header / bug-report OS line / testing note, `BRIEF.md`, `ARCHITECTURE.md` §1,
      `CLAUDE.md`, this file's Context/Decisions/Verification sections, the `release.yml` download
      table, and a new `FEATURES.md` platform-support row, ❌ undocumented), the website OS-grid card,
      and a new "System requirements" section in the docs getting-started page. As planned, no code
      changed (re-confirmed: no `<supportedOS>` GUIDs, no `SupportedOSPlatformVersion`, no
      version-gated code paths). **Remaining:** create the Windows 10 22H2 VM and run the manual
      smoke checklist above on a release build; redeploy the docs site (`deploy.ps1`) and regenerate
      the PDF manual; flip the FEATURES.md row when the docs section is audited.

## Step 57

57. **Linux support (verify + declare, like step 56).** Move the Linux builds from "runs the same
    managed code" toward a defensible support statement. The build/packaging groundwork is done — the
    remaining gap is *verification breadth*, and the honest interim posture is **experimental on modern
    glibc-based desktops (glibc ≥ 2.35 / Ubuntu 22.04+), software encode/decode dependable, hardware
    accel driver-dependent; musl/Alpine unsupported.** Two phases, most of it landed:
    - **✅ DONE (2026-08-03) — baseline, diagnostics, reproducibility, and CI breadth (plan Phases 1–4).**
      A declared **glibc 2.35 baseline** (raised from the initial 2.31/20.04 floor once the bundled
      OpenAL Soft failed to load on Ubuntu 20.04 — newer libstdc++ ABI; musl/Alpine detected and reported unsupported); a headless
      **`--doctor`** self-check (`src/Sprocket.App/Doctor.cs`) reporting libc/distro, loading the bundled
      FFmpeg/OpenAL, and `dlopen`-probing the host libraries the bundled build needs with per-distro
      (apt/dnf/pacman/zypper) install hints — run automatically by `packaging/linux/install.sh`; a
      headless **`--mcp-check`** (`src/Sprocket.App/McpCheck.cs`) that drives a real `initialize` +
      `tools/list` over the packaged loopback MCP endpoint; an **FFmpeg pin** (`scripts/ffmpeg.lock.json`
      via `release.ps1 -UpdateFFmpegLock`) that fails a release on BtbN "latest" drift so the tested
      system-library baseline can't change silently; and a **multi-distro + emulated-arm64 smoke**
      (`scripts/linux-distro-smoke.sh` + the `linux-distro-smoke` job in `.github/workflows/release.yml`)
      running the published zips in clean Ubuntu 22.04 / Debian / Fedora / openSUSE / Arch containers and
      a QEMU `linux-arm64` one — `--mcp-check` also wired into every OS's release smoke. Docs updated
      (`RELEASE_NOTES.md` baseline + dependency table + reframed limitations, `README.md` platform row,
      `FEATURES.md` diagnostics row). The distro matrix is deliberately **informational, not a release
      gate** (Linux is experimental for the alpha); promote a leg into the `release` job's `needs` once
      it is reliably green.
    - **Remaining — real hardware acceleration verification (plan Phase 5; gated on physical / self-hosted
      machines).** CI runners have no GPU, so actual **VAAPI** (Intel/AMD) and **NVENC** (NVIDIA) encode
      *and* decode are still unverified end-to-end on Linux; today only the software fallback and the
      `LibVaPreflight` symbol-sweep (step 6) are exercised. To promote hardware accel from experimental:
      run a real VAAPI export + decode on an Intel/AMD box (libva + Mesa / intel-media-driver) and a real
      NVENC export + decode on an NVIDIA box (matching driver + `libnvidia-encode`), confirming the
      `ExportFormat` probe order (VAAPI → NVENC → software) picks the GPU and that software fallback
      engages cleanly when drivers are absent/unstable. Options: a self-hosted GPU runner wired into
      `release.yml`, or a documented manual per-release checklist (mirroring step 56's Windows 10 VM
      checklist) until one exists. Until then, keep advertising HW accel as opt-in and driver-dependent
      (already stated in `RELEASE_NOTES.md`), with software as the guarantee. Acceptance: a documented
      pass of VAAPI + NVENC hardware encode/decode on real hardware, and the FEATURES.md hardware-accel
      row reflecting verified-on-Linux status.

