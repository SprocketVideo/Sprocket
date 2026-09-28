# Source-monitor marks + three-point editing

🟡 **Phases 1–3 ✅ (2026-09-28)** — targeting / sync lock / lock, source marks, patching + Insert / Overwrite; phases 4–6 open (planned 2026-09-28). This covers: In/Out marks in the Source monitor for each media item; Insert (`,`) and
Overwrite (`.`) three-point edits using the Premiere rules; source patching; track targeting, sync lock, and track lock;
drag from the Source monitor (whole clip, video only, or audio only) that uses the marked range; and changing drops onto
the timeline to overwrite by default, with Ctrl held for insert. Tracked in [PLAN.md](../../PLAN.md) as step 61 and in
the Open work list. Relative links resolve from the repo root.

## Why / product context

Sequence marks and Lift / Extract shipped on 2026-09-28
([DONE log](../history/steps-58plus.md#inout-marks-completion--lift--extract-unscheduled-feature-2026-09-28--done)).
The other half of the standard editing loop is still missing: pick a range in the Source monitor and cut it into the
sequence at a chosen point. Today the only way to place media is to drag the whole file from the bin. The clip lands
wherever it's dropped and overlaps whatever is there, and the "last clip wins" rule in `Track.ResolveActiveClip`
decides what shows. There's no way to push the rest of the sequence right to make room.

How the leading editors do it:

| | Premiere Pro | DaVinci Resolve | Final Cut Pro |
|---|---|---|---|
| Source marks | I/O in the Source monitor, saved per master clip (the bin item) | I/O in the Source viewer, saved per clip | Range selection in the browser, saved per clip |
| Insert / Overwrite | `,` / `.` | F9 / F10 | W / D |
| Other edits | Replace (menu), Fit to Fill (Fit Clip dialog on 4-point) | Replace F11, Fit to Fill Shift+F11, Place on Top F12, Append Shift+F12 | Connect Q, Append E, Replace Shift+R |
| Where it lands | Source patching (V1/A1 source indicators on the left of the track header) | Destination controls on the track header | The primary storyline (magnetic) |
| What moves on Insert | Tracks with **sync lock** on | Tracks with **Auto Track Selector** on | n/a (magnetic) |
| Lift / Extract / paste scope | **Track targeting** (track-name toggle) | Auto Track Selector | n/a |
| Drag to timeline | Overwrite; **Ctrl-drag = insert** | Overwrite; dropping on the Timeline viewer shows an edit overlay (Insert / Overwrite / Replace / Fit to Fill / Place on Top) | Connect |
| Drag one stream only | "Drag Video Only" / "Drag Audio Only" icons under the Source monitor | Drag from the viewer's video/audio icons | Browser Audio/Video Only (Shift+2/3) |

**Decision: use Premiere's model throughout.** The existing mark keys (`I`/`O`/`X`/`/`/`;`/`'`/`Shift+I`/`Ctrl+Shift+X`)
are already Premiere's, so `,` and `.` follow from them. Separating source patching (where a source edit lands) from
track targeting (what Lift / Extract / paste act on) is the most familiar split, and Resolve users map it onto their
destination controls plus Auto Track Selector without trouble. Deliberate departures, and why:

- **No dual (side-by-side) monitor in this feature.** Program and Source stay two tabs of one `MonitorPane`
  (`MainWindow.axaml` l.431). Three-point editing works with tabs because the timeline ruler always shows the sequence
  marks. A docked dual-monitor layout is a separate UI item ([UI.md §5](../../UI.md), "full panel docking").
- **A 4-point edit (both source marks and both sequence marks set) doesn't open a Fit Clip dialog in phase 3.** The
  source In and the sequence range win, and the source Out is ignored. This matches the "Ignore Source Out" option in
  Premiere's dialog, and a status message says so. **Fit to Fill** is added in phase 6 as an explicit command (Resolve's
  Shift+F11). It uses the existing constant-speed retime, so no dialog is needed.
- **Selecting a timeline clip keeps loading its whole media** into the Source monitor, as it does today. It doesn't
  open the clip's own range for in-place editing (Premiere's double-click on a timeline clip). The Source monitor shows
  the media item's own source marks. The Premiere behavior is out of scope (see "Deferred").
- **Replace, Place on Top, Append, and Match Frame (`F`) are deferred.** Each is a thin addition once the three-point
  resolver and the insert / overwrite builders exist.

## Existing seams to build on

- **Source monitor:** `src/Sprocket.App/Monitors.cs`. `SourceMonitor` (l.113) implements `IMonitor` and has
  `SetSource(MediaRef?)`, `Activate` / `Deactivate`, and `Rebuild`. `BuildSourceProject` (l.208) makes a throwaway
  one-clip project. It's video-only on `SoftwareClock`, and audio-only media is refused in `ShowInSourceMonitor`
  (`MainWindow.axaml.cs` l.2483).
- **Monitor and key routing:** `_active` / `_source` / `_program` fields; `WireMonitorTabs` (l.2502); `OnKeyDown`
  (l.937–1088). The mark keys at l.1062–1075 always target the Program sequence. `_activeArea` (`WorkArea`, l.193)
  follows clicks and focus, so it can decide which monitor gets the mark keys. `,` and `.` are unbound: `OemComma` is
  only used with Ctrl for Preferences.
- **Sequence marks and range edits:** `Sequence.MarkIn/MarkOut` and `SetSequenceMarksCommand`
  (`src/Sprocket.Core/Commands/RangeEdits.cs` l.11). `RangeEdits.Build` (l.100) cuts a range out of every track. It
  splits at most twice per straddling clip, removes the pieces in range, removes transitions whose cut falls in range,
  gives tail pieces fresh link groups (`tailGroups`), and ripples with `ShiftClipsCommand`. This is the core of
  Overwrite (cut out the range, then add) and of Insert (split at the point, then shift). The private
  `FrameHoldEdits.InsertFreeze` (l.175) is already a working split-shift-add insert template.
- **Clip placement:** `src/Sprocket.App/Timeline/ClipPlacement.cs` `BuildPlaceCommand` (l.59) builds linked A/V
  `AddClipCommand`s in a `CompositeCommand`, with a shared `LinkGroupId` and `PrependDetectedColorTransform`. It's
  shared with MCP `PlaceClip`. It always uses `SourceIn = 0` and `SourceOut = Info.Duration`, so it needs a
  source-range parameter. Stills get `MediaImport.DefaultStillDuration` as their length.
- **Timeline drop:** `TimelineControl.OnDragOver` / `OnDrop` / `DropMedia` (l.3166 / 3192 / 3252). The track is the
  lane under the cursor, or the first track of that kind; the companion stream goes to the first track of the other
  kind. `DragFormats.MediaRefId` carries only a GUID.
- **Track headers:** custom drawn in `TimelineControl.DrawHeaders` (l.1790), with toggles in `HandleHeaderClick`
  (l.2283) through an undoable `SetPropertyCommand<bool>`. `NameAreaWidth` (l.1853) reserves space for the toggles.
  `Track` has only `Enabled` (plus `Muted` / `Solo` on audio tracks). There is no lock, sync-lock, or target state, and
  tracks have **no id**, so they're referenced by index in persistence. Video is indexed bottom-up, the MCP
  convention.
- **Persistence:** `MediaRefDto` and `TrackDto` (`src/Sprocket.Persistence/ProjectDto.cs` l.57 / l.140), and
  `TimelineDto.MarkInTicks/MarkOutTicks` (l.135) written only through `ProjectSerializer.ToDto(Sequence)`. The
  render-cache hasher serializes the bare `ToDto(Timeline)`. Track flags must stay out of that hash just as the marks
  do, because none of them change a frame. `Enabled` is the exception and stays in.
- **MCP:** `add_clip_to_timeline` (`SprocketTools.cs` l.183) calls `IEditorSession.PlaceClip`, which always uses the
  full source. There are no MCP tools for marks, Lift/Extract, or insert/overwrite, and `StateFormatter` doesn't expose
  marks.

## Implementation sketch

Built in six phases. Each phase can be merged on its own and leaves the editor shippable.

### Phase 1: track targeting, sync lock, and track lock (Core + header UI) — ✅ shipped 2026-09-28

> Shipped as below, except for one change: targeting is a separate `V1`/`A1` chip on the header's bottom row, not a click on the track name, so rename keeps its double-click. DONE log: [steps-58plus.md § Step 61](../history/steps-58plus.md#step-61).

This phase comes first because it gives Insert and Overwrite a scope. It also removes the "every track" departure
currently on Lift / Extract.

1. **Model.** Add three fields to `Track`:
   - `Targeted` (default `true`): what Lift, Extract, paste and Mark Clip act on.
   - `SyncLocked` (default `true`): which tracks shift on ripple edits (Insert, Extract, ripple delete).
   - `Locked` (default `false`): no edits at all.

   Premiere's defaults are all targeted and all sync-locked, so existing projects behave the same as today. Setting
   the fields goes through `SetPropertyCommand<bool>`.
2. **`RangeEdits.Build` takes an explicit scope.** It gets two parameters:
   - `carveTracks`: the targeted tracks that aren't locked.
   - `rippleTracks`: the sync-locked tracks that aren't locked.

   Lift carves `carveTracks`. Extract carves `carveTracks` and shifts clips starting at or after Out on every track in
   `rippleTracks`. A sync-locked track that isn't targeted still shifts, which is Premiere's rule for keeping sync. A
   straddling clip on a ripple-only track is left alone and doesn't shift, and a status note reports how many sync
   breaks that caused. Locked tracks are skipped, and if one would have been cut, the edit reports it.
3. **Honor `Locked` across the existing edit paths.** Before this lands, list every path that creates an
   `IEditCommand` on a clip: `ClipEdits`, `ClipPlacement`, drag, trim, and the MCP tools. In each one, refuse to act on
   clips of a locked track. This is the largest mechanical piece. Premiere also greys out the clips on a locked track;
   we draw a hatch over the lane.
4. **Header UI.** The targeting toggle is the track name box: click it to toggle, and it's highlighted when targeted,
   as in Premiere. Add a lock toggle and a sync-lock toggle to the lane header. Both use `STYLE_GUIDE.md` tokens and
   extend `NameAreaWidth`. Rename moves to double-click on the name or to the context menu, so a single click can
   toggle targeting. The track context menu gets Target / Sync Lock / Lock entries, plus "Target All / Target None".
5. **Persistence.** Add nullable `TrackDto.Targeted` / `SyncLocked` / `Locked`. They're written only when they differ
   from the default, and missing means default. Add a hash-invariance guard test like the one for marks.
6. **MCP.** Add `set_track_state(trackKind, index, targeted?, syncLocked?, locked?)`. `StateFormatter` lists the three
   flags on each track.

### Phase 2: source marks on media items — ✅ shipped 2026-09-28

1. **Model.** Add `MediaRef.SourceMarkIn` / `SourceMarkOut` as `Timecode?` in the media's own time. They're per bin
   item, like Premiere's master-clip marks, so every place that uses that media sees them. They're set through a new
   `SetSourceMarksCommand(media, in?, out?)`, which is undoable and dirties the project the way sequence marks do.
2. **Persistence.** Add nullable `MediaRefDto.SourceMarkInTicks` / `SourceMarkOutTicks`. They're project content and
   travel with the project, not in the per-user `ProjectLayout`.
3. **Mark-key routing.** `I`, `O`, `Shift+I`, `Shift+O`, `Alt+I`, `Alt+O`, `Ctrl+Shift+X`, and Play In to Out follow
   the monitor that has focus. When `_activeArea == Monitor` and the Source tab is showing, they act on the Source
   media's marks at the Source playhead. Otherwise they act on the Program sequence, as today. This matches Premiere's
   panel-focused routing. `X` (Mark Clip) and `/` (Mark Selection) keep acting on the sequence.
4. **Source monitor UI.**
   - Mark ticks and a shaded range on the shared scrubber when the Source tab is active. This reuses the drawing idea
     from `TimelineControl.DrawInOutRange`.
   - In, Out and duration readouts in the Source monitor.
   - Mark In, Mark Out, Insert and Overwrite buttons shown only on the Source tab, using the transport-bar style from
     `STYLE_GUIDE.md`.
   - Play In to Out on the Source engine (`PlaybackEngine.PlayInToOut` already exists).
   - The Source monitor keeps showing the whole media and never trims its playback to the marks (Premiere does the
     same).
5. **Bin integration.** A bin tile whose media has marks shows a small range badge. Dragging that tile to the timeline
   uses the marked range; Premiere does the same when dragging a master clip that has marks. `ClipPlacement.BuildPlaceCommand`
   gains `(Timecode sourceIn, Timecode sourceOut)` and defaults them from the marks.
6. **MCP.** Add `set_source_marks(mediaRefId, inTicks?, outTicks?)` and `set_sequence_marks(inTicks?, outTicks?)`, and
   expose both kinds of marks in state.

### Phase 3: source patching + Insert / Overwrite — ✅ shipped 2026-09-28

> Shipped as below, except: the patch holds track references (not indices), so removing a track needs no command change — a patch to a track that's gone resolves to the default, and undoing the delete brings it back. Clip ▸ Insert already names the generators submenu, so the menu items are "Insert Edit" / "Overwrite Edit". DONE log: [steps-58plus.md § Step 61](../history/steps-58plus.md#step-61).

1. **Patch model.** Add `Sequence.SourcePatch` as a small record: `VideoTrackIndex?` and `AudioTrackIndex?`, with null
   meaning that stream isn't patched. It's per sequence, like Premiere's. It defaults to the bottom video track and
   the first audio track, clamped to the tracks that exist. It's set through `SetSourcePatchCommand` (undoable),
   persisted as nullable `TimelineDto.SourcePatchVideo` / `SourcePatchAudio`, and kept out of the render hash.

   Removing or reordering tracks has to keep the patch valid. `RemoveTrackCommand` and the track-reorder command also
   re-point or clear it, and undo restores it.
2. **Header UI.** A source-indicator column at the far left of the header, as in Premiere. The patched video lane
   shows a `V1` chip and the patched audio lane an `A1` chip in the accent colour. Clicking the column on a lane moves
   the chip there, and clicking the chip itself un-patches that stream. The column only shows chips when the Source
   monitor has media; otherwise it's an empty gutter. Media with no audio shows no `A1` chip, and the same for video.
3. **Three-point resolver (pure Core).** `ThreePointResolver.Resolve(srcIn?, srcOut?, srcLength, seqIn?, seqOut?,
   playhead)` returns `(sourceIn, sourceOut, recordIn, notes)`, following Premiere's precedence:
   - The source range is `[srcIn ?? 0, srcOut ?? srcLength)`.
   - If `seqIn` is set, the edit starts there.
     - If `seqOut` is also set, the duration is `seqOut − seqIn`. When only the source Out is marked, the source is
       backtimed (`srcIn = srcOut − dur`). Otherwise the source Out is ignored, and in a full 4-point edit a note says
       so.
     - If `seqOut` isn't set, the duration is the source duration.
   - If only `seqOut` is set, the edit is backtimed so it ends at `seqOut`.
   - If no sequence marks are set, the edit starts at the playhead with the source duration.
   - A source that's shorter than the range needs is clamped to the media that's available, with a note ("insufficient
     source media"), as Premiere does.
   - Stills and other media with no fixed length use `DefaultStillDuration` as `srcLength`.
4. **Edit builders (Core).** Put them in `ThreePointEdits` in `src/Sprocket.Core/Commands/`. Both return one
   `CompositeCommand` labelled "Insert" or "Overwrite".
   - `Overwrite(seq, clipsToAdd, recordIn, patch)` carves `[recordIn, recordIn + dur)` on the patched tracks only.
     It uses `RangeEdits` carve with `carveTracks` = the patched tracks and no ripple, then adds the clips.
   - `Insert(...)` does the following:
     - Splits straddling clips at `recordIn` on the patched tracks and every sync-locked track.
     - Removes transitions whose cut is at `recordIn`.
     - Shifts clips at or after `recordIn`, and the right halves, by `dur` on those tracks. Transition cut points and
       sequence markers after the point shift too. Premiere moves markers on Insert, so this is where marker ripple
       starts; Extract could adopt it later.
     - Adds the clips.

   The added A/V clips share a new `LinkGroupId`, and the video clip gets `PrependDetectedColorTransform`. That means
   `ClipPlacement`'s clip construction moves into a Core helper (or the App passes in pre-built `Clip`s), so the App and
   MCP build clips the same way. Refuse the edit, with a message, if a patched track is locked or nothing is patched.
5. **After the edit (Premiere behavior).** The sequence marks are cleared, the playhead parks at the end of the new
   clip, and the source marks stay. It's all one undo step, including the mark clear.
6. **Keys and menu.** `,` for Insert and `.` for Overwrite, from either monitor, as long as the Source monitor has
   media. They have no `InputGesture`, for the same Oem-name reason as `;` and `'`. Add Clip ▸ Insert and Clip ▸
   Overwrite items, plus the phase-2 Source-monitor buttons.
7. **Audio-only sources.** The Source monitor has to accept audio-only media, or it can't be used for three-point
   edits. Allow it with a blank frame and a "Audio only" label in `SourceMonitor.Rebuild`. Marks and the duration still
   work; audio playback in the Source monitor is phase 5.
8. **MCP.** Add `insert_edit` and `overwrite_edit` taking `(mediaRefId, sourceInTicks?, sourceOutTicks?,
   recordInTicks?, recordOutTicks?, videoTrackIndex?, audioTrackIndex?)`, with defaults from the stored marks, patch,
   and playhead. Also add `lift` / `extract` tools, which MCP doesn't have yet.

### Phase 4: drag from the Source monitor + overwrite-on-drop — ✅ shipped 2026-09-28

> Shipped as below. The companion stream goes to its patched track, or the first editable track of its kind when that's locked; an un-patched companion still comes along (patching steers the keyed edits, not drags). A drop uses the `Linked` toggle, keeps the sequence marks, and leaves the playhead put. DONE log: [steps-58plus.md § Step 61](../history/steps-58plus.md#step-61).

1. **New drag format.** Add `DragFormats.SourceRange`, a small serialized payload: `mediaRefId`, `sourceIn`,
   `sourceOut`, and `streams` (both, video, or audio).
2. **Drag sources.** Dragging the Source preview image sends both streams. The "Drag Video Only" and "Drag Audio Only"
   handles under the Source monitor, as in Premiere, send one stream.
3. **Drop behavior.** A drop is an **Overwrite** at the snapped drop time on the drop lane: the lane under the cursor,
   with the companion stream on the patched (or first) track of the other kind. **Ctrl+drop is an Insert.** The drag
   shows a ghost of the clip, and during a Ctrl-drag a right-pointing ripple indicator, like Premiere's.
4. **Bin drops use the same builders**, so dropping from the bin overwrites instead of stacking an overlap. **This
   changes shipped behavior.** Amend the FEATURES.md "Drag media from bin onto timeline tracks" row and the user-docs
   page in the same change.
5. **Clip moves are out of scope.** `ClipEdits.MoveSet` keeps its overlap behavior for now. Making moves overwrite
   (Premiere) is a separate change, and "Deferred" records it.

### Phase 5: Source-monitor audio

1. Play the Source monitor's audio through the mixer the way Program does. This means a second `AudioEngine` master
   clock for the Source engine, or switching the single output between the engines when the tab changes. Also show a
   waveform for audio-only media.
2. Pause the Source engine's audio whenever Program plays, as the video already does through `Deactivate`, so the two
   engines never both drive the device.

This phase is its own step because audio playback touches the master-clock seam (ARCHITECTURE §6, §8). Phases 1–4
don't depend on it.

### Phase 6: Fit to Fill, docs, and inventory

1. **Fit to Fill (`Shift+F11`, Resolve's key).** Premiere has no default key for it. It needs a 4-point edit. It
   overwrites the source range into the sequence range with the constant-speed retime set so that `speed = srcDur /
   seqDur` (step 21), and it respects the retime speed limits. One `CompositeCommand`.
2. **Docs.** Add a user-docs page `edit/three-point-editing.md`, and update `edit/marks-and-markers.md`,
   `performance/preview-and-monitors.md`, and the timeline-headers page.
3. **Inventory.**
   - New FEATURES.md rows, starting ❌: source marks, source patching, targeting / sync lock / lock, Insert / Overwrite,
     drag from the Source monitor, Ctrl-drag insert, and Fit to Fill.
   - Amended rows: Lift / Extract (now scoped to targeted tracks), mark keys (now routed by monitor), bin drag (now
     overwrites), and the Source monitor row (audio-only sources).
   - Check the §9 keyboard shortcut table.
   - Check whether the README Features section needs a "three-point editing" bullet.

## Deferred (not in this feature)

- A dual side-by-side monitor layout.
- Replace edit, Place on Top, Append (Resolve Shift+F12 / FCP E), and Match Frame (`F`).
- Premiere's double-click on a timeline clip to edit its range in the Source monitor.
- Sequences as sources in the Source monitor (nesting through three-point editing). Sequences aren't bin items today.
- Overwrite on clip moves, and marker ripple on Extract.
- Keyboard shortcuts for patching and targeting (Premiere has none by default; do it with the key-binding registry
  work).

## Tests

- **Core (`Sprocket.Core.Tests`):**
  - `ThreePointResolverTests`: a table covering every combination of the four marks, backtiming, clamping for short
    source, stills, and the 4-point note.
  - `ThreePointEditsTests`: Insert and Overwrite on patched, targeted, sync-locked and locked tracks; straddling
    splits; tail link groups; transition removal and shifting; marker ripple on Insert; undo and redo restore the model
    exactly.
  - Extend `RangeEditsTests` for the scoped Lift / Extract, including a sync-locked track that isn't targeted and a
    locked track.
  - Patch re-pointing on track removal and reorder, with undo.
- **Persistence:** round trips for track flags, source marks, and the patch. Old files load unchanged with the
  defaults. Hash invariance: changing flags (other than `Enabled`), marks or patch never changes the render-cache hash.
- **App (`Sprocket.App.Tests`, where headless-testable):** mark-key routing by `_activeArea` and tab; the
  `DragFormats.SourceRange` payload round trip; drop overwrite vs Ctrl-insert command construction (tested through the
  builders, since there are no drag UI tests; manual QA covers the gesture).
- **Mcp:** `insert_edit` / `overwrite_edit` / `lift` / `extract` / `set_source_marks` / `set_track_state` go through
  `EditHistory` and are undoable.
- **Manual QA:**
  - A full three-point cut from the bin through the Source monitor with `,` and `.`.
  - Ctrl-drag insert.
  - Video-only and audio-only drags.
  - Locked-track refusal.
  - The Source-monitor audio A/V check in phase 5.

## On completion

Flip the step-61 row in PLAN.md and check its todo. Append the DONE log to `plan/history/steps-58plus.md` under
`## Step 61`, and update FEATURES.md and the README. Then delete or archive the parts of this file that are no longer
open.
