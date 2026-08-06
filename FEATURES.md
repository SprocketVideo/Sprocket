# Sprocket feature inventory

Working document that drives user-documentation coverage. It lives **in the app
repo** so that shipping or changing a user-facing feature and updating its row
happen in the same commit. The guides it references live in the docs repo:
`../sprocket-docs/src/content/docs/` (published at docs.sprocketvideo.org).
Entries in the Docs column are `folder/guide.md#anchor` paths relative to that
folder.

One row per feature at the granularity a user thinks of ("Ripple edit", not
"ripple drag hit-testing").

> **Docs coverage audited against** `sprocket` @ `92226ec` on 2026-07-02.
> A full re-audit only needs `git log 92226ec..HEAD` plus RELEASE_NOTES.md
> diffs; update the affected rows and bump this stamp.

> **IA note (2026-07-10):** the docs site was reorganized into grouped folders
> (`get-started/`, `media/`, `edit/`, `effects-color/`, `audio/`, `export/`,
> `performance/`, `ai/`), each a sidebar group. The numbered sections below
> mirror those groups; a group with several guides is split into `###`
> subsections. The old flat `getting-started.md`/`color-grading.md` mega-guides
> were split — the everyday-effects, audio, and export walkthrough steps now
> have dedicated guides (`effects-color/effects.md`, `audio/audio-mixing.md`,
> `export/exporting.md`), and log-footage moved to `media/log-footage.md`.

**Doc-coverage symbols** — every feature listed here is shipped and working; this column
only tracks whether a user guide covers it yet, not whether it exists.

| | |
|---|---|
| ✅ | documented — the Docs column links to the covering guide + anchor |
| 🟡 | partially documented — exists in a guide but incomplete or shallow |
| ❌ | undocumented — shipped in the app, no guide covers it yet |
| ➖ | deliberately not documented (internal, developer-facing, or disabled in the UI) |

**Maintenance contract**

- **App side (this repo):** when a user-facing feature ships or changes
  behavior, add or amend its row in the same change (new features start ❌).
  This complements PLAN.md's step markers: PLAN tracks *build order*, this file
  tracks *doc coverage*.
- **App side (§0 rows):** when a change matches a §0 row's staleness trigger,
  flip that row to 🟡/❌ in the same change with a one-line note of what
  changed (e.g. "🟡 stale — new Ripple button in toolbar; quick tour
  screenshot + text").
- **Docs side (`../sprocket-docs`):** when a guide is written or extended,
  update the affected rows' Docs status and Docs columns in the same change.
- **Docs side (§0 rows):** when a page listed in §0 is updated, bump its
  Audited @ stamp to the app commit the update was checked against and reset
  its Status, in the same change.
- The numbered sections below are the intended docs-site information
  architecture (one per sidebar group): new guides should map to one section or
  `###` subsection.
- Docs anchors listed here are load-bearing (see sprocket-docs/CLAUDE.md) —
  if one changes, fix it here too.

---

## 0. Doc pages & cross-cutting sections

Unlike feature rows, these track *pages/sections of the docs site* whose
content spans many features — the getting-started guide, the quick tour, the
shortcut reference, the landing page. Each row has its own audit stamp; a row
goes stale when any feature it draws from changes, even if no single feature
row below flips status. The **Staleness trigger** column states the condition
in terms an app-side committer can check against their diff.

| Page / section | Draws from | Staleness trigger | Audited @ | Status |
|---|---|---|---|---|
| get-started/getting-started.md (whole guide) | §§1–4, 6–7 (everyday subset) | a *common-task* workflow changes, or a new feature belongs in the everyday path | 92226ec | 🟡 stale — the playhead can now be parked past the last clip, so scrubbing/transport no longer stops at the end of the content; and the timeline now follows the playhead during playback (View ▸ Playback Auto-Scroll, Page Scroll by default) |
| get-started/getting-started.md#a-quick-tour-of-the-main-screen | §1 (all visible chrome) | anything visible in the main window changes: toolbar, panels, status bar, menus | 972911b | ✅ current (Program-monitor bullet now links out to the new preview-and-monitors guide) |
| get-started/getting-started.md#keyboard-shortcuts-worth-knowing | §9 (Keyboard shortcuts) | any shortcut in the curated table changes | pending | ✅ current (zoom row now shows the bare `-`/`=` keys — re-stamp Audited @ to this change's commit) |
| get-started/keyboard-shortcuts.md (full page) | §9; MainWindow.axaml.cs key handlers + menu InputGestures | any key handler or InputGesture added/changed | pending | ✅ current (bare `-`/`=` zoom rows + the text-field and off-screen-playhead notes added — re-stamp Audited @ to this change's commit) |
| get-started/projects-and-saving.md | §1 (Projects & saving); §9 (Autosave interval) | New/Open/Save/Save As, the unsaved-changes prompt, autosave + crash recovery, the autosave interval preference, or Relink Media change | 777f288 | ⚠️ stale — the unsaved-changes section still describes the old two-button Discard/Cancel prompt and does not cover closing / quitting |
| index.md (landing page) | guide list + group structure | a guide is added/renamed, or a sidebar group changes | 972911b | ✅ current (added the Preview and monitors guide under Playback & performance) |
| edit/editing-on-the-timeline.md | §3 (Timeline editing; Clips: speed / frame hold / frame edits) | any timeline tool/behavior changes, or the Speed/Duration or Frame Hold dialogs change | 487ace6 | 🟡 stale — the playhead can now be parked in the empty space past the last clip, a scrub held at the viewport edge auto-scrolls, and the view follows the playhead during playback (View ▸ Playback Auto-Scroll: No / Page / Smooth Scroll) |
| edit/marks-and-markers.md | §3 (Markers; In/Out marks; Play In to Out) | the Markers panel, in/out mark keys/overlay, or Play In to Out change | 487ace6 | ✅ current |
| edit/titles-and-generators.md | §3 (Titles, generators & layers) | the Clip ▸ Insert generator set, the title Inspector sections, or adjustment-layer placement change | 487ace6 | ✅ current |
| edit/multicam-and-sequences.md | §3 (Multicam & sequences) | multicam creation/angle-switching, the Sequence menu (New/Open/Settings), or nesting change | 0e665cb | ✅ current (sequence section rewritten for the frame-size picker; format-preset detail moved to aspect-ratio-and-framing.md; sequence-settings.png re-captured) |
| edit/aspect-ratio-and-framing.md | §3 (Sequence format presets; per-clip conform; Inspector Framing) | the New Sequence / Sequence Settings frame-size picker, the preset list, per-clip Fit/Fill conform, or the Inspector Framing controls (Conform / Center / Fill Height / Reset Framing) change | 0e665cb | ✅ current (new guide) |
| effects-color/effects.md | §4 (Effects & the effect stack) | the everyday effects, the Effects menu, or the Inspector effect UI change | 92226ec | ✅ current |
| effects-color/color-grading.md | §4 (Color grading & scopes) | grading effects, scopes, or wheel UI change | 438f6e2 | ✅ current |
| media/importing-media.md | §2 (Importing media & the Project panel) | the import formats/picker, media-bin tiles/badges/search, Project-panel tabs, still/sequence/alpha import, or the Interpret Footage dialog change | 5227505 | ✅ current |
| media/log-footage.md | §2 (Working with log & camera footage) | Input Color Transform, the camera LUT set, or ACES Filmic change | 438f6e2 | ✅ current |
| audio/audio-mixing.md | §5 (track mute/solo, the Fade effect; the mixer: gain/pan, master strip, loudness meters, normalization, insert chains) | per-track audio controls, the Fade effect, or any mixer control (strips, master, meters, Normalize targets, inserts) change | 9e7bc4d | ✅ current (expanded from levels-only to the full mixer: strips, master, loudness meters, normalize, inserts) |
| audio/audio-effects.md | §5 (audio-effect library: Gain/Pan, EQ, Compressor, Noise Gate, reverbs, delays; preset picker; Freeze/Unfreeze Clip Audio) | an audio effect is added/changed, the audio-effect Inspector UI or preset picker changes, or Freeze/Unfreeze Clip Audio changes | 9e7bc4d | ✅ current (new guide) |
| export/exporting.md | §6 (Export Settings; format matrix; audio-only; presets; burn-ins; handles; encoding; color; metadata; progress/reveal) | the Export Settings dialog, its defaults, the format/codec matrix, the built-in preset list, or the audio-only formats change | ddf153b | ✅ current (quality/encoding section rewritten for the two-mode Rate control; portrait/square resolutions added; export-settings.png re-captured + new export-rate-bitrate.png) |
| export/export-queue-and-interchange.md | §6 (Export Queue; EDL/Final Cut XML interchange) | the Export Queue window controls/statuses, or the interchange formats/warnings change | 48acaf5 | ✅ current (new guide) |
| performance/preview-and-monitors.md | §7 (Source monitor; Preview zoom; Guides overlay; Full-screen preview) | the Program/Source tabs, the Fit ▾ zoom levels, the Guides overlay, or full-screen preview change | 972911b | ✅ current (new guide) |
| ai/ai-control.md | §8 (+ §9 Preferences MCP settings, §4 effect tags) | the MCP tool surface, Preferences AI section, setup command, status-bar indicator, or effect-tag UI changes | c86c921 | ✅ current |

## 1. Get started

### Window & layout

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Supported platforms (Windows 10 & 11 — Win10 floor 64-bit, version 1809+ — Linux, macOS; self-contained builds, no .NET/FFmpeg install) | README.md platform table; PLAN.md step 56 | get-started/getting-started.md#system-requirements | ✅ |
| Main screen layout (menu bar, toolbar, panels, timeline, status bar) | UI.md §3; Sprocket.App/MainWindow.axaml | get-started/getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Custom window chrome, native per OS (drag, double-click maximize; Windows/Linux caption buttons — on Windows a real Win32 caption, so the maximize button shows the snap-layouts flyout; macOS uses the native traffic lights instead) | MainWindow.axaml.cs `ConfigureWindowChrome` / `WireWindowChrome` | — | ❌ |
| macOS system menu bar (File…Help at the top of the screen; About / Preferences ⌘, in the Sprocket application menu; Window ▸ Minimize ⌘M / Zoom) | MacMenuBridge.cs | — | ❌ |
| Full-screen window (View ▸ Full Screen; `F11`, `⌃⌘F` and the native green button on macOS; `Esc` exits; title bar/menu stays visible; never persisted across launches) | MainWindow.axaml.cs `ToggleWindowFullScreen` | — | ❌ |
| Resizable panels (splitters) | MainWindow.axaml GridSplitters | get-started/getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Show/hide Project & Inspector panels (View menu) | MainWindow.axaml.cs `SetPanelVisible` | — | ❌ |
| Reset Layout | MainWindow.axaml.cs `ResetLayout` | — | ❌ |
| Window-state persistence (reopens maximized/centered; size, position and maximized/full-screen state also survive opening a project) | Sprocket.App/WindowStateStore.cs (`WindowStateStore`, `WindowPlacement`) | — | ➖ (invisible; mention only if asked) |
| Project name + saved/unsaved indicator in title bar | MainWindow.axaml.cs:1609 | get-started/getting-started.md#11-save-your-project | ✅ |
| Status bar (engine state, messages, live fps/size/duration) | UI.md §3.7; MainWindow.axaml.cs `RenderTelemetry` | get-started/getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Help ▸ About (version, open logs folder) | Sprocket.App/Dialogs.cs `AboutDialog` | — | ❌ |
| Help ▸ Third-Party Notices (bundled library/font/media licenses) | Sprocket.App/Dialogs.cs `ThirdPartyNoticesDialog`; THIRD-PARTY-NOTICES.md | — | ❌ |
| Auto-update (Help ▸ Check for Updates; status-bar badge with Help-menu discoverability dot + age-based colour escalation; one-time first-run toast per version; installed builds download + Install & Restart in-app with an inline "what's new" changelog for the offered version and a "Full Release Notes" link to that release's GitHub page; portable builds link to the releases page) | Sprocket.App/UpdateService.cs; UpdateDialogs.cs; UpdateEscalation.cs; scripts/release.ps1 `Get-FeedReleaseNotes`; PLAN.md steps 36 + 45 | — | ❌ |
| Add to applications menu (Linux AppImage only: first-run offer + Help ▸ Add/Remove from Applications Menu; writes a per-user launcher + icon so the AppImage appears in the menu) | Sprocket.App/LinuxDesktopIntegration.cs; MainWindow.axaml.cs `MaybeOfferLinuxDesktopIntegrationAsync` | — | ❌ |
| Help ▸ Sprocket Website (opens sprocketvideo.org; also linked in About) | MainWindow.axaml.cs `OpenWebsiteAsync`; Dialogs.cs `AboutDialog.WebsiteUrl` | — | ➖ (trivial; mention only if asked) |
| Help ▸ Documentation (opens sprocketvideo.org/docs/) | MainWindow.axaml.cs `OpenUriAsync`; Dialogs.cs `AboutDialog.DocsUrl` | — | ➖ (trivial; mention only if asked) |
| Help ▸ Report an Issue (opens the GitHub new-issue chooser) | MainWindow.axaml.cs `OpenUriAsync`; Dialogs.cs `AboutDialog.ReportIssueUrl`; .github/ISSUE_TEMPLATE/ | — | ➖ (trivial; mention only if asked) |
| Help ▸ Support Sprocket ♥ (opens the GitHub Sponsors funding page) | MainWindow.axaml.cs `OpenUriAsync`; Dialogs.cs `AboutDialog.SupportUrl`; .github/FUNDING.yml | — | ➖ (trivial; mention only if asked) |

### Projects & saving

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| New Project (`Ctrl+N`) | MainWindow.axaml.cs `NewProject` | get-started/getting-started.md#open-something-to-work-with | ✅ |
| Open Project (`Ctrl+O`) | MainWindow.axaml.cs `OpenProjectAsync` | get-started/getting-started.md#open-something-to-work-with | ✅ |
| Open Sample Project | MainWindow.axaml.cs `OpenSampleProject` | get-started/getting-started.md#open-something-to-work-with | ✅ |
| Save / Save As (`Ctrl+S` / `Ctrl+Shift+S`) | MainWindow.axaml.cs `Save`/`SaveAsAsync` | get-started/getting-started.md#11-save-your-project | ✅ |
| Unsaved-changes prompt when dirty (Save · Don't Save · Cancel) — guards New / Open / Open Sample **and** closing the window / Exit / Quit; Save that fails or is cancelled aborts the action | MainWindow.axaml.cs `ConfirmSaveIfDirtyAsync`, `ConfirmCloseAsync`, `OnClosing`; App.axaml.cs `OnShutdownRequested` | get-started/projects-and-saving.md#the-unsaved-changes-safety-check | ❌ (page documents the old two-button discard prompt, and predates the close/quit guard) |
| Autosave + crash recovery (recover-newer-autosave prompt) | Sprocket.App/AutosaveService.cs; `ShouldRecoverAsync` | get-started/projects-and-saving.md#autosave-and-crash-recovery | ✅ |
| Relink Media (folder pick, match preview) | MainWindow.axaml.cs `RelinkMediaAsync` | get-started/projects-and-saving.md#relink-moved-or-missing-media | ✅ |
| Undo / Redo with named steps (`Ctrl+Z` / `Ctrl+Shift+Z`, `⌘Z` / `⌘⇧Z` on macOS; `Ctrl+Y` alias on Windows/Linux only) | UI.md; MainWindow.axaml.cs:322 | get-started/getting-started.md#10-undo-and-redo | ✅ (`Ctrl+Y` alias in the shortcut reference) |

## 2. Import & organize media

### Importing media & the Project panel

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Import Media (`Ctrl+I`, file picker) | MainWindow.axaml.cs `ImportDialogAsync` | media/importing-media.md#import-media-into-your-project | ✅ (also in the getting-started quick path at #open-something-to-work-with) |
| Drag-and-drop files from OS to import | MainWindow.axaml.cs:795 | media/importing-media.md#import-media-into-your-project | ✅ |
| Supported import formats (16 video + 11 audio + 9 image extensions) | MainWindow.axaml.cs `VideoFileType`/`AudioFileType`/`ImageFileType` | media/importing-media.md#supported-file-formats | ✅ |
| Media bin: thumbnails, waveforms, format/duration badges | Sprocket.App/MediaBrowser/MediaBrowserPanel.cs | media/importing-media.md#reading-the-media-bin | ✅ |
| Media bin search/filter | MediaBrowser/MediaSearch.cs | media/importing-media.md#finding-media-in-the-bin | ✅ |
| Project panel tabs: Media / Effects / Transitions / Audio | MediaBrowserPanel.cs:77 | media/importing-media.md#the-project-panel | ✅ |
| Drag media from bin onto timeline tracks | TimelineControl.cs `OnDrop` | media/importing-media.md#putting-media-on-the-timeline | ✅ |
| Alpha-channel media import & compositing | PLAN.md step 26; Sprocket.Render/SkiaEffectPipeline.cs (premultiplied-alpha compositing); MediaBrowser/MediaBadges.cs | media/importing-media.md#transparent-alpha-media | ✅ (Alpha badge + compositing on an upper track; the premultiplied render path itself stays internal) |
| Image-sequence import (numbered stills → one clip, fps choice) | PLAN.md step 42; ImageSequenceDetection.cs, ImageSequenceImportDialog.cs, MediaImport.cs | media/importing-media.md#image-sequences | ✅ |
| Still-image import (single image, default duration preference) | PLAN.md step 42; MediaImport.cs, PreferencesDialog.cs | media/importing-media.md#still-images | ✅ |
| Interpret Footage (reassign frame rate; media-bin & Clip menu) | PLAN.md step 42; ReinterpretFootageCommand, InterpretFootageDialog.cs | media/importing-media.md#interpret-footage-changing-a-clips-frame-rate | ✅ |

### Working with log & camera footage

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Log footage: Input Color Transform (DJI D-Log family via vendor LUT; ARRI LogC3/LogC4, Sony S-Log3, Panasonic V-Log, Canon C-Log3, Blackmagic Film Gen 5, Fujifilm F-Log2, Nikon N-Log via math curve), ACES Filmic | EffectCatalog.cs; Sprocket.Render/{ColorLuts,CubeLut}.cs + Effects/AcesFilmicEffect.cs; PLAN.md steps 37, 52 | media/log-footage.md#log-footage-input-color-transform-and-aces-filmic | ✅ |

## 3. Edit

### Timeline editing

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Select tool — move & edge-trim (side-specific trim cursor on edge hover) | UI.md §3.2; TimelineControl.cs; TimelineMath.HoverCursor | edit/editing-on-the-timeline.md#select--move-and-trim | ✅ (hover trim cursor covered in #the-editing-tools) |
| Blade tool — split clips (hover cut-line preview) | TimelineControl.cs `BladeClip` / `DrawBladePreview` | get-started/getting-started.md#5-split-a-clip-with-the-blade; edit/editing-on-the-timeline.md#the-editing-tools | ✅ (split in getting-started; cut-line preview noted in #the-editing-tools) |
| Per-tool custom cursors (trim/ripple brackets, roll, slip/slide, razor, hand, magnifier) | ToolCursors.cs; TimelineMath.HoverCursor | edit/editing-on-the-timeline.md#the-editing-tools | ✅ |
| Ripple tool | PLAN.md step 22; TimelineControl `DragKind.Ripple` | edit/editing-on-the-timeline.md#ripple | ✅ |
| Roll tool | TimelineControl `DragKind.Roll` | edit/editing-on-the-timeline.md#roll | ✅ |
| Slip tool | TimelineControl `DragKind.Slip` | edit/editing-on-the-timeline.md#slip | ✅ |
| Slide tool | TimelineControl `DragKind.Slide` | edit/editing-on-the-timeline.md#slide | ✅ |
| Hand & Zoom view tools | TimelineControl.cs | edit/editing-on-the-timeline.md#getting-around-hand-and-zoom | ✅ |
| Snapping toggle | TimelineControl.Snapping | edit/editing-on-the-timeline.md#snapping | ✅ |
| Linked A/V toggle + Link / Unlink (Clip menu & context menu; `Ctrl+L` toggles by selection state — Link needs a multi-selection spanning video + audio) | MainWindow.axaml.cs:1134; TimelineControl `LinkSelected`/`UnlinkSelected`; ClipEdits.cs link builders; PLAN.md steps 13/55 | edit/editing-on-the-timeline.md#keeping-audio-and-video-together | ✅ |
| Timeline zoom in/out/fit (`-`/`=` or `Ctrl+-`/`Ctrl+=`; `Shift+Z` fits). Zoom anchors on the playhead, or on the viewport centre when the playhead is off-screen | TimelineControl `ZoomIn/Out/ToFit`; TimelineMath `ZoomAnchorX` | get-started/getting-started.md#2-zoom-the-timeline-in-and-out | ✅ |
| Move / delete / Ripple Delete (`Delete` / `Shift+Delete`) | TimelineControl.cs:2184 | edit/editing-on-the-timeline.md#deleting-clips-and-closing-gaps | ✅ |
| Cut / Copy / Paste clips (paste at playhead; a multi-clip selection pastes as a set with relative offsets preserved) | Sprocket.App/ClipboardOps.cs; PLAN.md step 54 | edit/editing-on-the-timeline.md#cut-copy-and-paste | ✅ |
| Nudge clip by one frame (`Alt+←` / `Alt+→`) | MainWindow.axaml.cs:368 | edit/editing-on-the-timeline.md#nudging-with-the-keyboard | ✅ |
| Multi-clip selection: Ctrl-click toggles, Shift-click extends, rubber-band marquee on empty lane area, Select All (`Ctrl+A`); batch Delete / Ripple Delete / Cut / Copy / Nudge / Enable act on the set as one undo entry; a plain drag moves the set rigidly; the primary clip drives the Inspector and dialog-backed operations | PLAN.md step 54; Timeline/ClipSelection.cs; ClipEdits.cs batch builders; TimelineControl.cs | edit/editing-on-the-timeline.md#selecting-several-clips-at-once | ✅ |
| Clip right-click context menu, shaped by lane kind (common: Cut/Copy/Paste/Duplicate, Delete/Ripple Delete, Split at Playhead, Enable, Unlink, Link, Speed, Nest; video clips add Frame Hold ▸, Interpret Footage, Multicam ▸; audio clips add Normalize Audio) | PLAN.md step 53; TimelineControl `ClipContextMenuRequested`; MainWindow.axaml.cs `ShowClipContextMenu` | edit/editing-on-the-timeline.md#the-right-click-menu | ✅ |
| Split at Playhead (`Ctrl+K`, Clip menu & context menu) | PLAN.md step 53; TimelineControl `SplitAtPlayhead`; ClipEdits.cs | edit/editing-on-the-timeline.md#splitting-a-clip-at-the-playhead | ✅ |
| Duplicate clip (Clip menu & context menu; linked pair copies under a fresh group) | PLAN.md step 53; TimelineControl `DuplicateSelected`; ClipEdits.cs | edit/editing-on-the-timeline.md#duplicating-a-clip | ✅ |
| Enable/Disable clip (`Shift+E`, checkable Clip-menu/context item; disabled clips render nothing in preview *and* export, draw dimmed) | PLAN.md step 53; Clip.Enabled; TimelineControl `ToggleSelectedEnabled`; PlaybackEngine `ActiveVideoClip` | edit/editing-on-the-timeline.md#turning-a-clip-off-without-deleting-it | ✅ |
| Add video/audio tracks (+ Track) | MainWindow.axaml.cs `AddTrack` | get-started/getting-started.md#9-add-a-track | ✅ |
| Track header toggles: Enable (eye) / Mute / Solo | TimelineControl.cs `HandleHeaderClick` | audio/audio-mixing.md#adjust-the-audio | 🟡 (M/S/eye covered; per-track *video* enable only parenthetical) |
| Rename a track (double-click header) | TimelineControl.cs:1380 | edit/editing-on-the-timeline.md#renaming-and-resizing-tracks | ✅ |
| Resize track-header column | TimelineControl.cs:1371 | edit/editing-on-the-timeline.md#renaming-and-resizing-tracks | ✅ |
| Fade handles & opacity rubber-band on clips | PLAN.md step 39; TimelineControl.cs:40–99 | edit/editing-on-the-timeline.md#fading-a-clip-in-or-out | ✅ |
| Clip filmstrip & waveform thumbnails | TimelineControl.cs:1257 | get-started/getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Markers: add (`M`), navigate (`Shift+M`/`Ctrl+Shift+M`), Markers panel | MainWindow.axaml.cs `AddMarker`, `BuildMarkersPanel` | edit/marks-and-markers.md#markers | ✅ (add/navigate + the Markers panel; panel supports add/seek/remove only) |
| In/Out marks: set (`I`/`O`), clear (`Alt+I`/`Alt+O`), range overlay | MainWindow.axaml.cs `SetMarkAtPlayhead` | edit/marks-and-markers.md#in-and-out-marks | ✅ |
| Play In to Out (`Ctrl+Shift+Space`, Sequence menu): plays only the marked range, stops at the out mark, replays from the in mark; plain Space stays unconstrained | PlaybackEngine `PlayInToOut`; MainWindow.axaml.cs `PlayInToOut` | edit/marks-and-markers.md#play-just-the-marked-range | ✅ |

### Clips: speed, freeze frames & stop motion

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Clip Speed / Duration dialog (constant speed, presets) | Dialogs.cs `SpeedDialog`; PLAN.md step 21 | edit/editing-on-the-timeline.md#changing-a-clips-speed | ✅ |
| Frame hold / freeze frame: Frame Hold Options…, Add Frame Hold, Insert Frame Hold Segment (Clip menu; HOLD badge, Inspector Hold row) | PLAN.md step 43; FrameHoldOptionsDialog.cs, TimelineControl `AddFrameHoldAtPlayhead` | edit/editing-on-the-timeline.md#freezing-a-frame-frame-hold | ✅ |
| Stop-motion frame edits: Duplicate Frame / Remove Frame (source-frame grid, ripple ±1 frame) | PLAN.md step 43; Sprocket.Core/Commands/FrameHoldEdits.cs | edit/editing-on-the-timeline.md#duplicate-or-remove-a-single-frame-stop-motion | ✅ |

### Titles, generators & layers

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Insert generators: Title, Lower Third, Credits Roll, Crawl, Color Matte | Sprocket.Core/Model/GeneratorCatalog.cs | edit/titles-and-generators.md#adding-a-title-or-other-generator | ✅ |
| Edit title text inline (double-click title clip) | TimelineControl.cs:1432 | edit/titles-and-generators.md#editing-a-titles-text | ✅ |
| Rich text & titles (styling, lower thirds, credits) | PLAN.md step 40; Sprocket.Render/{TitleRenderer,TitleFonts}.cs; Sprocket.Core/Model/{Generator,GeneratorCatalog}.cs | edit/titles-and-generators.md#styling-a-title-in-the-inspector | ✅ |
| Adjustment layers | MainWindow.axaml.cs:390; PLAN.md step 19 | edit/titles-and-generators.md#adjustment-layers | ✅ |

### Multicam & sequences

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Multicam: Create Multicam Source + angle switch (`1`–`9`) | MainWindow.axaml.cs `CreateMulticamSource`; PLAN.md step 24 | edit/multicam-and-sequences.md#cutting-between-camera-angles-multicam | ✅ (angle-buttons Inspector shot deferred — sample project has one video clip) |
| Multiple sequences: New / Open / Sequence Settings (rename + editable frame size) | MainWindow.axaml.cs `NewSequenceAsync`, `SwitchToSequence`, `ShowSequenceSettingsAsync`; Dialogs.cs `SequenceSettingsDialog` | edit/multicam-and-sequences.md#working-with-more-than-one-sequence | ✅ (New Sequence + Sequence Settings now describe the frame-size picker; format presets detailed in aspect-ratio-and-framing.md) |
| Sequence format presets incl. portrait/square (1080×1920, 1080×1350, 1080×1080, 2160×3840 + Custom) in New Sequence / Sequence Settings | Sprocket.App/SequenceFormatPresets.cs; Dialogs.cs `SequenceSettingsDialog` | edit/aspect-ratio-and-framing.md#choose-your-videos-shape | ✅ |
| Per-clip conform mode: Fit (letterbox) / Fill (centre-crop) for mismatched-resolution media | Sprocket.Core/Model/Clip.cs `ConformMode`; Sprocket.Render/FramePresenter.cs `ComputeFillRect` | edit/aspect-ratio-and-framing.md#fit-or-fill-how-a-clip-meets-the-frame | ✅ |
| Inspector Framing section: Conform dropdown + Center / Fill Height / Reset Framing buttons | InspectorPanel.cs `BuildFramingSection`; Sprocket.App/FramingOps.cs | edit/aspect-ratio-and-framing.md#the-framing-controls | ✅ |
| Nest selection into a sequence (compound clips; the whole multi-clip selection nests) | MainWindow.axaml.cs `NestSelection`; PLAN.md steps 23, 54 | edit/multicam-and-sequences.md#nesting-clips-into-a-sequence | ✅ |

## 4. Effects & color

### Effects & the effect stack

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Applying effects (Effects menu, browser drag, + Effect button) | MainWindow.axaml.cs `RefreshEffectsMenu`; `ApplyEffectToSelected` | effects-color/effects.md#change-how-a-clip-looks | 🟡 (guide covers the 4 everyday effects and acknowledges the rest of the menu; browser-drag apply still unshown) |
| Transform effect (scale/position/rotation/anchor/opacity) | Sprocket.Core/Model/EffectCatalog.cs | effects-color/effects.md#change-how-a-clip-looks | ✅ |
| Color effect (exposure/contrast/saturation/vibrance) | EffectCatalog.cs | effects-color/effects.md#change-how-a-clip-looks | ✅ |
| Brightness effect | EffectCatalog.cs | effects-color/effects.md#change-how-a-clip-looks | ✅ |
| Fade effect (video opacity + audio gain) | EffectCatalog.cs | audio/audio-mixing.md#adjust-the-audio | ✅ |
| Inspector: sections, sliders/numeric entry, remove effect | Sprocket.App/Inspector/InspectorPanel.cs | get-started/getting-started.md#3-select-a-clip | 🟡 (sliders/typing covered; the typed controls — checkbox toggles for on/off params (keyframeable, hold-stepped), dropdowns for choices, integer-snapped sliders, unit-aware typing like "1.5 EV" — are new and undocumented) |
| Percentage display for ratio parameters (Opacity, Scale, and the audio effects' mix/amount controls read and accept `%`, e.g. `50%`; typing a bare `50` works too) | EffectCatalog.cs `EffectParameterDescriptor.DisplayScale`; Inspector/InspectorFormat.cs | — | ❌ |
| Drag-to-scrub numeric fields (drag over any Inspector or mixer number to change it, Shift = coarse, Ctrl = fine, Esc cancels; click to type) | Controls/DragNumber.cs; Controls/DragNumberMath.cs | — | ❌ |
| Enable/bypass an effect (green status LED in the effect header; parameters kept while bypassed) | InspectorPanel.cs `BuildEffectSection`; ModelCommands.cs `SetEffectEnabledCommand` | — | ❌ |
| Effect reference tags (unique per-instance tag chip in the effect header, e.g. RV-1 — how AI/MCP clients address an effect) | Sprocket.Core/Model/EffectTags.cs; InspectorPanel.cs | ai/ai-control.md#effect-reference-tags | ✅ |
| Effect parameter tooltips (hover a parameter label for a plain-language description) | EffectCatalog.cs `EffectParameterDescriptor.Description`; InspectorPanel.cs | — | ❌ |
| Reorder effects in the stack (drag a section header; Move Up/Down context menu) | InspectorPanel.cs; ModelCommands.cs `MoveChainEffectCommand`; PLAN.md step 51 | — | ❌ |
| Inspector: Expand All / Collapse All section buttons (pane header) | MainWindow.axaml `InspectorExpandAllButton`; InspectorPanel.cs `SetAllSectionsExpanded` | — | ❌ |

### Transitions

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Transitions: browse (Cross Dissolve, Dip to Black/White, Wipe), apply to a cut, delete | Sprocket.Core/Model/TransitionCatalog.cs; MediaBrowserPanel.cs:402 | effects-color/transitions.md | ✅ |

### Keyframing & animation

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Keyframing: animate parameters, keyframe lanes, velocity graph, `[`/`]` navigation | InspectorPanel.cs; Inspector/KeyframeLane.cs | effects-color/keyframing.md | ✅ (dedicated guide; the two former teasers in effects.md + getting-started.md now link to it) |

### Color grading & scopes

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Color grading: White Balance, Color Wheels, Curves, HSL Qualifier | EffectCatalog.cs; Sprocket.Render/Effects/{WhiteBalance,ColorWheels,Curves,HslQualifier}Effect.cs; PLAN.md step 34 | effects-color/color-grading.md | ✅ |
| Grading scopes: Waveform / RGB Parade / Vectorscope / Histogram | Sprocket.App/ScopeView.cs | effects-color/color-grading.md#judging-your-grade-with-scopes | ✅ |

## 5. Audio

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Track mute / solo | TimelineControl.cs headers | audio/audio-mixing.md#adjust-the-audio | ✅ |
| Audio fades (Fade effect; fade handles on clips) | EffectCatalog.cs; TimelineControl fade handles | audio/audio-mixing.md#adjust-the-audio; edit/editing-on-the-timeline.md#fading-a-clip-in-or-out | ✅ (Fade effect in audio-mixing; on-clip fade handles in editing-on-the-timeline) |
| Mixer (Audio tab): per-track gain/pan/mute/solo, master strip | Sprocket.App/Mixer/MixerView.cs | audio/audio-mixing.md#set-a-tracks-gain-pan-mute-and-solo; audio/audio-mixing.md#the-master-strip | 🟡 (faders/mute/solo covered; the gain and pan read-outs are now editable fields — type an exact dB value (`-6`, `-inf`) or pan (`C`, `L50`, `-50`), or drag over the number to scrub — and that is undocumented) |
| Mixer insert chains: track / Sequence Bus / Master inserts (add, enable LED, remove, Move Up/Down; click an insert to edit its parameters/keyframes in the Inspector) | MixerView.cs `BuildInsertsBlock`; Mixer/AudioChainTarget.cs; InspectorPanel.cs `SetSelectedChain`; PLAN.md step 31 follow-on | audio/audio-mixing.md#add-insert-effects-to-a-track-bus-or-the-master | ✅ |
| Loudness meters (LUFS + true peak, EBU R128) | Sprocket.Audio/Loudness/*.cs | audio/audio-mixing.md#read-the-loudness-meters | ✅ |
| Loudness normalization (clip / track / master to LUFS target) | MixerView.cs; `NormalizeSelectedClip` | audio/audio-mixing.md#match-a-loudness-target | ✅ |
| Audio effects: Gain/Pan, Parametric EQ, Compressor, Reverb (Lite) | Sprocket.Audio/Effects/*.cs; EffectCatalog.cs | audio/audio-effects.md#add-an-audio-effect | ✅ (Gain/Pan + Compressor in #levels--dynamics, Parametric EQ in #equalizers, Reverb (Lite) in #reverb) |
| Studio Reverb (Dattorro plate/hall; presets Room–Cathedral–Ambient Bloom via Inspector preset picker) | Sprocket.Audio/Effects/StudioReverbEffect.cs; EffectCatalog.cs | audio/audio-effects.md#reverb | ✅ |
| Delay effects: Digital, Tape (wow/flutter + saturation), Multi-Tap (8 taps), Stereo (Ping Pong) | Sprocket.Audio/Effects/{DigitalDelay,TapeDelay,MultiTapDelay,StereoDelay}Effect.cs; EffectCatalog.cs | audio/audio-effects.md#delay--echo | ✅ |
| Noise Gate (threshold/attack/hold/release, range floor, hysteresis) | Sprocket.Audio/Effects/NoiseGateEffect.cs; EffectCatalog.cs | audio/audio-effects.md#levels--dynamics | ✅ |
| Shelving EQ (standalone low + high shelves: freq/gain/slope, per-shelf enable) | Sprocket.Audio/Effects/ShelvingEqEffect.cs; EffectCatalog.cs | audio/audio-effects.md#equalizers | ✅ |
| Shimmer Reverb (pitch-shifted feedback wash; interval control; presets Classic–Dark–Fifth–Drone) | Sprocket.Audio/Effects/ShimmerReverbEffect.cs; EffectCatalog.cs | audio/audio-effects.md#reverb | ✅ |
| Freeze / Unfreeze Clip Audio (pre-render heavy audio chains; Sequence menu) | MainWindow.axaml.cs `UnfreezeClipAudio`; RenderCacheService.cs | audio/audio-effects.md#freeze-a-heavy-audio-chain | ✅ |
| Audio output device (Edit ▸ Preferences ▸ Audio): pick where playback is monitored; applies immediately without interrupting playback; survives restart; auto-recovers to the system default if a device is lost or unavailable; always runs on the bundled OpenAL Soft, even when a legacy system OpenAL (Creative router) is installed, so enumeration/switch/loss-recovery behave the same on every machine | UserSettings.AudioOutputDevice; PreferencesDialog.cs; Sprocket.Audio/OpenAlAudioOutput.cs `EnumerateOutputDevices`; AudioEngine `SwitchOutputDevice`/device-loss recovery | — | ❌ |

## 6. Export & share

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Export Settings dialog (`Ctrl+E`): container/codec/rate control/resolution/frame rate | Dialogs.cs `ExportSettingsDialog` | export/exporting.md#export-your-finished-video | ✅ |
| Portrait/square export resolution overrides (2160×3840, 1080×1920, 720×1280, 1080×1350, 1080×1080) with an orientation-aware 4K cap | Dialogs.cs `ExportSettingsDialog` `Resolutions`; VideoExporter.cs `ComputeExportResolution` | export/exporting.md#set-the-resolution-and-frame-rate | ✅ |
| Rate control: Constant quality (CRF slider on the codec's own scale + plain-language readout) or Target bitrate (Mbps + optional max, resolution-scaled default) | Dialogs.cs `ExportSettingsDialog` (Rate control picker); Sprocket.Export/ExportFormat.cs `ExportRateControl`/`CrfFor`/`DefaultTargetBitrate`; VideoExporter.cs `ExportOptions` | export/exporting.md#set-the-quality-and-encoding | ✅ |
| Format matrix: MP4/MOV/MKV/WebM/AVI/TS × H.264/HEVC/AV1/VP9/MPEG-2/ProRes × AAC/MP3/PCM/FLAC/AC-3/Opus | Sprocket.Export/ExportFormat.cs | export/exporting.md#format-reference | ✅ (reference tables for container×codec + audio-only formats) |
| Audio-only export (master mix, no video): WAV/PCM, FLAC, MP3, AAC/M4A, Opus | Sprocket.Export/ExportFormat.cs `ExportAudioFormat`; VideoExporter.cs `ExportAudioOnly` | export/exporting.md#export-the-audio-only | ✅ |
| Delivery presets (built-in + save your own) | ExportPresetStore.cs; UserExportPresets.cs | export/exporting.md#save-and-reuse-presets | ✅ (built-in preset list + Save Preset flow) |
| Burn-ins (timecode / clip name / watermark, 9-point position) | Dialogs.cs:593 | export/exporting.md#add-burn-ins | ✅ (all three fields + nine positions) |
| Handles (extra frames around an in/out range) | Dialogs.cs:609 | export/exporting.md#add-handles | ✅ |
| Hardware vs software encoding choice (hardware now honors the quality setting via each vendor's CQ/QP knob — NVENC cq, QSV ICQ, AMF/VAAPI CQP, VideoToolbox qscale) | Dialogs.cs `ExportSettingsDialog` (Encoding picker); Sprocket.Media/MediaEncoder.cs `VideoEncoderSettings.HardwareCandidates` + `DescribeHardwareQualityOptions`; PLAN.md step 29 | export/exporting.md#set-the-quality-and-encoding | ✅ |
| Export color handling (bake log transform vs pass-through) | Dialogs.cs:618 | export/exporting.md#choose-how-color-is-handled | ✅ |
| Export metadata tags (title/author/copyright/comment) | Dialogs.cs:622 | export/exporting.md#add-file-details-metadata | ✅ |
| Export progress, cancel, reveal in folder | Dialogs.cs `ExportProgressDialog` | export/exporting.md#while-it-exports | ✅ |
| Closing / quitting mid-export prompts first (Cancel Export and Close · Keep Exporting); confirming cancels the export — single, MCP, or queue — and waits for it to unwind so the partly-written file is deleted | MainWindow.axaml.cs `ConfirmAbortExportAsync`, `BeginExport`/`EndExport` | export/exporting.md#while-it-exports | ❌ |
| Export Queue (`Ctrl+Shift+E`): batch jobs, per-job progress | Sprocket.App/ExportQueueWindow.cs | export/export-queue-and-interchange.md#export-several-files-at-once | ✅ |
| Interchange export: EDL (CMX3600), Final Cut XML (+ warnings) | MainWindow.axaml.cs `ExportInterchangeAsync` | export/export-queue-and-interchange.md#hand-your-edit-to-another-editor | ✅ |
| Export in-to-out range only (Range selector in the Export Settings dialog: Entire sequence / In/Out range, defaulting to the marked range when marks are set; applies to single export and queued jobs) | Dialogs.cs `ExportSettingsDialog` Range picker; MainWindow.axaml.cs `MarkedExportRange` | export/exporting.md#export-just-part-of-the-timeline | ✅ |

## 7. Playback & performance

### Playback & monitors

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Transport: play/pause (`Space`), jump start/end, frame step | MainWindow.axaml.cs:899–903 | get-started/getting-started.md#1-play-and-preview-your-video | ✅ |
| Scrubber + timeline-ruler scrubbing (frame-snapped; the playhead tracks the pointer immediately, one seek per frame crossed) | MainWindow.axaml.cs:910; TimelineControl.cs:1406 | get-started/getting-started.md#1-play-and-preview-your-video | 🟡 stale — the playhead can now be parked past the last clip (see the row below); re-check the scrubbing walkthrough |
| Playhead can be parked in the empty space past the last clip (Premiere/Resolve-style open-ended sequence; a scrub held at the viewport edge auto-scrolls out; the preview goes black, the sequence duration and export are unchanged) | PlaybackEngine.AllowPlayheadPastEnd / NavigableEnd; TimelineMath.ScrollExtentPx; TimelineControl.SeekToX | — | ❌ |
| Playback auto-scroll: the timeline follows the playhead during playback — View ▸ Playback Auto-Scroll ▸ No Scroll / **Page Scroll** (default; jumps a screen at a time) / Smooth Scroll (playhead stays centred), Premiere's three modes and default. Persisted across restarts. A transport jump made while stopped (`End`, `[`/`]`, `Shift+M`) always reveals the playhead; a scrub, pan or clip drag is never fought | TimelineMath.AutoScrollX; TimelineControl.FollowPlayhead; UserSettings.TimelineAutoScroll | — | ❌ |
| Source monitor (preview a clip's raw footage; Program/Source tabs) | Sprocket.App/Monitors.cs; `WireMonitorTabs` | performance/preview-and-monitors.md#program-vs-source-the-two-monitors | ✅ |
| Preview zoom: Fit / 50 / 100 / 200% | MainWindow.axaml.cs `WireZoomAndGuides` | performance/preview-and-monitors.md#zoom-the-preview | ✅ |
| Guides overlay (frame outline / safe areas / framing grid; the frame's empty canvas shows black, matching export) | PreviewSurface.ShowGuides; Sprocket.Render/MonitorOverlay.cs | performance/preview-and-monitors.md#overlay-framing-guides | ✅ (portrait-canvas fix 2026-07-16 covered; aspect-ratio-and-framing.md's Fit/Fill shot still predates the fix — re-shoot with Guides on) |
| Full-screen preview (View ▸ Full Screen Preview; `Ctrl+F`, `⌘F` on macOS; `Esc` or the shortcut exits; transport keys stay live; works for Program and Source) | MainWindow.axaml.cs `EnterFullscreenPreview`; PreviewSurface.cs | performance/preview-and-monitors.md#full-screen-preview | ✅ |
| Timecode readouts (position / duration) | MainWindow.axaml:360 | get-started/getting-started.md#1-play-and-preview-your-video | ✅ |
| Playback Statistics overlay (View menu) | Sprocket.App/PlaybackStatsOverlay.cs | performance/troubleshooting-playback.md | 🟡 stale — dropped-frame/preview-rate semantics now speed-aware (slow-motion holds count as delivered, never as drops), pump pacing is phase-locked to the master clock, and the audio master clock is now smoothed over the device's update quantum (raw device counters step ~20–25 ms), so the counter reads 0 during healthy playback instead of a permanent aliasing floor; re-verify the metric descriptions |

### Performance: proxies & render cache

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Proxy media (automatic background proxies; status-bar indicator) | Sprocket.App/Proxy/*.cs; PLAN.md step 18 | performance/proxies-and-render-cache.md#proxies-automatic-help-with-heavy-footage | 🟡 (automatic behavior + the ready/pending messages are covered; the internal tier/resolution/CRF numbers stay undocumented. **Needs amending:** the status bar now also announces a build *starting* and reports failures — `proxy generation failed (n) — previewing originals`, the message a user sees when `ffmpeg` isn't on PATH. **Needs amending:** auto-proxy criteria are now format-aware — besides >1080p sources, 1080p-class HEVC/AV1/VP9 and 10-bit / 4:2:2 footage proxies automatically (ProRes/DNx exempt); at the Full tier that can be a same-resolution conversion) |
| Proxy window (View ▸ Proxy): per-asset proxy state with live progress + ETA and on-disk size, plus live controls — Use proxies on/off, resolution tier (Quarter / Half / Full), Pause/Resume, Rebuild All, Delete All Proxies, and per-row Delete / Generate; rows carry the why (demanding codec / 10-bit / playback dropped frames) | Sprocket.App/ProxyStatusWindow.cs; Sprocket.App/ProxySettingsOps.cs; PLAN.md step 18 | — | ❌ |
| Proxy recommendation from playback (when a single software-decoded original sustains dropped frames, the source is marked "Recommended" in the Proxy window and a status-bar hint appears; never auto-built by any path — the user clicks Generate; once per source per session; no recommendation while several clips are layered, where drops can't be attributed) | Sprocket.App/Proxy/ProxyAdvisor.cs; Sprocket.App/MainWindow.axaml.cs `SampleProxyAdvisor`; PLAN.md step 18 | — | ❌ |
| Render In to Out / Selection / Audio (Sequence menu) | MainWindow.axaml.cs `RenderRangeAsync` | performance/proxies-and-render-cache.md#render-a-range-for-smooth-playback | ✅ |
| Render bar (green/yellow/red cache states) | RenderCache/RenderBarModel.cs | performance/proxies-and-render-cache.md#the-render-bar-what-needs-rendering | ✅ |
| Delete Render Files (with disk footprint) | MainWindow.axaml.cs `DeleteRenderFilesAsync` | performance/proxies-and-render-cache.md#reclaim-disk-space | ✅ |
| Hardware-accelerated decode (automatic; software fallback, incl. auto-skip of VAAPI when the Linux system libva lacks a symbol the bundled FFmpeg trampolines — needs libva ≥ 2.21; software decode is multi-threaded) | Sprocket.Media/HardwareContext.cs; Sprocket.Media/LibVaPreflight.cs; PLAN.md step 6; README | performance/troubleshooting-playback.md#if-the-preview-cant-keep-up | ➖ (automatic; covered only as a troubleshooting note) |

## 8. AI control (MCP) & command line

**AI control is a user-facing, headline feature** — it needs its own prominent,
dedicated guide (enable → connect → edit by prompt), not a footnote under
Preferences or an "advanced" page.

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Enable AI control (Edit ▸ Preferences, or `--mcp` / `--mcp-port` flags) | Sprocket.App/PreferencesDialog.cs; CliOptions.cs | ai/ai-control.md#turn-on-ai-control + #starting-from-the-command-line | ✅ |
| Connect an AI client (Copy setup command, bearer token, loopback security model) | PreferencesDialog.cs; McpServerService.cs | ai/ai-control.md#connect-an-ai-client + #require-a-bearer-token | ✅ |
| What AI can do: the tool surface (~70 tools — edit, effects, markers, export, transport; export_video takes rate-control params: rateControl quality/bitrate, crf, bitrateMbps, maxBitrateMbps, hardware) | Sprocket.Mcp/SprocketTools*.cs; PLAN.md step 38 | ai/ai-control.md#what-ai-can-do-the-tool-reference | 🟡 (tool reference predates the export_video rate-control params) |
| AI edits are undoable (route through the same undo stack) | Sprocket.Mcp/McpEditorSession.cs | ai/ai-control.md#ai-edits-are-undoable | ✅ |
| Address effects by reference tag (effect_tag, e.g. RV-1 — stable across reorders; shown as the Inspector tag chip) | Sprocket.Core/Model/EffectTags.cs; Sprocket.Mcp/SprocketTools*.cs `ResolveEffect` | ai/ai-control.md#effect-reference-tags | ✅ |
| MCP status in the status bar | MainWindow.axaml.cs `UpdateMcpStatus` | ai/ai-control.md#the-status-bar-tells-you-when-its-on | ✅ (also named in the quick tour) |
| Open a media file from the command line (bare arg) | Sprocket.App/Program.cs | — | ❌ |
| Diagnostics: `--version`, `--ffmpeg-check`, `--audio-check`, `--probe`, `--doctor` (Linux env self-check: glibc baseline ≥ 2.35, bundled-native load, missing system libs with per-distro install hints; run by `install.sh`), `--mcp-check` (headless MCP endpoint smoke: initialize + tools/list) | Program.cs; Doctor.cs; McpCheck.cs | — | 🟡 (`--probe`/`--doctor` appear in RELEASE_NOTES bug-report + Linux-setup instructions only; docs site says nothing) |

## 9. Reference

### Keyboard shortcuts

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Complete shortcut reference (on macOS the primary modifier is `⌘` wherever Windows/Linux use `Ctrl`; Ctrl does not alias it there; `Ctrl+Y` redo is Windows/Linux-only) | MainWindow.axaml.cs `OnKeyDown` key handlers; menu InputGestures (macOS gestures swapped in `WireCommandMenus`) | get-started/keyboard-shortcuts.md | ✅ (full reference page grouped by task, with macOS differences; getting-started keeps the curated teaser) |
| Quit, per-OS native (Windows File ▸ Exit / `Alt+F4`; Linux File ▸ **Quit** / `Ctrl+Q`, the GNOME convention; macOS has no File item — AppKit's Sprocket ▸ **Quit Sprocket** `⌘Q` is the only route). Quitting or closing with unsaved changes prompts first — gated at the application level so it holds on macOS, where a Quit never reaches the window's close handler | MainWindow.axaml.cs `WireMenu` quit block + `OnKeyDown` + `OnClosing`; hidden on macOS in `WireCommandMenus`; App.axaml.cs `OnShutdownRequested` | get-started/keyboard-shortcuts.md | ❌ (page predates the Linux `Ctrl+Q` / macOS-app-menu split and the save-on-quit prompt) |
| Work-area focus ring: `Tab` / `Shift+Tab` step between the Project, Monitor, Timeline and Inspector panes (hidden panes skipped) instead of crawling every widget; the active pane reads with an accent edge. `Shift+1`–`Shift+4` activate one directly (1 Project, 2 Timeline, 3 Monitor, 4 Inspector). Text fields keep native `Tab`; inside an Inspector section `Tab` walks that section's fields first | Sprocket.App/WorkAreaFocus.cs; MainWindow.axaml.cs `WireWorkAreaFocus` / `OnShellKeyDown` | get-started/keyboard-shortcuts.md | ❌ |

### Preferences

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Preferences dialog (`Ctrl+,`) | Sprocket.App/PreferencesDialog.cs | — | ❌ |
| Cache management (clear proxy / render caches; "Clear proxy cache" routes through the proxy service so View ▸ Proxy stays consistent) | PreferencesDialog.cs | — | ❌ |
| Export metadata defaults (with `{project}`/`{username}`/`{year}`/`{date}` tokens) | PreferencesDialog.cs; Sprocket.App/MetadataTokens.cs | — | ❌ |
| Autosave interval | PreferencesDialog.cs | get-started/projects-and-saving.md#change-how-often-sprocket-autosaves | ✅ |
| Update check settings (enable/disable) | PreferencesDialog.cs; Sprocket.App/UpdateService.cs | — | ❌ |
| AI control (MCP) settings | PreferencesDialog.cs | ai/ai-control.md#turn-on-ai-control | ✅ |

---

## Planned / not yet built — do NOT document

Per PLAN.md status markers @ `92226ec`. Documenting these would describe
features users can't use; recheck each audit and promote to the matrix when built.

| Feature | Status source |
|---|---|
| Variable / ramped speed retime (also reverse) | PLAN.md step 21 (constant-speed only is done; freeze-frame shipped as the step-43 frame hold); SpeedDialog notes deferral |
| Native VST3 / AU audio plugin hosting | PLAN.md step 31 (🟡 partial) |
| Native OCIO / OFX hosting; scene-linear color management | PLAN.md step 33 (🟡 partial); [COLOR_GRADING_ROADMAP.md](COLOR_GRADING_ROADMAP.md) has the detailed parity sequence and follow-on grading roadmap |
| Convolution reverb | PLAN.md step 49 (Studio Reverb + audio freeze shipped in step 41; Shimmer Reverb shipped in step 50) |
| Code-signing & macOS notarization (installers themselves shipped: Windows Setup.exe, Linux AppImage, macOS .app via scripts/release.ps1 + Velopack; alpha is unsigned) | PLAN.md step 36 (✅ done except signing/notarization, deliberately deferred) |

## Not user-facing — never document

| Item | Why |
|---|---|
| Plugin host/SDK internals (`IVideoEffect`, load contexts) | No user-facing plugin manager yet; SDK docs are a separate deliverable |
| FFmpeg P/Invoke binding, render graph, command stack internals | Architecture, not behavior |
| Build/release scripts, CI | Developer tooling |
| `SPROCKET_APP_SECONDS`, `SPROCKET_HWACCEL` env vars | Diagnostics; at most a troubleshooting footnote |

---
