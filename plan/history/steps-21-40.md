# Build-order history: steps 21-40 (editorial depth, delivery, packaging, grading, MCP)

> Build-order step details (steps 21-40) moved **verbatim** out of [PLAN.md](../../PLAN.md) in the
> 2026-08-26 restructure. PLAN.md keeps the status ledger + open todos; this archive preserves
> each step's original spec and `✅ DONE` implementation log. Anchors are stable: `#step-N`
> (e.g. `#step-16b`). Sibling files: [steps-01-20.md](steps-01-20.md),
> [steps-21-40.md](steps-21-40.md), [steps-41-57.md](steps-41-57.md),
> [performance-log.md](performance-log.md).
> Relative links inside the moved content are as originally written — relative to the
> **repo root** (e.g. `ARCHITECTURE.md`, `UI.md`, `BRIEF.md` live at the root), not to this folder.

## Step 21

21. **Retime & speed controls.** Per-clip speed as a first-class, non-destructive property — the most
    important missing editorial feature — landing on the existing clip / render-graph / time model:
    - **Model.** A `Clip.SpeedRatio` (and a `Reverse` flag) as a `Rational` / `AnimatableValue` so the
      timeline→source time map is `sourceTime = SourceIn + (t − TimelineStart) × speed` (constant speed)
      or an integrated map when speed is keyframed (**speed ramps**). The clip's timeline duration
      derives from the retimed source span; **freeze frame** = speed 0 over a span (hold one source
      frame); **reverse** = negative mapping. All changes route through the command stack (undoable) and
      serialize additively (§12).
    - **Render graph (§5).** `PlanVideoFrame` / `PlanAudioBuffer` apply the time map when resolving a
      clip's source time, so preview and export stay identical and deterministic, with no per-frame
      managed pixels (§1). Frame interpolation for smooth slow-motion (blend / optical-flow) is a later
      quality tier behind the same seam — ship nearest-source-frame first.
    - **Audio.** Retimed audio resamples in the mixer (pitch-preserving time-stretch is a later DSP
      refinement, step 31); reverse plays the source backward.
    - **UI.** The **Clip ▸ Speed/Duration** menu item (built but disabled at step 16c) + an inspector
      control + a speed-ramp keyframe lane (reusing step-16b/16d keyframing).
    - **✅ DONE — constant-speed retime (`Sprocket.Core/Model/Clip` + `Timing/{Timecode,Rational}` +
      `Commands/ModelCommands` + `Rendering/{RenderPlan,RenderGraph}`; `Sprocket.Audio/AudioMixer`;
      `Sprocket.Persistence`; `Sprocket.App/{SpeedFormat,Dialogs,Inspector/InspectorPanel,Timeline/TimelineControl,
      MainWindow}`; 24 new tests — Core +8, Audio +3, Persistence +2, App +11, all green).** Per-clip speed lands
      on the existing clip / render-graph / time model with no redesign (ARCHITECTURE.md §5/§17). Delivered:
      - **Model (Core, §4).** `Clip.SpeedRatio` is a strictly-positive `Rational` (default 1/1, non-destructive — the
        source bytes and the selected `SourceIn`/`SourceOut` span are untouched). The clip's timeline `Duration`
        derives from it (`(SourceOut − SourceIn) / Speed`, so 2× is half as long, ½× twice as long) and the
        timeline→source map is `MapToSource(t) = SourceIn + (t − TimelineStart) × Speed`. Both go through a new exact
        `Timecode.Scale(Rational)` (Int128 product, rounded), and `Rational.One` was added as the identity. A blade
        split copies the speed onto both halves (`CloneContentForSpan`), so the two halves still sum to the original
        timeline span.
      - **Command (Core, step 10).** `SetClipSpeedCommand` applies/reverts the ratio and coalesces per clip, so a
        Speed dialog / inspector edit is one undo entry. The source span is never touched — only `Duration` and the
        map derive from the new speed.
      - **Render graph (Core, §5).** Video needs no new plumbing — `PlanVideoFrame` already maps each layer's source
        time through `clip.MapToSource`, so a retimed clip walks its source proportionally faster/slower on **preview
        and export** with no per-frame managed pixels (§1). `AudioLayer` gained a `SpeedRatio` (default 1/1) that
        `PlanAudioBuffer` fills from the clip, so the mixer knows the resample factor.
      - **Audio (Sprocket.Audio).** `AudioMixer` resamples a retimed layer's source PCM by the speed factor with a
        **streaming linear resampler**: a per-source carried window holds source frames already pulled but not yet
        consumed, so reading stays sequential across buffers (no per-buffer seek) and the source cursor never drifts;
        a jump still re-seeks and resets the window. The **1× fast path is completely untouched** (read sequentially,
        no resample). Pitch is not preserved — a deliberate first cut (pitch-preserving time-stretch is step 31).
      - **Persistence.** `ClipDto` gains additive, nullable `speedNum`/`speedDen`: a normal-speed (1/1) clip writes
        neither (`WhenWritingNull`), so pre-21 files load at 1× and un-retimed projects serialize byte-identically
        (no schema bump, §12). A retimed clip's speed round-trips and its derived duration comes back right.
      - **UI (App, manual-verified).** **Clip ▸ Speed / Duration…** (enabled when a clip is selected) opens a small
        percentage dialog (100% = normal, with 25/50/100/200/400% presets); the **Inspector** Clip section grew an
        editable **Speed %** row. Both retime the selected clip **and its linked companions together** (so companion
        audio stays in sync) through `TimelineControl.SetSelectedClipSpeed` / the inspector commit, as one undo entry.
        The percentage↔ratio conversions live in a pure, tested `SpeedFormat` helper (mirroring the `TimelineMath`
        split); the Duration row updates on the resulting rebuild.
      - **Tests (24).** Core — duration/map at 1×/2×/½×, positive-speed guard, `Timecode.Scale` rounding,
        `SetClipSpeedCommand` apply/revert/coalesce, split-preserves-speed-on-both-halves; Audio — mixer resamples a
        known source ramp at 2×/½× (exact on the source grid) and **streams across buffers without re-seeking**;
        Persistence — speed round-trip + 1× omits-the-field/loads-as-unity; App — `SpeedFormat` parse/format/round-trip
        + non-positive rejection (the deferred reverse/freeze inputs). Clean build (0 warnings); full suite
        **410 tests green** (Core 139, Media 28, Render 23, Audio 19, Playback 47, Export 10, Persistence 30,
        App 114) — the FFmpeg-native suites (Media/Playback/Export) verified against the bundled FFmpeg-8 shared
        natives, confirming the 1× fast path is behaviour-unchanged and the retimed-audio resample feeds the real
        decode → mixer → export round-trip.
      - **Deferred (noted, on the same seam — additive when picked up):** **reverse** playback (the `Reverse` flag —
        needs backward decode in the feed/export provider, not just a negated map), keyframed **speed ramps** (an
        integrated time map from a keyframed-speed `AnimatableValue`), **freeze frame** (speed 0 — needs an
        independent timeline duration rather than one derived from the source span; **✅ shipped in step 43**,
        which models it as a `HoldFrameAt`/`HoldDuration` field rather than speed 0, leaving the
        `SpeedRatio > 0` invariant intact), and **pitch-preserving**
        time-stretch / frame-interpolated slow-motion (step 31 / a later quality tier behind the same seam).
    - **✅ DONE 2026-08-27 — reverse playback + keyframed speed ramps (the step 21 remainder;
      `Sprocket.Core/Model/{Clip,SpeedRamp}` + `Commands/ModelCommands` + `Rendering/{RenderPlan,RenderGraph}`;
      `Sprocket.Media/{GopFrameWindow,ReverseVideoDecodeRing}`; `Sprocket.Playback/{IVideoFrameFeed,VideoTrackPlayer,
      PlaybackMath,PlaybackEngine}`; `Sprocket.Audio/AudioMixer`; `Sprocket.Export/ExportFrameProvider`;
      `Sprocket.Persistence/{ProjectDto,ProjectSerializer}`; `Sprocket.Mcp/{SprocketTools.Clips,StateFormatter}`;
      `Sprocket.App/{SpeedFormat,Dialogs,MainWindow,MediaBootstrap,Timeline/TimelineControl,Inspector/InspectorPanel}`;
      46 new tests — Core +21, Audio +7, Persistence +3, Playback +4, Export +2, Media +5, App +4; full suite
      2235 green).** Both land on the seams the constant-speed cut left open (ARCHITECTURE.md §5/§17):
      - **Model (Core, §4).** `Clip.Reverse` (a flag beside the always-positive `SpeedRatio`, the "Reverse Speed"
        convention of leading editors rather than a negative ratio) and `Clip.SpeedCurve` — an `AnimatableValue?`
        of speed-as-fraction-of-normal whose keyframe times are **clip-local ticks** (0 = the clip start; a
        deliberate departure from effect keyframes' absolute time, because the ramp determines the clip's own
        duration, so anchoring it to the clip keeps a moved clip's length/content unchanged without a rebase; the
        Inspector lane shifts by the clip start for display). The new `SpeedRamp` static class is the integrated
        time map: `Integrate` (exact trapezoids for Hold/Linear, fixed-step Simpson for eased/Bezier — a pure,
        deterministic function so preview and export agree, §5), `SourceOffsetAt`, and `SolveDuration` (segment
        walk + bisection for the local time at which the integral covers the source span). Speed values are
        clamped to [1%, 100×] so the integral always advances (speed 0 stays the step-43 frame hold).
        `Clip.Duration` derives from `SolveDuration` under a ramp (cached per curve/span), `MapToSource` becomes
        `SourceIn + offset` / `SourceOut − offset` (reverse mirrors from the exclusive out-point, so the last
        frame of the span shows first and every frame provider's "latest frame ≤ target" rule needs no change);
        the map stays unclamped so transition handles still resolve. Precedence: hold ▸ ramp ▸ constant speed.
        `CloneContentForSpan` copies both fields.
      - **Commands (step 10).** `SetClipSpeedCurveCommand` (coalescing — a lane drag is one undo entry) and
        `SetClipReverseCommand`. `SplitClipCommand` is direction-aware — a reversed clip's left half keeps the
        *top* of the span (`SourceIn = split`) and the right half gets `[SourceIn, split)`, so frame continuity
        across the cut holds — and re-anchors a right-hand half's clip-local curve (`Shifted(start − at)`) so the
        keyframes stay put on the timeline.
      - **Render graph (§5).** Video needs nothing new. `AudioLayer` gains `SourceEnd` (the map at the buffer
        *end*) and `Reverse`; `PlanAudioBuffer` fills both (multicam angles offset both ends), so the mixer
        resamples exactly the source span the clip covers per buffer — drift-free under a ramp, and reducing to
        the constant ratio for constant speed.
      - **Audio.** `AudioMixer` derives the per-buffer speed from `SourceEnd − SourceStart` (exact identity keeps
        the untouched 1× fast path, guarded so a ramp crossing 1× with a carried resample window keeps
        resampling). Reverse reads through a `Pull` abstraction: an 8192-frame block below the cursor is pulled
        (one reader seek), flipped, and served sample-by-sample across buffers — through the same streaming
        resampler when retimed; past source time zero reads as silence. Pitch is still not preserved (later tier).
      - **Media.** `GopFrameWindow` — GOP-aware backward access: seeks one window's worth before the target
        (landing on the preceding keyframe), decodes forward to the target, retains the last N (default 32)
        pooled frames in presentation order, and hands them out newest-first; pixels stay native (§1).
        `ReverseVideoDecodeRing` — the descending-order sibling of `VideoDecodeRing` (same generation-tagged
        seek / bounded channel / park-at-end contract): after a seek it emits the window's frames newest-first,
        refills at `JustBefore(FirstPts)` (past the 1 ms match tolerance, so no boundary duplicates) and parks at
        source time zero. Each GOP is decoded once per walk (plus a bounded head re-decode when a GOP exceeds the
        window).
      - **Playback.** `IVideoFrameFeed.IsReverse` (default false) + `ReverseRingVideoFrameFeed`. The feed
        factory is now direction-aware — `Func<MediaRefId, bool, IVideoFrameFeed?>` (new `PlaybackEngine` ctor;
        the old one wraps forward-only) — and `VideoTrackPlayer` rebuilds its feed when the active clip's
        direction changes. The reverse promote rule (`PlaybackMath.ShouldPromoteReverse`) advances to the next
        (earlier) frame while the *shown* frame's PTS is above the target, landing on the latest frame ≤ target
        from above. A reversed clip on a forward-only fixed feed (tests, the render cache) falls back to
        re-seeking ¼ s before each new target and promoting forward — correct output at a GOP-decode cost.
      - **Export.** `ExportFrameProvider` enters a reverse mode when requests run backwards (detected against the
        last request too, since the exclusive out-point leaves no current frame), serving from a `GopFrameWindow`
        and refilling below it; a request at/past the end falls back to the window's latest frame. Verified by a
        frame-order golden test (exported frame k ≈ source frame N−1−k by pixel MAE) and a ramp-duration test.
      - **Persistence (§12).** `ClipDto.Reverse` / `SpeedCurve` — additive + nullable; forward constant-speed
        clips write neither (byte-identical to earlier files). Curve keyframes serialize through the existing
        `AnimatableValueDto`.
      - **UI (App, manual-verified).** Speed / Duration dialog gains a **Reverse speed** checkbox (result is
        speed + direction; a constant speed from the dialog replaces any ramp, the leading-editor convention);
        clip context menu gains **Reverse Speed / Play Forward**; the Inspector's Speed row is now the shared
        keyframeable slider row (◇ toggles keyframing at the playhead, the step-16b/16d lane + velocity graph
        edit the ramp; percentage display via `DisplayScale`) plus a **Reverse** checkbox — all retime linked
        companions together as one undo entry. Clip bodies show a retime pill (`50%`, `RAMP`, `◀` prefix when
        reversed) beside HOLD. The Select tool's plain trim is now speed-, direction- and ramp-aware (a reversed
        clip's end edge moves its source *in*-point; a ramp's start trim re-anchors the curve and consumes the
        integrated head), fixing the pre-existing approximation where a plain trim moved the source edge by the
        timeline delta regardless of speed. Ripple / roll / slide abort on reversed or ramped clips (like held
        ones) — their constant-speed source-edge math doesn't model those maps; the plain trim covers them.
      - **MCP.** `set_clip_speed` gains an optional `reverse` argument; clip state reports `reverse` /
        `speed_ramp`.
      - **Deferred (later quality tiers, same seams):** pitch-preserving time-stretch (audio-effects DSP tier),
        frame-interpolated slow motion (blend / optical flow behind the render-graph seam — nearest-source-frame
        remains the behaviour), and ripple/roll/slide gestures on reversed / ramped clips.
## Step 22

22. **Ripple / roll / slide editing.** Trim modes that preserve timeline continuity — basic editor
    ergonomics — extending the step-12/13 timeline tools (Select / Blade / Slip already exist). Each is a
    new pure timeline operation issued as a command (or `CompositeCommand`) so it stays undoable:
    - **Ripple trim / delete** — trimming a clip's edge (or deleting a clip) shifts all downstream clips
      on the track (optionally all tracks — "ripple all") to close / open the gap, keeping the sequence
      contiguous.
    - **Roll edit** — adjust the cut point between two adjacent clips, moving the shared edge so one
      clip's out and the next clip's in change together while their combined duration (and everything
      downstream) stays fixed.
    - **Slide** — move a clip along the timeline while its neighbours absorb the change (the complement
      of the existing slip).
    The geometry / clamping lives in the pure `TimelineMath` (the step-12 split), headless-tested; the
    tool palette gains ripple / roll affordances ([UI.md §3.2](UI.md)). Linked A/V (step 13) participates
    so a ripple moves companion audio too.
    - **✅ DONE (`Sprocket.Core/Commands/ModelCommands.cs` + `Sprocket.App/Timeline/{TimelineMath,TimelineControl}`
      + `MainWindow.axaml`/`.cs`; 15 new tests — Core +8, App +7, all green).** All three trim modes (plus ripple
      delete) land on the existing clip / command / time model with no redesign (ARCHITECTURE.md §17). Each is a
      pure, undoable timeline operation; the tool palette now carries the full professional-NLE trim toolset
      (**Select · Blade · Ripple · Roll · Slip · Slide · Hand · Zoom**, [UI.md §3.2](UI.md)). Delivered:
      - **Three Core commands (step 10).** `RippleTrimCommand` — trims one edge (the clip's `TimelineStart` stays
        fixed for *both* edges) and shifts a captured downstream set by the duration change; re-derives each
        downstream start from its captured original + the latest shift so a coalesced drag stays exact.
        `RollEditCommand` — moves the shared cut between two adjacent clips (left out + right in/start together),
        keeping their combined span and everything downstream fixed. `SlideClipCommand` — moves a clip while its
        (optional) prev/next neighbours absorb it; the slid clip's source window is untouched. All three coalesce
        per gesture (one undo entry) and revert exactly. **Ripple delete** (Shift+Delete, the leading-editor
        convention; Edit ▸ Ripple Delete) composes `RemoveClipCommand` + downstream `SetClipPlacementCommand`s into
        one `CompositeCommand`.
      - **Pure clamping (App `TimelineMath`, mirroring the step-12 split).** `ClampRollDelta` / `ClampSlideDelta`
        (shared shape: the growing side limited by its remaining media, the shrinking side floored at the minimum
        clip duration) and `RippleTrimBounds` (the per-edge ripple travel) — all in timeline ticks, headless-tested;
        the control converts each clip's source/media headroom to timeline ticks (÷ its retime speed, step 21)
        before calling them, so retimed clips clamp correctly.
      - **Tool palette + gestures (App `TimelineControl`).** `EditTool` gained `Ripple` / `Roll` / `Slide`; a
        `DragKind` now routes each clip-drag (the Select-tool body drag still previews-then-commits for cross-track
        moves, step 16e; Trim/Slip/Ripple/Roll/Slide mutate live inside a coalescing scope). Ripple/Roll act on an
        edge (a body click just selects); Roll resolves the two clips sharing the dragged cut and aborts when there
        is no adjacent clip; Slide captures the butted neighbours. Snapping snaps the moving edge/cut/clip to
        nearby edits & the playhead. **Linked A/V participates:** a ripple trims every companion's matching edge and
        ripples each companion's own track (one `CompositeCommand`); a ripple delete removes the companions and
        ripples their tracks too. Each tool sets a matching cursor.
      - **Menu / accelerators (App `MainWindow`).** Three new tool radio buttons (wired to `ActiveTool`), the
        **Edit ▸ Ripple Delete** item (Shift+Delete, context-enabled with the selection), and the Shift+Delete
        accelerator (guarded so it doesn't steal a focused text field's input).
      - **Tests (15) + verification.** Core — `RippleTrimCommand` out-extend/in-trim + downstream shift + undo +
        drag-coalesces-to-one-entry; `RollEditCommand` cut-move keeps the combined span + undo + coalesce;
        `SlideClipCommand` neighbours-absorb + source-window-untouched + no-prev-neighbour + undo + coalesce. App —
        `ClampRollDelta` (within-bounds / left-media / right-min / left-roll-headroom), `ClampSlideDelta` (mirror),
        `RippleTrimBounds` (both edges). The control's pointer/tool wiring rests on these + manual verification (the
        App is a UI-bound `WinExe`): clean build (0 warnings) and a `SPROCKET_APP_SECONDS=5` smoke launch starts the
        shell with the full trim toolset + Ripple Delete wired and tears down cleanly (exit 0). The managed suites
        are green — **Core 148** (incl. the 8 new), **App 129** (incl. the 7 new), Audio 19, Render 23,
        Persistence 23; the FFmpeg-native suites (Media/Playback/Export) were not run in this sandbox (a test-host
        DLL-search limitation blocks loading the bundled FFmpeg-8 natives — the App itself launches fine with them),
        and this change touches no Media/Playback/Export source, so those paths are behaviour-unchanged.
      - **Deferred (noted, on the same seam):** a **"ripple all tracks"** mode (today ripple closes the gap on the
        edited clip's own track + linked companions' tracks; a global ripple-all toggle slots onto the same
        downstream-shift composite), and **linked roll / slide** (companions follow on ripple/delete today; roll &
        slide operate on the clicked track's clips — applying the identical clamped delta to aligned companions is
        additive when picked up).
## Step 23

23. **Sequences (nesting / compound clips).** Generalise the project's single `Timeline` to
    **multiple named sequences**, and let a whole sequence be **placed inside another sequence as a
    clip** (what leading editors call a "nested sequence" or "compound clip"). To the render graph a
    nested-sequence clip is just another `IFrameSource` / `IPcmReader` that renders the child sequence's
    timeline at the requested time — the graph already turns a (timeline, t) into a frame
    ([ARCHITECTURE §5](ARCHITECTURE.md), [§17](ARCHITECTURE.md)) — so **edit operations apply to the
    whole nested sequence as one unit** (trim, effects, opacity/blend, audio gain/fade). Reuse is
    first-class: the **same sequence can be referenced by many sequences**, and (already true) the
    **same source clip can appear in more than one sequence** — these are references, not copies, so
    editing a child updates everywhere it is used. Model: `Project` gains `Sequences : Sequence[]`
    (today's `Timeline` becomes the active sequence) and a `Clip` may reference a `SequenceId` as its
    source alongside `MediaRefId`; render-graph recursion needs **cycle detection** (a sequence can't
    contain itself, directly or transitively) and a depth guard. The **Sequence** menu and the sequence
    badge / settings (placeholders from step 11, [UI.md §2](UI.md)) drive create / nest / open /
    settings. Sequences serialize as part of the project JSON (additive, schema-versioned, §12).
    Depends only on the done model + render graph — grouped here with the other non-raw-media building
    blocks (generators, adjustment layers), and foundational for the compound editorial workflows below
    (multicam, render cache). Heavy nests can be **pre-rendered** so they don't recompute each playback
    pass (step 32, [ARCHITECTURE §20](ARCHITECTURE.md)).
    - **✅ DONE (Core `Model/{Sequence,Project,Clip,SequenceGraph,SequenceNesting}` + `Rendering/{RenderPlan,RenderGraph}`
      + `Commands/ModelCommands`; `Sprocket.Audio/AudioMixer`; `Sprocket.Export/VideoExporter`;
      `Sprocket.Persistence/{ProjectDto,ProjectSerializer}`; `Sprocket.Playback/PlaybackEngine`; App
      `MainWindow.axaml`/`.cs` + `Timeline/TimelineControl` + `{Monitors,PreviewSurface,Dialogs,SequenceNaming}`;
      31 new headless tests — Core +21, Persistence +4, App +4, Audio +2 — + 1 sandbox-blocked Export test, all
      green.)** Multiple named sequences + nested/compound clips land entirely on the existing seams (no redesign,
      ARCHITECTURE.md §17): a nested-sequence clip is just a `Clip` whose source is a `SequenceId`, and the render
      graph's existing (project, t) → frame/buffer recursion renders the child. Delivered:
      - **Model (Core).** `Sequence` (id + name + the existing `Timeline` as its content) and a `SequenceId` value
        type; `Project` now holds `Sequences` with an `ActiveSequence`, and `Project.Timeline` **delegates to the
        active sequence** so the whole render/playback/export/App stack addresses it unchanged — multiple sequences
        are purely additive. `Clip` gains `ClipKind.Sequence` + `SourceSequenceId` and a `CreateSequenceClip`
        factory. `SequenceGraph` is the pure cycle/reachability reasoning (`WouldCreateCycle`, `MaxNestingDepth = 16`);
        `SequenceNesting.CreateNest` builds the "Nest" / "compound clip" edit familiar from leading editors (selection → new child
        sequence, one linked V+A nested clip replaces it in the parent) as a single undoable `CompositeCommand`.
        `AddSequenceCommand` / `RemoveSequenceCommand` (step 10); switching the *active* sequence is navigation, not
        a command (so undo never strips it — the App self-heals if a sequence-add is undone).
      - **Render graph (Core).** `PlanVideoFrame` / `PlanAudioBuffer` recurse through nested-sequence layers
        (`LayerKind.Sequence` / `VideoLayer.NestedPlan`, `AudioLayer.NestedPlan`), carrying a **visited-set on the
        recursion path for cycle detection** and a **depth guard**; the nested plan inherits the parent layer's
        effects / opacity / blend (video) and gain envelope (audio), so a nest edits as one unit. Master gain is
        applied once at the root. The generic `Render<TImage>` executor renders a `Sequence` layer by recursing on
        its nested plan — the **same code drives preview and export** (determinism preserved).
      - **Audio (mixer).** `AudioMixer` mixes a nested layer's child sub-mix into per-depth scratch buffers, applies
        the nesting clip's gain/fade over the whole unit, then hard-limits once at the top — no per-frame managed
        allocation (§1, §6). (Deferred: a **retimed** nested-sequence clip's audio plays at 1×.)
      - **Persistence (additive, §12).** `Sequences` + `ActiveSequenceId` + `Clip.SourceSequenceId` serialize only
        when used: a single-sequence project with no nesting writes the **byte-identical pre-step-23 Timeline-only
        shape** (no schema bump); nested ids round-trip and resolve by preserved id (dangling refs render as nothing,
        §15).
      - **App (UI, manual/smoke-verified).** The **Sequence menu** is live — New Sequence (creates + opens a fresh
        sequence in the active format), **Nest** (context-enabled with a selection; routes the selection + linked
        companions through `SequenceNesting`), **Open Sequence ▸** (a submenu of every sequence, active checked,
        click switches), and **Sequence Settings…** (read-only format + undoable rename). `SwitchToSequence` re-points
        the model + Program monitor resolution + preview and rewinds so the engine's pump reconciles its players onto
        the new sequence's tracks; the **sequence badge** now shows the active sequence's name + format. The timeline
        labels nested clips with the child sequence's name and tints them a distinct teal. **Nested-sequence preview**
        draws a placeholder fill (live nested compositing in the Program monitor is deferred to the render cache,
        step 32 — the child renders fully on **export** and when **opened**; both are tested/exercised).
      - **Tests + verification.** Core `SequenceTests` (model, render-graph recursion + time mapping + effects/opacity,
        missing-ref, **direct cycle**, **deep-chain depth guard**, nested audio, executor over a fake compositor),
        Audio `NestedAudioMixerTests` (nested audio reaches the mix; nesting-track gain applies to the whole sub-mix),
        Persistence `SequencePersistenceTests` (multi-sequence + nested round-trip, active-selection round-trip,
        single-sequence omits the array, nested writes the sequences shape), App `SequenceNamingTests` (unique /
        gap-filling / case-insensitive naming). Managed suites green — **Core 169, Audio 21, Render 23,
        Persistence 34, App 133**. The FFmpeg-native suites (Media/Playback/Export) were not run in this sandbox (a
        test-host DLL-search limitation blocks loading the bundled FFmpeg-8 natives — the App itself launches fine
        with them); the Export nested-composite test is written and correct but rests on CI. Clean build (0 warnings)
        and a `SPROCKET_APP_SECONDS=5` smoke launch starts the shell with the Sequence menu wired and tears down
        cleanly (exit 0). *Also fixed in passing:* a pre-existing stray-paren syntax error in
        `Sprocket.App/MediaBootstrap.cs` (the App had not been compiled since it was introduced).
      - **Deferred (noted, on the same seam):** **live nested-sequence compositing in the Program monitor** (the
        render cache, step 32 — preview shows a placeholder today; export + open-the-child render fully); and a
        **retimed** nested clip's audio at non-1× speed. *(Sequence-format editing, originally deferred here, has
        since shipped: Sequence Settings edits the frame size undoably — presets incl. portrait/square + Custom —
        New Sequence opens the same format picker, and per-clip **conform** (Fit/Fill) plus the Inspector Framing
        section handle mismatched-resolution media; frame rate / sample rate remain read-only.)*
## Step 24

24. **Multicam editing & clip sync.** Synced multi-angle editing — a major omission for interview,
    live-event, documentary, and studio / YouTube workflows — placed immediately after sequences because
    synced source groups and nested editorial structure (step 23) now exist to build on:
    - **Clip sync.** Align a set of source clips by **timecode, in/out markers, or audio-waveform
      cross-correlation** into a synced group (the audio-analysis path reuses the step-15 waveform / PCM
      reading). Sync offsets are model data, undoable.
    - **Multicam source.** A **multicam clip** = a synced angle group exposed to the render graph as a
      single `IFrameSource` / `IPcmReader` (the same seam nested sequences and proxies use, §17) whose
      active angle is selectable over time — built naturally on the step-23 nested-sequence machinery (a
      multicam source is a specialized synced sequence).
    - **Angle editing.** A multicam monitor view (an angle grid in the Program / Source monitor, step 17)
      with **live angle cutting** — switching the active angle at the playhead lays down cuts via the
      command stack; angle switches and per-cut effect / audio overrides are model edits. Export resolves
      the chosen angles through the same render graph (deterministic).
    - **✅ DONE (Core `Model/{Multicam,ClipSync,AudioSync,MulticamBuilder,Clip,Project}` +
      `Rendering/RenderGraph` + `Commands/ModelCommands`; `Sprocket.Persistence/{ProjectDto,ProjectSerializer}`;
      App `Timeline/TimelineControl` + `Inspector/InspectorPanel` + `MainWindow.axaml`/`.cs`; 31 new headless tests
      — Core +28, Persistence +3, all green.)** Synced multi-angle editing lands entirely on the existing seams
      (no redesign, ARCHITECTURE.md §17): a multicam source is a synced angle group, and its active angle resolves
      to an **ordinary media frame at the synced source time**, so multicam rides the media seam the render graph,
      mixer, preview, and export already drive — no recursion, no new compositor seam. Delivered:
      - **Model (Core).** `MulticamSource` (id + name + an ordered `MulticamAngle` list) and a `MulticamId` value
        type; `Project.MulticamSources` (+ `GetMulticam`). Each `MulticamAngle` carries its video `MediaRefId`, an
        optional separate `AudioMediaRefId` (dual-system sound; `EffectiveAudioRefId` falls back to the video file),
        and a `SyncOffset` — the per-angle alignment, so at multicam time `s` the angle's source frame is at
        `s + SyncOffset`. `Clip` gains `ClipKind.Multicam` + `SourceMulticamId` + a mutable `ActiveAngle` and a
        `CreateMulticamClip` factory; a blade split copies both onto each half (`CloneContentForSpan`), so the angle
        program is just the run of multicam segments on the track.
      - **Clip sync (Core, pure + tested).** `ClipSync.ComputeOffsets` reduces all three methods to one number per
        angle (the source time of a shared instant), relative to a reference angle — markers feed the marked source
        time, timecode feeds the source-time-at-a-common-TC, audio feeds the cross-correlation lag.
        `AudioSync.FindBestLag`/`FindBestOffset` is the **audio-waveform cross-correlation** (energy-normalized, with
        a min-overlap floor and a confidence in [-1,1]); it recovers a known delay (sign-correct), handles negative
        lags, and converts a sample lag to a `Timecode` offset. `ClipSync.AngleSourceTime` is the synced sampling
        time the render graph uses.
      - **Multicam source / render graph (Core, §5).** `PlanVideoFrame`/`PlanAudioBuffer` resolve a multicam clip by
        looking up its active angle and emitting a plain **media video layer** / **media audio layer** at
        `ClipSync.AngleSourceTime` (the angle's `MediaRefId` / `EffectiveAudioRefId`); a missing source or a stale
        angle index contributes nothing (renders as empty, §15). Because it's a media layer, **preview, the audio
        mixer, and export work unchanged** — switching `ActiveAngle` switches the resolved source, and export
        resolves the chosen angles deterministically through the same graph (`MediaBootstrap`'s per-source feed /
        PCM-reader factories already open any `MediaRefId`, so no Playback/Media/Export source changed).
      - **Angle editing + commands (Core + App).** `SetClipAngleCommand` (a discrete angle switch),
        `Add`/`RemoveMulticamSourceCommand`, and `SetMulticamOffsetsCommand` (a re-sync of every angle, undoable) join
        the step-10 set. `MulticamBuilder.CreateMulticam` (mirroring `SequenceNesting`) turns a set of angle clips
        into a synced source and replaces them with a single **linked video + audio multicam clip** as one undoable
        `CompositeCommand` (angles synced by the clips' existing placement by default). In the App, **Clip ▸ Create
        Multicam Source** collapses the stacked video angles, the **number keys 1–9** do **live angle cutting** (blade
        the clip — and its linked audio companion — at the playhead and set the new segment's angle, one undo entry),
        and the **Inspector** grows a Multicam section (one button per angle, the active one highlighted, showing each
        angle's sync offset) that sets the segment's angle. The timeline draws multicam clips in a distinct violet and
        labels them `{source} · {active angle}`.
      - **Persistence (additive, §12).** `MulticamSourceDto`/`MulticamAngleDto` + `Clip` DTO's `sourceMulticamId` /
        `activeAngle` serialize only when used (orthogonal to the sequence shape; `WhenWritingNull`), so a
        multicam-free project serializes **byte-identically** to a pre-step-24 file (no schema bump) and pre-24 files
        load unchanged; the source (angles, names, offsets, separate audio) and the clip's active angle round-trip.
      - **Tests + verification.** Core `MulticamTests` (model/factory, render resolution of the active angle to a
        synced media/audio layer, angle switching, out-of-range/missing → nothing, blade keeps the angle, the sync
        offset math, audio cross-correlation incl. negative/identical/empty, all four commands, and the builder's
        create/undo/render/`<2`-angle-null), Persistence `MulticamPersistenceTests` (source+clip round-trip,
        multicam-free omits the field, multicam writes the shape). **Full suite green — 498 tests, 0 failures**
        (Core 197, Media 28, Render 23, Audio 21, Playback 48, Export 11, Persistence 37, App 133); clean build
        (0 warnings) and a `SPROCKET_APP_SECONDS=6` smoke launch starts the shell with the multicam menu/keys/Inspector
        wired and tears down cleanly (exit 0).
      - **Fixed the FFmpeg-native test suites (they now actually run).** Steps 20–23 each recorded that the
        Media/Playback/Export suites "couldn't run in the sandbox — a test-host native-loading limitation." That was a
        misdiagnosis: the real bug was that `tests/Directory.Build.targets` copied **every** RID's cache extract into
        one output dir (Windows `.dll` *and* Linux `.so` *and* macOS `.dylib`), and `FFmpegLoader.FindBundledLib`
        matched the Linux soname **first, unconditionally**, so on Windows it picked `libavcodec.so.62` and
        `NativeLibrary.TryLoad` failed with `BadImageFormatException` — the whole FFmpeg load aborted. (A shipped build
        bundles only one OS's libs, so the bug stayed latent.) Two fixes: `FindBundledLib` now considers **only the
        current OS's** library type, and the test-natives copy is gated per-OS so the output dir stays single-platform.
        With that, all three FFmpeg suites pass locally with no `%SPROCKET_FFMPEG8_DIR%`, and the multicam render path
        is now exercised end-to-end through the real decode→render→export round-trips, not just headlessly.
      - **Deferred (noted, on the same seam):** the **live multi-angle grid monitor** (decode-bound — it needs every
        angle decoded at once into thumbnails, the same heavy-decode work the nested-sequence preview deferred to the
        render cache, step 32; the active angle previews live today); an **App "Sync by Audio" action** that reads each
        angle's PCM via `AudioSource` and applies `SetMulticamOffsetsCommand` (the cross-correlation engine + the
        re-sync command are delivered and tested — this is the decode-bound App glue, like `ThumbnailService`);
        **sync by embedded source timecode** (the offset math is ready; reading a source's start TC from FFmpeg is the
        missing input); and an **independent audio-follows-angle vs audio-follows-video** choice (audio follows the
        same active angle today).
## Step 25

25. **Transitions.** Transition library (Project panel **Transitions** tab) + overlapping-clip
    resolution in the render graph ([ARCHITECTURE §17](ARCHITECTURE.md)).
    - **✅ DONE (Core `Model/{Transition,TransitionCatalog,Track}` + `Rendering/{RenderGraph,RenderPlan,Seams}` +
      `Commands/ModelCommands`; `Sprocket.Render/SkiaEffectPipeline`; `Sprocket.Export/VideoExporter`; persistence;
      `Sprocket.App/{DragFormats,MediaBrowser/MediaBrowserPanel,Timeline/TimelineControl,MainWindow}`; 26 new tests —
      Core +15, Render +7, Export +1, Persistence +3). Transitions land on the existing render-graph seam exactly as
      ARCHITECTURE §17 anticipates ("transitions extend clip resolution in the render graph"), following the
      convention used by leading editors. Delivered:
      - **Model (Core, §4).** A `Transition` is a non-destructive overlay on a `Track` (`Track.Transitions`) anchored
        at a cut: `{ TransitionTypeId, CutPoint, Duration, Alignment, Parameters }`, with the window derived from the
        alignment (`CenterOnCut` default / `EndAtCut` / `StartAtCut`) and a `ProgressAt(t)` ramp 0→1. It does **not**
        move or overlap the clips' timeline spans — the two clips stay adjacent and the transition samples their
        **handles** (trimmed-off source past the cut) the way every NLE does. `Track.ResolveTransitionAt(t)` and
        `ResolveTransitionClips(transition)` (the outgoing clip just before the cut, the incoming clip at it) drive
        resolution. A `TransitionCatalog` mirrors `EffectCatalog`: the v1 library is **Cross Dissolve** (default),
        **Dip to Black**, **Dip to White**, and a left-to-right **Wipe**.
      - **Render graph (Core, §5).** `PlanVideoFrame` emits a new `LayerKind.Transition` layer carrying a
        `ResolvedTransition` (type id, progress, and both sides as fully-resolved `VideoLayer`s with their own clip
        effects) when a valid transition is active; an invalid one (no real cut / a side that resolves to nothing)
        falls back to ordinary single-clip resolution. The generic `Render<TImage>` executor and the
        `IVideoCompositor<T>.ApplyTransition` seam handle it, so the resolution stays pure/serializable and
        headlessly testable (the same path preview and export share). Per-clip layer resolution was factored into one
        `ResolveClipLayer` used by both the normal path and each transition side.
      - **Shaders (Render, §7).** `SkiaEffectPipeline` adds four two-input SkSL programs — cross dissolve
        (premultiplied `mix`), dip to black, dip to white, and a soft-edged wipe — and `DrawTransition`, which folds
        each side through its own effect chain (refactored into a shared `BuildChainShader`) then combines them at the
        transition's progress; an unknown (plugin) id degrades to a cross dissolve. All premultiplied-correct and
        compositing with the track's opacity/blend.
      - **Export (deterministic).** `VideoExporter` composites a transition layer by snapshotting each side's content
        into an independent image (so a transition between two clips of the **same** source doesn't recycle the first
        frame) and blending via `DrawTransition`; a missing side composites the other alone (§15).
      - **Persistence (additive, no schema bump).** `TrackDto.Transitions` is nullable/`WhenWritingNull`, so a
        transition-free project serializes byte-identically to a pre-25 file and pre-25 files load with none.
      - **App UI.** The Project panel's **Transitions** tab lists the library (drag a row onto a cut, or double-click
        to apply it to the selected clip's cut — both through the step-10 command stack as an undoable
        `AddTransitionCommand`, the duration snapped to whole frames and clamped inside both clips). The timeline draws
        each transition as the classic translucent bow-tie "X" box over the cut; clicking selects it and **Delete**
        removes it (`RemoveTransitionCommand`), reusing the existing Edit/Delete wiring. A `SetTransitionWindowCommand`
        (coalescing) is in place for adjusting a transition's length.
      - **Tests (26).** Core: window/progress math for all three alignments, render-graph resolution (blend layer with
        correct From/To source times incl. handle sampling, track opacity/blend carried, fall-back outside the window /
        with no second clip), the executor blend, and the three commands (apply/revert/coalesce). Render: the real
        SkSL on an offscreen surface — cross dissolve at 0/0.5/1, dip to black/white at the midpoint, the wipe's
        left/right split, and unknown-id → cross dissolve. Export: a real encode→decode round-trip of a black→white
        cross dissolve, mid-grey at the cut where a plain cut would be white. Persistence: field-for-field round-trip +
        byte-identical omission. Full suite: **526 tests green** (Core 209, Media 28, Render 30, Audio 21, Playback 52,
        Export 12, Persistence 40, App 134). Clean build (0 warnings) + smoke launch (exit 0).
      - **Deferred (documented).** **Live preview of the transition blend** is deferred to the render cache (step 32),
        consistent with the nested-sequence preview deferral — the per-track single-feed preview engine can't decode
        two clips of one track at once; the preview shows the cut and the on-timeline overlay, while **export renders
        the blend fully**. **Audio crossfades** reuse the same `Transition` model + the mixer's gain ramp but are a
        follow-up (video transitions ship first, like the slice's other compositing features); real NLEs separate
        audio and video transitions anyway. Wipe direction/softness and per-transition parameters ride the existing
        `Parameters`/Inspector mechanism when needed.
## Step 26

26. **Alpha-channel media compositing.** Premultiplied-alpha path through the render graph (e.g.
    `Logo_Anim.mov` flagged `Alpha`).
    - **✅ DONE (`Sprocket.Core/Model/MediaRef` + `Sprocket.Media/{Native/LibAv,Native/AvStructs,VideoFrame,MediaSource}`
      + `Sprocket.Render/SkiaEffectPipeline` + `Sprocket.Playback/PlaybackEngine` + `Sprocket.Export/VideoExporter` +
      `Sprocket.App/{PreviewSurface,MediaBrowser/MediaBadges}` + persistence; 6 new tests — Render +3, Media +1, App +2).
      Alpha media now composites over the layers beneath it in both preview and export, following the convention
      used by leading editors (ProRes 4444 / QuickTime Animation logos). **Key finding:** the decode path already carried alpha —
      swscale normalises every source into the pooled `AV_PIX_FMT_RGBA` buffer preserving the alpha channel (§11) — but
      the Skia compositor wrapped every frame as `SKAlphaType.Opaque`, discarding it. This lands entirely on existing
      seams (§17); no render-graph redesign. Delivered:
      - **Alpha detection (Media).** `MediaSource.Probe` reads the stream's `codecpar->format` and tests the
        `AV_PIX_FMT_FLAG_ALPHA` flag via a new `av_pix_fmt_desc_get` binding (`Native/LibAv` + a minimal
        `AvPixFmtDescriptor` view reading only `flags`), setting a new `ProbedMediaInfo.HasAlpha` at import without
        decoding a frame. Every decoded `VideoFrame` carries the flag (`VideoFrame.HasAlpha`).
      - **Premultiplied compositing (Render).** `SkiaEffectPipeline.DrawLayer`/`Present` take a `hasAlpha` flag:
        alpha frames wrap as `SKAlphaType.Unpremul` (FFmpeg RGBA is straight alpha) so Skia premultiplies and composites
        them source-over the lower layers, revealing them through transparent pixels; opaque frames stay
        `SKAlphaType.Opaque` — the alpha bytes ignored, the layer fully replacing what's beneath — so the measured
        allocation-clean opaque hot path (steps 1/4/7) is byte-for-byte unchanged.
      - **Threaded through preview + export.** `PresentedVideoLayer`/`PresentedFrame` carry `HasAlpha` (populated from
        the frame); `PreviewSurface` and `VideoExporter` (media layers **and** transition sides) pass it to `DrawLayer`,
        so the same premultiplied path serves the real-time preview and the deterministic export (§5).
      - **Media-bin badge + persistence.** The media browser shows an **`Alpha`** badge on alpha video
        (`Logo_Anim.mov · 00:05 · Alpha`, UI.md §3.3, `MediaBadges`). `ProbedInfoDto.HasAlpha` is additive/nullable
        (`WhenWritingNull`): opaque media omits it and serializes byte-identically to a pre-26 file; pre-26 files load
        as opaque; only alpha media writes `true`.
      - **Tests (6, deterministic).** Render: a straight-alpha layer over a coloured background — transparent reveals the
        background, 50%-alpha blends (~premultiplied source-over), and the same bytes with `hasAlpha:false` fully replace
        (proving the opaque path is unchanged). Media (real FFmpeg): a `qtrle`/`argb` fixture reports `HasAlpha` on the
        info **and** every frame, and its 50% alpha survives swscale into the RGBA buffer (opaque `yuv420p` fixture stays
        false). App: the `Alpha` badge appears for alpha video and not for opaque. Persistence: `HasAlpha` round-trips.
        Full suite: **532 tests green** (Core 209, Media 29, Render 33, Audio 21, Playback 52, Export 12, Persistence 40,
        App 136). Clean build (0 warnings).
      - **Deferred (documented).** Alpha carried only as **side data** (VP8/VP9 alpha, where `codecpar->format` reads
        `yuv420p` but the decoded frame is `yuva420p`) isn't flagged yet — the probe reads the container-level pixel
        format, which covers the ProRes 4444 / qtrle / PNG cases this step targets; a decode-time re-check is the
        follow-up. Alpha **poster thumbnails** still render opaque (a representative frame, not a composite).
## Step 27

27. **Broad media format support (import coverage + export format/codec matrix).** Open and write the
    **common containers and codecs**, not just the slice's H.264/AAC MP4. *Import* is mainly a
    coverage/robustness task — `MediaSource`/`AudioSource` decode through the hand-rolled FFmpeg 8
    binding (steps 2–3), which already handles most formats — so this verifies and hardens a **support
    matrix**: containers
    **MP4 / MOV / MKV / WebM / AVI / MXF / TS**; video **H.264, HEVC, AV1, VP9, MPEG-2, ProRes,
    DNxHD/HR**; audio **AAC, MP3, PCM/WAV, FLAC, AC-3, Opus**; plus **10–12-bit, 4:2:2 / 4:4:4, HDR
    transfer, alpha, and variable-frame-rate (VFR)** sources — with file-dialog extension filters and
    graceful unsupported/offline handling (§15). *Export* generalises the step-8 `MediaEncoder` from its
    hard-wired H.264/AAC into a **container × video-codec × audio-codec matrix** with quality/bitrate,
    pixel-format/bit-depth, and frame-rate controls; **hardware encoders** (NVENC / QSV / AMF /
    VideoToolbox) behind the existing `IHardwareContext` with a software (x264 / x265 / SVT-AV1) fallback.
    Export still renders through the **same render graph** at full resolution — only the muxer/encoder
    back end changes (§5/§17). **Export resolution is capped at 4K for now** (≤ 3840×2160 UHD /
    4096×2160 DCI; higher tiers — 5K/6K/8K — may be enabled later); this is an **export-side limit
    only** — import, the timeline, and sequence canvas sizes are unrestricted. This matrix is for
    **import and final delivery**; *preview/cache* intermediates instead pick fast, OS-specific codecs
    (step 32). **Licensing:** codec choice interacts
    with the FFmpeg build's LGPL/GPL split (x264/x265 → GPL) — decide the bundled build before
    distribution ([ARCHITECTURE §11](ARCHITECTURE.md)).
    - **✅ DONE (`Sprocket.Media` encoder/probe + `Sprocket.Export` format matrix + `Sprocket.App` dialog; 16 new
      tests — Export +14, Media +1, Persistence assertions; full suite **547 green**).** Export generalised from
      a hard-wired H.264/AAC MP4 into a container × video-codec × audio-codec matrix, and import hardened to probe
      and surface the source's real format — all behind Core's unchanged seams (§17), only the muxer/encoder back
      end changes so export stays deterministic (§5). Delivered:
      - **`MediaEncoder` codec matrix.** `Create(path, video, audio, containerFormat)` now takes the FFmpeg muxer
        name (mp4/mov/matroska/webm/avi/mpegts) and picks encoders **by name** (`avcodec_find_encoder_by_name`) so
        the matrix is robust across FFmpeg builds without baking codec-id tables. The pixel/sample format is
        **negotiated against the chosen encoder** (`avcodec_get_supported_config`, the FFmpeg-8 replacement for the
        removed `AVCodec.pix_fmts`/`sample_fmts`): video picks the requested format if supported else yuv420p else
        the encoder's first; audio prefers `fltp` (the mixer-friendly deinterleave) else the encoder's first, and
        `WriteAudioFrame` feeds **planar or packed** planes accordingly (so PCM/FLAC packed s16, Opus packed flt,
        and AAC/AC-3/MP3 planar fltp all encode). Quality: CRF for the crf-capable encoders (x264/x265/SVT-AV1/VP9),
        a resolution-scaled default bit rate otherwise; the chroma-aware even-dimension rule replaced the old
        always-even guard (4:4:4 accepts odd sizes). Hardware encode (NVENC/QSV/AMF/VideoToolbox) slots in as
        another encoder name behind this same shape — the software encoders stay the deterministic default;
        full `hw_frames_ctx` GPU-frame upload is the follow-up (catalogued in `Native/FUTURE_BINDINGS.md`).
      - **New curated bindings** (no new struct-offset regen): `av_get_pix_fmt`/`av_get_sample_fmt`/
        `av_sample_fmt_is_planar`/`av_get_pix_fmt_name`, `avcodec_get_supported_config`, `avcodec_get_name`; plus
        `AvPixFmtDescriptor` chroma-log2 + comp0 depth, `AvCodecParameters.color_trc`, and an
        `AllocOutput(path, formatName)` container override.
      - **`Sprocket.Export` format model.** `ExportFormat` (container/video/audio) + `ExportContainer`/
        `ExportVideoCodec`/`ExportAudioCodec` enums with a single-source-of-truth `ExportCodecs` registry
        (encoder names, pixel formats, presets, and the container→codec validity matrix), curated delivery
        `Presets`, and a per-family CRF quality mapping. `ExportOptions` gained `Format`/`Quality`/`PixelFormat`
        (its `default` is still MP4/H.264/AAC, so step-8 behaviour is byte-for-byte unchanged); `VideoExporter`
        validates the pairing up front and passes the container/codecs through. **Export resolution capped at 4K**
        (`ComputeExportResolution` scales down preserving aspect, rounds even — an export-side limit only).
      - **Import coverage.** The media open dialog's filters broadened to the full container/audio set
        (MP4/MOV/MKV/WebM/AVI/MXF/TS/… + WAV/MP3/AAC/FLAC/AC-3/Opus/…), with per-file graceful failure already in
        place (§15). `MediaSource.Probe` now records the source's **codec (canonical name), pixel-format name,
        component bit depth, HDR-transfer flag (PQ/HLG), and a VFR heuristic** on `ProbedMediaInfo` (additive,
        defaulted; persisted as nullable so pre-27 files round-trip byte-identically and opaque/8-bit/SDR/CFR media
        keeps a minimal diff). VFR/10–12-bit/4:2:2-4:4:4/HDR/alpha sources decode frame-accurately by PTS as before;
        the probe just makes their properties visible (media-bin display).
      - **UI.** An `ExportSettingsDialog` (cascading container → valid-codec dropdowns + quality tier + a
        4K-capped output-resolution readout) runs before the save picker, whose extension / file-type filter now
        follow the chosen container; `MainWindow` passes the resulting `ExportOptions` to `VideoExporter`.
      - **Tests.** `ExportMatrixTests` round-trips six container/codec combinations (MP4·H.264+AAC, MOV·ProRes+PCM,
        MKV·HEVC+FLAC, WebM·VP9+Opus, MP4·AV1+AAC, TS·MPEG-2+MP3) — reopening each and asserting the muxed streams'
        canonical codecs — plus a ProRes 10-bit check, an invalid-pairing rejection, a quality-tier file-size
        ordering, and the 4K-cap resolution math; `MediaSourceTests` gains a probe-details assertion and
        `ProjectSerializerTests` verifies the new probe fields round-trip. Clean build (0 warnings) and a smoke
        launch starts + tears down cleanly. Deliberate deferrals (noted above): hardware encode as a whole (a
        hardware `ExportVideoCodec` option + the `hw_frames_ctx` GPU-frame upload path) — the name-based encoder
        selection is the seam it will use, but export stays software/deterministic for now; and any HDR tone-map
        (the `IsHdr` flag is informational until the later color step). Full suite: **547 tests green** (Core 209,
        Media 30, Render 33, Audio 21, Playback 52, Export 26, Persistence 40, App 136).
## Step 28

28. **Interchange & relink workflow (EDL / XML interchange formats, batch relink, collab-ready format).** Pulled
    earlier than the specialized finishing work because it becomes necessary the moment projects leave
    the original machine or asset paths change. Three strands, all additive on the persistence and
    media-pool seams:
    - **Interchange export / import.** At minimum **EDL** (CMX3600) export of the active sequence; then
      **XML-based interchange formats used by leading editors** for round-tripping cuts (clips, in/out,
      track layout, basic transitions) with other NLEs. A pure mapper between the `Project` / `Sequence`
      model and each interchange format (Core / Persistence), tested against known fixtures; lossy fields
      are reported, not silently dropped.
    - **Batch relink & offline recovery.** A relink workflow that re-points many `MediaRef`s at once when
      assets move (pick a new root folder → match by filename / path / size, preview matches, apply),
      strengthening the step-9 offline-tolerant load: offline clips stay in the project rendering as
      black / silence (§15) and surface in a "missing media" list that the relink dialog drives.
    - **Collab-ready format split.** Separate asset paths from the shared project file: the diffable
      project JSON references each source by stable `MediaRef` **Id** only, while the
      **absolute/local path lives in a per-user sidecar "media link" file** (not normally committed or
      merged) — so pulling a collaborator's project-file change never forces you to relocate your own
      clips, because your local link file still resolves the Ids. This refactors step 9's "relative +
      absolute path stored in the project file" into **Id-in-project + path-in-sidecar**, and keeps each
      logical edit a small, localized, stable-ordered diff so projects version-control and merge cleanly
      ([ARCHITECTURE §12](ARCHITECTURE.md), [§15](ARCHITECTURE.md)). Full multi-user editing (presence,
      locking, or CRDT / operational-transform merge) is a larger later product-platform effort this
      format enables — **not** in the 1.0 set; the actionable deliverable here is the **format split**.
    - **✅ DONE (`src/Sprocket.Persistence`: `MediaLinks`, `MediaRelink`, `Interchange/{InterchangeReport,SmpteTimecode,
      EdlExporter,FinalCutXmlInterchange}`, `ProjectSerializer`/`ProjectDto` refactor; `src/Sprocket.App/MainWindow`;
      50 new headless tests — Persistence 40 → 90, all green; full suite 597.)** All three strands land additively on
      the persistence + media-pool seams (ARCHITECTURE.md §12/§17), no schema bump, no Core model change. Delivered:
      - **Collaboration-ready format split.** The committed project file now references each source by stable
        `MediaRefId` (+ its content-derived, diffable `ProbedMediaInfo`) **only**; the per-user asset **paths** move to
        a `.links.json` **media-link sidecar** (`MediaLinks`, atomic temp→promote write, independently schema-versioned).
        `ProjectSerializer.Save`/`Load` operate on the **pair** (Id-only project file + sidecar) — the diffable,
        merge-friendly form — while `Serialize` (to a lone string: autosave/undo-snapshot) stays **self-contained** with
        paths inlined (a string has nowhere else to put them). Resolution order on load: **sidecar link → inlined
        relative (if the file exists) → inlined absolute → offline** (empty path, renders as black/silence §15). **No
        schema bump** — the `MediaRefDto` path fields became additive/nullable, so pre-28 files (inline paths) still
        load and a sidecar entry wins over a stale inline path; a project shared *without* its sidecar loads every source
        offline, ready to relink (the collaboration payoff — pulling a project-file change never relocates your clips).
      - **Batch relink & offline recovery.** `MediaRelink` (I/O) finds offline sources (empty or now-missing path),
        recursively scans a chosen root folder for candidates (skipping unreadable dirs), and applies a previewed
        `RelinkPlan`; the **pure** `MediaRelinkMatcher` matches by **file name** (case-insensitive), disambiguating
        same-named candidates by **longest common path tail** (cross-separator, so a Windows-recorded path matches a
        POSIX candidate) then by known **size**, and reports `Matches` / `Ambiguous` / `Unmatched` rather than guessing.
        Relinking updates only the per-user path → written straight to the sidecar (`MediaLinks.Write`), never dirtying
        the shared project. Strengthens step 9's offline-tolerant load into a real "missing media" workflow.
      - **Interchange export / import** (`Sprocket.Persistence.Interchange`, a pure model↔format mapper): **CMX3600 EDL**
        export (`EdlExporter` over a pure, drop-frame-aware `SmpteTimecode` — verified against the reference DF algorithm)
        flattening the active sequence to a record-ordered, numbered event list (one video track + up to 4 audio
        channels, `* FROM CLIP NAME` comments, valid cuts); and **Final Cut Pro 7 XML** (`xmeml` v5) **export + import
        round-trip** (`FinalCutXmlInterchange` — the lingua franca leading editors read): sequence rate (NTSC
        `timebase`+`ntsc`) / resolution / name, video+audio track layout, each clip's record placement + source in/out,
        and source `<file>` references (id + `pathurl`, defined once then referenced by id). Everything a format can't
        carry (effects, transitions, retimes, track opacity/blend/gain/mute/solo, generated/nested/multicam clips,
        markers, source codec/bit-depth/HDR/alpha/VFR metadata) is **reported** via `InterchangeReport`, never silently
        dropped. Frame-based interchange snaps to whole frames (true of every NLE); Sprocket clips are frame-aligned so
        a cut round-trips exactly (verified at 29.97 NTSC).
      - **App wiring (thin, smoke-verified).** File menu gains **Relink Media…** (offline scan → folder picker →
        previewed confirm → apply → sidecar write → media-bin refresh) and **Export Interchange ▸ EDL (CMX3600)… /
        Final Cut XML…** (save picker → export → a dialog listing any lossy fields). Interchange needs no export-style
        pipeline quiesce (it's a pure mapping, no in-process muxer, §15). Normal **Save** now writes the `.links.json`
        sidecar alongside the project.
      - **Tests (+50).** `MediaLinkPersistenceTests` (Save omits paths + writes sidecar, project-file-only → offline,
        self-contained inline round-trip, sidecar-wins-over-inline, pre-28 inline load, missing-sidecar/offline skips),
        `MediaRelinkTests` (single/none/tail-disambiguation/ambiguous/size-tiebreak/no-name/case-insensitive matcher +
        cross-separator tail + find-offline/plan-apply I/O), `SmpteTimecodeTests` (non-drop + DF reference values, DF vs
        non-drop divergence, rate classification, parse round-trips), `EdlExportTests` (header/FCM, numbered events,
        source/record timecodes, channels, lossy report, file write), `FinalCutXmlInterchangeTests` (format/name/layout/
        placement/media-pool round-trip, define-once-reference-by-id, lossy report, non-xmeml rejection, file round-trip).
        The updated moved-project test copies the sidecar (paths now live there). Clean build (0 warnings) and a
        `SPROCKET_APP_SECONDS=5` smoke launch starts the shell with the new menu items wired and tears down cleanly
        (exit 0). Full suite: **597 tests green** (Core 209, Media 30, Render 33, Audio 21, Playback 52, Export 26,
        Persistence 90, App 136).
      - **Deferred (noted, same seam):** **the newer Apple XML interchange format (v1.x)** (its DTD supersedes
        `xmeml`, which already gives leading editors round-trip compatibility); a richer **relink preview dialog** (per-file candidate override — the matcher
        already reports ambiguous/unmatched, and the App confirms the plan before applying); and **EDL dissolves** (a
        transition currently exports as a plain cut + a lossy warning).
## Step 29

29. **Export queue, burn-ins, handles & presets + status-bar telemetry.** Standard delivery workflow on
    top of the step-8 export path and the step-27 format/codec matrix:
    - **Export queue.** Queue multiple export jobs (different sequences / in-out ranges / presets) that
      run sequentially on the background export path with per-job progress and cancel.
    - **Burn-ins & handles.** Optional **burn-in overlays** (timecode, clip name, watermark) baked by an
      effect-stack stage on the export render (§5/§7, so they're deterministic and never touch preview's
      hot path) and **handles** (extra frames before / after each clip's in/out) for review / conform
      outputs.
    - **Presets.** An export dropdown of saved selections over the step-27 matrix (container × codec ×
      quality × resolution / frame-rate), user-definable and persisted.
    - **Hardware export encoders (carried over from step 27).** Step 27 shipped the *software* codec matrix and
      left hardware encode explicitly deferred; finish it here. Add hardware `ExportVideoCodec` options
      (**NVENC / QSV / AMF** on Windows, **VAAPI / NVENC** on Linux, **VideoToolbox** on macOS) behind the
      existing `IHardwareContext`, negotiated by a runtime encoder probe with **automatic software fallback**
      (`libx264`/`libx265`/SVT-AV1). `MediaEncoder` already selects encoders by name — the seam exists; the work
      is the encoder-probe/fallback path plus the `hw_frames_ctx` **GPU-frame upload** so the composited frames
      reach the GPU encoder (the binding surface for this is catalogued in
      [`Native/FUTURE_BINDINGS.md`](src/Sprocket.Media/Native/FUTURE_BINDINGS.md) — "hardware encode … still
      open"). Preview/cache intermediates use the same hardware encoders for speed (step 32); **final delivery
      keeps a deterministic software path available** for golden-frame reproducibility (§5).
    - **Status-bar telemetry.** Surface engine state, GPU / hardware-accel status, live fps, resolution,
      and duration ([ARCHITECTURE §15](ARCHITECTURE.md)) — **no framework/runtime text** in the UI
      ([UI.md §3.7](UI.md)).
    - **✅ Export-queue strand DONE (`Sprocket.Export`: `ExportQueue`/`ExportJob`/`ExportRange` + `VideoExporter`
      sequence/range overload; `Sprocket.Core` + `Sprocket.Audio` sequence-audio overloads; `Sprocket.App`:
      `ExportQueueWindow` + File ▸ Export Queue…; 20 new tests — Export 26 → 44, Audio 21 → 23; full suite 597 → 617).**
      The queue is one of the five strands of step 29 (with burn-ins & handles and presets now also done below; the
      others — hardware export encoders and status-bar telemetry — **remain**). Delivered, all on the existing
      seams (ARCHITECTURE.md §5/§17 — only the orchestration around the render graph is new, the render is unchanged):
      - **`ExportQueue` + `ExportJob` (Export).** A sequential batch runner: jobs run one-at-a-time on a background
        worker (so only one in-process libav* muxer is ever live — concurrent muxing crashes the native encoder,
        the same hazard the single Export quiesces for), each reporting its own progress and cancellable
        individually, with a queue-wide **Stop**. Jobs enqueued mid-run are picked up. It is **decoupled from the
        encoder by an injected `ExportJobRunner`** — the queue owns ordering / status transitions / cancellation,
        not rendering — so it unit-tests without FFmpeg and the composition root binds the runner to `VideoExporter`.
        `ExportJob` splits an immutable spec (output path, `ExportOptions`, target `SequenceId?`, `ExportRange?`)
        from the runtime `Status`/`Progress`/`Error` the queue drives; `ExportJobStatus` = Queued/Running/Succeeded/
        Cancelled/Failed. Thread-safe surface (list + state under a lock; `Changed` raised outside it, so a UI
        subscriber marshals).
      - **`VideoExporter` sequence + in-out range (Export).** A new overload
        `Export(project, path, options, SequenceId?, ExportRange?, …)` renders **any** sequence (not just the active
        one) and an optional half-open `[In, Out)` timeline slice; the delegating original signature is byte-for-byte
        unchanged. A sub-range samples the timeline at `rangeIn + offset` while the encoded file's own timestamps
        start at zero (a slice plays from 0). `ExportRange` (clamp-to-timeline, validity, `Whole`) is the pure
        range type. *(2026-07-11)* The interactive export UI now feeds it: `ExportSettingsDialog` gained a
        **Range** selector (Entire sequence / In/Out range — the Premiere/Resolve export-dialog convention),
        defaulting to the step-32 timeline in/out marks when set; both File ▸ Export and the queue's Add… pass
        the resolved `ExportRange`. The render graph already planned per-sequence **video**; this adds the matching per-sequence
        **audio**: `RenderGraph.PlanAudioBuffer(project, sequence, …)` (Core) + `AudioMixer.MixInto(…, sequence)`
        (Audio) overloads, so an exported sequence's audio mixes correctly regardless of which sequence is open.
      - **`ExportQueueWindow` + wiring (App).** File ▸ **Export Queue…** (Ctrl+Shift+E) opens a live job list — name,
        format, status, per-job progress bar — with **Add… / Start / Stop / Clear Finished** and per-row Cancel /
        Remove. **Add…** reuses the step-27 `ExportSettingsDialog` + a Save picker and captures the **active
        sequence's id**, so switching sequences between adds queues different sequences; **Start** quiesces the
        Program/Source decode pipelines (as the single export does) and runs the queue to completion on the
        background path, then resumes. Rows update **in place** on each progress tick (the tree only rebuilds when
        the job set/order changes), so a running encode's frequent progress reports don't rebuild the list. Session
        swaps (New / Open) are blocked while an export is in flight so the worker's project/engine isn't torn out.
      - **Tests (20).** `ExportQueueTests` (11, fake runner, no FFmpeg): sequential in-order run, progress
        forwarding + terminal status, a failing job → Failed with message + queue continues, cancel-queued (skipped)
        / cancel-running (Cancelled, next runs) / Stop-all, Remove guards (queued yes, running no), mid-run enqueue
        picked up, ClearCompleted, Changed fires, defaults. `ExportRangeTests` (7, real encode→decode): `ExportRange`
        duration/validity/clamp math; sub-range exports fewer frames + shorter duration than the whole; empty range
        + unknown sequence id throw; **exporting a non-active sequence by id** renders that sequence (white matte ≫
        active black); a **queue end-to-end** runs a whole-timeline + a sub-range job through the real `VideoExporter`
        runner and writes both files. `SequenceAudioMixerTests` (2): the default `MixInto` mixes the active sequence,
        the new overload mixes the given one. Clean build (0 warnings); a `SPROCKET_APP_SECONDS` smoke launch starts
        the shell with the queue menu item wired and tears down cleanly (exit 0). Full suite: **617 tests green**
        (Core 209, Media 30, Render 33, Audio 23, Playback 52, Export 44, Persistence 90, App 136).
    - **✅ Burn-ins & handles strand DONE (`Sprocket.Core`: `BurnIn`/`BurnInField`/`BurnInPosition` + `BurnInResolver`,
      `SmpteTimecode` moved into `Core.Timing`; `Sprocket.Render`: `BurnInRenderer`; `Sprocket.Export`: `ExportOptions`
      `BurnIns`/`HandleFrames` + `ExportRange.WithHandles`; `Sprocket.App`: `ExportSettingsDialog` burn-in/handles
      controls; 27 new tests — Core 209 → 215, Render 33 → 50, Export 44 → 48; full suite 617 → 644).** With presets
      (below) now done too, only hardware export encoders and status-bar telemetry **remain**. Everything lands on
      the deterministic export render (ARCHITECTURE.md §5/§7) — burn-ins are baked *after* compositing and never
      touch the preview hot path or allocate pixels (§1):
      - **Burn-in model (Core).** `BurnIn(Field, Position, Text?)` is pure delivery data that flows with
        `ExportOptions` through the queue. `BurnInField` = Timecode / ClipName / Text (watermark); `BurnInPosition`
        is a nine-point alignment grid (matching the convention in leading editors). `BurnInResolver` turns a burn-in + timeline time
        into the string to draw — timecode via the shared `SmpteTimecode`, or the **topmost content clip's** name
        (media file name / nested-sequence name / generator label; adjustment layers skipped, a gap → ""). Resolution
        is pure model so it is unit-tested headlessly. **`SmpteTimecode` moved from `Persistence.Interchange` into
        `Core.Timing`** (its existing tests still pass) so the EDL/FCXML exporters and the burn-in share one
        drop-frame-correct formatter rather than duplicating it.
      - **`BurnInRenderer` (Render).** A pure post-composite overlay like `MonitorOverlay`: it draws the resolved
        strings white over a translucent pill (legible on any content) at the nine anchor points, inset from the
        edge. The layout (`ComputeTextTopLeft`) is pure/testable; the exporter calls it after `CompositePlan` and
        before pixel readback. The timecode shows the **record (timeline) time**, so a conform/review TC matches the
        project regardless of the export range.
      - **Handles + burn-ins in the exporter (Export).** `ExportOptions` gains `HandleFrames` (extra frames before /
        after the in-out range for review/conform outputs) and `BurnIns`; both flow through the queue unchanged.
        `ExportRange.WithHandles(head, tail)` grows the range (negatives clamp to zero), then the exporter re-clamps
        to the timeline — so handles reach only media that exists there and a whole-timeline export is unaffected.
        The delegating original `Export` signatures are unchanged. Handles = the timeline-range (conform) reading;
        per-clip media-management handles (one file per clip) are the natural deferred extension.
      - **UI (App).** `ExportSettingsDialog` gains a **Burn-ins** section — Timecode / Clip name checkboxes and a
        Watermark textbox, each with a nine-point position picker — plus a **Handles (frames)** field, wrapped in a
        `ScrollViewer`. Because both the single **Export** and **Export Queue ▸ Add…** already read their options from
        this one dialog, both paths carry burn-ins/handles for free. Defaults reproduce the pre-step-29 behaviour.
      - **Tests (27).** `BurnInTests` (Core, 6): timecode format at the sequence rate, topmost-clip-name over the
        clip / "" over a gap / topmost-track wins / adjustment-layer skipped / generator labels, literal watermark.
        `BurnInRendererTests` (Render, 17): the nine anchor points land in the right column/row with the margin,
        centring is exact, degenerate/empty draws no-op. `BurnInAndHandlesExportTests` (Export, 4, real encode): a
        top-left timecode burn-in brightens that corner over a black matte and stays localised; handles extend an
        in-out range (more frames) but never past the timeline; whole-timeline handles add nothing; `WithHandles`
        math. Clean build (0 warnings); a `SPROCKET_APP_SECONDS` smoke launch starts the shell and tears down cleanly
        (exit 0). Full suite: **644 tests green** (Core 215, Media 30, Render 50, Audio 23, Playback 52, Export 48,
        Persistence 90, App 136).
    - **✅ Presets strand DONE (`Sprocket.Export`: `ExportPreset` extended + `ExportPresetStore` + `ExportOptions`
      `Resolution`/`FrameRate` overrides in `VideoExporter`; `Sprocket.App`: `UserExportPresets` + `ExportSettingsDialog`
      preset/resolution/frame-rate controls; 17 new tests — Export 48 → 65; full suite 644 → 661).** The fourth of the
      five strands; only hardware export encoders and status-bar telemetry now **remain**. Presets are saved selections
      over the step-27 matrix — container × codec × quality × **resolution / frame-rate** — user-definable and
      persisted, all on the existing deterministic export render (no new render path):
      - **Resolution / frame-rate overrides in the exporter (Export).** `ExportOptions` gains `Resolution?` and
        `FrameRate?` (both `null` = keep the sequence's own, so `default(ExportOptions)` is byte-for-byte the
        pre-step-29 behaviour). A resolution override flows through the same `ComputeExportResolution` (4K cap + even
        rounding) and sizes the offscreen surface, so the composite scales/letterboxes into it. A frame-rate override
        just samples the timeline at the new frame instants — the render graph is a pure function of time, so this is
        the standard NLE resample-on-export (frames duplicated / dropped), with **handles still counted in the
        timeline's own frames**. No change to the render itself (ARCHITECTURE.md §5).
      - **Preset model + store (Export).** `ExportPreset` extended with optional `Resolution` / `FrameRate` and a
        `ToOptions()`; the curated `ExportCodecs.Presets` gains resolution-pinned web-delivery presets (YouTube 4K /
        1080p, Web 720p — mirroring the presets leading NLEs ship). `ExportPresetStore` (de)serialises the **user's**
        presets to JSON through a flat DTO (enums by name → a stable, human-editable file), reads/writes best-effort
        (missing/corrupt → empty, never throws), and merges built-ins + user for the dropdown. Burn-ins/handles are
        deliberately **not** part of a preset (they're per-export review options, not a delivery format).
      - **UI (App).** `UserExportPresets` persists the user list under `%AppData%/Sprocket/export-presets.json`
        (mirrors `WindowStateStore`). `ExportSettingsDialog` gains a **Preset** dropdown (Custom + built-ins + user
        presets; selecting one applies its format/quality/resolution/rate, and any manual edit snaps back to Custom),
        **Resolution** and **Frame rate** pickers (a closed set so every saved preset round-trips to a dropdown entry),
        and a **Save Preset…** button (name prompt → persist → reselect). Both the single **Export** and **Export
        Queue ▸ Add…** read this one dialog, so both carry the overrides for free.
      - **Tests (17).** `ExportPresetTests` (Export, headless): `ToOptions` mapping, JSON round-trip preserving
        overrides, enums-by-name, blank/corrupt → empty, nameless entries skipped, file load/save + missing-path,
        built-in-then-user merge order, resolution-pinned built-ins present. `ResolutionFrameRateExportTests` (Export,
        real encode): a frame-rate override resamples the timeline (½× / 2× frame counts), a resolution override
        encodes at the chosen (even-rounded) size, and a preset drives the whole preset→options→encode path. Clean
        build (0 warnings); a `SPROCKET_APP_SECONDS` smoke launch starts the shell with the new dialog and tears down
        cleanly (exit 0). Full suite: **661 tests green** (Core 215, Media 30, Render 50, Audio 23, Playback 52,
        Export 65, Persistence 90, App 136).
    - **✅ Status-bar telemetry strand DONE (`Sprocket.App`: `StatusBarFormat` + live status-bar driver in
      `MainWindow`; 7 new tests — App 136 → 143; full suite 661 → 668).** The fifth and final surface strand of
      step 29 — **only hardware export encoders now remain**. The status bar was half-static (a fixed green dot +
      "Ready", and a *nominal*-rate telemetry cell); this makes it live per UI.md §3.7 — render/decode state,
      **GPU / hardware-accel status**, **live fps**, resolution, duration — with **no framework/runtime text**
      (UI.md §3.7 / §5), reusing the diagnostics counters the Playback Statistics overlay already reads
      ([ARCHITECTURE §15](ARCHITECTURE.md)):
      - **The non-negotiable perf rule (ARCHITECTURE.md §1).** Zero work lands on the render/decode hot path: the
        readout only *reads* the engine's existing cumulative counters (`GetStatistics()` — a couple of
        `Interlocked` reads) and its cached decode-info snapshot (`GetActiveVideoDecodeInfo()`, managed, never
        native state). The live poll is a 1 Hz `DispatcherTimer` that runs **only while playing** — it is started
        on the transition to Playing and stopped on Paused/Stopped, so a paused/idle editor incurs **no periodic
        wake-ups** and the readout is purely event-driven at rest. Assignments are change-guarded (compare before
        set) so a steady readout never re-lays-out the status bar. This is strictly lighter than the per-frame
        `PositionChanged` marshaling that already drives the scrubber.
      - **Live state + GPU/hw-accel (App).** The left group shows `State · GPU · <DEVICE>` (hardware) or
        `State · CPU · software`, with the state dot green while playing, **amber on the software (CPU) path** (the
        usual 1080p-stutter tell, matching the overlay), and neutral at rest; "Ready" is the parked/stopped word
        (mockup wording). A one-shot `FramePresented` handler (self-unsubscribing, marshalled off the pump thread)
        refreshes the decode path once frame 0 has decoded, so the GPU/CPU status shows at rest without any idle
        poll. The active-monitor accessor (`_active?.CurrentEngine ?? _engine`) means a Program↔Source tab switch
        re-points the readout, exactly as the transport does.
      - **Live fps (App).** The right cell shows the *measured* preview rate while playing (present-count delta over
        the real elapsed interval), settling back to the sequence's *nominal* rate when stopped — so a healthy
        1080p preview reads its true 23.98/30/… and a struggling one visibly dips.
      - **`StatusBarFormat` (App).** The label/telemetry strings are a pure static helper (mirroring
        `MarkerListFormat`/`SpeedFormat`), so the code-behind only maps strings onto the `TextBlock`s + picks the
        dot colour, and the formatting is unit-tested headlessly.
      - **Tests (7).** `StatusBarFormatTests`: the state word for Stopped/Paused/Playing with no decode; the
        hardware label (`Playing · GPU · D3D11VA`, device upper-cased) and software label (`Ready · CPU · software`);
        the `fps · WxH · duration` readout and its whole-rate trimming (`30 fps`, not `30.00`). Clean build
        (0 warnings); `SPROCKET_APP_SECONDS` smoke launches (empty + with the sample clip, exercising the transport
        wiring and the one-shot present handler) start and tear down cleanly (exit 0). Full suite: **668 tests
        green** (Core 215, Media 30, Render 50, Audio 23, Playback 52, Export 65, Persistence 90, App 143).
    - **✅ Hardware export encoders strand DONE — step 29 now complete** (`Sprocket.Media`: `VideoEncoderSettings.HardwareCandidates`
      + probe/fallback & GPU-upload in `MediaEncoder`, `MediaEncoder.IsHardwareVideo`, `HardwareDevice.EncoderDeviceType`,
      new `av_hwframe_ctx_alloc`/`_ctx_init`/`_get_buffer` bindings + `AvHwFramesContext`/`AvBufferRef` views +
      `AVCodecContext.hw_frames_ctx`; `Sprocket.Export`: `ExportAcceleration` + `ExportCodecs.HardwareEncoderCandidates`/
      `PlatformHardwareVendors` + `ExportOptions.Acceleration`; `Sprocket.App`: an **Encoding** picker in
      `ExportSettingsDialog`; 16 new tests — Media 30 → 34, Export 65 → 77; full suite 668 → 684). The last of the five
      strands. **Deliberate departure from the literal wording** ("add hardware `ExportVideoCodec` options"): rather than
      exploding the codec enum into per-vendor values, hardware is modelled as an acceleration preference *orthogonal* to
      the codec — the way leading editors present it (an explicit software/hardware toggle, or an encoder picker) — so H.264/HEVC/
      AV1/… each gain GPU encoding without a combinatorial enum, and the deterministic software encoder stays the delivery
      default. All the strand's deliverables land on the existing `MediaEncoder` "select encoders by name" seam (§11):
      - **Probe + automatic software fallback (Media).** `VideoEncoderSettings.HardwareCandidates` is an ordered chain of
        hardware encoder names tried **before** the software `CodecName`. `MediaEncoder.OpenVideo` opens each in turn,
        engaging the first that succeeds and **silently degrading** to the next — then to software — on any failure (no
        device, encoder not built in, open rejected the GPU), mirroring how `MediaSource` negotiates hardware *decode*.
        The software `CodecName` open is unchanged and still surfaces its own errors, so a machine with no usable GPU
        produces the **identical deterministic software output** (ARCHITECTURE.md §5). A failed hardware attempt tears
        down atomically (device / frames-pool / staging frames / encoder ctx) and staging frames are allocated only after
        `avcodec_open2` succeeds, so a rejected candidate never leaves an orphan stream in the muxer for the next to
        double up on. `IsHardwareVideo` reports which path engaged.
      - **Two GPU-frame paths, chosen by the encoder's advertised pixel formats (Media).** An encoder that lists a CPU
        (non-`HWACCEL`) pixel format — NVENC / QSV / AMF / VideoToolbox — is fed the composited frame swscaled to nv12
        and **uploads internally**; one that lists *only* device-surface formats — VAAPI — takes the **`hw_frames_ctx`
        upload path**: a pooled GPU surface is drawn from an `AVHWFramesContext` (`av_hwframe_get_buffer`) and the nv12
        staging frame uploaded into it (`av_hwframe_transfer_data`) each frame before encode. Both are native→GPU copies
        with **zero managed pixel allocation** (§1). Quality was originally driven by bit rate only (an explicit
        `VideoBitRate` winning over a resolution-scaled default); *(2026-07-12)* hardware now **honours the
        constant-quality setting too** via each vendor's own CQ knob — see the rate-control addendum below.
      - **Platform candidate resolver (Export).** `ExportCodecs.HardwareEncoderCandidates(codec)` builds `{base}_{vendor}`
        names for the current OS, most-preferred first per the brief — **Windows** NVENC → QSV → AMF, **Linux** VAAPI →
        NVENC, **macOS** VideoToolbox — and `VideoExporter` passes them as the candidate chain only when
        `ExportOptions.Acceleration == Hardware` (the default `Software` carries none, so `default(ExportOptions)` is
        byte-for-byte the pre-step-29 delivery path). Nonexistent combos (e.g. `prores_nvenc`) are harmless — the probe
        skips any name this FFmpeg build lacks.
      - **UI (App).** `ExportSettingsDialog` gains an **Encoding** picker (Software / Hardware — if available) beside
        Quality. It is deliberately **not** part of a preset (a performance choice, not a delivery format), so it neither
        snaps the preset to Custom nor is captured by Save Preset. Both single **Export** and **Export Queue ▸ Add…** read
        this dialog, so both carry the choice.
      - **Tests (16).** `HardwareExportTests` (Export, 12): the resolver is the platform vendor product most-preferred
        first (per-OS exact lists) and uses each codec's hardware base name; a Hardware-acceleration export round-trips to
        the requested codec family whether a GPU engaged **or fell back** (stable because the *decoded* codec is the
        family); software + hardware requests both produce valid files; `default(ExportOptions).Acceleration` is Software.
        `HardwareEncodeTests` (Media, 4): no-candidates is pure software; an unavailable candidate (and a whole chain of
        them) falls back to software with a single well-formed stream; the real platform H.264 candidates **engage or fall
        back but always produce a decodable file**. On this NVIDIA dev box the opportunistic tests exercised the **real
        `h264_nvenc` upload+encode path** (`IsHardwareVideo` true, valid H.264 out); the VAAPI-only `hw_frames_ctx` surface
        path is code-verified but Linux-only (no VAAPI device here). Clean build (0 warnings); `SPROCKET_APP_SECONDS` smoke
        launch with the sample clip starts and tears down cleanly (exit 0). Full suite: **684 tests green** (Core 215,
        Media 34, Render 50, Audio 23, Playback 52, Export 77, Persistence 90, App 143).
    - **✅ *(2026-07-12)* Rate-control upgrade (steps 27/29 export quality, revisited).** The opaque High/Medium/Low
      tier became a **two-mode rate control** matching the leading-NLE convention (Resolve's Quality vs "Restrict to"
      bitrate; Premiere's VBR target), and the hardware path's silent quality no-op is fixed:
      - **Model (Export).** `ExportRateControl` (Quality | Bitrate) + `ExportOptions.RateControl`/`Crf`/`MaxBitRate`
        (additive; `default(ExportOptions)` unchanged). Quality mode resolves an explicit CRF (else the tier's
        `CrfFor`); bitrate mode a target (else the new `ExportCodecs.DefaultTargetBitrate` — ≈40 Mbps@4K, 16@1080p,
        8@720p, fps-scaled) with an optional `maxrate`/`bufsize` ceiling. New helpers `MaxCrfFor` (51 / 63 for
        AV1-VP9) and `QualityLabel` (plain-language slider readout). `ExportPreset` + its JSON DTO carry the new
        fields (nullable/omitted → legacy files load as before).
      - **Hardware constant quality (Media).** `MediaEncoder.DescribeHardwareQualityOptions` maps the 0–51 CRF domain
        onto each vendor's own knob — NVENC `rc=vbr`+`cq` (+5 offset, cq runs ~5 generous vs x264), QSV ICQ via
        `global_quality`, AMF `rc=cqp`+`qp_i/p/b`, VAAPI `rc_mode=CQP`+`qp`, VideoToolbox the generic qscale
        mechanism (`+qscale`, `global_quality = q×FF_QP2LAMBDA` on its inverted 0–100 scale); AV1/VP9's 0–63 CRF is
        rescaled first (`NormalizeCrfTo51`). A vendor that rejects an option fails open → candidate skipped →
        software (which honours CRF exactly), the existing degrade chain. The render cache's hardware intermediates
        inherit constant quality (documented in `PreviewRenderer`); proxies are unaffected (out-of-process ffmpeg CLI).
      - **UI (App).** The dialog's Quality dropdown became a **Rate control** picker with swappable sub-panels: a CRF
        slider on the selected codec's own scale with a live "CRF N — visually lossless/high quality/good for web/…"
        readout, or Target/Max Mbps boxes seeded with the recommended default for the output resolution. Presets
        capture the mode + values.
      - **MCP.** `export_video` gained optional `rateControl`/`crf`/`bitrateMbps`/`maxBitrateMbps`/`hardware`
        params (validated with clear errors), threaded through `IEditorSession.StartExport` as primitives.
      - **Tests.** Media +16 (`HardwareQualityMappingTests` — exact per-vendor keys/values, offset/inversion/rescale),
        Export +17 (`RateControlTests` — exact `CrfFor`/`MaxCrfFor`/label/default-bitrate values, preset round-trip +
        legacy-JSON defaulting, real encodes: CRF ordering, bitrate targeting within budget, max-rate capping),
        Mcp +1 (param pass-through + validation).
## Step 30

30. **Audio loudness metering, normalization & editorial audio polish.** The delivery-grade audio
    visibility that effects alone don't provide — the first of the two audio-post layers (the second is
    plugin hosting + deeper DSP, step 31):
    - **Loudness metering.** Real-time **LUFS** metering (integrated / short-term / momentary) + true-peak
      per the EBU R128 / ITU-R BS.1770 model, plus channel meters, computed on the
      [ARCHITECTURE §6](ARCHITECTURE.md) audio path without per-buffer managed allocation, displayed in
      the audio mixer / meters UI.
    - **Normalization.** Loudness normalization to a target (e.g. −14 / −16 / −23 LUFS) applied as a
      computed gain at clip / track / master scope, plus a per-clip gain-match pass — all model gain (the
      mixer already does gain/fade, step 5), undoable.
    - **Editorial audio polish.** Audio meters, per-track gain / pan controls, and the **Audio** tab /
      mixer surface ([UI.md §3.3](UI.md)) brought to editorial completeness.
    - **✅ Loudness-metering strand DONE (`Sprocket.Audio/Loudness`: `KWeightingFilter`, `TruePeakMeter`,
      `LoudnessMeter` + `LoudnessSnapshot`; tapped into `AudioEngine`; 15 new tests — Audio 23 → 38; full suite
      684 → 699).** The first of step 30's three strands. Real-time EBU R128 / ITU-R BS.1770-4 loudness is now
      measured on the [ARCHITECTURE §6](ARCHITECTURE.md) audio path with **zero per-buffer managed allocation**
      (§1); the read-out is ready for the meters UI, which lands with the editorial-audio-polish strand. The
      **normalization** and **editorial polish** strands remain. Delivered:
      - **`KWeightingFilter` (Audio).** The two-stage BS.1770 K-weighting pre-filter (high-shelf "head model" +
        RLB high-pass) as cascaded Transposed-Direct-Form-II biquads, one state set per channel. Coefficients are
        computed from the analog prototypes for **any** sample rate (the libebur128 mapping — not just the 48 kHz
        table in the standard), so 44.1/48/96 kHz projects all measure correctly; verified against the curve
        (DC removed, ~0 dB at 1 kHz, +~4 dB high-shelf plateau, strong sub-bass roll-off).
      - **`TruePeakMeter` (Audio).** True peak (dBTP) by **4× oversampling** with a Hann-windowed-sinc polyphase
        FIR (BS.1770-4 Annex 2), each branch normalised to unity DC gain; per-channel history rings + a peak-hold
        running max keep it allocation-free. Catches inter-sample overshoot the sampled peak misses (a full-scale
        fs/4 sine phased onto ±0.707 samples reconstructs to ~0 dBTP).
      - **`LoudnessMeter` + `LoudnessSnapshot` (Audio).** Accumulates K-weighted energy in 100 ms segments; a
        fixed ring of the last 30 gives the **momentary** (400 ms) and **short-term** (3 s) sliding windows, and
        overlapping 400 ms gating blocks feed a **bounded 0.1-LU histogram** for the **gated integrated** loudness
        (absolute −70 LUFS + relative −10 LU gates, computed with the standard two-pass mean-of-energy). Also
        tracks true peak and per-channel sample peak. `Process` is feeder-thread-confined and lock-free except a
        tiny publish at each 100 ms boundary; `TakeSnapshot` reads the published values from the UI thread;
        `RequestReset` (thread-safe flag) restarts the integrated measurement. `Flush` closes the final partial
        segment so a finite offline stream's tail is measured (reused by normalization next).
      - **`AudioEngine` tap.** The feeder meters **only the buffers actually enqueued** for playback (a
        superseded-generation mix is dropped, not metered) and does so **off the transport lock** so the DSP never
        stalls `Now`; `Seek` calls `RequestReset`. `AudioEngine.CurrentLoudness` exposes the snapshot to the UI.
      - **Tests (15, headless, no device).** K-weighting DC-block / 1 kHz-unity / 10 kHz-shelf / sub-bass
        roll-off; true-peak inter-sample overshoot + ≥ sample-peak; and loudness invariants: silence → −∞, DC far
        quieter than a tone (RLB), **+6.02 LU per amplitude doubling**, **+3.01 LU stereo-vs-mono**, full-scale
        1 kHz calibration band, **absolute gate** ignores a near-silent tail, **relative gate** ignores a much
        quieter section, momentary window shorter than short-term, and reset restarts integrated. Full suite:
        **699 tests green** (Core 215, Media 34, Render 50, Audio 38, Playback 52, Export 77, Persistence 90,
        App 143).
    - **✅ Normalization-engine strand DONE (`Sprocket.Core`: `Clip.GainDb`, `AudioPlanScope` + scoped/clip-gain
      `PlanAudioBuffer`, `Audio.LoudnessNormalization`; `Sprocket.Audio`: `LoudnessAnalyzer` + `LoudnessMeasurement`,
      scoped `AudioMixer.MixInto`; `Sprocket.Persistence`: additive `ClipDto.GainDb`; 16 new tests — Core 215 → 224,
      Audio 38 → 44, Persistence 90 → 91; full suite 699 → 715).** The second of step 30's three strands: the loudness
      normalization **engine** (measurement + gain math + per-clip model gain + measurement scoping), all on the
      existing audio path. The **undoable Normalize actions + target UI + per-clip gain-match** land with the
      editorial-audio-polish strand (the mixer/Audio-tab surface where they belong, as the metering DSP shipped
      ahead of its meters UI); the editorial-polish strand is what **remains** of step 30. Delivered:
      - **`Clip.GainDb` (Core).** A per-clip audio gain (dB, 0 = unity) folded into the audio plan alongside track
        gain and the fade envelope (`RenderGraph.PlanAudioBuffer` — media, multicam, and nested-sequence layers),
        cloned by blade-split, and **persisted additively** (`ClipDto.GainDb`, `WhenWritingNull` — unity writes
        nothing, so pre-30 files load at unity and un-gained projects serialize byte-identically). This is the model
        gain clip-scope normalization sets; no mixer change (it already ramps the plan's per-layer gain).
      - **`AudioPlanScope` (Core).** An optional measurement scope on `PlanAudioBuffer` (and a matching scoped
        `AudioMixer.MixInto`): isolate one track (ignoring its mute/solo so its content is measurable) and/or force
        unity track / master gain, applied only at the measured sequence's top level. It lets a clip / track /
        master scope's **raw** loudness be measured, so normalization is an absolute set (`gain = target −
        measuredRaw`) rather than a fragile delta. The full-mix (null-scope) path is byte-for-byte unchanged.
      - **`LoudnessNormalization` (Core, pure).** Delivery targets (−14 / −16 streaming, −23 EBU R128) + a default
        −1 dBTP ceiling, and `ComputeGainDb(measuredLufs, measuredTruePeakDbtp, target, ceiling)` = the gain to hit
        the target, **reduced so the true peak stays under the ceiling** (true-peak limiting only ever cuts below
        target; silence returns 0). Dependency-free numbers, so commands/tests use it without the Audio layer.
      - **`LoudnessAnalyzer` + `LoudnessMeasurement` (Audio).** Offline (faster-than-real-time) measurement through
        a private `LoudnessMeter`: `MeasureSource` (one PCM reader over a clip's used span → clip scope) and
        `MeasureMix` (a mixed sequence with an optional scope → master / track scope). One reusable buffer, no
        per-chunk allocation, and it never touches the live playback meter.
      - **Tests (21, headless, no FFmpeg).** Core: clip gain folds into the layer gain and multiplies with track
        gain; scope isolates a track (ignoring solo) and forces unity track/master gain; `ComputeGainDb`
        turn-up/turn-down/silence/ceiling-caps-boost/ceiling-forces-cut. Audio (`SinePcmReader`): `MeasureSource`
        finite loudness + **+6 LU per amplitude doubling** + zero-duration silent; `MeasureMix` two identical tracks
        **+6 LU** over one (scoped) and unity-master scope ignores master gain; and an **end-to-end normalize** —
        measure a tone, compute the gain to −23 LUFS, boost, re-measure → lands on target. Persistence: clip audio
        gain round-trips. Full suite: **715 tests green** (Core 224, Media 34, Render 50, Audio 44, Playback 52,
        Export 77, Persistence 91, App 143).
    - **✅ Editorial-audio-polish strand DONE — step 30 now complete** (`Sprocket.Core`: `AudioTrack.Pan` + `Audio.PanLaw`
      + pan in the audio plan; `Sprocket.Audio`: pan in the mixer; `Sprocket.Persistence`: additive `TrackDto.Pan`;
      `Sprocket.App`: `MixerFormat`, `Mixer/MixerView`, mixer hosting in the Project panel's Audio tab, live-loudness
      wiring, and track/master/clip **Normalize** actions; 29 new tests — Core 224 → 231, Audio 44 → 46, App 143 → 163;
      full suite 715 → 744). The third and final strand brings the meters + per-track controls + normalization to the
      UI (UI.md §3.3). The Avalonia surfaces rest on build + a `SPROCKET_APP_SECONDS` smoke launch (the App is a
      UI-bound `WinExe`); the pure model / formatting is unit-tested. Delivered:
      - **Pan / stereo balance (Core + Audio + Persistence).** `AudioTrack.Pan` in [-1, 1] (clamped) with a linear
        **balance** law (`PanLaw.Balance`): centre = unity on both channels — so a centred track mixes byte-identically
        to the pre-pan behaviour — and panning attenuates the opposite channel to silence at the extreme. It folds into
        the audio plan as per-layer `AudioLayer.PanLeft/PanRight` (default 1/1 = unchanged) and the mixer's `SumWithRamp`
        applies it per channel on a stereo output (a no-op for mono). Additive `TrackDto.Pan` (`WhenWritingNull`) keeps
        un-panned projects byte-identical.
      - **`MixerFormat` (App, pure).** Gain (`+3.0 dB` / `-∞ dB`), pan (`C` / `L50` / `R100`), LUFS / dBTP read-outs,
        and the 0–1 meter fill fraction — free of any Avalonia control (mirrors `StatusBarFormat`), so all strings and
        fractions are unit-tested.
      - **`MixerView` (App).** The Audio tab is now a mixer: a **master strip** with the live EBU R128 read-out
        (integrated / short-term / momentary LUFS + true peak) and **L/R peak meters** (green→amber→red, peak-hold),
        plus a master fader; and a **channel strip per audio track** with a gain fader, pan/balance, and mute / solo.
        Every edit routes through `EditHistory` — faders open one `BeginCoalescing()` scope for the drag so a whole
        gesture is a single undo entry (the timeline's pattern), mute/solo issue `SetPropertyCommand<bool>` — and undo
        refreshes the widgets without re-issuing commands. The meters poll `AudioEngine.CurrentLoudness` on a ~15 Hz
        `DispatcherTimer` that runs **only while the Audio tab is on screen** (started/stopped on attach/detach), so an
        idle or hidden mixer costs nothing (§1). The live loudness is surfaced by carrying the `AudioEngine` on
        `MediaBootstrap.Result` (a non-owning reference; the engine still owns it) through to the shell.
      - **Loudness normalization actions (App).** A **Normalize to** target picker (−14 / −16 / −23 LUFS) drives
        per-track and master **Normalize** buttons (measure the scope's raw loudness at unity via `LoudnessAnalyzer`
        + a `MediaBootstrap.CreateAnalysisMixer`, compute the true-peak-limited gain via `LoudnessNormalization`, set
        the model gain as one undoable edit), and **Clip ▸ Normalize Audio** normalizes the selected clip's
        `Clip.GainDb` over its used source span to the same target — applied clip-by-clip it is the gain-match pass.
      - **Notes.** Pan is a stereo balance (centre-unity, non-disruptive) rather than a −3 dB constant-power pan, so
        existing centred mixes are unchanged. The strips carry controls but not per-track live meters — the single
        engine meter measures the mixed master (its L/R bars are the required channel meters); per-track live metering
        would need per-track metering in the engine and is a natural later refinement. Clean build (0 warnings); a
        `SPROCKET_APP_SECONDS` smoke launch with the sample clip (audio wired → the mixer's meters live) starts and
        tears down cleanly (exit 0). Full suite: **744 tests green** (Core 231, Media 34, Render 50, Audio 46,
        Playback 52, Export 77, Persistence 91, App 163). **Step 30 is complete.**
      - **✅ FOLLOW-ON (2026-07-27): the strip read-outs are editable.** The gain / pan / master-gain labels
        were display-only `TextBlock`s, so dragging the fader was the only way to set a level — every
        professional mixer lets you click the dB number and type an exact value. They are now compact
        `TextBox` fields (Inspector styling: `PanelBg` fill + `InputEdge` border per STYLE_GUIDE.md) that
        commit on Enter/blur as one discrete undo entry, revert on an unparseable entry rather than silently
        muting a track, and carry the same drag-to-scrub gesture as the Inspector's numeric boxes
        (`Controls/DragNumber`, one undo entry per scrub via the existing fader coalescing scope).
        `MixerFormat` gained the parse side of its labels — `TryParseGainDb` (`-6`, `-6.0 dB`, `+3dB`, and
        `-∞` / `-inf` / `-infinity` → the `SilenceFloorDb` fader floor, now a named constant the slider's
        minimum also reads) and `TryParsePan` (`C`, `L50`, `R25`, or a bare −100..100, the Premiere pan-field
        convention) — both round-trip-tested against the label formatters. **Level stays in dB**, not
        percent: swapping it would break the `-∞` floor, the +12 dB boost range, and the LUFS/dBTP read-outs
        beside it.
## Step 31

31. **Audio effects & plugin hosting (VST3 / AU).** The deeper audio-post layer (loudness
    metering/normalization is the earlier step 30). Give audio an effect chain mirroring video's
    `IVideoEffect` stack: a new Core **`IAudioEffect`** seam and an audio effect chain on audio **clips,
    tracks, sequences, and the master bus**, run by the `AudioMixer` as a per-buffer DSP pass in the
    [ARCHITECTURE §6](ARCHITECTURE.md) audio path (allocation-free on the audio thread, processing blocks
    of float32 at the project rate/layout). Ship a few **built-in managed effects** first (parametric EQ,
    compressor, reverb, gain/pan) so the chain is useful with no native deps, then **host native
    plugins** behind the same seam: **VST3** (cross-platform — Win/Linux/macOS) and **Audio Units**
    (macOS-only). Per the **no-C++/CLI** rule ([ARCHITECTURE §1](ARCHITECTURE.md), [§13](ARCHITECTURE.md))
    each format is reached through a thin **native C-ABI bridge shim** (the VST3 SDK is C++/COM-style and
    AU is Obj-C — each wrapped to a flat C ABI the way the FFmpeg/Skia natives are), one bridge per
    format, bundled per RID alongside the other natives (steps 35–36). Plugins are scanned and
    instantiated **off** the audio thread; the host can open a plugin's own editor GUI in a window.
    **Parameter automation** rides the existing `AnimatableValue` / keyframe mechanism (step 16d), so
    plugin parameters keyframe like any other effect. **Persistence:** an audio effect serializes as
    plugin id + an opaque **state blob** (e.g. VST3 component/controller state) + its automation —
    additive and schema-versioned (§12); a missing plugin loads **offline** (the chain bypasses it)
    rather than failing the load (§15). Builds on the audio mixer (steps 5/7) and the plugin host
    (step 33). **Licensing:** the VST3 SDK is GPLv3-or-Steinberg-dual-licensed — choose the license
    deliberately before distribution (cf. the FFmpeg LGPL/GPL note). A track or chain can also be
    **frozen** (pre-rendered) via the render cache (step 32) so heavy or non-deterministic plugins
    aren't recomputed every playback pass.
    - **🟡 PARTIAL — built-in managed phase DONE; native VST3/AU hosting NOT started** (`Sprocket.Core/Audio/IAudioEffect.cs`
      + `Sprocket.Audio/Effects/*` + mixer/render-graph/persistence wiring; 37 new tests — Core +12, Audio +21,
      Persistence +4, all green). The step's "built-in managed effects first" phase shipped whole; the native
      half was deliberately deferred because it builds on the step-33 plugin host, which doesn't exist yet.
      Delivered:
      - **The `IAudioEffect` seam (Core, §19):** process one block of interleaved float32 in place at the
        project rate/layout + `Reset()`; stateful implementations, driven by the single mixing thread,
        allocation-free in steady state (§1). The pure-data `EffectInstance`/`AnimatableValue` model is reused
        unchanged — an audio chain is an ordered `EffectInstance[]`, keyframeable like any effect (params are
        evaluated **per block** at the buffer start; the sample-accurate in-block ramp of §19 stays a later
        refinement). `EffectTypeIds.IsAudio` (the `builtin.audio.*` prefix) splits a clip's single effect stack:
        audio ids feed the mixer chain, video ids feed the shaders, and **Fade stays the shared gain envelope**
        (not a chain stage) so one fade keeps driving video alpha + audio gain.
      - **All four chain scopes (§19), clip → track → sequence bus → master:** clip chains live on the existing
        `Clip.Effects`; new chain lists on `AudioTrack.Effects` (inserts), `Timeline.AudioEffects` (the
        sequence's output bus — a nested sequence's chain processes the sub-mix its nesting clip receives, keyed
        per nesting clip so two nests of one child keep independent DSP state), and
        `ProjectSettings.MasterAudioEffects` (beside the master gain it precedes). `RenderGraph.PlanAudioBuffer`
        resolves them into the plan (`ResolvedAudioChain` — evaluated params + an identity `StateKey` the
        executor persists DSP state under) and **splits the layer gain** into clip-level (clip gain × fade,
        ramped) and track-fader (static) parts so the mixer can run inserts at the standard **pre-fader** point;
        the combined `GainStartLinear`/`GainEndLinear` remain as computed properties, so every existing
        gain consumer/test reads exactly what it did before. A `UnityMasterGain` measurement scope (step 30)
        bypasses the master chain too.
      - **Mixer execution (§6/§19):** per layer — clip chain → clip gain/fade ramp → track inserts → track
        fader + pan → sum; then per plan — bus/master `OutputChains` → master gain → single top-level hard
        limit. Chain-less layers take the **byte-identical pre-31 fast path** (one combined summing ramp).
        Stateful effect instances are cached per `StateKey` and rebuilt only when the chain's ids change, so
        filter memory/envelopes/tails carry across buffers (deliberately not reset on seeks — tails settle in
        ms, matching NLE behaviour); unknown ids pass through, mirroring the video pipeline (§15). Export and
        loudness analysis inherit the chains for free (both drive this mixer).
      - **Four built-in managed effects (`Sprocket.Audio/Effects`, pure C#, deterministic):** **Gain/Pan**
        (dB + the step-30 `PanLaw` balance), **Parametric EQ** (three-band low-shelf / mid-peak-with-Q /
        high-shelf, RBJ-cookbook biquads per channel, coefficients recomputed only on param change, 0 dB bands
        bypassed exactly), **Compressor** (stereo-linked peak envelope, one-pole attack/release, hard-knee
        threshold/ratio + make-up — the textbook feed-forward design), and **Reverb** (Freeverb: 8 damped
        combs + 4 allpasses per channel, standard 44.1 kHz tunings scaled to the project rate, +23-sample
        stereo spread; `mix = 0` is an exact pass-through). All registered in `EffectCatalog` under
        `EffectCategory.Audio` with typed parameter descriptors, so the Effects browser lists them and the
        type-driven Inspector edits them (clip scope works end-to-end in the UI today via the existing
        add-effect flow).
      - **Persistence (§12):** additive + nullable `effects` (audio tracks), `audioEffects` (timeline bus), and
        `masterAudioEffects` (settings) DTO fields — no schema bump; empty chains write nothing, so pre-31
        files load with none and chain-less projects serialize **byte-identically**. New
        `AddChainEffectCommand`/`RemoveChainEffectCommand` (Core) make track/bus/master chain edits undoable
        with position-preserving undo (clip scope reuses `AddEffectCommand`).
      - **Tests (37):** plan resolution (per-scope chains + state keys, audio/video id filtering, split-gain
        composition, keyframed per-block evaluation, unity-scope bypass, nested-bus keying, command
        apply/revert); DSP units (gain/pan math, EQ pass-through / shelf boost / mid cut / cross-block state
        continuity, compressor above/below threshold + make-up, reverb tail + reset); mixer integration
        (each scope applies, **pre-fader insert order proven** via a compressor that must see the signal before
        the fader, clip fade feeding the track chain, unknown-id pass-through, bus tail ringing across buffers
        past the clip's end, chain-less fast path unchanged); persistence round-trips + the byte-identical
        empty-chain check. Full suite: **781 tests green** (Core 243, Media 34, Render 50, Audio 67,
        Playback 52, Export 77, Persistence 95, App 163); clean build (0 warnings), smoke launch exit 0.
      - **Still outstanding (blocked on / sequenced with step 33):** the native **VST3 / AU hosting** via
        per-format C-ABI bridge shims (`sprocket_vst3host` / `sprocket_auhost`), plugin scan/instantiate off
        the audio thread, plugin editor GUI embedding, **plugin delay compensation**, the opaque state-blob
        persistence + offline-plugin bypass, per-RID bridge bundling (steps 35–36), and the VST3 SDK licensing
        decision.
      - **✅ FOLLOW-ON DONE (2026-07-06 — track/bus/master chain UI, the "mixer-panel inserts" item formerly
        on the outstanding list above; `Sprocket.App/Mixer/{AudioChainTarget,MixerView}` +
        `Inspector/InspectorPanel` + `EffectRelevance.ForAudioChain` + MainWindow wiring; 15 new tests —
        App `AudioChainTargetTests` +14, `EffectRelevanceTests` +1; full suite 1340 green, 0 warnings, smoke
        launch OK.)** Follows the leading-editor audio track mixer convention (per-strip insert slots; deep editing
        elsewhere): every mixer channel strip, a new **Sequence Bus** pseudo-strip, and the master panel carry
        an **Inserts** block — "+" flyout of the catalog's audio effects (`AddChainEffectCommand`), per-insert
        enable LED (`SetEffectEnabledCommand`), remove (`RemoveChainEffectCommand`), and Move Up / Down
        context menu (`MoveChainEffectCommand`) — and clicking an insert opens the chain in the **Inspector**
        (`InspectChainRequested` → `SetSelectedChain`), whose per-effect sections were generalised from the
        clip stack (a `ChainContext` carries the chain list, remove command, and mixer DSP state key), so
        chain effects get the full parameter/keyframe/preset UI **and live compressor metering** (the new
        `AudioChainTarget.StateKey` matches the render graph's chain keying — track object / timeline /
        settings — so `AudioMixer.TryPeekEffect` works at chain scope). Chain keyframe lanes span the whole
        sequence (chain automation is in sequence time). A stale target (track removed by undo, sequence
        switched) is dropped via `AudioChainTarget.IsAlive`; a timeline deselect keeps the chain view, a real
        clip selection replaces it. Mixer insert rows rebuild only when `AudioChainTarget.Signature` (chain
        identities + effect refs + enabled flags) changes, so fader-drag command streams don't tear strips
        down mid-gesture. Heavy-tail hints at chain scope say "recomputed on every playback pass" (freeze is
        clip-scoped; track-scope freeze remains deferred, see step 41).
## Step 32

32. **Preview render cache (pre-render / "freeze").** Expensive subgraphs — nested sequences
    (step 23), adjustment-layer spans (step 19), deep effect chains, and audio plugin chains
    (step 31) — shouldn't be recomputed every playback pass. Because the render graph is a **pure,
    deterministic function of (project, t)** with no hidden state ([ARCHITECTURE §5](ARCHITECTURE.md),
    [§6](ARCHITECTURE.md), §1.6), a computed range can be cached and replayed, then invalidated when the
    edit that produced it changes. The cache reuses the existing seams: a rendered range is exposed back
    to the parent graph as **just another `IFrameSource`** (video — rendered to a fast all-intra
    intermediate via `MediaEncoder`, or a short GPU texture ring) / **`IPcmReader`** (audio — cached PCM,
    i.e. "freezing" a track, valuable for non-deterministic native plugins), the same seam media, proxies
    (§17) and nested sequences already use — so **no new render-graph machinery**. Intermediates are
    encoded for **speed, not size** — all-intra and **hardware where available**, and the codec **may
    vary by host OS** (e.g. ProRes/VideoToolbox on macOS, NVENC/QSV on Windows, VAAPI on Linux, MJPEG /
    x264 *ultrafast* as the cross-platform fallback; audio as uncompressed PCM) since the cache is local
    and regenerable — with **no effect on export determinism** ([ARCHITECTURE §11](ARCHITECTURE.md)
    "Preview vs. delivery codecs", §1.6). Cache entries are keyed
    by a **content hash of the cached subtree's serializable state** (the persist DTO, §12) + range +
    render settings; any model edit (always via the command stack, §4) re-hashes and marks the affected
    range **dirty** (exact invalidation, no stale frames). A **render bar** over the ruler shows rendered
    vs. needs-render ranges (green/yellow/red), with *Render In to Out* / *Render Selection* /
    *Render Audio* / *Delete Render Files* commands. The cache is a **local derived artifact** kept in a
    cache dir beside the project (not in the diffable project file, not merged — cf. step 28) and is
    always **safely discardable**. **Export ignores the preview cache by default** and re-renders full-res
    originals (§17) so output stays deterministic; reusing a full-quality cache is an opt-in. Lands on the
    done render graph + the `IFrameSource` / `IPcmReader` seams; full value comes once sequences (step 23)
    and audio effects (step 31) exist, hence its place here, but the video side can ship with step 23.
    [ARCHITECTURE §20](ARCHITECTURE.md).
    - **✅ DONE (Core `Rendering/RenderCacheSeams.cs`; `Sprocket.Playback/PlaybackEngine` cache player;
      `Sprocket.Audio/AudioEngine` feeder splice; `Sprocket.Export/{PreviewRenderer,WavePcm}` + `ExportOptions`
      additions; `Sprocket.Persistence/RenderCacheHasher`; App `RenderCache/{RenderCacheService,RenderCacheStore,
      RenderBarModel}` + `TimelineControl` render bar/in-out marks + `MainWindow` Sequence-menu commands; 40 new
      tests — Persistence +11, Export +9, Playback +4, Audio +2, App +14; full suite **851 green** (Core 250,
      Media 34, Render 59, Audio 69, Playback 56, Export 86, Persistence 106, Plugins 10, App 181); clean build
      (0 warnings), smoke launch exit 0.)** Memoization of the pure render graph, landed on the existing seams:
      - **Core seams (§20).** `IVideoRenderCache` (position → the valid `CachedRenderSegment` covering it: a
        content-addressed synthetic source id + range + intermediate file) and `IAudioRenderCache` (fill a feeder
        buffer with cached master-mix PCM, whole-buffer coverage or refuse). The render graph itself is untouched
        — only playback consults the seams, and **export never does** (§17, determinism verified by the suite's
        untouched export round-trips).
      - **Keying & exact invalidation (Persistence).** `RenderCacheHasher.ComputeHash(project, sequence, range,
        scope)` — SHA-256 over the range's **serializable state** (the same DTOs that persist, §12), filtered to
        what can affect the output: overlapping clips + overlapping-window transitions per scope-matching track,
        track/bus/master state (audio scope), every reachable nested sequence / multicam source (hashed whole,
        transitively, cycle-safe), and referenced **media identity** (path+size+mtime, so a replaced source file
        invalidates). Proven properties: edits outside the range or in the other scope leave the hash unchanged
        (an audio gain ride never dirties a video render), and **undo re-validates without re-rendering** —
        validity is `storedHash == currentHash`, recomputed (debounced) off the step-10 `EditHistory.Changed`.
      - **Rendering (Export).** `PreviewRenderer` drives the identical deterministic offline pipeline export uses
        (same render graph, full-res originals, never proxies): video → **all-intra** (GOP 1) H.264/MP4 at the
        **ultrafast** preset with the platform **hardware encoders probed first** (NVENC/QSV/AMF / VideoToolbox /
        VAAPI, software fallback — the OS-varying speed-first policy of §11), **video-only** (two additive
        `ExportOptions` fields: `Preset`, `VideoOnly`); audio → the master mix as **uncompressed float32 WAV**
        (`WavePcmWriter`/`WavePcmReader`, pure managed I/O — bit-identical to a live mix, no codec). All-intra
        verified by a mid-file seek landing on target; a cancelled/failed render deletes the partial file.
      - **Playback splice.** `PlaybackEngine.RenderCache`: inside a valid segment the engine pumps a single
        synthetic **cache player** decoding the intermediate through the normal native ring (pixels stay native,
        §1) while the per-track decoders idle; `UseLayers` presents the cached frame as the one composited layer
        (effects/opacity baked in). Boundary crossings re-seek whichever side resumes; an invalidated segment
        stops resolving on the next pump; an unreadable file degrades to live compositing (§15). This is what
        makes **nested sequences and transition blends preview at full fidelity once rendered** (the step-23/25
        deferrals). `AudioEngine.RenderCache`: the feeder replays cached PCM when a buffer is fully covered, else
        mixes live — the `IPcmReader` seam step 41's heavy reverb freeze consumes.
      - **Storage (App `RenderCacheStore`).** A cache dir **beside the project** — `Sprocket Render Files/<project
        name>/` (the leading NLEs convention; per-user fallback for untitled projects, `SPROCKET_RENDER_CACHE_DIR`
        override) — holding the intermediates + a small manifest (scope, sequence, range, hash, file). Local,
        derived, never in the project file, **safely discardable**; a reopened project's still-matching renders
        validate immediately with no re-render. Atomic manifest writes; orphaned files swept best-effort.
      - **Surface (App).** The **render bar** over the timeline ruler (`RenderBarModel`, pure + tested): green =
        valid cached render, red = un-rendered nests/transition windows (can't fully composite live), yellow =
        un-rendered effect chains / adjustment layers. **Timeline in/out marks** (I / O at the playhead, Alt+I/O
        clear, drawn as a shaded ruler range — session UI state, cleared on sequence switch). *(2026-07-11)* The
        marks also drive **Play In to Out** (Ctrl+Shift+Space, Sequence menu — `PlaybackEngine.PlayInToOut` plays
        the marked range and parks at the out mark; plain Space stays unconstrained, the leading-editors
        convention) and the export dialogs' **Range** selector (see step 29). Sequence menu:
        **Render In to Out** (marks, else the whole sequence), **Render Selection**, **Render Audio**, and
        **Delete Render Files…** — which shows the cache's **current disk footprint** in the menu item and the
        confirm dialog (a small deliberate addition beyond the step text, matching leading editors' cache UI).
        Renders run like export: quiesce every in-process decode pipeline (`SuspendAsync` — the ProxyTranscoder
        muxer hazard), background task + cancellable progress dialog, resume; the committed segment plays back on
        the next pump and re-rendering an unchanged range short-circuits ("already rendered").
      - **Deferred (documented, on the same seams):** the **opt-in export reuse** of a full-quality matching cache
        (export ignores the cache unconditionally today); the **bounded GPU texture ring** for short scrub ranges
        (§20 lists it as an alternative store — the disk intermediate covers both today); per-clip/track **freeze
        commands + Inspector badges** (step 41 consumes the delivered audio seam); OS-specific intermediate
        codecs beyond the hardware-candidate probe (ProRes/MJPEG alternates); and relocating the cache dir on
        Save-As mid-session (the new location is picked up on the next open; the old cache is discardable).
## Step 33

33. **Plugins & advanced color management.** Plugin host (collectible `AssemblyLoadContext`,
    [ARCHITECTURE §13](ARCHITECTURE.md)), then OpenColorIO / ACES / OFX scene-linear color management.
    (The creative color-grading toolset — wheels, curves, qualifiers, scopes — is its own step, 34.)
  See [COLOR_GRADING_ROADMAP.md](COLOR_GRADING_ROADMAP.md) for the detailed parity sequence, acceptance
  criteria, validation strategy, and post-parity grading features that extend steps 33, 34, 37, and 52.
    - **🟡 PARTIAL — plugin host DONE; native OCIO / OFX / VST3 hosting outstanding
      (`src/Sprocket.Plugins/*`; `Sprocket.Core/Rendering/IVideoEffect.cs` + `Core/Audio/IAudioEffectProvider.cs`;
      `Sprocket.Render/Effects/AcesFilmicEffect.cs`; `Sprocket.App/PluginService.cs`; 30 new tests — Core +7,
      Render +9, App +4, Plugins +10; full suite 811 green, 0 warnings, smoke launch exit 0).** Delivered:
      - **`IVideoEffect` contract (Core, §13):** declarative — catalog `EffectDescriptor` (typed params → the
        Inspector UI falls out), SkSL source (`uniform shader src;`, premultiplied colour), and a per-frame
        `BindUniforms` over the GPU-agnostic `IUniformWriter`. Render owns compile/execute:
        `SkiaEffectPipeline.RegisterEffect/UnregisterEffect` is a process-wide registry (validated compile at
        registration; per-pipeline compiled cache; a faulting/unregistered effect degrades to pass-through per
        §15) that the effect-chain `switch` falls back to — so plugins never touch SkiaSharp and render
        identically in preview and export (§5/§7). `IAudioEffectProvider` (Core) is the audio analogue:
        descriptor (category **Audio** — `EffectTypeIds.IsAudio` now consults the catalog so plugin audio ids
        route to the mixer chain) + a factory for stateful `IAudioEffect` DSP instances, injected into
        `AudioMixer`'s existing `effectFactory` seam (plugins first, then built-ins).
      - **Plugin host (`Sprocket.Plugins` → Core only):** `PluginLoadContext` (collectible ALC,
        `AssemblyDependencyResolver` isolation, `Sprocket.*` deferred to the default context so contract type
        identities unify) + `PluginHost` (interface scan for public parameterless-ctor implementations;
        per-file/per-type failures recorded in `Errors`, never thrown; reserved `builtin.` ids rejected;
        `Unload` verified collectible by test). Discovery from `<exe>/Plugins` and `%APPDATA%/Sprocket/Plugins`,
        wired by `Sprocket.App/PluginService` at startup (broken plugins log to the crash log and are skipped).
        `EffectCatalog` grew `Register`/`Unregister`/`All`; browsers, the Effects menu, and the Inspector draw
        from `All`, so plugin effects appear everywhere built-ins do (and persistence already round-trips
        unknown ids — a project using an uninstalled plugin loads with those effects passing through).
        End-to-end proven by a real separate plugin assembly (`tests/Sprocket.TestPlugin`, loaded through the
        ALC path only) and an app smoke launch with it installed.
      - **ACES color management (first slice):** a built-in **ACES Filmic** effect (`builtin.aces.filmic`,
        keyframeable exposure) — sRGB → scene-linear → exposure → the fitted ACES RRT+ODT tone curve (Stephen
        Hill approximation) → sRGB, premultiplied-safe, entirely in SkSL. Deliberately implemented **through
        the new registry** (not a hard-coded pipeline case) so the plugin seam is exercised by a built-in.
      - **Effect-relevance filtering (UX fix, alongside):** the Inspector's `+ Effect` flyout and the Effects
        menu now offer only the effects that apply to the selected clip's track kind (`EffectRelevance`):
        audio-track clips get the audio DSP stages, video-track clips the video/colour shader stages —
        cross-kind effects would silently no-op, so they're no longer offered.
      - **Still outstanding:** hosting a full **OpenColorIO/ACES config** (native lib via a C-ABI P/Invoke
        wrapper — per-RID bundling belongs to steps 35–36) with working-space management beyond the built-in
        fit; an **OFX / frei0r** C-ABI adapter; the native **VST3/AU audio plugin bridges** + plugin editor
        GUIs + delay compensation + opaque state-blob persistence (carried from step 31); plugin
        unload/reload UI (plugins currently load for the app's lifetime) and a Manage Plugins panel.
## Step 34

34. **Color grading.** A professional grading toolset on top of the step-16 `Color` effect, all as
    SkSL effect-chain stages (§7) so preview and export stay identical and GPU-resident (§1, §5):
    **lift / gamma / gain color wheels** (shadows / mids / highlights), **RGB + per-channel curves**,
    **HSL secondaries / qualifiers** (key a hue/sat/luma range and grade only that), **white balance**
    (temp / tint), and saturation / vibrance — each a new built-in `IVideoEffect` registered in
    `EffectCatalog`, keyframeable via `AnimatableValue` (step 16d) and edited in the type-driven Inspector
    (step 16). Reference **scopes** — waveform / vectorscope / RGB parade / histogram — computed from the
    rendered frame (extending the step-17 monitor scopes) to grade against. Composes with the input color
    transform / log handling (step 37) and the advanced OCIO / ACES color management (step 33). Lands
    entirely on the existing effect seam ([ARCHITECTURE §7](ARCHITECTURE.md), [§17](ARCHITECTURE.md)) — no
    render-graph redesign; builds on the done effect pipeline, so it can be pulled earlier if prioritized.
    - **✅ DONE (`Sprocket.Render/Effects/{WhiteBalance,ColorWheels,Curves,HslQualifier}Effect.cs`;
      `Sprocket.Render/Scope{Analyzer,Renderer}.cs`; `Sprocket.App/ScopeView.cs`; full suite 893 green,
      0 warnings, app smoke launch OK).** Delivered, all on the step-33 `IVideoEffect` registry path (no
      new pipeline cases — the plugin seam carries the whole toolset) and each keyframeable/persisted/
      Inspector-edited for free via the type-driven descriptor model:
      - **White Balance** (`builtin.whitebalance`): temp/tint gains in linear light (Lumetri's ±100
        slider convention), luma-normalised so the correction is chromatic-only.
      - **Color Wheels** (`builtin.colorwheels`): lift/gamma/gain × (master + R/G/B) with the standard
        video LGG transfer — lift pivots on white, gain on black, gamma a mids power curve (the
        color-wheel semantics standard in leading editors).
      - **Curves** (`builtin.curves`): RGB master + per-channel red/green/blue **parametric** curves —
        five points (blacks/shadows/mids/highlights/whites at fixed inputs) offsetting the identity,
        Catmull-Rom interpolated in the shader. Deliberate departure: the parametric (Lightroom-style)
        form instead of a freeform point list, because it keeps every point an animatable scalar the
        existing `AnimatableValue`/Inspector model already handles.
      - **HSL Qualifier** (`builtin.hsl.qualify`): softened hue/sat/luma key with hue-shift / saturation /
        exposure corrections applied through the mask, plus the standard Show Mask matte preview.
      - **Vibrance** added to the step-16 `Color` effect (saturation weighted toward muted colours;
        absent param defaults neutral, so old projects are unchanged).
      - **Grading scopes** (extending the step-17 monitor): **waveform / RGB parade / vectorscope /
        histogram**, selected from the monitor header, drawn in a panel under the picture. The monitor
        surface samples its own composited output inside the same GPU lease (snapshot → ≤256-wide
        persistent raster sample → `ScopeAnalyzer` bins, arrays reused per frame — one bounded readback,
        no per-frame managed pixel buffers), so the scopes measure exactly what the monitor shows,
        grade/transitions/adjustment layers included; `ScopeRenderer` draws the bins from one persistent
        trace bitmap with graticules.
      - Tests: Core catalog/neutral-default coverage, Render centre-pixel math for all four effects +
        vibrance (incl. legacy-project pass-through), pure `ScopeAnalyzer` binning (waveform split,
        parade, vectorscope quadrants, stride padding, bin reuse) and `ScopeRenderer` offscreen smoke.
      - **Still outstanding (UI polish, not render seams):** bespoke 2D wheel / curve-editor Inspector
        widgets (wheels and curve points are edited today as the type-driven sliders every other param
        uses) and export-side scope overlays.
## Step 35

35. **Cross-platform native-lib bundling.** Make the build self-contained per RID: copy the FFmpeg 8
    `.dll`/`.so`/`.dylib` set and `SkiaSharp.NativeAssets.{Win32,Linux,macOS}` + OpenAL Soft natives
    into the publish output for `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64` so the app runs with no
    system FFmpeg ([ARCHITECTURE §11](ARCHITECTURE.md)). Needed for the slice to *run* on Linux/macOS
    at all; promoted to its own step because it gates every on-device verification.
    - **✅ DONE (`scripts/release.ps1` + `.github/workflows/release.yml`).** Self-contained **folder**
      publish per RID (single-file was dropped for step 36's Velopack packaging, which diffs the folder
      for delta updates), FFmpeg 8 natives bundled next to the exe (BtbN for win/linux; **Homebrew via
      `scripts/macos-bundle-ffmpeg.sh` on macOS** — full transitive dylib closure, `@loader_path`
      rewrite, ad-hoc re-sign, hard libavcodec-62 assert), SkiaSharp natives via the NuGet native-asset
      packages, and **OpenAL Soft now bundled on every RID** via `Silk.NET.OpenAL.Soft.Native` (the
      folder publish lands them loose; the one fix-up is renaming Linux's `libopenal.so` to the
      `libopenal.so.1` name Silk.NET probes — before this, Linux silently fell back to system OpenAL /
      the software clock). A headless `--audio-check` flag (Program.cs) proves the OpenAL native
      resolves, mirroring `--ffmpeg-check`. **CI matrix builds exist:** the tag-triggered release
      workflow builds every RID on its native OS runner and smoke-checks each artifact.
## Step 36

36. **Packaging & distribution (incl. macOS executable).** Produce a runnable artifact per OS: a
    Windows folder/installer, a Linux AppImage/tarball, and a **macOS `.app` bundle** with the FFmpeg
    dylibs under `Contents/Frameworks` (resolved via `@loader_path`), **code-signed and notarized**,
    shipped for Apple Silicon (`osx-arm64`) and Intel (`osx-x64`). CI builds on win/linux/macOS runners;
    a smoke launch + sample export validates each artifact.
    - **✅ DONE except code-signing/notarization (deliberately deferred).** Packaging is **Velopack**
      across all three OSes (`vpk` pinned to the Velopack NuGet version; packId `Sprocket`, bundleId
      `org.sprocketvideo.sprocket`, one update channel per RID): `scripts/release.ps1 -Package velopack`
      produces the Windows **Setup.exe**, the Linux **AppImage** (desktop entry + icon embedded;
      `packaging/linux/` scripts still ship in the portable zip), and the macOS **`.app`** (icns
      generated on the fly from the 1024px PNG; FFmpeg dylibs `@loader_path`-resolved beside the
      binary) — plus full/delta update packages and `releases.<rid>.json` feeds. **Auto-update** is in
      the app (`UpdateService.cs` on Velopack `UpdateManager` + GitHub releases; Install & Restart from
      the update dialog). **Linux AppImage desktop integration** is also in-app
      (`LinuxDesktopIntegration.cs`): because an AppImage never registers itself, a running AppImage
      offers on first launch to add Sprocket to the applications menu (writing
      `~/.local/share/applications/sprocket.desktop` with `Exec` resolved from `$APPIMAGE`, plus the
      hicolor/pixmaps icon, then refreshing the caches) — the in-app equivalent of the portable zip's
      `packaging/linux/install.sh`; the same add/remove actions live under **Help** and the one-time
      offer is remembered in `UserSettings.LinuxDesktopIntegrationPrompted`. The tag-triggered CI matrix (`.github/workflows/release.yml`, cut locally by
      the `scripts/gh-release.ps1` tag-cutter) builds win/linux/macos (arm64 + `macos-15-intel`) on
      native runners, **smoke-launches every artifact headlessly** (`--version` / `--ffmpeg-check` /
      `--audio-check`; Windows additionally does a real silent install), and a single final job
      publishes the GitHub release with all assets. **Deferred, still outstanding:** Windows
      code-signing and macOS notarization (alpha ships unsigned with documented SmartScreen/Gatekeeper
      steps in RELEASE_NOTES.md); a `linux-arm64` AppImage (zip-only until an arm runner smokes it);
      the sample-export CI validation (smoke is launch + native checks today).
## Step 36a

36a. **Third-party notices / license acknowledgements.** Before distribution, inventory every third-party
    library, bundled native binary, codec build, font, icon, sample-media asset, and other redistributable
    resource Sprocket ships; record license, copyright/notice text, source URL, and any source-offer or
    attribution obligations. Generate a packaged `THIRD-PARTY-NOTICES` / `NOTICE` artifact alongside the
    installers and expose it from **Help ▸ Third-Party Notices** (or an equivalent **Third-Party Notices** /
    **Licenses** button inside **Help ▸ About**, whichever best matches the final platform conventions).
    Keep the main About view focused on product name/version (step 16c); the notices surface is for
    compliance details, including FFmpeg LGPL/GPL build choices and any bundled assets.
    - **✅ DONE (inventory + in-app surface).** [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) at the
      repo root inventories every bundled NuGet package, native library (FFmpeg 8, OpenAL Soft), font
      (Liberation), transcribed icon set (Feather), and bundled media (DJI D-Log LUTs, the Pixabay sample
      clip) with license + source URL. It is copied next to the exe by `Sprocket.App.csproj` (so it rides
      along in every `scripts/release.ps1` bundle automatically — no separate packaging step needed) and
      surfaced via **Help ▸ Third-Party Notices** (`Dialogs.cs` `ThirdPartyNoticesDialog`), reading the
      shipped copy by path the same way the bundled sample clip is resolved. **GPL-vs-LGPL decided
      (2026-07-03): keep the GPL builds.** Sprocket is MIT (GPL-compatible), so bundling GPL-configured
      FFmpeg (BtbN `gpl-shared` on win/linux, Homebrew's build on macOS) is fine and keeps the
      libx264/libx265 export encoders; the GPL §3 obligation is met by the **FFmpeg source availability**
      section in THIRD-PARTY-NOTICES.md (links the corresponding BtbN source tarballs / Homebrew formula
      upstream). Revisit only if proprietary distribution is ever considered — that is also the moment
      to take legal advice on H.264/HEVC patent-pool licensing.
## Step 37

37. **Log media & color management (D-Log).** Support DJI **D-Log / D-Log M / D-Log 2** as a
    per-clip **input color transform**, landing on the existing effect seam
    ([ARCHITECTURE §18](ARCHITECTURE.md), §7, §17) — **not** via FFmpeg's `lut3d`/`WriteableBitmap`,
    which would break [§1](ARCHITECTURE.md) (managed per-frame pixels + CPU round-trip) and
    [§5](ARCHITECTURE.md) (preview/export divergence). All color math stays on the GPU in Skia,
    like brightness/fade. Pieces:
    - **Metadata probe (Media).** Extend `ProbedMediaInfo` with color transfer / primaries / space
      and a format-metadata dictionary; read them in `MediaSource.Probe` from the codec parameters
      and `AVFormatContext`/`AVStream` metadata. Auto-detect the DJI log profile on import; fall
      back to a manual per-clip tag.
    - **Color-transform effect (Core + Render).** New built-in effect id `builtin.colortransform`
      (params: source profile, target space, bypass) added to `EffectTypeIds`; an SkSL stage in
      `SkiaEffectPipeline` that samples a **3D LUT packed into a 2D texture** (trilinear) supplied as
      a `uniform shader` child — chained like brightness/fade. The detected transform is
      **prepended** to the clip's effect stack so the input transform runs first.
    - **LUT bundling.** DJI official `.cube` files as `EmbeddedResource`s in `Sprocket.Render`,
      decoded once into the packed LUT texture and cached (first data-asset precedent; today all
      effects are inline SkSL strings).
    - **Inspector (depends on step 16).** A COLOR-section "Input transform / color space" control to
      set or override the per-clip log profile; auto-set from detection.
    - **Export.** Bake the transform in (default, via the same render graph) or pass through the log
      encoding — a per-export toggle.
    - **Scopes (with/after step 17).** A log ↔ transformed toggle for waveform/monitor so colorists
      can read either space.
    - **Persistence.** The effect serializes for free via the existing `EffectInstance` JSON; the
      new `ProbedMediaInfo` color fields are additive (nullable/defaulted, no schema bump).
    - **Upgrade path.** Full scene-linear / OpenColorIO color management remains the later step-33
      upgrade ([ARCHITECTURE §18](ARCHITECTURE.md)).
    - **✅ DONE (Media natives + probe, Core model/commands/graph, Render LUT stage, App auto-detect +
      Inspector + export dialog, Export toggle, Persistence; 37 new tests — Core +16, Render +14, Media +3,
      Persistence +1, Export +1, App +2; full suite 1002 green, 0 warnings, smoke launch clean).**
      - **Metadata probe.** `AVCodecParameters.color_range/primaries/space` + `AVStream.metadata` offsets and
        `av_dict_get` / `av_color_*_name` added to the hand-rolled binding (exactly the surface
        `Native/FUTURE_BINDINGS.md` recorded); `MediaSource.Probe` now records the four color names (`""`
        when undeclared) and scans the merged container+stream metadata dictionary for a DJI log profile —
        five additive `ProbedMediaInfo` fields incl. `DetectedColorProfile`. Detection policy is the pure
        `ColorProfiles.DetectDjiLog` in Core (normalised "dlog"/"dlogm" substring match over tag values,
        most-specific wins) — conservative by design; leading editors have no auto-detect at all, so the
        manual tag remains the professional-parity fallback. Metadata dict is consumed at probe time, not
        stored (keeps `ProbedMediaInfo` record equality; nothing needed it persisted).
      - **Effect.** `builtin.colortransform` (catalog: "Input Color Transform", Color category; one numeric
        `sourceProfile` index into the append-only `ColorProfiles.All`) — a hard-coded pipeline case, the
        first effect to bind a **texture child**: DJI's official `.cube` (33³) parsed once by `CubeLut`,
        packed N blue slices side-by-side into an RgbaF16 `SKImage` (linear-filterable everywhere, keeps the
        "Not-Clipped" out-of-range samples), sampled by SkSL with hardware bilinear in-slice + manual slice
        lerp (trilinear), unpremul→LUT→repremul. Bypass = the standard effect `Enabled` toggle; target space
        fixed at Rec.709 until step 33.
      - **LUT bundling.** `Sprocket.Render/Luts/*.cube` as `EmbeddedResource`s (the step-40 font precedent;
        provenance + DJI download URLs in `Luts/NOTICE.md`), decoded/cached process-lifetime by `ColorLuts`.
        The D-Log expectations are cross-checked in tests against DJI's public whitepaper math (linearise →
        D-Gamut→Rec.709 matrix → 709-encode).
      - **Auto-detect → prepend.** `ClipPlacement.PrependDetectedColorTransform` builds the transform into a
        new video clip (index 0) before its `AddClipCommand` on both placement paths (bin drag + bootstrap),
        so undo removes clip+transform together. New `InsertEffectAtCommand` gives the Inspector's manual
        "+ Effect" path the same runs-first position (stack order is processing order; nothing modeled
        "prepend" before).
      - **Inspector.** The effect's section renders a Source Profile dropdown (`ColorProfiles.DisplayNames`)
        instead of the auto slider row — the manual per-clip tag / override.
      - **Export.** `ExportOptions.BakeColorTransform` (default true) + a "Bake input color transform
        (log → Rec.709)" checkbox in Export Settings; pass-through is pure plan surgery via the new
        `RenderGraph.StripEffects(plan, id)` (recurses nested plans + both transition sides, reuses
        untouched instances) — the project and the shared preview path are never touched (§5).
      - **Deliberate departures:** PLAN said "D-Log / D-Log M / D-Log 2" — **D-Log 2 does not exist** as a
        DJI profile (no whitepaper, no LUT, no product references), so the shipped set is D-Log + D-Log M;
        `ColorProfiles.All` is append-only for when DJI adds one. **Deferred:** the scopes log↔transformed
        toggle (scopes tap the finished composite; a pre-transform view needs a second tap point — small,
        standalone follow-up), an Inspector "COLOR" badge in the media bin, and HDR *output* tagging on
        export (`AVCodecContext` color fields, noted in FUTURE_BINDINGS).
## Step 38

38. **AI control via an application-hosted MCP server (off by default).** Host an in-process
    [Model Context Protocol](https://modelcontextprotocol.io) server inside `Sprocket.App` so an
    external AI client (e.g. Claude) can drive the editor — inspect the project and issue edits —
    over a local connection. **Disabled by default**; both the **enabled** toggle and the **listen
    port** are user-configurable in **application settings**. This is a new capability landing on
    existing seams, not a rewrite ([ARCHITECTURE §17](ARCHITECTURE.md)). Pieces:
    - **Global Settings / Preferences dialog (prerequisite — new).** There is no app-level
      preferences mechanism today (the only `Settings` is `Project.Settings`, which is per-project
      and lives in the `.sprocket.json` file). Introduce a **user-scoped** settings store persisted
      to the platform's per-user config location (e.g. `%AppData%` / `~/.config` /
      `~/Library/Application Support`), separate from the project file, and surface it in a
      cross-platform **Settings / Preferences** dialog. Initial entries / actions should cover:
      clearing local derived artifacts (**proxy cache** and **render cache**), **video-export
      metadata defaults** (author/copyright/comment/title defaults applied by the export dialog / queue),
      an **autosave interval** control if the fixed debounce from step 20 needs to become user-tunable,
      and, once the MCP server for this step is implemented, an **MCP enabled** toggle plus an
      **MCP listen port** control.
    - **MCP server.** A new component (e.g. `Sprocket.Mcp`, referenced by `Sprocket.App`) exposing
      the official **C# MCP SDK** (`ModelContextProtocol`) over a local transport, bound to
      **loopback**. Started/stopped purely from the settings toggle — **never auto-started**; a
      port change restarts the listener.
    - **Tools route through the command stack (§4 / step 10).** Every state-changing MCP tool
      issues `IEditCommand`s through `EditHistory`, so AI-driven edits are **undoable by
      construction** and share the UI's validation; read-only tools expose project / timeline /
      media-pool / playhead state. Model mutations marshal to the UI thread (the thread that owns
      the model, §8); decode/render/audio threads are untouched.
    - **Security.** Off by default, loopback-only, and clearly indicated in the UI while running so
      the user knows the app is externally controllable; no remote/network exposure in this step.
    - **✅ DONE (new `src/Sprocket.Mcp` + `tests/Sprocket.Mcp.Tests`; `Sprocket.App/{UserSettingsStore,
      UserSettingsFile,PreferencesFormat,PreferencesDialog,McpEditorSession,McpServerService}.cs`;
      62 new tests — App +32 (`UserSettingsStoreTests`, `PreferencesFormatTests`), Mcp +28
      (`RuntimeIdsTests`, `StateFormatterTests`, `SprocketToolsTests`, `McpEndToEndTests`), Export +2
      (`ExportMetadataTests`); full suite **1064 green**, 0 warnings, smoke launch exit 0.)** An external
      AI client can now inspect and edit the open project over loopback MCP, and the app gained its
      user-scoped Preferences. Delivered:
      - **Preferences (the prerequisite):** a `UserSettings` record persisted to
        `%AppData%/Sprocket/settings.json` (tested store `UserSettingsStore` + thin `UserSettingsFile`,
        the `ExportPresetStore`/`WindowStateStore` split), surfaced in a code-built **Edit ▸ Preferences…**
        (Ctrl+,) dialog: **clear proxy cache** (new `ProxyCache.SizeBytes/DeleteAll`) and **render cache
        (this project)** with live sizes; **export-metadata defaults**; **autosave interval** (1–600 s,
        fed to `AutosaveService`'s existing ctor param); and the **MCP controls** — enable toggle, port
        (default **41008**), optional **bearer-token requirement** (token generated on first enable,
        preserved on disable), and a **Copy setup command** button producing the paste-ready
        `claude mcp add --transport http sprocket http://127.0.0.1:<port>/mcp [--header …]` line.
      - **Export metadata defaults:** `ExportOptions.MetaTitle/Author/Copyright/Comment` → written by
        `MediaEncoder` via the already-bound `av_dict_set` (after the provenance tags, so a user comment
        wins) with FFmpeg generic keys (`title`/`artist`/`copyright`/`comment`); export-dialog "Metadata"
        section prefills from the defaults; verified end-to-end against the `ffmpeg -i` banner. The stored
        defaults ship as **resolvable `{token}` templates** (`Title = {project}`, `Author = {username}`,
        `Copyright = © {year} {username}`; Comment blank) — `MetadataTokens.Resolve` (pure, tested)
        substitutes the live user/year/project/date when the export dialog prefills, so users see final,
        editable text and the copyright year never goes stale.
      - **`Sprocket.Mcp`** (references Core + Persistence + pinned **`ModelContextProtocol.Core` 1.4.1**
        — the official SDK's no-ASP.NET-Core package): **stateless Streamable HTTP** on a plain
        `HttpListener` bound to `http://127.0.0.1:{port}/mcp/` (`McpServerHost`, mirroring the SDK's own
        AspNetCore stateless handler sequence — the documented fallback if ever needed); **22 tools** —
        reads (`get_project_state`/`list_media`/`list_clips`/`get_playhead`/`list_effect_types`),
        transport (`seek`/`play`/`pause`), history/persistence (`undo`/`redo`/`save_project`), and edits
        (`import_media`, `add_clip_to_timeline` via the UI's own `MediaImport`/`ClipPlacement` — linked
        A/V + log-color-transform prepend for free — `trim_clip`, `move_clip`, `split_clip`,
        `delete_clip`, `add_effect`, `set_effect_parameter`, `remove_effect`, `add_marker`,
        `remove_marker`). **Clips/tracks are addressed by per-session runtime ids** (`RuntimeIds`, a
        `ConditionalWeakTable` registry — ids survive undo because commands re-insert the same
        instances; nothing touches the model or serializer).
      - **Commands + threading as specified:** every edit tool builds `IEditCommand`s through the one
        `EditHistory` (undoable by construction, shared with the UI), inside a single
        `IEditorSession.OnModelThreadAsync` callback marshalled to the UI thread (§8) — atomic against
        user edits; a paused preview refreshes via a same-position seek after structural edits.
      - **Lifecycle & security:** the app-scoped `McpServerService` survives File ▸ New/Open window
        swaps (the session slot re-attaches; parked = 503), starts **only** from the settings toggle,
        restarts on port/token change, and disposes before engine teardown; requests with an `Origin`
        header get 403 (DNS-rebinding guard), wrong/missing token gets 401 when required; a status-bar
        **"MCP :port" indicator** (green dot; red + tooltip on bind failure) shows whenever the editor
        is externally controllable.
      - **End-to-end tested with the SDK's real client:** in-memory stream transport (CI-safe, no ports)
        and the actual HttpListener endpoint on an ephemeral port — list tools (22, schemas from
        signatures), read state, edit, undo, tool-error (not protocol-error) on a bad id, 403/401/503
        enforcement, and session re-attach after a swap.
      - **Deliberate scope notes:** export start/status/cancel tools, ripple/roll/slide, speed/fade,
        transition, and track/sequence/multicam tools deferred (all mechanical on the same
        command-routed pattern); `split_clip`/`delete_clip` act on the addressed clip only (linked
        partner documented, not auto-affected); stateless mode has no server→client push (GET → 405).
    - **✅ FOLLOW-ON DONE (2026-07-02 — tool surface expanded 22 → 65 after a real-use transcript
      review; `Sprocket.Mcp/SprocketTools{,.Clips,.Structure,.Session}.cs`, `IEditorSession.cs`,
      `StateFormatter.cs`, `RuntimeIds.cs`; `Sprocket.Core/Commands/EditHistory.cs` (cross-call
      `BeginTransaction` groups), `Model/FadeOps.cs` (moved from App so MCP and the UI author the
      identical fade envelope), `Clip.CloneContentForSpan` public; App seam
      `McpEditorSession`/`MainWindow` MCP members; full suite 1103 green.)** Highlights:
      - **Linked-aware editing everywhere:** `trim/move/split/delete` gained `includeLinked`
        (default **true**, the NLE convention — linked partners edit together as one undo entry);
        `add_clip_to_timeline` gained `linked` + `stream` ("video"/"audio"/"both") so a video-only
        placement never creates the audio partner; new `duplicate_clip` (effects cloned with
        keyframes rebased via `CloneShifted`, partners duplicated onto a fresh shared link group),
        `unlink_clip`, `link_clips`.
      - **Fades & animation:** `set_clip_fade(fadeIn/fadeOutTicks)` drives `SetClipFadeCommand`
        through the shared `FadeOps.BuildOpacity` (the exact envelope the timeline handles author);
        `set_effect_parameter_keyframes` writes `AnimatableValue.Animated` with clip-relative times
        and per-keyframe interpolation; `get_clip` reports every parameter as constant-or-keyframes
        (never flattened) plus fades, link partners, media, and generator detail.
      - **Pro trims:** `ripple_trim`, `roll_edit`, `slide_clip`, `ripple_delete` (linked-aware) on
        the existing step-22 commands; `set_clip_speed` (linked-aware retime), `set_clip_gain`,
        `set_effect_enabled` (bypass), `copy_effects` (paste attributes).
      - **Structure:** `add/remove_track`, transitions (`list_transition_types`, `add/remove/
        set_transition` with runtime ids + transitions in the track read-out), `update_marker`
        (move/rename/recolor/comment as one entry), generators/titles (`list_generator_types`,
        `add_generator_clip` with the UI's topmost-free-else-new-track stacking,
        `set_generator_text/parameter`), and the audio chains at all three scopes
        (`list_audio_chain`, `add/remove_chain_effect`, `set_chain_effect_parameter`).
      - **Edit groups:** `begin/end/cancel_edit_group` over the new `EditHistory.BeginTransaction` —
        a multi-call AI edit lands as **one labelled undo entry** (user edits interleaving on the
        model thread join the group; Undo/Redo seal an open group; groups don't nest);
        `undo`/`redo` take a `steps` count.
      - **Session & delivery:** `open_project`/`new_project`/`close_project` (dirty-gated with
        `discardChanges`, reusing the `SessionRequested` swap — the MCP session re-attaches),
        `save_project_as`; `export_video` (background `VideoExporter` run with the same
        quiesce/resume discipline as the UI export, default MP4/H.264+AAC, optional range +
        video-only) with `get_export_status` polling and `cancel_export`; transport rounded out
        with `stop`, `go_to_start`, `go_to_end`, `step_frames`.
      - **Read-side ergonomics:** `get_project_state(sections=…)` trims the payload and reports
        `dirty`; `list_effect_types(category, nameQuery)` filters the catalog and now emits the
        descriptor `step`/`unit` fields.
    - **✅ FOLLOW-ON DONE (2026-07-02 — CLI scripting flags; `Sprocket.App/CliOptions.cs` (new),
      `App.axaml.cs`, `MediaBootstrap.Create(string? path)`, `Program.cs`/`McpServerService.cs` docs;
      +15 App tests (`CliOptionsTests`), verified end-to-end: flag launch → readiness line → MCP
      `initialize` over HTTP.)** `--mcp` starts the loopback MCP server for the session (persisted
      port/token settings) and `--mcp-port <n>` overrides the port (implies `--mcp`) — so
      `Sprocket clip.mp4 --mcp` launches straight into a scriptable editing session. The override is
      **session-only** (never written to settings; a later Preferences apply supersedes it), preserving
      the never-auto-started rule — the CLI flag is as explicit a user switch as the toggle. When the
      flag is used the app prints one `mcp: listening on http://127.0.0.1:<port>/mcp` line to stdout
      (errors to stderr) so launch-and-connect scripts can wait deterministically instead of polling.
      Tokens stay off argv (visible to other processes): a session-only bearer token comes from
      **`SPROCKET_MCP_TOKEN`** instead. Unknown `--flags` are ignored for media-path detection, so the
      first bare arg remains the file to open. Deliberate scope: no headless `--no-ui` mode (the
      session lives in `MainWindow`).

## Step 39

39. **Fade handles & opacity rubber-band (on-timeline fade editing + visualization).** Make a clip's
    fade in/out directly **visible and editable on the timeline**, so a fade is never an invisible
    surprise — the lesson from the Alt-copy "second clip plays black" bug (fixed 2026-06-30): the clip
    carried a keyframed fade with nothing on the timeline to show it. Follows the convention in
    leading editors. This lands on **existing seams**, not a new model — the fade is already the keyframed
    `EffectTypeIds.Fade` / `EffectParamNames.Opacity` `AnimatableValue` that drives both video alpha
    (shader, step 7) and audio gain (mixer, [§6](ARCHITECTURE.md)); this step is a timeline affordance over it.
    Pieces:
    - **Fade handles.** Small draggable triangles in each top corner of a clip, drawn in
      `TimelineControl.DrawClips` (step 12). Dragging the left/right handle inward sets the fade-in/out
      length; the clip body draws the resulting opacity ramp so the fade reads at a glance. Zero length =
      the "no fade" rest state.
    - **Opacity rubber-band (companion view).** A horizontal opacity line across the clip body the user can
      pull down or add points to — the inline form of the same keyframe envelope already shown in the
      Inspector's keyframe lane (step 16d), so the two stay in sync.
    - **Edits route through the command stack (step 10).** Dragging a handle / rubber-band point issues
      `IEditCommand`s that author or adjust the Fade effect's opacity keyframes, coalesced into one undo
      entry per drag (mirrors the trim/slip drag coalescing).
    - **Keyframes stay clip-aligned.** Effect keyframe times are **absolute timeline time**, so handles must
      author keyframes at the clip's actual edges, and clip move/copy must keep the fade aligned with the
      clip. Copy/paste rebasing was fixed 2026-06-30 (`ClipboardOps.Paste` → `EffectInstance.CloneShifted`);
      the matching rebasing for a plain **move** (`SetClipPlacementCommand`, which today changes only
      `TimelineStart`) is the remaining gap to close as part of this step.
    - **✅ DONE (`Sprocket.Core/Commands/ModelCommands.cs` + `Model/EffectInstance.cs`;
      `Sprocket.App/Timeline/{FadeOps,TimelineMath,TimelineControl}.cs`; 29 new tests — Core +10
      (`FadeCommandTests`), App +19 (`FadeOpsTests`); full suite **925 green**, clean build 0 warnings, smoke
      launch exit 0.)** A clip's fade is now visible and editable on the timeline, and moves keep keyframed
      effects aligned. Delivered:
      - **Fade handles + ramp visualization (`TimelineControl.DrawFadeOverlay`):** small triangles pinned to a
        clip's top edge at the fade-ramp tops (the corners at zero length — the "no fade" rest state);
        dragging one inward sets the fade-in/out length, live and clamped so the two fades never cross. The
        clip body draws the **opacity envelope line** (top = 1, bottom = 0) with the faded-away region above it
        shaded, so any fade — handle-authored or hand-keyframed — reads at a glance; the selected clip also
        shows its keyframe dots.
      - **Opacity rubber-band:** the envelope line is grabbable — a vertical drag moves the grabbed segment's
        two bounding keyframes (or a single keyframe when grabbed directly, or the whole flat level on a
        constant/absent envelope), clamped to [0, 1]; **Ctrl+click adds a point** at the pointer, which the
        rest of the drag then moves — the inline form of the Inspector's step-16d keyframe lane, editing the
        very same `AnimatableValue` so the two stay in sync.
      - **Envelope semantics in a pure, headless-tested `FadeOps`:** `ReadFades` recognises edge ramps (an
        arbitrary interior envelope reads as 0/0), `BuildOpacity` rebuilds edge ramps while **preserving
        interior rubber-band points and a lowered plateau level** (a shrunk fade re-tops at the plateau, not
        mid-ramp), plus the band's `GrabKeyframes` / `WithValueDelta` / `WithAddedPoint`; handle hit-testing
        and level↔Y mapping live in `TimelineMath` beside the other tested geometry.
      - **One undo entry per drag via a new Core `SetClipFadeCommand`:** sets the Fade effect's opacity
        envelope and **creates the Fade effect when the clip has none** in the same command (undo removes it
        again), coalescing per clip — so a handle/band drag on a fade-less clip is still a single entry.
      - **Move rebasing closed (the step's named gap), generalised to every pure-move command:** a new
        in-place `EffectInstance.ShiftKeyframes` is applied by `SetClipPlacementCommand` (only when the source
        span is unchanged — trims/slips stay anchored, since their timeline edges don't move),
        `MoveClipToTrackCommand`, `RippleTrimCommand`'s downstream shifts (so ripple delete/trim keeps fades
        aligned), and `SlideClipCommand`'s slid clip. Deltas are computed against the clip's **current** start
        at apply/revert time, so coalesced drags and merged undo entries stay exact — proven by tests.
      - **Deferred:** dragging a band keyframe horizontally (time) — vertical value edits + the Inspector lane
        cover it; fade-handle snapping; and trim-time fade re-anchoring (a trimmed-shorter clip keeps its
        keyframes at the old edge — now at least *visible* on the clip body, which was this step's motivating
        bug class).

## Step 40

40. **Rich text & titles (styling, lower thirds, rolling credits).** Grow the step-19 minimal Title
    generator (single-line centred text, one fill colour, animatable size) into a professional titles
    toolset, following the convention in leading editors — a title is a **generator clip on its
    own track**, edited in place and in the Inspector. Lands entirely on existing seams
    ([ARCHITECTURE §17](ARCHITECTURE.md)): a title is already a `ClipKind.Generator` on the
    `GeneratorCatalog`, so this adds generator types, string / animatable params, and Render layout with
    **no render-graph, effect-chain, or persistence redesign**. Captions / subtitles (SRT/VTT, a
    toggleable caption track) are a **distinct subsystem deferred to a separate step**, as in every NLE.
    Extends step 19 and depends only on done seams, so it can be **pulled forward / prioritised** whenever
    desired.
    - **Editable text objects (post-hoc).** A title stays fully editable after creation (the universal NLE
      behaviour): **double-click** the clip in the Program monitor / timeline to edit the content, and an
      Inspector TEXT section for every attribute. All edits route through the step-10 command stack (a
      generator-param command mirroring `SetEffectParameterCommand`), so they undo / redo and coalesce
      (drag gestures) like effect params — no new mutation path.
    - **Typography & styling (Inspector, stored on `GeneratorSpec`).** Extend `GeneratorParamNames` with
      **font family** (a typeface picker — step 19 hard-codes `SKTypeface.Default`), **bold** weight and
      **italic** (`SKFontStyle`), **fill colour** (exists), **stroke / outline** (colour + width, `SKPaint`
      stroke), **drop shadow** (colour + offset + blur, `SKImageFilter.CreateDropShadow`), a **background
      box** (colour + opacity + padding — step 19's full-frame `backgroundColor` becomes a padded box),
      **alignment** (left / centre / right, `SKTextAlign`; centre-only today), and **tracking** (letter
      spacing) + **leading** (line spacing). Sizes stay fractions of frame height (resolution-independent);
      colours stay `#AARRGGBB`. Font size stays an `AnimatableValue`; the new numeric attributes (stroke
      width, shadow, tracking, leading) join it as animatable params so they keyframe (step 16d). String
      attributes (family, alignment, style) live in `GeneratorSpec.Strings`.
    - **Multi-line & paragraph layout (Render).** `RenderGeneratorContent` grows from one centred line to
      **word-wrapped multi-line** layout in a text box (measure / break / stack lines, honouring alignment,
      leading, tracking). Reuses the offscreen-surface → snapshot → effect-chain path; only the content
      draw changes.
    - **Positioning / transform (reuse step 16).** Position, scale, rotation, and anchor come from the
      existing **Transform** effect on the title clip — not reinvented. The step-17 **action-safe /
      title-safe** guides frame title placement.
    - **Title templates (`GeneratorCatalog`).** Register built-ins beside step-19 Title / Color Matte:
      **Lower Third** (a two-field name+role title with a background bar — the "chyron" / "super"), **Roll**
      (credits), and **Crawl** (ticker) — each a generator descriptor with defaults, listed in the Project
      bin / **Clip ▸ Insert** menu like current generators.
    - **Rolling credits (Roll) & Crawl — a *property of the title*, duration-driven.** Following the
      industry norm (leading editors' **Roll / Crawl / Scroll** title options), scrolling is a **scroll mode
      on the generator**, *not* a hand-keyframed Transform. A
      `scrollMode` (None / Roll-up / Crawl-left) param, with **clip duration setting the speed** (longer
      clip ⇒ slower — the model used by leading editors) plus optional **ease-in / out** and **start / end off-screen**
      (options also found in leading editors). The generator computes the offset from its **clip-local progress**, so it
      stays a pure, deterministic function of (project, t) ([ARCHITECTURE §5](ARCHITECTURE.md)) with **no
      fragile absolute-timeline keyframes** (cf. the step-39 keyframe-rebasing caveat). **Small additive
      Core change:** pass the clip's local elapsed time + duration (or a normalised progress) into
      `RenderGraph.ResolveGenerator` / `ResolvedGenerator`; today the generator receives only absolute `t`.
      *(Deliberate departure from "everything is a keyframe": pros drive roll speed by clip length, and it
      survives trim / move cleanly.)*
    - **Entrance / exit animation presets.** Convenience presets applied as keyframes (step 16d) on the
      title: **fade in / out** (the existing Fade effect + the step-39 on-timeline fade handles),
      **pop / scale** and **slide** (Transform keyframes), and **typewriter** (a `revealFraction` param
      driving how many characters draw). Presets author standard keyframes; nothing bespoke in the graph.
    - **Cross-platform determinism — bundle the title fonts.** Text must rasterise **byte-identically
      across Windows / Linux / macOS** so preview == export and the golden-frame / cross-OS PNG-hash tests
      hold ([ARCHITECTURE §5](ARCHITECTURE.md), Verification). System-font substitution differs per OS and
      would break that, so the title fonts are **bundled per-RID as `EmbeddedResource`s** (loaded via
      `SKFontManager` / `SKTypeface.FromStream`) — the data-asset precedent step 37 notes for LUTs. The
      family picker lists the bundled set; opt-in system fonts (non-delivery use) are a later add.
    - **Persistence (additive, §12).** Every new attribute is a string or `AnimatableValue` generator
      param, so all round-trip through the existing `GeneratorDto` with **no schema bump**; a step-19 title
      loads unchanged and a title using none of the new fields serialises byte-identically.
    - **Testing.** Core: generator-param command undo / redo + coalescing; scroll-offset math from
      clip-local progress (start / steady / end, ease, off-screen); catalog registration of the new
      templates. Render (offscreen-raster goldens): multi-line wrap + alignment, stroke, shadow, background
      box, a lower third, and a roll at 0 / 0.5 / 1 progress. Persistence: round-trip of every new field +
      step-19 byte-identical omission. Export: a roll renders on the deterministic raster path
      (golden-frame).
    - **✅ DONE (`Sprocket.Core`: `Model/Generator.cs` param names + `TitleScrollModes`/`TitleScroll` +
      `GeneratorCatalog` templates + `Rendering/{RenderPlan,RenderGraph}` clip-local progress +
      `Commands/ModelCommands` generator commands; `Sprocket.Render`: `TitleFonts` + `TitleRenderer` +
      bundled `Fonts/*.ttf`; `Sprocket.App`: Inspector TEXT/Text-Style/Scroll sections + timeline
      double-click editor + animation presets; 40 new tests — Core 269 → 291, Render 93 → 109,
      Persistence 106 → 107, Export 86 → 87; full suite **965 green**, clean build 0 warnings, smoke launch
      exit 0.)** The step-19 minimal Title grew into the professional titles toolset, all on existing seams
      (no render-graph/effect-chain/persistence redesign). Delivered:
      - **Typography & styling (`TitleRenderer`, Render):** word-wrapped multi-line layout (hard newlines +
        measure/break to 90 % frame width) with **alignment** (left/centre/right), **tracking** (manual
        per-glyph advance — Skia has no letter-spacing) and **leading**; **bold/italic**, **stroke**
        (round-joined outline pass), **drop shadow** (offset + blur-mask pass), and a **padded background
        box** that moves with the block (the lower third's bar). New generator params: string attributes
        (`fontFamily`/`bold`/`italic`/`align`/`strokeColor`/`shadowColor`/`boxColor`/`text2`/`scrollMode`/…)
        in `GeneratorSpec.Strings`, numeric ones (stroke width, shadow x/y/blur, box padding, tracking,
        leading, `fontSize2`, position, reveal) as `AnimatableValue`s so they keyframe (step 16d). A
        **block position** (`positionX`/`positionY`, animatable) places the text box — a small addition
        beyond the step's bullets so the Lower Third template can anchor lower-left without conscripting
        the Transform effect; scale/rotation still come from Transform (step 16). Sizes stay fractions of
        frame height; colours stay `#AARRGGBB`. A step-19 title (no new params) renders as before.
      - **Cross-platform determinism — bundled fonts (`TitleFonts`, Render):** the **Liberation** family
        trio (Sans / Serif / Mono, four styles each — SIL OFL 1.1, licence shipped alongside) embedded as
        `EmbeddedResource`s and loaded once via `SKTypeface.FromStream` (the step-37 data-asset precedent);
        titles never touch system fonts, so text rasterises byte-identically across the three OSes (§5).
        The Inspector family picker lists the bundled set; opt-in system fonts stay a later add.
      - **Roll & Crawl — duration-driven scroll on the generator:** `ResolvedGenerator` gains **`Progress`**
        (the clip's normalised local time, computed by the planner and the preview's
        `RenderGraph.ResolveGenerator(clip, t)` overload) so scrolling is a pure function of (project, t):
        clip duration sets the speed, the motion survives trim/move, and no absolute-time keyframes are
        involved (the step-39 caveat). `scrollMode` = roll (bottom→top) / crawl (right→left, flattened to
        one line) with **ease-in/out** (`TitleScroll.Eased` — smoothstep / p² / 1−(1−p)²) and
        **start/end off-screen** toggles (off = rest at the block's position, options also found in leading editors).
      - **Templates (`GeneratorCatalog`):** **Lower Third** (name+role over a translucent bar, left-aligned,
        anchored lower-left), **Credits Roll**, and **Crawl** — distinct ids (`builtin.gen.lowerthird`/
        `.roll`/`.crawl`) sharing the Title render path via `GeneratorTypeIds.IsTitle`, so the browser/
        timeline label them individually while Render has one rich-text code path.
      - **Editable post-hoc (App):** the Inspector grows **Text / Text Style / Scroll & Animate** sections
        for title clips — multi-line text + secondary field (live preview while typing; one undo entry per
        focus via the coalescing scope), font picker, B/I + alignment toggles, colour swatch+hex rows, and
        every numeric attribute on the same animatable slider/keyframe-lane rows effect parameters use
        (`BuildParamRow` refactored into a delegate-driven `BuildAnimatableRow`). **Double-clicking a title
        clip on the timeline** overlays an inline text editor (mirroring the track-rename overlay; Enter/
        blur commit, Esc cancels). In-place editing on the Program monitor is deferred.
      - **Command stack (step 10):** new Core `SetGeneratorParameterCommand` (animatable, coalescing per
        spec+param — a slider drag is one entry) and `SetGeneratorStringCommand` (empty value removes the
        entry so cleared attributes serialize as absent; typing coalesces), mirroring the effect-parameter
        command; the timeline/Inspector/presets all route through them.
      - **Entrance/exit presets ("Animate ▾" in the Text section):** **Fade In/Out** ride the step-39 fade
        envelope (`FadeOps.BuildOpacity` + `SetClipFadeCommand`), **Pop In** and **Slide In From Left/
        Right** author `EaseInOut` Transform keyframes (adding the Transform effect in the same
        `CompositeCommand` when absent — one undo entry), and **Typewriter** keyframes the new
        `reveal` fraction (0→1), which truncates drawn characters in reading order while the block's
        layout stays fixed.
      - **Persistence:** zero schema change — every new attribute rides the existing `GeneratorDto`
        string/parameter dictionaries; a rich round-trip test covers the new fields (incl. a keyframed
        reveal) and step-19 titles keep serializing byte-identically.
      - **Tests (40):** Core 22 (`TitleTests` — easing curves/clamps, clip-local progress through both
        resolve paths + the plan, template defaults, `IsTitle`, command apply/revert/coalesce); Render 16
        (`TitleRendererTests` — centring, hard-line stack + word wrap, alignment, stroke/shadow/box,
        position, lower third, reveal 0/half/full, roll off-screen at 0 and 1 + crossing at 0.5 + monotonic
        motion, crawl right→left, anchored (non-off-screen) roll, bold coverage, tracking width);
        Persistence 1 (rich-title round-trip); Export 1 (a Credits Roll scrolls through the frame on the
        deterministic raster export — off-screen / visible / off-screen by whole-frame luminance).
      - **Deferred:** WYSIWYG editing in the Program monitor (timeline double-click + Inspector cover
        post-hoc editing today); captions/subtitles (SRT/VTT) stay their own step as noted; opt-in system
        fonts; per-word/character styling spans (a title has one style per field).

