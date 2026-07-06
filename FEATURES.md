# Sprocket feature inventory

Working document that drives user-documentation coverage. It lives **in the app
repo** so that shipping or changing a user-facing feature and updating its row
happen in the same commit. The guides it references live in the docs repo:
`../sprocket-docs/src/content/docs/` (published at docs.sprocketvideo.org).
Entries in the Docs column are `guide.md#anchor` paths relative to that folder.

One row per feature at the granularity a user thinks of ("Ripple edit", not
"ripple drag hit-testing").

> **Docs coverage audited against** `sprocket` @ `92226ec` on 2026-07-02.
> A full re-audit only needs `git log 92226ec..HEAD` plus RELEASE_NOTES.md
> diffs; update the affected rows and bump this stamp.

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
- **Docs side (`../sprocket-docs`):** when a guide is written or extended,
  update the affected rows' Docs status and Docs columns in the same change.
- The section grouping below is the intended docs-site information
  architecture: new guides should map to one section (or a coherent slice).
- Docs anchors listed here are load-bearing (see sprocket-docs/CLAUDE.md) —
  if one changes, fix it here too.

---

## 1. Application window & layout

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Main screen layout (menu bar, toolbar, panels, timeline, status bar) | UI.md §3; Sprocket.App/MainWindow.axaml | getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Frameless window chrome (drag, double-click maximize, caption buttons) | MainWindow.axaml.cs `WireWindowChrome` | — | ❌ |
| Resizable panels (splitters) | MainWindow.axaml GridSplitters | getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Show/hide Project & Inspector panels (View menu) | MainWindow.axaml.cs `SetPanelVisible` | — | ❌ |
| Reset Layout | MainWindow.axaml.cs `ResetLayout` | — | ❌ |
| Window-state persistence (reopens maximized/centered) | Sprocket.App/WindowStateStore.cs | — | ➖ (invisible; mention only if asked) |
| Project name + saved/unsaved indicator in title bar | MainWindow.axaml.cs:1609 | getting-started.md#11-save-your-project | ✅ |
| Status bar (engine state, messages, live fps/size/duration) | UI.md §3.7; MainWindow.axaml.cs `RenderTelemetry` | getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Playback Statistics overlay (View menu) | Sprocket.App/PlaybackStatsOverlay.cs | — | ❌ |
| Help ▸ About (version, open logs folder) | Sprocket.App/Dialogs.cs `AboutDialog` | — | ❌ |
| Help ▸ Third-Party Notices (bundled library/font/media licenses) | Sprocket.App/Dialogs.cs `ThirdPartyNoticesDialog`; THIRD-PARTY-NOTICES.md | — | ❌ |
| Auto-update (Help ▸ Check for Updates; status-bar badge; installed builds download + Install & Restart in-app; portable builds link to the releases page) | Sprocket.App/UpdateService.cs; UpdateDialogs.cs; PLAN.md steps 36 + 45 | — | ❌ |
| Help ▸ Sprocket Website (opens sprocketvideo.org; also linked in About) | MainWindow.axaml.cs `OpenWebsiteAsync`; Dialogs.cs `AboutDialog.WebsiteUrl` | — | ➖ (trivial; mention only if asked) |

## 2. Projects & saving

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| New Project (`Ctrl+N`) | MainWindow.axaml.cs `NewProject` | getting-started.md#open-something-to-work-with | ✅ |
| Open Project (`Ctrl+O`) | MainWindow.axaml.cs `OpenProjectAsync` | getting-started.md#open-something-to-work-with | ✅ |
| Open Sample Project | MainWindow.axaml.cs `OpenSampleProject` | getting-started.md#open-something-to-work-with | ✅ |
| Save / Save As (`Ctrl+S` / `Ctrl+Shift+S`) | MainWindow.axaml.cs `Save`/`SaveAsAsync` | getting-started.md#11-save-your-project | ✅ |
| Discard-changes confirmation when dirty | MainWindow.axaml.cs `ConfirmDiscardIfDirty` | — | 🟡 (implied, never stated) |
| Autosave + crash recovery (recover-newer-autosave prompt) | Sprocket.App/AutosaveService.cs; `ShouldRecoverAsync` | — | ❌ |
| Relink Media (folder pick, match preview) | MainWindow.axaml.cs `RelinkMediaAsync` | getting-started.md#11-save-your-project | 🟡 (one-line mention; no walkthrough) |
| Undo / Redo with named steps (`Ctrl+Z` / `Ctrl+Shift+Z`, also `Ctrl+Y`) | UI.md; MainWindow.axaml.cs:322 | getting-started.md#10-undo-and-redo | ✅ (`Ctrl+Y` alias undocumented) |

## 3. Importing media & the Project panel

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Import Media (`Ctrl+I`, file picker) | MainWindow.axaml.cs `ImportDialogAsync` | getting-started.md#open-something-to-work-with | ✅ |
| Drag-and-drop files from OS to import | MainWindow.axaml.cs:795 | getting-started.md#open-something-to-work-with | ✅ |
| Supported import formats (16 video + 11 audio + 9 image extensions) | MainWindow.axaml.cs `VideoFileType`/`AudioFileType`/`ImageFileType` | — | ❌ (needs a reference list) |
| Media bin: thumbnails, waveforms, format/duration badges | Sprocket.App/MediaBrowser/MediaBrowserPanel.cs | getting-started.md#a-quick-tour-of-the-main-screen | 🟡 (named, not explained) |
| Media bin search/filter | MediaBrowser/MediaSearch.cs | — | ❌ |
| Project panel tabs: Media / Effects / Transitions / Audio | MediaBrowserPanel.cs:77 | getting-started.md#a-quick-tour-of-the-main-screen | 🟡 (tabs named; Effects/Transitions/Audio tabs never shown in use) |
| Drag media from bin onto timeline tracks | TimelineControl.cs `OnDrop` | getting-started.md#open-something-to-work-with | 🟡 (tip only) |
| Alpha-channel media import & compositing | PLAN.md step 26; MediaBadges.cs | — | ❌ |
| Image-sequence import (numbered stills → one clip, fps choice) | PLAN.md step 42; ImageSequenceDetection.cs, ImageSequenceImportDialog.cs, MediaImport.cs | — | ❌ |
| Still-image import (single image, default duration preference) | PLAN.md step 42; MediaImport.cs, PreferencesDialog.cs | — | ❌ |
| Interpret Footage (reassign frame rate; media-bin & Clip menu) | PLAN.md step 42; ReinterpretFootageCommand, InterpretFootageDialog.cs | — | ❌ |

## 4. Timeline editing

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Select tool — move & edge-trim | UI.md §3.2; TimelineControl.cs | editing-on-the-timeline.md#select--move-and-trim | ✅ |
| Blade tool — split clips | TimelineControl.cs `BladeClip` | getting-started.md#5-split-a-clip-with-the-blade | ✅ |
| Ripple tool | PLAN.md step 22; TimelineControl `DragKind.Ripple` | editing-on-the-timeline.md#ripple | ✅ |
| Roll tool | TimelineControl `DragKind.Roll` | editing-on-the-timeline.md#roll | ✅ |
| Slip tool | TimelineControl `DragKind.Slip` | editing-on-the-timeline.md#slip | ✅ |
| Slide tool | TimelineControl `DragKind.Slide` | editing-on-the-timeline.md#slide | ✅ |
| Hand & Zoom view tools | TimelineControl.cs | editing-on-the-timeline.md#getting-around-hand-and-zoom | ✅ |
| Snapping toggle | TimelineControl.Snapping | editing-on-the-timeline.md#snapping | ✅ |
| Linked A/V toggle + Unlink (Clip menu) | MainWindow.axaml.cs:1134; TimelineControl.cs:541 | editing-on-the-timeline.md#keeping-audio-and-video-together | 🟡 (toggle documented; Clip ▸ Unlink not mentioned) |
| Timeline zoom in/out/fit (`Ctrl+-`/`Ctrl+=`/`Shift+Z`) | TimelineControl `ZoomIn/Out/ToFit` | getting-started.md#2-zoom-the-timeline-in-and-out | ✅ |
| Move / delete / Ripple Delete (`Delete` / `Shift+Delete`) | TimelineControl.cs:2184 | editing-on-the-timeline.md#deleting-clips-and-closing-gaps | ✅ |
| Cut / Copy / Paste clips (paste at playhead) | Sprocket.App/ClipboardOps.cs | getting-started.md#6-move-delete-and-close-gaps | 🟡 (named; paste-at-playhead behavior unexplained) |
| Nudge clip by one frame (`Alt+←` / `Alt+→`) | MainWindow.axaml.cs:368 | editing-on-the-timeline.md#nudging-with-the-keyboard | ✅ |
| Add video/audio tracks (+ Track) | MainWindow.axaml.cs `AddTrack` | getting-started.md#9-add-a-track | ✅ |
| Track header toggles: Enable (eye) / Mute / Solo | TimelineControl.cs `HandleHeaderClick` | getting-started.md#8-adjust-the-audio | 🟡 (M/S/eye covered; per-track *video* enable only parenthetical) |
| Rename a track (double-click header) | TimelineControl.cs:1380 | — | ❌ |
| Resize track-header column | TimelineControl.cs:1371 | — | ❌ |
| Fade handles & opacity rubber-band on clips | PLAN.md step 39; TimelineControl.cs:40–99 | — | ❌ (guides only teach the Fade *effect*) |
| Clip filmstrip & waveform thumbnails | TimelineControl.cs:1257 | getting-started.md#a-quick-tour-of-the-main-screen | ✅ |
| Markers: add (`M`), navigate (`Shift+M`/`Ctrl+Shift+M`), Markers panel | MainWindow.axaml.cs `AddMarker`, `BuildMarkersPanel` | getting-started.md#keyboard-shortcuts-worth-knowing | 🟡 (add-marker shortcut + a teaser; no guide section) |
| In/Out marks: set (`I`/`O`), clear (`Alt+I`/`Alt+O`), range overlay | MainWindow.axaml.cs `SetMarkAtPlayhead` | — | ❌ |

## 5. Clips: speed, generators, multicam, sequences

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Clip Speed / Duration dialog (constant speed, presets) | Dialogs.cs `SpeedDialog`; PLAN.md step 21 | — | ❌ (Inspector shows Speed; never explained) |
| Frame hold / freeze frame: Frame Hold Options…, Add Frame Hold, Insert Frame Hold Segment (Clip menu; HOLD badge, Inspector Hold row) | PLAN.md step 43; FrameHoldOptionsDialog.cs, TimelineControl `AddFrameHoldAtPlayhead` | — | ❌ |
| Stop-motion frame edits: Duplicate Frame / Remove Frame (source-frame grid, ripple ±1 frame) | PLAN.md step 43; Sprocket.Core/Commands/FrameHoldEdits.cs | — | ❌ |
| Insert generators: Title, Lower Third, Credits Roll, Crawl, Color Matte | Sprocket.Core/Model/GeneratorCatalog.cs | — | ❌ |
| Edit title text inline (double-click title clip) | TimelineControl.cs:1432 | — | ❌ |
| Rich text & titles (styling, lower thirds, credits) | PLAN.md step 40 | — | ❌ |
| Adjustment layers | MainWindow.axaml.cs:390; PLAN.md step 19 | — | ❌ |
| Multicam: Create Multicam Source + angle switch (`1`–`9`) | MainWindow.axaml.cs `CreateMulticamSource`; PLAN.md step 24 | — | ❌ |
| Multiple sequences: New / Open / Sequence Settings (rename) | MainWindow.axaml.cs `NewSequence`, `SwitchToSequence` | — | ❌ |
| Nest selection into a sequence (compound clips) | MainWindow.axaml.cs `NestSelection`; PLAN.md step 23 | — | ❌ |

## 6. Playback, monitors & scopes

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Transport: play/pause (`Space`), jump start/end, frame step | MainWindow.axaml.cs:899–903 | getting-started.md#1-play-and-preview-your-video | ✅ |
| Scrubber + timeline-ruler scrubbing | MainWindow.axaml.cs:910; TimelineControl.cs:1406 | getting-started.md#1-play-and-preview-your-video | ✅ |
| Source monitor (preview a bin clip; Program/Source tabs) | Sprocket.App/Monitors.cs; `WireMonitorTabs` | — | ❌ (guides only cover Program) |
| Preview zoom: Fit / 50 / 100 / 200% | MainWindow.axaml.cs `WireZoomAndGuides` | getting-started.md#a-quick-tour-of-the-main-screen | 🟡 (named, not explained) |
| Guides overlay (safe areas / framing grid) | PreviewSurface.ShowGuides | getting-started.md#a-quick-tour-of-the-main-screen | 🟡 (named, not explained) |
| Grading scopes: Waveform / RGB Parade / Vectorscope / Histogram | Sprocket.App/ScopeView.cs | — | ❌ |
| Timecode readouts (position / duration) | MainWindow.axaml:360 | getting-started.md#1-play-and-preview-your-video | ✅ |

## 7. Effects, keyframing & color

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Applying effects (Effects menu, browser drag, + Effect button) | MainWindow.axaml.cs `RefreshEffectsMenu`; `ApplyEffectToSelected` | getting-started.md#7-change-how-a-clip-looks | 🟡 (guide names only 4 effects; catalog has 14 — see accuracy note below) |
| Transform effect (scale/position/rotation/anchor/opacity) | Sprocket.Core/Model/EffectCatalog.cs | getting-started.md#7-change-how-a-clip-looks | ✅ |
| Color effect (exposure/contrast/saturation/vibrance) | EffectCatalog.cs | getting-started.md#7-change-how-a-clip-looks | 🟡 (Vibrance not mentioned) |
| Brightness effect | EffectCatalog.cs | getting-started.md#7-change-how-a-clip-looks | ✅ |
| Fade effect (video opacity + audio gain) | EffectCatalog.cs | getting-started.md#8-adjust-the-audio | ✅ |
| Color grading: White Balance, Color Wheels, Curves, HSL Qualifier | EffectCatalog.cs; PLAN.md step 34 | — | ❌ |
| Log footage: Input Color Transform (DJI D-Log family), ACES Filmic | EffectCatalog.cs; PLAN.md step 37 | — | ❌ |
| Inspector: sections, sliders/numeric entry, remove effect | Sprocket.App/Inspector/InspectorPanel.cs | getting-started.md#3-select-a-clip | ✅ |
| Effect parameter tooltips (hover a parameter label for a plain-language description) | EffectCatalog.cs `EffectParameterDescriptor.Description`; InspectorPanel.cs | — | ❌ |
| Reorder effects in the stack (drag a section header; Move Up/Down context menu) | InspectorPanel.cs; ModelCommands.cs `MoveChainEffectCommand`; PLAN.md step 51 | — | ❌ |
| Inspector: Expand All / Collapse All section buttons (pane header) | MainWindow.axaml `InspectorExpandAllButton`; InspectorPanel.cs `SetAllSectionsExpanded` | — | ❌ |
| Keyframing: animate parameters, keyframe lanes, velocity graph, `[`/`]` navigation | InspectorPanel.cs; Inspector/KeyframeLane.cs | — | ❌ (teased twice in guides; never taught) |
| Transitions: browse (Cross Dissolve, Dip to Black/White, Wipe), apply to a cut, delete | Sprocket.Core/Model/TransitionCatalog.cs; MediaBrowserPanel.cs:402 | — | ❌ |

## 8. Audio

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Track mute / solo | TimelineControl.cs headers | getting-started.md#8-adjust-the-audio | ✅ |
| Audio fades (Fade effect; fade handles on clips) | EffectCatalog.cs; TimelineControl fade handles | getting-started.md#8-adjust-the-audio | 🟡 (effect covered; on-clip handles not) |
| Mixer (Audio tab): per-track gain/pan/mute/solo, master strip | Sprocket.App/Mixer/MixerView.cs | — | ❌ |
| Loudness meters (LUFS + true peak, EBU R128) | Sprocket.Audio/Loudness/*.cs | — | ❌ |
| Loudness normalization (clip / track / master to LUFS target) | MixerView.cs; `NormalizeSelectedClip` | — | ❌ |
| Audio effects: Gain/Pan, Parametric EQ, Compressor, Reverb (Lite) | Sprocket.Audio/Effects/*.cs; EffectCatalog.cs | — | ❌ |
| Studio Reverb (Dattorro plate/hall; presets Room–Cathedral–Ambient Bloom via Inspector preset picker) | Sprocket.Audio/Effects/StudioReverbEffect.cs; EffectCatalog.cs | — | ❌ |
| Delay effects: Digital, Tape (wow/flutter + saturation), Multi-Tap (8 taps), Stereo (Ping Pong) | Sprocket.Audio/Effects/{DigitalDelay,TapeDelay,MultiTapDelay,StereoDelay}Effect.cs; EffectCatalog.cs | — | ❌ |
| Noise Gate (threshold/attack/hold/release, range floor, hysteresis) | Sprocket.Audio/Effects/NoiseGateEffect.cs; EffectCatalog.cs | — | ❌ |
| Shelving EQ (standalone low + high shelves: freq/gain/slope, per-shelf enable) | Sprocket.Audio/Effects/ShelvingEqEffect.cs; EffectCatalog.cs | — | ❌ |
| Shimmer Reverb (pitch-shifted feedback wash; interval control; presets Classic–Dark–Fifth–Drone) | Sprocket.Audio/Effects/ShimmerReverbEffect.cs; EffectCatalog.cs | — | ❌ |
| Freeze / Unfreeze Clip Audio (pre-render heavy audio chains; Sequence menu) | MainWindow.axaml.cs `UnfreezeClipAudio`; RenderCacheService.cs | — | ❌ |

## 9. Export & delivery

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Export Settings dialog (`Ctrl+E`): container/codec/quality/resolution/frame rate | Dialogs.cs `ExportSettingsDialog` | getting-started.md#12-export-your-finished-video | ✅ |
| Format matrix: MP4/MOV/MKV/WebM/AVI/TS × H.264/HEVC/AV1/VP9/MPEG-2/ProRes × AAC/MP3/PCM/FLAC/AC-3/Opus | Sprocket.Export/ExportFormat.cs | — | ❌ (needs a reference table) |
| Audio-only export (master mix, no video): WAV/PCM, FLAC, MP3, AAC/M4A, Opus | Sprocket.Export/ExportFormat.cs `ExportAudioFormat`; VideoExporter.cs `ExportAudioOnly` | — | ❌ |
| Delivery presets (built-in + save your own) | ExportPresetStore.cs; UserExportPresets.cs | getting-started.md#12-export-your-finished-video | 🟡 (mentioned; built-in preset list undocumented) |
| Burn-ins (timecode / clip name / watermark, 9-point position) | Dialogs.cs:593 | getting-started.md#12-export-your-finished-video | 🟡 (position options undocumented) |
| Handles (extra frames around an in/out range) | Dialogs.cs:609 | getting-started.md#12-export-your-finished-video | 🟡 (named in screenshot; unexplained — depends on undocumented in/out marks) |
| Hardware vs software encoding choice | Dialogs.cs; PLAN.md step 29 | — | ❌ |
| Export color handling (bake log transform vs pass-through) | Dialogs.cs:618 | — | ❌ |
| Export metadata tags (title/author/copyright/comment) | Dialogs.cs:622 | — | ❌ |
| Export progress, cancel, reveal in folder | Dialogs.cs `ExportProgressDialog` | — | ❌ |
| Export Queue (`Ctrl+Shift+E`): batch jobs, per-job progress | Sprocket.App/ExportQueueWindow.cs | — | ❌ |
| Interchange export: EDL (CMX3600), Final Cut XML (+ warnings) | MainWindow.axaml.cs `ExportInterchangeAsync` | — | ❌ |
| Export in-to-out range only | in/out marks + export dialog | — | ❌ |

## 10. Performance: proxies & render cache

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Proxy media (automatic background proxies; status-bar indicator) | Sprocket.App/Proxy/*.cs; PLAN.md step 18 | — | ❌ |
| Render In to Out / Selection / Audio (Sequence menu) | MainWindow.axaml.cs `RenderRangeAsync` | — | ❌ |
| Render bar (green/yellow/red cache states) | RenderCache/RenderBarModel.cs | — | ❌ |
| Delete Render Files (with disk footprint) | MainWindow.axaml.cs `DeleteRenderFilesAsync` | — | ❌ |
| Hardware-accelerated decode (automatic; software fallback) | PLAN.md step 6; README | — | ➖ (automatic; cover only in a troubleshooting note) |

## 11. Preferences

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Preferences dialog (`Ctrl+,`) | Sprocket.App/PreferencesDialog.cs | — | ❌ |
| Cache management (clear proxy / render caches) | PreferencesDialog.cs | — | ❌ |
| Export metadata defaults | PreferencesDialog.cs | — | ❌ |
| Autosave interval | PreferencesDialog.cs | — | ❌ |
| Update check settings (enable/disable) | PreferencesDialog.cs; Sprocket.App/UpdateService.cs | — | ❌ |
| AI control (MCP) settings | PreferencesDialog.cs | — | ❌ (cover within the AI-control guide — see §13) |

## 12. Keyboard shortcuts

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Complete shortcut reference | MainWindow.axaml.cs:456–547 key handlers; menu InputGestures | getting-started.md#keyboard-shortcuts-worth-knowing | 🟡 (a "worth knowing" table exists; full reference page missing — code is the only complete source, incl. `I`/`O`, `[`/`]`, `1`–`9`, `Shift+M`, `Ctrl+Y`, `Ctrl+Shift+E`, `Ctrl+,`) |

## 13. AI control (MCP) & command line

**AI control is a user-facing, headline feature** — it needs its own prominent,
dedicated guide (enable → connect → edit by prompt), not a footnote under
Preferences or an "advanced" page.

| Feature | Source of truth | Docs | Docs status |
|---|---|---|---|
| Enable AI control (Edit ▸ Preferences, or `--mcp` / `--mcp-port` flags) | Sprocket.App/PreferencesDialog.cs; CliOptions.cs | — | ❌ |
| Connect an AI client (Copy setup command, bearer token, loopback security model) | PreferencesDialog.cs; McpServerService.cs | — | ❌ |
| What AI can do: the tool surface (~50 tools — edit, effects, markers, export, transport) | Sprocket.Mcp/SprocketTools*.cs; PLAN.md step 38 | — | ❌ (needs a reference list, like export formats) |
| AI edits are undoable (route through the same undo stack) | Sprocket.Mcp/McpEditorSession.cs | — | ❌ (key trust point — state it explicitly) |
| MCP status in the status bar | MainWindow.axaml.cs `UpdateMcpStatus` | — | ❌ |
| Open a media file from the command line (bare arg) | Sprocket.App/Program.cs | — | ❌ |
| Diagnostics: `--version`, `--ffmpeg-check`, `--probe` | Program.cs | — | 🟡 (`--probe` appears in RELEASE_NOTES bug-report instructions only; docs site says nothing) |

---

## Planned / not yet built — do NOT document

Per PLAN.md status markers @ `92226ec`. Documenting these would describe
features users can't use; recheck each audit and promote to the matrix when built.

| Feature | Status source |
|---|---|
| Variable / ramped speed retime (also reverse) | PLAN.md step 21 (constant-speed only is done; freeze-frame shipped as the step-43 frame hold); SpeedDialog notes deferral |
| Native VST3 / AU audio plugin hosting | PLAN.md step 31 (🟡 partial) |
| Native OCIO / OFX hosting; scene-linear color management | PLAN.md step 33 (🟡 partial) |
| Convolution reverb | PLAN.md step 49 (Studio Reverb + audio freeze shipped in step 41; Shimmer Reverb shipped in step 50) |
| Installers / packaging (Windows installer, AppImage, notarized macOS app) | PLAN.md step 36 (⏳ not done) |
| Disabled menu items: Edit ▸ Select All, Clip ▸ Enable, Clip ▸ Link | greyed out in MainWindow.axaml |

## Not user-facing — never document

| Item | Why |
|---|---|
| Plugin host/SDK internals (`IVideoEffect`, load contexts) | No user-facing plugin manager yet; SDK docs are a separate deliverable |
| FFmpeg P/Invoke binding, render graph, command stack internals | Architecture, not behavior |
| Build/release scripts, CI, spike projects (`Sprocket.Spike`) | Developer tooling |
| `SPROCKET_APP_SECONDS`, `SPROCKET_HWACCEL` env vars | Diagnostics; at most a troubleshooting footnote |

---

