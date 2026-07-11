using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// The custom-drawn timeline (PLAN.md step 12, UI.md §3.6): a ruler + playhead, one lane per track (video on
/// top, audio below) with clips drawn as schematic filmstrip/waveform blocks, drag-to-move and edge-trim, track
/// mute/solo/enable toggles, zoom and horizontal scroll. Geometry lives in <see cref="TimelineMath"/> (pure,
/// tested); every model mutation flows through the step-10 <see cref="EditHistory"/> — a drag is one coalesced
/// undo entry. Real decoded thumbnails / waveforms are step 15; the slice draws schematic fills.
/// </summary>
public sealed class TimelineControl : Control
{
    // Layout constants (px).
    private const double DefaultHeaderWidth = 132;
    private const double MinHeaderWidth = 72;
    private const double MaxHeaderWidth = 360;
    private const double RulerHeight = 26;
    private const double TrackHeight = 46;
    private const double TrackGap = 4;
    private const double EdgeGrip = 7;
    private const double NameLeft = 10;
    private const double MinPxPerSecond = 8;
    private const double MaxPxPerSecond = 600;
    private const double SnapTolerancePx = 8;

    // Fade handles + opacity rubber-band (PLAN.md step 39): the vertical inset of the band's 0/1 levels inside
    // the clip body, the grab tolerance around the band line, and the corner-handle geometry (its grip radius
    // and the top strip it wins in — checked before the edge-trim zones so a zero-length fade's corner handle
    // stays reachable).
    private const double FadeBandPad = 2;
    private const double FadeBandGrip = 4;
    private const double FadeHandleGrip = 6;
    private const double FadeHandleBand = 9;

    private static readonly IBrush PaneBg = Brush("#101016");
    private static readonly IBrush RulerBg = Brush("#16161C");
    private static readonly IBrush HeaderBg = Brush("#1A1A22");
    private static readonly IBrush LaneEven = Brush("#14141B");
    private static readonly IBrush LaneOdd = Brush("#171720");
    private static readonly IBrush VideoFill = Brush("#2F3A5C");
    private static readonly IBrush AudioFill = Brush("#2C4A39");
    private static readonly IBrush SequenceFill = Brush("#1F5C63"); // nested-sequence clips (teal, distinct from media)
    private static readonly IBrush MulticamFill = Brush("#5C3A6B"); // multicam clips (violet, distinct from media/nest)
    private static readonly IBrush ClipDetail = Brush("#4A567E");
    private static readonly IBrush AudioDetail = Brush("#4F7A60");
    // Core tokens from the shared Palette (Palette.cs). Text/MutedText were a touch dimmer here (#C9D1DA /
    // #8A93A2) and FaintText was #6A7180 (which failed WCAG AA at the ruler/label sizes) — all unified to the
    // shell values so this control can't drift from the rest of the app.
    private static readonly IBrush Text = Palette.TextBrush;
    private static readonly IBrush MutedText = Palette.MutedTextBrush;
    private static readonly IBrush FaintText = Palette.FaintTextBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly IBrush ToggleOn = Palette.AccentBrush;
    private static readonly IBrush ToggleOff = Palette.EdgeBrush;
    private static readonly Pen GridPen = new(Brush("#24242E"), 1);
    private static readonly IBrush MarkerLine = Brush("#33FFFFFF");
    private static readonly Pen EdgePen = new(Palette.EdgeBrush, 1);
    private static readonly Pen PlayheadPen = new(Palette.AccentBrush, 1.5);
    private static readonly Pen SelectPen = new(Palette.AccentBrush, 2);

    // Cached translucent overlays for the draw path — fixed colour + opacity, hoisted out of the per-frame
    // render so no brush/pen is allocated on every redraw (audit #4): accent-tinted move/transition
    // decorations, plus 0.55 ghosts of the two clip fills used by the cross-track move preview.
    private static readonly IBrush MovePreviewLaneFill = new ImmutableSolidColorBrush(Palette.Accent, 0.08);
    private static readonly IBrush TransitionFill = new ImmutableSolidColorBrush(Palette.Accent, 0.22);
    private static readonly Pen TransitionXPen = new(new ImmutableSolidColorBrush(Palette.Accent, 0.85), 1.5);
    private static readonly Pen TransitionBorderPen = new(new ImmutableSolidColorBrush(Palette.Accent, 0.8), 1);
    private static readonly IBrush VideoGhostFill = new ImmutableSolidColorBrush(((ISolidColorBrush)VideoFill).Color, 0.55);
    private static readonly IBrush AudioGhostFill = new ImmutableSolidColorBrush(((ISolidColorBrush)AudioFill).Color, 0.55);

    // Disabled-clip shade (PLAN.md step 53): drawn over a disabled clip's body so it reads dimmed at a glance,
    // cached like every draw-path brush so the render path never allocates.
    private static readonly IBrush DisabledShade = new ImmutableSolidColorBrush(Colors.Black, 0.55);

    // Multi-selection treatment (PLAN.md step 54): the marquee band, and the dimmer accent border non-primary
    // members of a multi-selection draw (the primary keeps SelectPen, so it stays visually distinct). Cached
    // like every draw-path brush so the render path never allocates.
    private static readonly IBrush MarqueeFill = new ImmutableSolidColorBrush(Palette.Accent, 0.12);
    private static readonly Pen MarqueePen = new(new ImmutableSolidColorBrush(Palette.Accent, 0.9), 1);
    private static readonly Pen MultiSelectPen = new(new ImmutableSolidColorBrush(Palette.Accent, 0.55), 2);

    // Render bar (PLAN.md step 32): the classic NLE strip at the top of the ruler — green = a valid cached
    // render covers the span, red = needs render and can't fully composite live (nests, transitions),
    // yellow = un-rendered effects. In/out marks (I / O) shade their range over the ruler.
    private static readonly IBrush RenderedBar = Brush("#3FA34D");
    private static readonly IBrush NeedsRenderBar = Brush("#D9A514");
    private static readonly IBrush NeedsRenderHeavyBar = Brush("#C94F4F");
    private static readonly IBrush InOutFill = new ImmutableSolidColorBrush(Colors.White, 0.08);
    private static readonly Pen InOutPen = new(new ImmutableSolidColorBrush(Palette.Accent, 0.9), 1);

    // Fade overlay brushes (PLAN.md step 39), hoisted like the other draw-path brushes: the rubber-band line,
    // the shading over the faded-away region above it, and the corner-handle triangles.
    private static readonly Pen FadeBandPen = new(new ImmutableSolidColorBrush(Colors.White, 0.55), 1.2);
    private static readonly IBrush FadeShade = new ImmutableSolidColorBrush(Colors.Black, 0.28);
    private static readonly IBrush FadeHandleFill = new ImmutableSolidColorBrush(Colors.White, 0.75);
    private static readonly IBrush FadePointFill = new ImmutableSolidColorBrush(Colors.White, 0.9);

    private Project? _project;
    private EditHistory? _history;
    private PlaybackEngine? _engine;

    private double _pxPerSecond = 70;
    private double _scrollX;
    private Timecode _playhead = Timecode.Zero;

    // Width of the left track-header column. Resizable by dragging its right edge (session-only).
    private double _headerWidth = DefaultHeaderWidth;
    private bool _resizingHeader;

    // The multi-clip selection (PLAN.md step 54): an ordered set with a primary clip. _selected caches the
    // last announced primary so the single-clip surface throughout this control reads it directly; every
    // selection mutation funnels through OnSelectionMutated, which syncs the cache, raises
    // SelectedClipChanged when the primary changed, and repaints.
    private readonly ClipSelection _selection = new();
    private Clip? _selected;
    private bool _scrubbing;

    // Rubber-band marquee state (PLAN.md step 54): a Select-tool drag on empty lane area selects every clip
    // the band touches (live, as it moves); Ctrl/Shift at the press adds to the existing selection. A press
    // released below the drag threshold stays a plain click — clear the selection and move the playhead.
    private const double MarqueeDragThresholdPx = 4;
    private bool _marquee;
    private bool _marqueeDragged;
    private bool _marqueeAdditive;
    private Point _marqueeOrigin, _marqueeCurrent;
    private List<Clip> _marqueeBase = [];

    // Render bar + in/out marks (PLAN.md step 32). The spans are computed by RenderBarModel (MainWindow pushes
    // fresh ones after every model change); the marks are session UI state driving Render In to Out.
    private IReadOnlyList<RenderCache.RenderBarSpan> _renderSpans = [];
    private Timecode? _markIn, _markOut;

    // The selected transition (PLAN.md step 25), mutually exclusive with the clip selection. Selecting one clears
    // the other; Delete removes whichever is selected.
    private Transition? _selectedTransition;
    private Track? _selectedTransitionTrack;

    // Active clip-drag gesture state.
    private Clip? _dragClip;
    private Track? _dragSourceTrack;
    private ClipDragMode _dragMode = ClipDragMode.None;
    private long _dragPressTicks;
    private Timecode _dragOrigIn, _dragOrigOut, _dragOrigStart;
    private long _minDurTicks = 1;
    private IReadOnlyList<long> _snapPoints = [];
    private IDisposable? _coalesce;

    // Move-gesture preview (PLAN.md step 16e). The Select tool's clip-body drag does not mutate the model
    // live (unlike Trim/Slip, which coalesce); it tracks a ghost across tracks and commits exactly one command
    // on release, so cross-track + copy (Alt) + horizontal-lock (Shift) are each one undo entry. _movePreview
    // is set for that gesture; the preview fields hold the target track, snapped start, and copy flag.
    private bool _movePreview;
    private long _movePreviewStart;
    private Track? _movePreviewTrack;
    private bool _movePreviewCopy;

    // Linked companions captured at drag start (their track, clip, and original start) plus the group's
    // minimum original start, so a linked move shifts every member by one locked delta and none goes negative.
    private List<(Clip clip, Timecode origStart)> _dragLinked = [];
    private long _dragGroupMinStart;

    // Trim-family gesture state (PLAN.md step 22). A clip-body drag with the Select tool previews-then-commits
    // (MovePreview); every other edit gesture mutates the model live inside a coalescing scope. The kind is fixed
    // at BeginClipDrag from the active tool + which part of the clip was grabbed.
    private enum DragKind { None, Trim, Slip, MovePreview, Ripple, Roll, Slide, FadeIn, FadeOut, Band }

    private DragKind _dragKind = DragKind.None;

    // Fade-gesture state (PLAN.md step 39): the opacity envelope + fade lengths captured at press (so every
    // drag update is computed from one stable original), the clip's on-screen rect (for level↔Y mapping), and
    // — for a rubber-band drag — the grabbed keyframe times and the level under the initial press.
    private AnimatableValue? _fadeOrigOpacity;
    private long _fadeOrigIn, _fadeOrigOut;
    private Rect _fadeClipRect;
    private IReadOnlyList<long> _bandGrabTimes = [];
    private double _bandPressLevel;

    // Ripple: the dragged clip plus any linked companions, each with its captured trim, speed/media bounds, and
    // the downstream clips on its own track (so each track stays gap-free). Captured once at drag start.
    private readonly record struct RippleUnit(
        Clip Clip, Rational Speed, long MediaDuration, Timecode OrigIn, Timecode OrigOut,
        IReadOnlyList<(Clip Clip, Timecode OrigStart)> Downstream);

    private readonly List<RippleUnit> _rippleUnits = new();
    private bool _rippleTrimEnd;

    // Roll: the two clips sharing the dragged cut, with their captured edge/placement and bounds.
    private Clip? _rollLeft, _rollRight;
    private Rational _rollLeftSpeed, _rollRightSpeed;
    private long _rollLeftMedia, _rollRightMedia;
    private Timecode _rollOrigLeftOut, _rollOrigRightIn, _rollOrigRightStart, _rollOrigCut;

    // Slide: the slid clip's neighbours (either may be absent), with their captured placement and bounds.
    private Clip? _slidePrev, _slideNext;
    private Rational _slidePrevSpeed, _slideNextSpeed;
    private long _slidePrevMedia, _slideNextMedia;
    private Timecode _slideOrigPrevOut, _slideOrigNextIn, _slideOrigNextStart;

    // Hand-tool panning state.
    private bool _panning;
    private double _panPressX, _panOrigScroll;

    // Drag-and-drop preview: the X of the drop indicator while a bin tile / effect hovers (PLAN.md step 16b).
    private double? _dropPreviewX;

    /// <summary>Raised when the primary selected clip changes (for the Inspector / header — the inherently
    /// single-clip surfaces track the multi-selection's primary, PLAN.md step 54). Null = nothing selected.</summary>
    public event Action<Clip?>? SelectedClipChanged;

    /// <summary>Raised with a short message for the status strip (e.g. why a transition couldn't be applied).</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Raised when a track name is double-clicked, requesting an inline rename. The <see cref="Rect"/> is the
    /// name area in control-local coordinates so the shell can position an editor over it (the timeline is
    /// custom-drawn and cannot host a child <c>TextBox</c> itself).
    /// </summary>
    public event Action<Track, Rect>? TrackRenameRequested;

    /// <summary>
    /// Raised when a title-family generator clip is double-clicked, requesting the inline text editor
    /// (PLAN.md step 40 — a title stays editable post-hoc). The <see cref="Rect"/> is the clip's body in
    /// control-local coordinates so the shell can position the editor over it (the timeline is custom-drawn
    /// and cannot host a child <c>TextBox</c> itself).
    /// </summary>
    public event Action<Clip, Rect>? TitleEditRequested;

    /// <summary>
    /// Raised when a clip is right-clicked (PLAN.md step 53), after it has been selected, so the shell can build
    /// and open the clip context menu at the pointer. The menu lives in <see cref="MainWindow"/> because its
    /// dialog-backed items (Speed/Duration, Interpret Footage) do, and the custom-drawn timeline cannot host
    /// child controls itself. The clip's <see cref="Track"/> rides along so the shell can shape the menu by
    /// lane kind (video-only items like Frame Hold vs audio-only items like Normalize Audio, as leading editors do).
    /// </summary>
    public event Action<Clip, Track>? ClipContextMenuRequested;

    /// <summary>Whether edge/playhead snapping is active during drags.</summary>
    public bool Snapping { get; set; } = true;

    /// <summary>Whether linked A/V move and blade together (UI.md §3.2, PLAN.md step 13).</summary>
    public bool Linked { get; set; } = true;

    /// <summary>The active timeline tool (Select / Blade / Slip / Hand / Zoom).</summary>
    public EditTool ActiveTool
    {
        get => _activeTool;
        set
        {
            _activeTool = value;
            Cursor = ToolCursor(value);
            if (value != EditTool.Blade)
                SetBladeHover(null);
        }
    }

    private EditTool _activeTool = EditTool.Select;

    /// <summary>The cursor for a tool away from any hover context — the <see cref="ActiveTool"/> setter and
    /// drag-release restore. Idle hover refines this per-position via <see cref="TimelineMath.HoverCursor"/>.</summary>
    private Cursor ToolCursor(EditTool tool) =>
        CursorFor(TimelineMath.HoverCursor(tool, ClipDragMode.None));

    // The header-grip resize cursor, cached (like every cursor here) so the pointer-move path never allocates.
    private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);

    /// <summary>Translates the pure <see cref="TimelineCursor"/> kind to its custom bitmap cursor, rendered
    /// at this window's display scale (<see cref="ToolCursors"/>; cached per kind + scale).</summary>
    private Cursor CursorFor(TimelineCursor kind) =>
        ToolCursors.Get(kind, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);

    /// <summary>The primary selected clip (the anchor of a multi-selection, PLAN.md step 54), or null.</summary>
    public Clip? SelectedClip => _selected;

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;

        // Drop target for media-bin tiles (place a clip) and Effects-browser rows (append an effect), PLAN.md
        // step 16b. The browser sets the payload under a DragFormats key; we route by which format is present.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropPreview());
    }

    /// <summary>Binds the timeline to a project, the shared edit history, and the playback engine. Call once.</summary>
    public void Attach(Project project, EditHistory history, PlaybackEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        _project = project;
        _history = history;
        _engine = engine;

        Rational fps = project.Timeline.FrameRate;
        _minDurTicks = Math.Max(1, fps.Num > 0 ? Timecode.FromFrames(1, fps).Ticks : 1);

        _history.Changed += OnHistoryChanged;
        if (_engine is not null)
            _engine.PositionChanged += OnEnginePosition;

        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_history is not null)
            _history.Changed -= OnHistoryChanged;
        if (_engine is not null)
            _engine.PositionChanged -= OnEnginePosition;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Zooms in (buttons / Ctrl+wheel), keeping the playhead roughly in view.</summary>
    public void ZoomIn() => SetZoom(_pxPerSecond * 1.25, AnchorX());

    /// <summary>Zooms out.</summary>
    public void ZoomOut() => SetZoom(_pxPerSecond * 0.8, AnchorX());

    /// <summary>
    /// Zooms so the whole sequence fits the viewport width and scrolls back to the start (View ▸ Zoom to Fit,
    /// Shift+Z) — the "frame the timeline" command found in professional NLEs. No-op on an empty timeline or before layout.
    /// </summary>
    public void ZoomToFit()
    {
        if (_project is null)
            return;
        long durTicks = _project.Timeline.Duration.Ticks;
        double view = Bounds.Width - _headerWidth - 24; // small right inset so the tail isn't flush to the edge
        if (durTicks <= 0 || view <= 0)
            return;
        double seconds = (double)durTicks / Timecode.TicksPerSecond;
        _pxPerSecond = Math.Clamp(view / seconds, MinPxPerSecond, MaxPxPerSecond);
        _scrollX = 0;
        ClampScroll();
        InvalidateVisual();
    }

    private double AnchorX() => TimelineMath.XAtTicks(_playhead.Ticks, _pxPerSecond, _scrollX, _headerWidth);

    private void OnHistoryChanged()
    {
        // Clips may have been removed by undo/redo; drop stale selection members (the primary hands off).
        if (_selection.Count > 0 && _project is not null)
            OnSelectionMutated(_selection.Prune(
                c => _project.Timeline.Tracks.Any(t => t.Clips.Contains(c))));
        // Likewise drop a transition selection that undo/redo removed.
        if (_selectedTransition is not null && _project is not null
            && !_project.Timeline.Tracks.Any(t => t.Transitions.Contains(_selectedTransition)))
        {
            _selectedTransition = null;
            _selectedTransitionTrack = null;
        }
        InvalidateVisual();
    }

    private void OnEnginePosition(Timecode t)
    {
        _playhead = t;
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    /// <summary>
    /// Resets the timeline's transient view state after the project's open (active) sequence changes (PLAN.md
    /// step 23): drops the selection (it belonged to the previous sequence) and rewinds the horizontal scroll, then
    /// repaints against the now-active sequence's tracks. The engine re-seek echoes the playhead back to the start.
    /// </summary>
    public void OnActiveSequenceChanged()
    {
        _scrollX = 0;
        OnSelectionMutated(_selection.Clear());
        _selectedTransition = null;
        _selectedTransitionTrack = null;
        InvalidateVisual();
    }

    // ── Clip editing (Edit / Clip menus, PLAN.md step 16c) ──────────────────────────────────────────

    // The clip clipboard for cut/copy/paste — one snapshot per selected clip (PLAN.md step 54); each flag
    // records whether it was copied from a video lane so a paste lands on a track of the matching kind.
    private List<(Clip Snapshot, bool IsVideo)> _clipboard = [];

    /// <summary>Whether any clip is selected (drives Edit ▸ Cut/Copy/Delete and Clip ▸ Nudge enablement).</summary>
    public bool HasSelection => _selection.Count > 0;

    /// <summary>The selected clips (insertion order). <see cref="SelectedClip"/> is the set's primary.</summary>
    public IReadOnlyList<Clip> SelectedClips => _selection.Clips;

    /// <summary>Whether there are clips on the clipboard to paste (drives Edit ▸ Paste enablement).</summary>
    public bool CanPaste => _clipboard.Count > 0;

    /// <summary>Whether the primary selected clip is part of a linked A/V group (drives Clip ▸ Unlink enablement).</summary>
    public bool SelectedIsLinked => _selected?.LinkGroupId is not null;

    /// <summary>Whether Select All has anything to select (drives Edit ▸ Select All enablement).</summary>
    public bool CanSelectAll =>
        _project is not null && _project.Timeline.Tracks.Any(t => t.Clips.Count > 0);

    /// <summary>Selects every clip on every track (Edit ▸ Select All, Ctrl+A — PLAN.md step 54).</summary>
    public void SelectAll()
    {
        if (_project is null)
            return;
        OnSelectionMutated(_selection.ReplaceAll(
            _project.Timeline.Tracks.SelectMany(t => t.Clips)));
    }

    /// <summary>Copies the selected clips to the clipboard (detached deep copies, links cleared — steps 13/16c;
    /// the whole selection is snapshotted, PLAN.md step 54).</summary>
    public void CopySelected()
    {
        if (_selection.Count == 0)
            return;
        // Primary first: PasteAll's contract is that element 0 is the primary's copy (it anchors the pasted
        // selection), and the set's primary need not be first in insertion order (a plain press or Extend can
        // re-anchor it). OrderByDescending is stable, so the rest keep their insertion order.
        _clipboard = _selection.Clips
            .OrderByDescending(c => ReferenceEquals(c, _selected))
            .Select(c => (ClipboardOps.Copy(c), TrackOf(c) is VideoTrack))
            .ToList();
    }

    /// <summary>Copies then deletes the selected clips (and their linked companions when Linked is on).</summary>
    public void CutSelected()
    {
        if (_selection.Count == 0)
            return;
        CopySelected();
        DeleteSelected();
    }

    /// <summary>
    /// Pastes the clipboard clips at the playhead — the earliest lands there and the rest keep their relative
    /// offsets, each onto the first track of its kind. The pasted clips are independent (no links), become the
    /// selection, and land as one undo entry (steps 10/54).
    /// </summary>
    public void PasteAtPlayhead()
    {
        if (_clipboard.Count == 0 || _history is null || _project is null)
            return;
        ClipboardOps.PasteResult? result = ClipboardOps.PasteAll(
            _clipboard, _playhead,
            _project.Timeline.VideoTracks.FirstOrDefault(),
            _project.Timeline.AudioTracks.FirstOrDefault());
        if (result is not { } paste)
            return;

        Execute(paste.Command);
        OnSelectionMutated(_selection.ReplaceAll(paste.Pasted));
        ClipPlaced?.Invoke();
    }

    /// <summary>
    /// Deletes the selected clips (and, when Linked is on, their companion A/V clips) as one undo entry, then
    /// clears the selection.
    /// </summary>
    public void DeleteSelected()
    {
        // A selected transition takes priority — Delete removes it (PLAN.md step 25).
        if (_selectedTransition is not null && _selectedTransitionTrack is not null && _history is not null)
        {
            Execute(new RemoveTransitionCommand(_selectedTransitionTrack, _selectedTransition));
            SelectTransition(null, null);
            return;
        }

        if (_history is null || _project is null)
            return;
        if (ClipEdits.DeleteAll(_project.Timeline, _selection.Clips, Linked) is not { } command)
            return;
        Execute(command);
        Select(null);
    }

    /// <summary>
    /// Nudges the selected clips by <paramref name="frames"/> frames along the timeline (Clip ▸ Nudge
    /// Left/Right). The whole selection — plus linked companions when Linked is on — shifts rigidly by one
    /// delta, clamped so no member crosses the origin. Each press is its own undo entry.
    /// </summary>
    public void NudgeSelected(int frames)
    {
        if (_selection.Count == 0 || _history is null || _project is null || frames == 0)
            return;
        long frameTicks = FrameTicks();
        if (frameTicks <= 0)
            return;
        if (ClipEdits.NudgeAll(_project.Timeline, _selection.Clips, (long)frames * frameTicks, Linked) is { } command)
            Execute(command);
    }

    // ── Markers (PLAN.md step 20) ────────────────────────────────────────────────────────────────────

    /// <summary>The colour band for a marker — shared by the ruler/clip drawing and the markers panel.</summary>
    public static IBrush MarkerBrush(MarkerColor color) => color switch
    {
        MarkerColor.Cyan => CyanMarker,
        MarkerColor.Green => GreenMarker,
        MarkerColor.Yellow => YellowMarker,
        MarkerColor.Orange => OrangeMarker,
        MarkerColor.Red => RedMarker,
        MarkerColor.Magenta => MagentaMarker,
        MarkerColor.Purple => PurpleMarker,
        MarkerColor.White => WhiteMarker,
        _ => BlueMarker,
    };

    private static readonly IBrush BlueMarker = Brush("#4C9AFF");
    private static readonly IBrush CyanMarker = Brush("#2BD9D9");
    private static readonly IBrush GreenMarker = Brush("#3FB950");
    private static readonly IBrush YellowMarker = Brush("#E3C341");
    private static readonly IBrush OrangeMarker = Brush("#E58A2E");
    private static readonly IBrush RedMarker = Brush("#E5534B");
    private static readonly IBrush MagentaMarker = Brush("#D957C8");
    private static readonly IBrush PurpleMarker = Brush("#9A6CE7");
    private static readonly IBrush WhiteMarker = Brush("#E6EAF0");

    // 0.18-opacity band drawn behind a span marker — cached per colour (built once from the brushes above)
    // so the ruler draw path doesn't allocate a brush per marker per frame (audit #4).
    private static readonly Dictionary<MarkerColor, IBrush> MarkerHighlights = BuildMarkerHighlights();

    private static Dictionary<MarkerColor, IBrush> BuildMarkerHighlights()
    {
        var map = new Dictionary<MarkerColor, IBrush>();
        foreach (MarkerColor c in Enum.GetValues<MarkerColor>())
            map[c] = new ImmutableSolidColorBrush(((ISolidColorBrush)MarkerBrush(c)).Color, 0.18);
        return map;
    }

    public static IBrush MarkerHighlightBrush(MarkerColor color) => MarkerHighlights[color];

    /// <summary>
    /// Adds a sequence marker at the playhead (the 'M' convention used by leading editors), undoable through the command stack.
    /// Returns the new marker (so the caller can offer to name it) or <see langword="null"/> when not ready.
    /// </summary>
    public Marker? AddMarkerAtPlayhead()
    {
        if (_history is null || _project is null)
            return null;
        var marker = new Marker(_playhead);
        Execute(new AddMarkerCommand(_project.Timeline.Markers, marker));
        return marker;
    }

    /// <summary>Removes a sequence marker through the command stack (for the markers panel).</summary>
    public void RemoveMarker(Marker marker)
    {
        if (_history is null || _project is null)
            return;
        Execute(new RemoveMarkerCommand(_project.Timeline.Markers, marker));
    }

    /// <summary>Unlinks the selected clip and its companions (clears their link group) as one undo entry (step 13).</summary>
    public void UnlinkSelected()
    {
        if (_selected is null || _history is null || _project is null)
            return;
        if (ClipEdits.Unlink(_project.Timeline, _selected) is { } command)
            Execute(command);
    }

    /// <summary>Whether the selection can be linked — ≥2 clips spanning video and audio, not already one
    /// whole group (drives Clip ▸ Link enablement, PLAN.md step 55).</summary>
    public bool CanLinkSelection =>
        _project is not null && ClipEdits.CanLink(_project.Timeline, _selection.Clips);

    /// <summary>Links the selected clips under one fresh shared group as one undo entry (PLAN.md step 55).
    /// A no-op when the selection is ineligible (see <see cref="CanLinkSelection"/>).</summary>
    public void LinkSelected()
    {
        if (_history is null || _project is null)
            return;
        if (ClipEdits.LinkAll(_project.Timeline, _selection.Clips) is { } command)
            Execute(command);
    }

    /// <summary>The Ctrl+L toggle (PLAN.md step 55): links an eligible selection, otherwise unlinks the
    /// primary selected clip's group.</summary>
    public void ToggleLinkSelected()
    {
        if (_history is null || _project is null)
            return;
        if (ClipEdits.ToggleLink(_project.Timeline, _selected, _selection.Clips) is { } command)
            Execute(command);
    }

    // ── Split at Playhead / Duplicate / Enable (PLAN.md step 53) ────────────────────────────────────

    /// <summary>Whether Split at Playhead would cut: the playhead lies strictly inside the selected clip
    /// (drives the Clip ▸ Split at Playhead / context-menu enablement).</summary>
    public bool CanSplitAtPlayhead =>
        _selected is { } clip && _playhead > clip.TimelineStart && _playhead < clip.TimelineEnd;

    /// <summary>Whether the selected clip is enabled (drives the checkable Enable menu item). False when
    /// nothing is selected.</summary>
    public bool SelectedIsEnabled => _selected is { Enabled: true };

    /// <summary>
    /// Splits the selected clip at the playhead (Ctrl+K, the Add Edit command in leading editors): the same cut the Blade tool makes,
    /// without the pointer — linked companions spanning the playhead split too (Linked on) and the right halves
    /// share a fresh link group, one undo entry, the right half selected. No-ops when the playhead is on/outside
    /// the clip's edges.
    /// </summary>
    public void SplitAtPlayhead()
    {
        if (_selected is null || _history is null || _project is null)
            return;
        Track? track = TrackOf(_selected);
        if (track is null)
            return;
        if (ClipEdits.Split(_project.Timeline, track, _selected, _playhead, Linked) is not { } split)
            return;
        Execute(split.Command);
        Select(split.Right);
    }

    /// <summary>
    /// Duplicates the selected clip in place (PLAN.md step 53): a copy — effects and markers included — placed
    /// butted after the original on the same track. With Linked on, companions duplicate together under a fresh
    /// link group. One undo entry; the new copy becomes the selection.
    /// </summary>
    public void DuplicateSelected()
    {
        if (_selected is null || _history is null || _project is null)
            return;
        Track? track = TrackOf(_selected);
        if (track is null)
            return;
        (IEditCommand command, Clip copy) = ClipEdits.Duplicate(_project.Timeline, track, _selected, Linked);
        Execute(command);
        Select(copy);
    }

    /// <summary>
    /// Toggles the selected clips' Enable flag (Shift+E, PLAN.md steps 53/54): a disabled clip renders nothing
    /// and contributes no audio but keeps its place. The whole selection — plus linked companions with Linked
    /// on — converges on the opposite of the primary's state, as one undo entry, matching leading editors.
    /// </summary>
    public void ToggleSelectedEnabled()
    {
        if (_selected is null || _history is null || _project is null)
            return;
        if (ClipEdits.ToggleEnabledAll(_project.Timeline, _selected, _selection.Clips, Linked) is { } command)
            Execute(command);
    }

    /// <summary>
    /// Retimes the selected clip to <paramref name="speed"/> (PLAN.md step 21), and — so companion audio stays in
    /// sync — every clip linked to it, as one undo entry. The source span is unchanged; the clip's timeline
    /// duration derives from the new speed.
    /// </summary>
    public void SetSelectedClipSpeed(Rational speed)
    {
        if (_selected is null || _history is null || _project is null || speed.Num <= 0)
            return;
        var members = new List<Clip> { _selected };
        members.AddRange(_project.Timeline.ClipsLinkedTo(_selected).Select(l => l.Clip));

        var commands = members
            .Select(c => (IEditCommand)new SetClipSpeedCommand(c, speed))
            .ToList();
        Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Change speed", commands));
    }

    /// <summary>The selected clip's current playback speed (1/1 when nothing is selected), for the Speed dialog.</summary>
    public Rational SelectedClipSpeed => _selected?.SpeedRatio ?? Rational.One;

    // ── Frame hold + stop-motion frame edits (PLAN.md step 43) ──────────────────────────────────────

    /// <summary>Leading editors' Insert Frame Hold Segment inserts a 2-second freeze; kept as the convention.</summary>
    private static readonly Timecode HoldSegmentDuration = Timecode.FromSeconds(2);

    /// <summary>Whether the selection can carry a frame hold: a clip with frame content (media / nested sequence /
    /// multicam — a generator animates by local progress, not source time) on a video track. Video holds only —
    /// linked audio keeps playing normally, matching leading editors (PLAN.md step 43).</summary>
    public bool SelectedCanFrameHold => _selected is { } clip && CanFrameHold(clip);

    /// <summary>Whether the selected clip is currently a frame hold (for the menu/dialog state).</summary>
    public bool SelectedIsHeld => _selected?.IsHeld == true;

    private bool CanFrameHold(Clip clip) =>
        clip.Kind is ClipKind.Media or ClipKind.Sequence or ClipKind.Multicam && TrackOf(clip) is VideoTrack;

    /// <summary>The source time under the playhead in the selected clip via the <em>unheld</em> speed map (the
    /// Frame Hold Options "Playhead" choice must retarget a held clip, whose live map is constant), or
    /// <see langword="null"/> when the playhead is outside the clip.</summary>
    public Timecode? SelectedClipSourceAtPlayhead =>
        _selected is { } clip && clip.Contains(_playhead)
            ? clip.SourceIn + (_playhead - clip.TimelineStart).Scale(clip.SpeedRatio)
            : null;

    /// <summary>Freezes the whole selected clip at source time <paramref name="holdAt"/> (Clip ▸ Frame Hold
    /// Options): its timeline span is kept, so nothing downstream moves. One undo entry.</summary>
    public void HoldSelectedClip(Timecode holdAt)
    {
        if (_selected is not { } clip || _history is null || !CanFrameHold(clip) || holdAt.Ticks < 0)
            return;
        Execute(new SetClipHoldCommand(clip, holdAt, clip.Duration, "Frame hold"));
    }

    /// <summary>Removes the selected clip's frame hold: the retained source span and speed take over again, so
    /// the derived duration is restored exactly (PLAN.md step 43).</summary>
    public void UnholdSelectedClip()
    {
        if (_selected is not { IsHeld: true } clip || _history is null)
            return;
        Execute(new SetClipHoldCommand(clip, null, default, "Remove frame hold"));
    }

    /// <summary>
    /// Clip ▸ Add Frame Hold (naming used by leading editors): splits the selected clip at the playhead and freezes the right
    /// half at the playhead frame — playback runs to the playhead and holds. The clip's span is unchanged (no
    /// ripple). With Linked on, companion clips spanning the cut are split too (the audio keeps playing across
    /// both halves); at the clip's very start the whole clip is held instead. One undo entry.
    /// </summary>
    public void AddFrameHoldAtPlayhead()
    {
        if (_selected is not { } clip || _history is null || _project is null || !CanFrameHold(clip))
            return;
        Track? track = TrackOf(clip);
        Timecode at = _playhead;
        if (track is null || at < clip.TimelineStart || at >= clip.TimelineEnd)
            return;
        if (at == clip.TimelineStart)
        {
            Execute(new SetClipHoldCommand(clip, clip.MapToSource(at), clip.Duration, "Add frame hold"));
            return;
        }

        List<(Track Track, Clip Clip)> companions = Linked
            ? _project.Timeline.ClipsLinkedTo(clip).Where(l => l.Clip.Contains(at) && l.Clip.TimelineStart < at).ToList()
            : [];
        Guid? rightGroup = (Linked && clip.LinkGroupId is not null && companions.Count > 0) ? Guid.NewGuid() : null;

        (IEditCommand primary, Clip held) = FrameHoldEdits.AddFrameHold(track, clip, at, rightGroup);
        if (companions.Count == 0)
        {
            Execute(primary);
        }
        else
        {
            var commands = new List<IEditCommand> { primary };
            foreach ((Track ctrack, Clip cclip) in companions)
                commands.Add(new SplitClipCommand(ctrack, cclip, at, rightGroup));
            Execute(new CompositeCommand("Add frame hold", commands));
        }
        Select(held);
    }

    /// <summary>
    /// Clip ▸ Insert Frame Hold Segment (naming used by leading editors): inserts a 2-second freeze of the playhead frame into
    /// the selected clip and ripples everything at/after the playhead — on every track, so A/V sync holds —
    /// right by the same amount. With Linked on, companions spanning the playhead are split with it. One undo
    /// entry; the new freeze segment is selected.
    /// </summary>
    public void InsertFrameHoldSegmentAtPlayhead()
    {
        if (_selected is not { } clip || _history is null || _project is null || !CanFrameHold(clip))
            return;
        Track? track = TrackOf(clip);
        Timecode at = _playhead;
        if (track is null || at < clip.TimelineStart || at >= clip.TimelineEnd)
            return;

        List<(Track Track, Clip Clip)> companions = Linked
            ? _project.Timeline.ClipsLinkedTo(clip).Where(l => l.Clip.Contains(at) && l.Clip.TimelineStart < at).ToList()
            : [];
        Guid? rightGroup = (Linked && clip.LinkGroupId is not null && companions.Count > 0) ? Guid.NewGuid() : null;

        List<(Clip Clip, Timecode OrigStart)> downstream = DownstreamFrom(at, clip, companions.Select(l => l.Clip).ToList());
        var commands = new List<IEditCommand>();
        foreach ((Track ctrack, Clip cclip) in companions)
        {
            var split = new SplitClipCommand(ctrack, cclip, at, rightGroup);
            commands.Add(split);
            downstream.Add((split.RightClip, at));
        }

        (IEditCommand insert, Clip held) =
            FrameHoldEdits.InsertFrameHoldSegment(track, clip, at, HoldSegmentDuration, downstream, rightGroup);
        commands.Add(insert);
        Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Insert frame hold segment", commands));
        Select(held);
    }

    /// <summary>
    /// Clip ▸ Duplicate Frame (stop-motion frame edit, PLAN.md step 43): inserts a one-frame hold of the source
    /// frame under the playhead right after it and ripples every track downstream by one frame. Repeating it is
    /// per-frame "on twos/threes" (whole-clip on-twos = Interpret Footage at half rate, step 42).
    /// </summary>
    public void DuplicateFrameAtPlayhead()
    {
        if (_selected is not { IsHeld: false } clip || _history is null || _project is null || !CanFrameHold(clip))
            return;
        Track? track = TrackOf(clip);
        Timecode at = _playhead;
        if (track is null || at < clip.TimelineStart || at >= clip.TimelineEnd)
            return;

        Rational fps = FrameEditRate(clip);
        FrameHoldEdits.FrameSpan span = FrameHoldEdits.SourceFrameSpan(clip, at, fps);
        Timecode boundary = Timecode.Min(span.TimelineEnd, clip.TimelineEnd);
        (IEditCommand command, Clip held) =
            FrameHoldEdits.DuplicateFrame(track, clip, at, fps, DownstreamFrom(boundary, clip));
        Execute(command);
        Select(held);
    }

    /// <summary>
    /// Clip ▸ Remove Frame (stop-motion frame edit, PLAN.md step 43): extracts the timeline span of the source
    /// frame under the playhead — snapped to the exact source-frame grid — and ripple-closes every track by one
    /// frame. Distinct from Ripple Delete (Shift+Delete), which removes the whole selected clip's span.
    /// </summary>
    public void RemoveFrameAtPlayhead()
    {
        if (_selected is not { IsHeld: false } clip || _history is null || _project is null || !CanFrameHold(clip))
            return;
        Track? track = TrackOf(clip);
        Timecode at = _playhead;
        if (track is null || at < clip.TimelineStart || at >= clip.TimelineEnd)
            return;

        Rational fps = FrameEditRate(clip);
        FrameHoldEdits.FrameSpan span = FrameHoldEdits.SourceFrameSpan(clip, at, fps);
        Timecode to = Timecode.Min(span.TimelineEnd, clip.TimelineEnd);
        Execute(FrameHoldEdits.RemoveFrame(track, clip, at, fps, DownstreamFrom(to, clip)));
        if (TrackOf(clip) is null) // a single-frame clip was removed outright
            Select(null);
    }

    /// <summary>The frame grid for the stop-motion edits: the source's own rate for media clips (correct for
    /// reinterpreted image sequences and NTSC sources), else the sequence rate.</summary>
    private Rational FrameEditRate(Clip clip) =>
        clip.Kind == ClipKind.Media && _project?.MediaPool.Get(clip.MediaRefId) is { Info.FrameRate.Num: > 0 } media
            ? media.Info.FrameRate
            : _project!.Timeline.FrameRate;

    /// <summary>Every clip on any track starting at/after <paramref name="at"/> with its current start — the
    /// cross-track downstream set the frame-edit ripples shift (A/V stays in sync). <paramref name="exclude"/>
    /// and <paramref name="alsoExclude"/> are the clips the composite handles internally.</summary>
    private List<(Clip Clip, Timecode OrigStart)> DownstreamFrom(Timecode at, Clip exclude, List<Clip>? alsoExclude = null)
    {
        var list = new List<(Clip, Timecode)>();
        foreach (Track t in _project!.Timeline.Tracks)
            foreach (Clip c in t.Clips)
                if (!ReferenceEquals(c, exclude) && (alsoExclude is null || !alsoExclude.Contains(c)) && c.TimelineStart >= at)
                    list.Add((c, c.TimelineStart));
        return list;
    }

    /// <summary>Appends an effect (by catalog id) to the selected clip via <see cref="AddEffectCommand"/> (steps 15–16).</summary>
    public void ApplyEffectToSelected(string effectTypeId)
    {
        if (_selected is null || _history is null || string.IsNullOrEmpty(effectTypeId))
            return;
        EffectInstance instance = EffectCatalog.Find(effectTypeId)?.CreateInstance() ?? new EffectInstance(effectTypeId);
        Execute(new AddEffectCommand(_selected, instance));
    }

    /// <summary>
    /// Applies a transition (PLAN.md step 25) at the cut adjacent to the selected clip — preferring the cut it
    /// shares with the next clip, falling back to the one with the previous clip. Used by the Transitions tab's
    /// double-click. Surfaces a status hint when there is no adjacent clip to transition with.
    /// </summary>
    public void ApplyTransitionToSelectedCut(string transitionTypeId)
    {
        if (_selected is null || _project is null)
        {
            Status?.Invoke("Select a clip beside a cut to add a transition.");
            return;
        }
        if (TrackOf(_selected) is not { } track)
            return;

        Timecode end = _selected.TimelineEnd;
        Timecode start = _selected.TimelineStart;
        bool hasNext = track.Clips.Any(c => !ReferenceEquals(c, _selected) && c.TimelineStart == end);
        bool hasPrev = track.Clips.Any(c => !ReferenceEquals(c, _selected) && c.TimelineEnd == start);

        if (hasNext && ApplyTransitionAt(track, end, transitionTypeId))
            return;
        if (hasPrev && ApplyTransitionAt(track, start, transitionTypeId))
            return;
        Status?.Invoke("The selected clip has no adjacent clip to transition with.");
    }

    /// <summary>
    /// Applies a transition of the given type at <paramref name="cut"/> on <paramref name="track"/>, when that cut
    /// sits between two distinct clips (PLAN.md step 25). The duration is the catalog default, snapped to whole
    /// frames and clamped so the window stays within both clips; the new transition becomes the selection. Returns
    /// whether one was added.
    /// </summary>
    private bool ApplyTransitionAt(Track track, Timecode cut, string transitionTypeId)
    {
        if (_history is null || _project is null)
            return false;

        Clip? from = track.ResolveActiveClip(cut - new Timecode(1));
        Clip? to = track.ResolveActiveClip(cut);
        if (from is null || to is null || ReferenceEquals(from, to))
        {
            Status?.Invoke("A transition needs two adjacent clips that share a cut.");
            return false;
        }

        long frame = Math.Max(1, FrameTicks());
        // Keep the window comfortably inside both clips, and at least one frame long.
        long maxByClips = Math.Min(from.Duration.Ticks, to.Duration.Ticks);
        long dur = Math.Min(TransitionCatalog.DefaultDuration.Ticks, maxByClips);
        dur = Math.Max(frame, dur / frame * frame); // snap down to whole frames
        if (dur <= 0)
            return false;

        var transition = new Transition(transitionTypeId, cut, new Timecode(dur));
        Execute(new AddTransitionCommand(track, transition));
        SelectTransition(transition, track);
        Status?.Invoke($"Added {TransitionCatalog.DisplayName(transitionTypeId)}.");
        return true;
    }

    /// <summary>The cut (where one clip ends and the next begins) on <paramref name="track"/> nearest the time at
    /// <paramref name="tTicks"/>, within a generous pixel tolerance — for dropping a transition near a cut.</summary>
    private bool TryFindCutNear(Track track, long tTicks, out Timecode cut)
    {
        cut = default;
        double tolTicks = 60.0 / Math.Max(1, _pxPerSecond) * Timecode.TicksPerSecond;
        long bestDist = long.MaxValue;
        foreach (Clip a in track.Clips)
        {
            long end = a.TimelineEnd.Ticks;
            bool isCut = track.Clips.Any(b => !ReferenceEquals(a, b) && b.TimelineStart.Ticks == end);
            if (!isCut)
                continue;
            long dist = Math.Abs(end - tTicks);
            if (dist < bestDist)
            {
                bestDist = dist;
                cut = new Timecode(end);
            }
        }
        return bestDist <= tolTicks;
    }

    /// <summary>Inserts a generator clip (title, colour matte) at the playhead (PLAN.md step 19), selecting it.</summary>
    public void InsertGenerator(GeneratorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        InsertSyntheticVideoClip(
            t => descriptor.CreateClip(GeneratorCatalog.DefaultDuration, t), $"Insert {descriptor.DisplayName}");
    }

    /// <summary>Inserts an adjustment layer at the playhead (PLAN.md step 19): its effects grade the tracks below
    /// it for its span. Always lands on a track above the content so it doesn't displace it.</summary>
    public void InsertAdjustmentLayer() =>
        // Fully qualified: a Control already has a `Clip` property (Geometry), which would shadow the model type here.
        InsertSyntheticVideoClip(t => Sprocket.Core.Model.Clip.CreateAdjustment(GeneratorCatalog.DefaultDuration, t), "Insert adjustment layer");

    /// <summary>
    /// Nests the current selection — and, while <see cref="Linked"/>, its linked companion clips — into a new
    /// child sequence (PLAN.md step 23, the "Nest" / "compound clip" gesture found in leading editors): the selected
    /// clips move into a fresh sequence and one nested-sequence clip replaces them in the active sequence, as a
    /// single undoable edit. The replacement clip becomes the selection. Returns the new child sequence (so the
    /// shell can offer to open it), or <see langword="null"/> when there is no selection to nest.
    /// </summary>
    public Sequence? NestSelection()
    {
        if (_selected is null || _history is null || _project is null)
            return null;

        // The whole multi-selection nests (PLAN.md step 54) — plus, while Linked, every member's companions.
        List<Clip> clips = ClipEdits.ExpandWithLinked(_project.Timeline, _selection.Clips, Linked)
            .Select(m => m.Clip)
            .ToList();

        string name = SequenceNaming.NextUnique(_project, "Nested Sequence");
        if (SequenceNesting.CreateNest(_project, _project.ActiveSequence, clips, name) is not { } nest)
            return null;

        Execute(nest.Command);
        Select(nest.PrimaryClip);
        ClipPlaced?.Invoke(); // the nested clip can change the active sequence's extent
        return nest.Child;
    }

    /// <summary>Whether a multicam source can be created — at least two video tracks carry a clip (the angles).</summary>
    public bool CanCreateMulticam =>
        _project is not null && _project.Timeline.VideoTracks.Count(vt => vt.Clips.Count > 0) >= 2;

    /// <summary>Whether the selected clip is a multicam clip (so angle switching applies).</summary>
    public bool SelectedIsMulticam => _selected?.Kind == ClipKind.Multicam;

    /// <summary>
    /// Creates a synced multicam source from the stacked camera angles (the first clip on each video track) and
    /// replaces them with a single multicam clip (PLAN.md step 24, the "Create Multi-Camera Source" gesture found
    /// in leading editors). The angles are synced by their current placement; switch angles later with the number keys or the
    /// Inspector. One undoable edit; the new multicam clip becomes the selection. Returns the source name, or
    /// <see langword="null"/> when there are fewer than two angle tracks.
    /// </summary>
    public string? CreateMulticamSource()
    {
        if (_history is null || _project is null)
            return null;

        var angleClips = new List<Clip>();
        foreach (VideoTrack vt in _project.Timeline.VideoTracks)
            if (vt.Clips.Count > 0)
                angleClips.Add(vt.Clips[0]);

        string name = $"Multicam {_project.MulticamSources.Count + 1}";
        if (MulticamBuilder.CreateMulticam(_project, _project.ActiveSequence, angleClips, name) is not { } result)
            return null;

        Execute(result.Command);
        Select(result.PrimaryClip);
        ClipPlaced?.Invoke();
        return result.Source.Name;
    }

    /// <summary>
    /// Switches the selected multicam clip to <paramref name="angleIndex"/> at the playhead (PLAN.md step 24, live
    /// angle cutting): when the playhead is inside the clip the clip is bladed there and the new (right) segment
    /// takes the angle — so the angle program is a run of segments — otherwise the whole segment's angle is set. With
    /// <see cref="Linked"/> on, the companion audio multicam clip cuts/switches together. One undoable edit.
    /// </summary>
    public void SwitchSelectedAngle(int angleIndex)
    {
        if (_selected is null || _selected.Kind != ClipKind.Multicam || _history is null || _project is null)
            return;
        if (_selected.SourceMulticamId is not { } id || _project.GetMulticam(id) is not { } source)
            return;
        if (angleIndex < 0 || angleIndex >= source.Angles.Count || _selected.ActiveAngle == angleIndex)
            return;

        var members = new List<Clip> { _selected };
        if (Linked)
            members.AddRange(_project.Timeline.ClipsLinkedTo(_selected)
                .Select(l => l.Clip).Where(c => c.Kind == ClipKind.Multicam));

        var at = _playhead;
        Guid? rightGroup = (Linked && _selected.LinkGroupId is not null && members.Count > 1) ? Guid.NewGuid() : null;

        var commands = new List<IEditCommand>();
        Clip? newPrimary = null;
        foreach (Clip c in members)
        {
            if (TrackOf(c) is not { } track)
                continue;
            bool cut = at > c.TimelineStart && at < c.TimelineEnd;
            if (cut)
            {
                var split = new SplitClipCommand(track, c, at, rightGroup);
                commands.Add(split);
                commands.Add(new SetClipAngleCommand(split.RightClip, angleIndex));
                if (ReferenceEquals(c, _selected))
                    newPrimary = split.RightClip;
            }
            else
            {
                commands.Add(new SetClipAngleCommand(c, angleIndex));
                if (ReferenceEquals(c, _selected))
                    newPrimary = c;
            }
        }
        if (commands.Count == 0)
            return;

        Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Switch angle", commands));
        if (newPrimary is not null)
            Select(newPrimary);
    }

    /// <summary>
    /// Adds a synthetic (generator / adjustment) clip at the playhead. It lands on the topmost video track when that
    /// track is free at the playhead; otherwise a new video track is created above so the clip stacks over (not
    /// displaces) existing content — both as one undoable entry. The new clip becomes the selection.
    /// </summary>
    private void InsertSyntheticVideoClip(Func<Timecode, Clip> create, string label)
    {
        if (_history is null || _project is null)
            return;

        Clip clip = create(_playhead);
        VideoTrack? top = _project.Timeline.VideoTracks.LastOrDefault();

        if (top is not null && top.ResolveActiveClip(_playhead) is null && top.ResolveActiveClip(clip.TimelineEnd - new Timecode(1)) is null)
        {
            Execute(new AddClipCommand(top, clip));
        }
        else
        {
            // Stack on a fresh top track so an adjustment grades the tracks beneath and a generator overlays them.
            var track = new VideoTrack { Name = $"V{_project.Timeline.VideoTracks.Count() + 1}" };
            Execute(new CompositeCommand(label,
            [
                new AddTrackCommand(_project.Timeline, track),
                new AddClipCommand(track, clip),
            ]));
        }

        Select(clip);
        ClipPlaced?.Invoke();
    }

    private long FrameTicks()
    {
        Rational fps = _project!.Timeline.FrameRate;
        return fps.Num > 0 ? Timecode.FromFrames(1, fps).Ticks : 0;
    }

    // ── Lane layout ─────────────────────────────────────────────────────────────────────────────────

    private List<(Track track, bool isVideo)> Lanes()
    {
        var lanes = new List<(Track, bool)>();
        if (_project is null)
            return lanes;
        // Video tracks top→bottom (highest z on top), then audio tracks.
        foreach (VideoTrack v in _project.Timeline.VideoTracks.Reverse())
            lanes.Add((v, true));
        foreach (AudioTrack a in _project.Timeline.AudioTracks)
            lanes.Add((a, false));
        return lanes;
    }

    private static double LaneTop(int index) => RulerHeight + index * (TrackHeight + TrackGap);

    private int LaneAtY(double y) => TimelineMath.LaneIndexAtY(y, RulerHeight, TrackHeight + TrackGap);

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(PaneBg, new Rect(size));

        if (_project is null)
            return;

        List<(Track track, bool isVideo)> lanes = Lanes();

        // Lane backgrounds + separators.
        for (int i = 0; i < lanes.Count; i++)
        {
            double top = LaneTop(i);
            var laneRect = new Rect(0, top, size.Width, TrackHeight);
            ctx.FillRectangle(i % 2 == 0 ? LaneEven : LaneOdd, laneRect);
            ctx.DrawLine(GridPen, new Point(0, top + TrackHeight), new Point(size.Width, top + TrackHeight));
        }

        DrawRuler(ctx, size);
        DrawInOutRange(ctx, size);
        DrawRenderBar(ctx, size);
        DrawClips(ctx, size, lanes);
        DrawTransitions(ctx, size, lanes);
        DrawSequenceMarkers(ctx, size);
        DrawHeaders(ctx, lanes);
        DrawBladePreview(ctx, size);
        DrawPlayhead(ctx, size);
        DrawDropPreview(ctx, size);
        DrawMovePreview(ctx, size);
        DrawMarquee(ctx, size);
    }

    // Sequence markers on the ruler (PLAN.md step 20): a coloured flag in the ruler with a faint line down the
    // lanes; span markers add a translucent band across the ruler.
    private void DrawSequenceMarkers(DrawingContext ctx, Size size)
    {
        if (_project is null || _project.Timeline.Markers.Count == 0)
            return;
        using var _ = ctx.PushClip(new Rect(_headerWidth, 0, size.Width - _headerWidth, size.Height));
        foreach (Marker marker in _project.Timeline.Markers)
        {
            double x = TimelineMath.XAtTicks(marker.Time.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            if (x < _headerWidth - 1 || x > size.Width)
                continue;
            IBrush brush = MarkerBrush(marker.Color);

            if (marker.IsSpan)
            {
                double xEnd = TimelineMath.XAtTicks(marker.End.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                ctx.FillRectangle(MarkerHighlightBrush(marker.Color),
                    new Rect(x, 0, Math.Max(1, xEnd - x), RulerHeight));
            }

            ctx.DrawLine(new Pen(MarkerLine, 1), new Point(x, RulerHeight), new Point(x, size.Height));
            // A small pennant in the ruler.
            var flag = new StreamGeometry();
            using (StreamGeometryContext g = flag.Open())
            {
                g.BeginFigure(new Point(x, 4), true);
                g.LineTo(new Point(x + 9, 4));
                g.LineTo(new Point(x + 9, 12));
                g.LineTo(new Point(x, 16));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, flag);
        }
    }

    // Clip markers on the clip body (PLAN.md step 20). A clip marker's time is within the clip's source, so its
    // timeline position is TimelineStart + (Time - SourceIn); only those inside the visible source span draw.
    private void DrawClipMarkers(DrawingContext ctx, Clip clip, Rect rect)
    {
        if (clip.Markers.Count == 0)
            return;
        foreach (Marker marker in clip.Markers)
        {
            if (marker.Time < clip.SourceIn || marker.Time >= clip.SourceOut)
                continue;
            long timelineTicks = clip.TimelineStart.Ticks + (marker.Time.Ticks - clip.SourceIn.Ticks);
            double x = TimelineMath.XAtTicks(timelineTicks, _pxPerSecond, _scrollX, _headerWidth);
            IBrush brush = MarkerBrush(marker.Color);
            // A small triangle pinned to the bottom edge of the clip.
            var tri = new StreamGeometry();
            using (StreamGeometryContext g = tri.Open())
            {
                g.BeginFigure(new Point(x - 4, rect.Bottom), true);
                g.LineTo(new Point(x + 4, rect.Bottom));
                g.LineTo(new Point(x, rect.Bottom - 6));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, tri);
        }
    }

    // A dashed accent line where a dragged bin tile would place a clip (PLAN.md step 16b).
    private void DrawDropPreview(DrawingContext ctx, Size size)
    {
        if (_dropPreviewX is not { } x || x < _headerWidth || x > size.Width)
            return;
        var pen = new Pen(Accent, 1.5) { DashStyle = new DashStyle([3, 3], 0) };
        ctx.DrawLine(pen, new Point(x, RulerHeight), new Point(x, size.Height));
    }

    // The cross-track drag ghost (PLAN.md step 16e): highlights the target lane and draws a translucent block
    // where the clip will land (its current snapped start + duration), with a "+" hint while copying (Alt).
    // Linked companions get the same ghost on their own lanes (they shift in time only), except while
    // copying — Alt duplicates just the primary. The real clips stay drawn in place — the model isn't
    // mutated until release.
    private void DrawMovePreview(DrawingContext ctx, Size size)
    {
        if (!_movePreview || _dragClip is null || _movePreviewTrack is null)
            return;
        List<(Track track, bool isVideo)> lanes = Lanes();
        int laneIndex = lanes.FindIndex(l => ReferenceEquals(l.track, _movePreviewTrack));
        if (laneIndex < 0)
            return;

        long delta = _movePreviewStart - _dragOrigStart.Ticks;

        // Every ghost as (lane, start, duration) — the primary on the hovered lane, companions on their own.
        var ghosts = new List<(int lane, long start, long dur)>
        {
            (laneIndex, _movePreviewStart, _dragOrigOut.Ticks - _dragOrigIn.Ticks),
        };
        if (!_movePreviewCopy)
            foreach ((Clip companion, Timecode origStart) in _dragLinked)
            {
                int lane = lanes.FindIndex(l => l.track.Clips.Contains(companion));
                if (lane >= 0)
                    ghosts.Add((lane, origStart.Ticks + delta, companion.SourceOut.Ticks - companion.SourceIn.Ticks));
            }

        foreach (int lane in ghosts.Select(g => g.lane).Distinct())
            ctx.FillRectangle(MovePreviewLaneFill,
                new Rect(_headerWidth, LaneTop(lane), size.Width - _headerWidth, TrackHeight));

        using var _ = ctx.PushClip(new Rect(_headerWidth, RulerHeight, size.Width - _headerWidth, size.Height - RulerHeight));
        foreach ((int lane, long start, long dur) in ghosts)
        {
            double x0 = TimelineMath.XAtTicks(start, _pxPerSecond, _scrollX, _headerWidth);
            double x1 = TimelineMath.XAtTicks(start + dur, _pxPerSecond, _scrollX, _headerWidth);
            var rect = new Rect(x0, LaneTop(lane) + 3, Math.Max(2, x1 - x0), TrackHeight - 6);
            ctx.DrawRectangle(lanes[lane].isVideo ? VideoGhostFill : AudioGhostFill, SelectPen, new RoundedRect(rect, 4));
            if (_movePreviewCopy)
                DrawIcon(ctx, Icons.Plus, new Rect(rect.X + 4, rect.Y + 2, IconSizes.Compact, IconSizes.Compact), Brushes.White);
        }
    }

    /// <summary>The render-bar spans to draw over the ruler (PLAN.md step 32) — computed by
    /// <see cref="RenderCache.RenderBarModel"/>; MainWindow pushes fresh spans after every model change.</summary>
    public IReadOnlyList<RenderCache.RenderBarSpan> RenderSpans
    {
        get => _renderSpans;
        set
        {
            _renderSpans = value ?? [];
            InvalidateVisual();
        }
    }

    /// <summary>The timeline in point (the I key), or <see langword="null"/> when unset — with
    /// <see cref="MarkOut"/> it scopes Render In to Out (PLAN.md step 32). Session-only UI state.</summary>
    public Timecode? MarkIn
    {
        get => _markIn;
        set { _markIn = value; InvalidateVisual(); }
    }

    /// <summary>The timeline out point (the O key), or <see langword="null"/> when unset.</summary>
    public Timecode? MarkOut
    {
        get => _markOut;
        set { _markOut = value; InvalidateVisual(); }
    }

    // The render bar (PLAN.md step 32): a 3px strip along the very top of the ruler, the familiar
    // green / yellow / red rendered-state model.
    private void DrawRenderBar(DrawingContext ctx, Size size)
    {
        if (_renderSpans.Count == 0)
            return;
        using var _ = ctx.PushClip(new Rect(_headerWidth, 0, size.Width - _headerWidth, RulerHeight));
        foreach (RenderCache.RenderBarSpan span in _renderSpans)
        {
            double x0 = TimelineMath.XAtTicks(span.InTicks, _pxPerSecond, _scrollX, _headerWidth);
            double x1 = TimelineMath.XAtTicks(span.OutTicks, _pxPerSecond, _scrollX, _headerWidth);
            if (x1 < _headerWidth || x0 > size.Width)
                continue;
            IBrush brush = span.State switch
            {
                RenderCache.RenderBarState.Rendered => RenderedBar,
                RenderCache.RenderBarState.NeedsRenderHeavy => NeedsRenderHeavyBar,
                _ => NeedsRenderBar,
            };
            ctx.FillRectangle(brush, new Rect(x0, 0, Math.Max(1, x1 - x0), 3));
        }
    }

    // The in/out range (I / O keys, PLAN.md step 32): a light shade across the ruler between the marks, with an
    // accent tick at each set mark — the work-area idiom found in leading editors, scoping Render In to Out.
    private void DrawInOutRange(DrawingContext ctx, Size size)
    {
        if (_markIn is null && _markOut is null)
            return;
        using var _ = ctx.PushClip(new Rect(_headerWidth, 0, size.Width - _headerWidth, RulerHeight));

        long inTicks = _markIn?.Ticks ?? 0;
        long outTicks = _markOut?.Ticks ?? _project?.Timeline.Duration.Ticks ?? inTicks;
        double x0 = TimelineMath.XAtTicks(inTicks, _pxPerSecond, _scrollX, _headerWidth);
        double x1 = TimelineMath.XAtTicks(outTicks, _pxPerSecond, _scrollX, _headerWidth);
        if (x1 > x0)
            ctx.FillRectangle(InOutFill, new Rect(x0, 0, x1 - x0, RulerHeight));

        if (_markIn is { } markIn)
        {
            double x = TimelineMath.XAtTicks(markIn.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            ctx.DrawLine(InOutPen, new Point(x, 0), new Point(x, RulerHeight));
        }
        if (_markOut is { } markOut)
        {
            double x = TimelineMath.XAtTicks(markOut.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            ctx.DrawLine(InOutPen, new Point(x, 0), new Point(x, RulerHeight));
        }
    }

    private void DrawRuler(DrawingContext ctx, Size size)
    {
        ctx.FillRectangle(RulerBg, new Rect(0, 0, size.Width, RulerHeight));
        ctx.DrawLine(EdgePen, new Point(0, RulerHeight), new Point(size.Width, RulerHeight));

        long interval = TimelineMath.RulerIntervalTicks(_pxPerSecond, 90);
        long firstTicks = TimelineMath.ClampNonNegative(TimelineMath.TicksAtX(_headerWidth, _pxPerSecond, _scrollX, _headerWidth));
        long t = firstTicks - (firstTicks % interval);
        using (ctx.PushClip(new Rect(_headerWidth, 0, size.Width - _headerWidth, RulerHeight)))
        {
            for (; ; t += interval)
            {
                double x = TimelineMath.XAtTicks(t, _pxPerSecond, _scrollX, _headerWidth);
                if (x > size.Width)
                    break;
                if (x < _headerWidth - 1)
                    continue;
                ctx.DrawLine(GridPen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
                ctx.DrawText(Label(TimeLabel(t), 10.5, MutedText), new Point(x + 4, 5));
            }
        }
    }

    private void DrawClips(DrawingContext ctx, Size size, List<(Track track, bool isVideo)> lanes)
    {
        using var _ = ctx.PushClip(new Rect(_headerWidth, RulerHeight, size.Width - _headerWidth, size.Height - RulerHeight));
        for (int i = 0; i < lanes.Count; i++)
        {
            (Track track, bool isVideo) = lanes[i];
            double top = LaneTop(i) + 3;
            double h = TrackHeight - 6;

            foreach (Clip clip in track.Clips)
            {
                double x0 = TimelineMath.XAtTicks(clip.TimelineStart.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                double x1 = TimelineMath.XAtTicks(clip.TimelineEnd.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                if (x1 < _headerWidth || x0 > size.Width)
                    continue;

                var rect = new Rect(x0, top, Math.Max(2, x1 - x0), h);
                var rounded = new RoundedRect(rect, 4);
                IBrush fill = clip.Kind switch
                {
                    ClipKind.Sequence => SequenceFill,
                    ClipKind.Multicam => MulticamFill,
                    _ => isVideo ? VideoFill : AudioFill,
                };
                ctx.DrawRectangle(fill, null, rounded);

                using (ctx.PushClip(rect))
                {
                    if (isVideo)
                        DrawFilmstrip(ctx, rect);
                    else
                        DrawWaveform(ctx, rect);
                    ctx.DrawText(Label(ClipName(clip), 11, Text), new Point(rect.X + 6, rect.Y + 4));
                    DrawClipMarkers(ctx, clip, rect);
                    DrawFadeOverlay(ctx, clip, rect);
                    if (clip.IsHeld)
                        DrawHoldBadge(ctx, rect);
                }

                // A disabled clip draws dimmed (PLAN.md step 53) — same body/detail, shaded to read at a glance.
                if (!clip.Enabled)
                    ctx.DrawRectangle(DisabledShade, null, rounded);

                // Every selection member draws the accent border; the primary's is full-strength so it stays
                // visually distinct in a multi-selection (PLAN.md step 54).
                if (_selection.Contains(clip))
                    ctx.DrawRectangle(null, ReferenceEquals(clip, _selected) ? SelectPen : MultiSelectPen, rounded);
            }
        }
    }

    // A held clip's "HOLD" pill, pinned to the clip body's top-right corner (PLAN.md step 43) — the freeze-frame
    // marker, so a hold reads at a glance like a fade or marker does. Skipped when the clip is too narrow.
    private static readonly IBrush HoldBadgeFill = Brush("#B3141821");

    private static void DrawHoldBadge(DrawingContext ctx, Rect rect)
    {
        FormattedText text = Label("HOLD", 9, Text);
        double w = text.Width + 10, h = text.Height + 3;
        var badge = new Rect(rect.Right - w - 4, rect.Y + 3, w, h);
        if (badge.X < rect.X + 4)
            return;
        ctx.DrawRectangle(HoldBadgeFill, null, new RoundedRect(badge, 3));
        ctx.DrawText(text, new Point(badge.X + 5, badge.Y + 1.5));
    }

    // Transitions on the cut (PLAN.md step 25): the classic NLE overlay — a translucent box spanning the
    // transition window with a bow-tie "X", outlined in the accent (brighter when selected). Drawn over the clips.
    private void DrawTransitions(DrawingContext ctx, Size size, List<(Track track, bool isVideo)> lanes)
    {
        using var _ = ctx.PushClip(new Rect(_headerWidth, RulerHeight, size.Width - _headerWidth, size.Height - RulerHeight));
        IBrush fill = TransitionFill;
        Pen xPen = TransitionXPen;
        Pen borderPen = TransitionBorderPen;

        for (int i = 0; i < lanes.Count; i++)
        {
            Track track = lanes[i].track;
            if (track.Transitions.Count == 0)
                continue;
            double top = LaneTop(i) + 3;
            double h = TrackHeight - 6;

            foreach (Transition tr in track.Transitions)
            {
                double x0 = TimelineMath.XAtTicks(tr.Start.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                double x1 = TimelineMath.XAtTicks(tr.End.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                if (x1 < _headerWidth || x0 > size.Width)
                    continue;

                var rect = new Rect(x0, top, Math.Max(6, x1 - x0), h);
                var rounded = new RoundedRect(rect, 3);
                ctx.DrawRectangle(fill, null, rounded);
                using (ctx.PushClip(rect))
                {
                    ctx.DrawLine(xPen, new Point(rect.X, rect.Y), new Point(rect.Right, rect.Bottom));
                    ctx.DrawLine(xPen, new Point(rect.X, rect.Bottom), new Point(rect.Right, rect.Y));
                }
                ctx.DrawRectangle(null, ReferenceEquals(tr, _selectedTransition) ? SelectPen : borderPen, rounded);
            }
        }
    }

    // Fade handles + opacity rubber-band (PLAN.md step 39), drawn inside the clip's clip rect: the opacity
    // envelope as a line across the body (top = 1, bottom = 0) with the faded-away region above it shaded so a
    // fade reads at a glance, keyframe dots on the selected clip, and the fade-in/out handle triangles pinned
    // to the top edge at the ramp tops (the corners when the fades are zero).
    private void DrawFadeOverlay(DrawingContext ctx, Clip clip, Rect rect)
    {
        AnimatableValue? opacity = FadeOps.FadeOpacity(clip);
        (long fadeIn, long fadeOut) = FadeOps.ReadFades(opacity, clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks);

        if (opacity is not null)
        {
            double xStart = Math.Max(rect.X, _headerWidth);
            double xEnd = Math.Min(rect.Right, Bounds.Width);
            if (xEnd > xStart)
            {
                var line = new StreamGeometry();
                var shade = new StreamGeometry();
                using (StreamGeometryContext lg = line.Open())
                using (StreamGeometryContext sg = shade.Open())
                {
                    sg.BeginFigure(new Point(xStart, rect.Y), true);
                    bool first = true;
                    for (double x = xStart; ; x += 4)
                    {
                        if (x > xEnd)
                            x = xEnd;
                        long t = TimelineMath.TicksAtX(x, _pxPerSecond, _scrollX, _headerWidth);
                        double level = Math.Clamp(opacity.Evaluate(new Timecode(t)), 0, 1);
                        var pt = new Point(x, TimelineMath.FadeYAtLevel(level, rect.Y, rect.Height, FadeBandPad));
                        if (first)
                        {
                            lg.BeginFigure(pt, false);
                            first = false;
                        }
                        else
                        {
                            lg.LineTo(pt);
                        }
                        sg.LineTo(pt);
                        if (x >= xEnd)
                            break;
                    }
                    sg.LineTo(new Point(xEnd, rect.Y));
                    sg.EndFigure(true);
                }
                ctx.DrawGeometry(FadeShade, null, shade);
                ctx.DrawGeometry(null, FadeBandPen, line);
            }

            // Keyframe dots on the selected clip, so the rubber-band's grab points are visible.
            if (ReferenceEquals(clip, _selected) && opacity.IsAnimated)
            {
                foreach (Keyframe k in opacity.Keyframes)
                {
                    double x = TimelineMath.XAtTicks(k.Time.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                    if (x < rect.X || x > rect.Right)
                        continue;
                    double y = TimelineMath.FadeYAtLevel(Math.Clamp(k.Value, 0, 1), rect.Y, rect.Height, FadeBandPad);
                    ctx.DrawEllipse(FadePointFill, null, new Point(x, y), 2.5, 2.5);
                }
            }
        }
        else if (ReferenceEquals(clip, _selected))
        {
            // No fade effect yet: show the band at full opacity on the selected clip so it is discoverable.
            double y = TimelineMath.FadeYAtLevel(1, rect.Y, rect.Height, FadeBandPad);
            ctx.DrawLine(FadeBandPen, new Point(rect.X, y), new Point(rect.Right, y));
        }

        if (rect.Width < 24)
            return; // too narrow for meaningful handles
        DrawFadeHandle(ctx, rect.X + TimelineMath.WidthOfTicks(fadeIn, _pxPerSecond), rect.Y,
            ReferenceEquals(clip, _dragClip) && _dragKind == DragKind.FadeIn);
        DrawFadeHandle(ctx, rect.Right - TimelineMath.WidthOfTicks(fadeOut, _pxPerSecond), rect.Y,
            ReferenceEquals(clip, _dragClip) && _dragKind == DragKind.FadeOut);
    }

    // A small downward triangle pinned to the clip's top edge — the draggable fade handle.
    private void DrawFadeHandle(DrawingContext ctx, double x, double top, bool active)
    {
        var tri = new StreamGeometry();
        using (StreamGeometryContext g = tri.Open())
        {
            g.BeginFigure(new Point(x - 4.5, top), true);
            g.LineTo(new Point(x + 4.5, top));
            g.LineTo(new Point(x, top + 7));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(active ? Accent : FadeHandleFill, null, tri);
    }

    // Schematic only (real poster frames are step 15): even vertical dividers like a filmstrip.
    private static void DrawFilmstrip(DrawingContext ctx, Rect rect)
    {
        for (double x = rect.X + 22; x < rect.Right - 2; x += 26)
            ctx.DrawLine(new Pen(ClipDetail, 1), new Point(x, rect.Y + 16), new Point(x, rect.Bottom - 3));
    }

    // Schematic only (real waveforms are step 15): a deterministic bar pattern around the centre line.
    private static void DrawWaveform(DrawingContext ctx, Rect rect)
    {
        double mid = rect.Y + rect.Height * 0.62;
        var pen = new Pen(AudioDetail, 1);
        for (double x = rect.X + 4; x < rect.Right - 2; x += 3)
        {
            double phase = (x - rect.X) * 0.20;
            double amp = (rect.Height * 0.30) * (0.35 + 0.65 * Math.Abs(Math.Sin(phase) * Math.Cos(phase * 0.37)));
            ctx.DrawLine(pen, new Point(x, mid - amp), new Point(x, mid + amp));
        }
    }

    private void DrawHeaders(DrawingContext ctx, List<(Track track, bool isVideo)> lanes)
    {
        ctx.FillRectangle(HeaderBg, new Rect(0, 0, _headerWidth, Bounds.Height));
        ctx.DrawLine(EdgePen, new Point(_headerWidth, 0), new Point(_headerWidth, Bounds.Height));

        for (int i = 0; i < lanes.Count; i++)
        {
            (Track track, bool isVideo) = lanes[i];
            double top = LaneTop(i);
            // Clip the name to the area left of the toggles so a long name can't bleed over them or past
            // the (now resizable) column edge.
            using (ctx.PushClip(new Rect(NameLeft, top, NameAreaWidth(isVideo), TrackHeight)))
                ctx.DrawText(Label(TrackName(track, isVideo), 11.5, Text), new Point(NameLeft, top + 7));

            if (isVideo)
            {
                DrawToggle(ctx, EnableBox(top), Icons.Eye, track.Enabled);
            }
            else
            {
                var audio = (AudioTrack)track;
                DrawToggle(ctx, MuteBox(top), "M", audio.Muted);
                DrawToggle(ctx, SoloBox(top), "S", audio.Solo);
            }
        }
    }

    private static void DrawToggle(DrawingContext ctx, Rect box, string glyph, bool on)
    {
        ctx.DrawRectangle(on ? ToggleOn : ToggleOff, null, new RoundedRect(box, 3));
        // Center the glyph in the box rather than a fixed left offset — narrower glyphs (S)
        // otherwise sit left with extra space on their right.
        var text = Label(glyph, 10, on ? Brushes.White : MutedText);
        ctx.DrawText(text, new Point(
            box.X + (box.Width - text.Width) / 2,
            box.Y + (box.Height - text.Height) / 2));
    }

    private static void DrawToggle(DrawingContext ctx, Rect box, Geometry icon, bool on)
    {
        ctx.DrawRectangle(on ? ToggleOn : ToggleOff, null, new RoundedRect(box, 3));
        var iconBox = new Rect(box.X + 4, box.Y + 4, Math.Max(0, box.Width - 8), Math.Max(0, box.Height - 8));
        DrawIcon(ctx, icon, iconBox, on ? Brushes.White : MutedText);
    }

    /// <summary>Draws vector icon geometry (Icons.cs) uniformly scaled and centered into <paramref name="box"/>
    /// — the DrawingContext equivalent of an Avalonia Path with Stretch="Uniform", for the custom-drawn
    /// Timeline canvas where there's no element tree to host a Path control.</summary>
    private static void DrawIcon(DrawingContext ctx, Geometry icon, Rect box, IBrush stroke)
    {
        Rect bounds = icon.Bounds;
        double scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
        double tx = box.X + (box.Width - bounds.Width * scale) / 2 - bounds.X * scale;
        double ty = box.Y + (box.Height - bounds.Height * scale) / 2 - bounds.Y * scale;
        using (ctx.PushTransform(new Matrix(scale, 0, 0, scale, tx, ty)))
            ctx.DrawGeometry(null, new Pen(stroke, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), icon);
    }

    private Rect MuteBox(double laneTop) => new(_headerWidth - 56, laneTop + TrackHeight - 24, 22, 17);
    private Rect SoloBox(double laneTop) => new(_headerWidth - 30, laneTop + TrackHeight - 24, 22, 17);
    private Rect EnableBox(double laneTop) => new(_headerWidth - 30, laneTop + TrackHeight - 24, 22, 17);

    // Width available for the track-name text on a lane: from NameLeft to just left of that kind's toggles.
    private double NameAreaWidth(bool isVideo) =>
        Math.Max(0, (isVideo ? _headerWidth - 30 : _headerWidth - 56) - 6 - NameLeft);

    private void DrawPlayhead(DrawingContext ctx, Size size)
    {
        double x = TimelineMath.XAtTicks(_playhead.Ticks, _pxPerSecond, _scrollX, _headerWidth);
        if (x < _headerWidth || x > size.Width)
            return;
        ctx.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, size.Height));
        // A small downward triangle handle at the top.
        var handle = new StreamGeometry();
        using (StreamGeometryContext g = handle.Open())
        {
            g.BeginFigure(new Point(x - 5, 0), true);
            g.LineTo(new Point(x + 5, 0));
            g.LineTo(new Point(x, 8));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Accent, null, handle);
    }

    // The Blade tool's cut-line preview: a thin vertical line at the exact (snapped) position the next click
    // would split — the skimmer / blade-line convention found in professional NLEs. Full lane height so the cut point can be
    // judged against clips on every track; visually lighter than the playhead so the two never read as one.
    private static readonly Pen BladePreviewPen = new(new ImmutableSolidColorBrush(Colors.White, 0.7), 1);

    private void DrawBladePreview(DrawingContext ctx, Size size)
    {
        if (_bladeHoverTicks is not { } ticks)
            return;
        double x = TimelineMath.XAtTicks(ticks, _pxPerSecond, _scrollX, _headerWidth);
        if (x < _headerWidth || x > size.Width)
            return;
        ctx.DrawLine(BladePreviewPen, new Point(x, RulerHeight), new Point(x, size.Height));
    }

    // ── Pointer interaction ─────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_project is null)
            return;

        // Right-click on a clip body/edge: select it and ask the shell for the context menu (PLAN.md step 53) —
        // the TitleEditRequested pattern, since this custom-drawn control can't host the menu's child controls.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            Point rp = e.GetPosition(this);
            if (rp.X >= _headerWidth && rp.Y >= RulerHeight
                && TryHitClip(rp, out Clip? rightClicked, out _) && rightClicked is not null
                && TrackOf(rightClicked) is { } rightClickedTrack)
            {
                Focus();
                // Right-clicking a member of a multi-selection keeps the set (so the menu's batch operations
                // act on it, PLAN.md step 54) and re-anchors the primary; otherwise it replaces the selection.
                if (_selection.Contains(rightClicked))
                    OnSelectionMutated(_selection.SetPrimary(rightClicked));
                else
                    Select(rightClicked);
                ClipContextMenuRequested?.Invoke(rightClicked, rightClickedTrack);
                e.Handled = true;
            }
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Point p = e.GetPosition(this);
        Focus();

        // Drag the column's right edge to resize the header (checked before the header branch, since the
        // grip band straddles the boundary).
        if (p.Y > RulerHeight && Math.Abs(p.X - _headerWidth) <= EdgeGrip)
        {
            _resizingHeader = true;
            e.Pointer.Capture(this);
            return;
        }

        // Track-header column: double-click a name to rename; single-click hits the toggles.
        if (p.X < _headerWidth)
        {
            if (e.ClickCount == 2)
                TryBeginRename(p);
            else
                HandleHeaderClick(p);
            return;
        }

        // View tools act anywhere in the lane/ruler area.
        if (_activeTool == EditTool.Zoom && p.X >= _headerWidth)
        {
            bool zoomOut = e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
            SetZoom(_pxPerSecond * (zoomOut ? 0.8 : 1.25), p.X);
            return;
        }
        if (_activeTool == EditTool.Hand && p.X >= _headerWidth)
        {
            _panning = true;
            _panPressX = p.X;
            _panOrigScroll = _scrollX;
            e.Pointer.Capture(this);
            return;
        }

        // Ruler → scrub.
        if (p.Y < RulerHeight)
        {
            _scrubbing = true;
            e.Pointer.Capture(this);
            SeekToX(p.X);
            return;
        }

        // A transition overlay on a cut (Select tool): clicking it selects it for Delete (PLAN.md step 25).
        if (_activeTool == EditTool.Select && TryHitTransition(p, out Transition? hitTransition, out Track? hitTrack))
        {
            SelectTransition(hitTransition, hitTrack);
            return;
        }

        // Clip body / edges.
        if (TryHitClip(p, out Clip? clip, out ClipDragMode mode) && clip is not null)
        {
            if (_activeTool == EditTool.Blade)
            {
                Select(clip);
                BladeClip(clip, p);
                return;
            }

            // Double-click a title clip → inline text editing (PLAN.md step 40), mirroring the track rename.
            if (e.ClickCount == 2 && _activeTool == EditTool.Select
                && clip.Kind == ClipKind.Generator && clip.Generator is { } gen
                && GeneratorTypeIds.IsTitle(gen.GeneratorTypeId))
            {
                Select(clip);
                int lane = LaneAtY(p.Y);
                double top = lane >= 0 ? LaneTop(lane) : p.Y;
                double x0 = Math.Max(_headerWidth, TimelineMath.XAtTicks(clip.TimelineStart.Ticks, _pxPerSecond, _scrollX, _headerWidth));
                double x1 = TimelineMath.XAtTicks(clip.TimelineEnd.Ticks, _pxPerSecond, _scrollX, _headerWidth);
                TitleEditRequested?.Invoke(clip, new Rect(x0 + 2, top + 4, Math.Max(60, x1 - x0 - 4), 20));
                return;
            }

            // Fade handles / opacity rubber-band (PLAN.md step 39) win over move/trim in the top handle band
            // and on the envelope line — checked before the multi-select gestures so Ctrl+click on the band
            // keeps adding a fade point (its pre-54 meaning) rather than toggling membership.
            if (_activeTool == EditTool.Select && TryBeginFadeGesture(clip, p, e.KeyModifiers))
            {
                Select(clip);
                e.Pointer.Capture(this);
                return;
            }

            // Multi-select gestures (PLAN.md step 54, Select tool): Ctrl-click toggles membership, Shift-click
            // extends — both only alter the selection (no drag begins).
            if (_activeTool == EditTool.Select && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                OnSelectionMutated(_selection.Toggle(clip));
                return;
            }
            if (_activeTool == EditTool.Select && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                OnSelectionMutated(_selection.Extend(clip));
                return;
            }

            // A plain press on a member of a multi-selection keeps the set (the drag below moves it rigidly)
            // and re-anchors the primary; releasing without movement collapses to just this clip (the
            // convention in leading editors, handled in CommitMovePreview). Any other plain press replaces the selection.
            if (_selection.Count > 1 && _selection.Contains(clip))
                OnSelectionMutated(_selection.SetPrimary(clip));
            else
                Select(clip);

            // Ripple and Roll act on an edge; a click on the clip body just selects.
            if (_activeTool is EditTool.Ripple or EditTool.Roll && mode == ClipDragMode.Move)
                return;
            BeginClipDrag(clip, mode, p);
            if (_dragKind == DragKind.None) // e.g. a Roll with no adjacent clip to roll against — nothing to drag
            {
                _dragClip = null;
                return;
            }
            e.Pointer.Capture(this);
            return;
        }

        // Empty lane area. With the Select tool a drag becomes the rubber-band marquee (PLAN.md step 54) —
        // the selection updates live as the band moves, additively with Ctrl/Shift held — while a plain click
        // (released under the drag threshold) keeps today's behavior: clear the selection, move the playhead.
        if (_activeTool == EditTool.Select)
        {
            _marquee = true;
            _marqueeDragged = false;
            _marqueeAdditive = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _marqueeOrigin = _marqueeCurrent = p;
            _marqueeBase = _marqueeAdditive ? [.. _selection.Clips] : [];
            e.Pointer.Capture(this);
            return;
        }

        // Other tools: move the playhead and clear the selection.
        Select(null);
        _scrubbing = true;
        e.Pointer.Capture(this);
        SeekToX(p.X);
    }

    // Updates the live marquee selection (PLAN.md step 54): every clip the band touches — lanes from the
    // band's vertical extent, clips by tick-span overlap (both pure, TimelineMath) — plus the additive base
    // captured at the press. The band's on-screen rect is drawn by DrawMarquee.
    private void UpdateMarquee(Point p)
    {
        _marqueeCurrent = p;
        if (!_marqueeDragged
            && Math.Abs(p.X - _marqueeOrigin.X) < MarqueeDragThresholdPx
            && Math.Abs(p.Y - _marqueeOrigin.Y) < MarqueeDragThresholdPx)
            return;
        _marqueeDragged = true;

        List<(Track track, bool isVideo)> lanes = Lanes();
        (int first, int last) = TimelineMath.MarqueeLaneRange(
            _marqueeOrigin.Y, p.Y, RulerHeight, TrackHeight + TrackGap, TrackHeight, lanes.Count);
        long t0 = TimelineMath.TicksAtX(Math.Min(_marqueeOrigin.X, p.X), _pxPerSecond, _scrollX, _headerWidth);
        long t1 = TimelineMath.TicksAtX(Math.Max(_marqueeOrigin.X, p.X), _pxPerSecond, _scrollX, _headerWidth);

        // Hash-set membership keeps the per-pointer-move rebuild O(n) on clip-heavy timelines.
        var hits = new List<Clip>(_marqueeBase);
        var seen = new HashSet<Clip>(_marqueeBase, ReferenceEqualityComparer.Instance);
        for (int i = first; i <= last; i++)
            foreach (Clip c in lanes[i].track.Clips)
                if (TimelineMath.MarqueeHitsSpan(t0, t1, c.TimelineStart.Ticks, c.TimelineEnd.Ticks) && seen.Add(c))
                    hits.Add(c);

        OnSelectionMutated(_selection.ReplaceAll(hits));
        InvalidateVisual(); // the band rect moved even when the selection didn't change
    }

    // The marquee band rect while a lane-area drag is in flight (PLAN.md step 54).
    private void DrawMarquee(DrawingContext ctx, Size size)
    {
        if (!_marquee || !_marqueeDragged)
            return;
        using var _ = ctx.PushClip(new Rect(_headerWidth, RulerHeight, size.Width - _headerWidth, size.Height - RulerHeight));
        var rect = new Rect(
            new Point(Math.Min(_marqueeOrigin.X, _marqueeCurrent.X), Math.Min(_marqueeOrigin.Y, _marqueeCurrent.Y)),
            new Point(Math.Max(_marqueeOrigin.X, _marqueeCurrent.X), Math.Max(_marqueeOrigin.Y, _marqueeCurrent.Y)));
        ctx.DrawRectangle(MarqueeFill, MarqueePen, rect);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);

        if (_resizingHeader)
        {
            _headerWidth = Math.Clamp(p.X, MinHeaderWidth, MaxHeaderWidth);
            ClampScroll();
            InvalidateVisual();
            return;
        }
        if (_scrubbing)
        {
            SeekToX(p.X);
            return;
        }
        if (_marquee)
        {
            UpdateMarquee(p);
            return;
        }
        if (_panning)
        {
            _scrollX = Math.Max(0, _panOrigScroll - (p.X - _panPressX));
            ClampScroll();
            InvalidateVisual();
            return;
        }
        if (_dragClip is not null)
        {
            UpdateClipDrag(p, e.KeyModifiers);
            return;
        }

        // Idle hover: show a resize cursor over the column edge, a hover-refined tool cursor over the lanes
        // (e.g. a trim cursor inside a clip's edge grip), the Blade cut-line preview, and the full track name
        // as a tooltip when the name is too long to fit the current column width.
        bool overGrip = p.Y > RulerHeight && Math.Abs(p.X - _headerWidth) <= EdgeGrip;
        if (overGrip)
        {
            Cursor = HorizontalResizeCursor;
            SetBladeHover(null);
        }
        else
        {
            TryHitClip(p, out Clip? hoverClip, out ClipDragMode hoverMode);
            Cursor = CursorFor(TimelineMath.HoverCursor(_activeTool, hoverMode));
            SetBladeHover(_activeTool == EditTool.Blade && hoverClip is not null
                ? TimelineMath.BladeCutTicks(
                    p.X, hoverClip.TimelineStart.Ticks, hoverClip.TimelineEnd.Ticks,
                    Snapping, _playhead.Ticks, SnapTolerancePx, _pxPerSecond, _scrollX, _headerWidth)
                : null);
        }
        UpdateHeaderTooltip(p, overGrip);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetBladeHover(null);
    }

    // The Blade tool's hover cut-line (timeline ticks), or null when no cut would land. Only redraws when the
    // value actually changes so idle mouse movement doesn't trigger redundant render passes.
    private long? _bladeHoverTicks;

    private void SetBladeHover(long? ticks)
    {
        if (_bladeHoverTicks == ticks)
            return;
        _bladeHoverTicks = ticks;
        InvalidateVisual();
    }

    // Sets the control tooltip to the full track name while hovering a truncated name in the header column;
    // clears it otherwise so no redundant tooltip shows for names that already fit.
    private void UpdateHeaderTooltip(Point p, bool overGrip)
    {
        string? tip = null;
        if (!overGrip && p.X < _headerWidth && p.Y > RulerHeight)
        {
            List<(Track track, bool isVideo)> lanes = Lanes();
            int i = LaneAtY(p.Y);
            if (i >= 0 && i < lanes.Count)
            {
                (Track track, bool isVideo) = lanes[i];
                string name = TrackName(track, isVideo);
                if (Label(name, 11.5, Text).Width > NameAreaWidth(isVideo))
                    tip = name;
            }
        }

        if (!Equals(ToolTip.GetTip(this), tip))
            ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _scrubbing = false;
        _panning = false;
        _resizingHeader = false;
        if (_marquee)
        {
            // A press that never crossed the drag threshold is a plain click on empty lane area: clear the
            // selection and move the playhead (the pre-54 behavior). With Ctrl/Shift held the click is a
            // no-op — an additive gesture that selected nothing shouldn't destroy the selection.
            if (!_marqueeDragged && !_marqueeAdditive)
            {
                Select(null);
                SeekToX(_marqueeOrigin.X);
            }
            _marquee = false;
            _marqueeDragged = false;
            _marqueeBase = [];
            InvalidateVisual();
        }
        if (_dragClip is not null)
        {
            if (_movePreview)
                CommitMovePreview(); // the Move gesture commits one command here (cross-track / copy / lock)
            _coalesce?.Dispose();    // seal a live (trim/slip/ripple/roll/slide) gesture as one undo entry
            _coalesce = null;
            _dragClip = null;
            _dragSourceTrack = null;
            _dragMode = ClipDragMode.None;
            _dragKind = DragKind.None;
            _dragLinked = [];
            _rippleUnits.Clear();
            _rollLeft = _rollRight = null;
            _slidePrev = _slideNext = null;
            _fadeOrigOpacity = null;
            _bandGrabTimes = [];
            _movePreview = false;
            _movePreviewTrack = null;
            _movePreviewCopy = false;
            Cursor = ToolCursor(_activeTool);
            InvalidateVisual();
        }
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double anchorX = e.GetPosition(this).X;
            SetZoom(_pxPerSecond * (e.Delta.Y > 0 ? 1.2 : 1 / 1.2), anchorX);
        }
        else
        {
            _scrollX = Math.Max(0, _scrollX - e.Delta.Y * 50);
            ClampScroll();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    private void HandleHeaderClick(Point p)
    {
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(p.Y);
        if (i < 0 || i >= lanes.Count)
            return;
        (Track track, bool isVideo) = lanes[i];
        double top = LaneTop(i);

        if (isVideo)
        {
            if (EnableBox(top).Contains(p))
                Execute(SetPropertyCommand<bool>.Create(
                    "Toggle track", () => track.Enabled, v => track.Enabled = v, !track.Enabled));
        }
        else
        {
            var audio = (AudioTrack)track;
            if (MuteBox(top).Contains(p))
                Execute(SetPropertyCommand<bool>.Create(
                    "Toggle mute", () => audio.Muted, v => audio.Muted = v, !audio.Muted));
            else if (SoloBox(top).Contains(p))
                Execute(SetPropertyCommand<bool>.Create(
                    "Toggle solo", () => audio.Solo, v => audio.Solo = v, !audio.Solo));
        }
    }

    // A double-click on a track name (not on its toggle buttons) requests an inline rename: raises
    // TrackRenameRequested with the name area's rect so the shell can position an editor over it.
    private void TryBeginRename(Point p)
    {
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(p.Y);
        if (i < 0 || i >= lanes.Count)
            return;
        (Track track, bool isVideo) = lanes[i];
        double top = LaneTop(i);

        // Ignore double-clicks that land on the toggles — they keep their single-click behaviour.
        if (EnableBox(top).Contains(p) || MuteBox(top).Contains(p) || SoloBox(top).Contains(p))
            return;

        var rect = new Rect(NameLeft - 2, top + 4, NameAreaWidth(isVideo) + 2, 20);
        TrackRenameRequested?.Invoke(track, rect);
    }

    /// <summary>
    /// Commits an inline track rename through the edit history (one undoable <see cref="SetPropertyCommand{T}"/>),
    /// mirroring the track toggles. No-op when the trimmed name is unchanged. Called by the shell's editor.
    /// </summary>
    public void CommitTrackRename(Track track, string newName)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_history is null)
            return;
        string trimmed = (newName ?? string.Empty).Trim();
        if (trimmed == track.Name)
            return;
        Execute(SetPropertyCommand<string>.Create(
            "Rename track", () => track.Name, v => track.Name = v, trimmed));
    }

    /// <summary>
    /// Commits the inline title text edit through the edit history (one undoable
    /// <see cref="SetGeneratorStringCommand"/>, PLAN.md step 40). No-op when unchanged or the clip is not a
    /// title. Called by the shell's overlay editor, mirroring <see cref="CommitTrackRename"/>.
    /// </summary>
    public void CommitTitleText(Clip clip, string newText)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (_history is null || clip.Generator is not { } gen)
            return;
        if (newText == gen.GetString(GeneratorParamNames.Text))
            return;
        Execute(new SetGeneratorStringCommand(gen, GeneratorParamNames.Text, newText));
    }

    private bool TryHitClip(Point p, out Clip? clip, out ClipDragMode mode)
    {
        clip = null;
        mode = ClipDragMode.None;
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(p.Y);
        if (i < 0 || i >= lanes.Count)
            return false;

        Track track = lanes[i].track;
        // Last clip wins so a clip drawn on top (later in the list) is hit first.
        foreach (Clip c in track.Clips)
        {
            double x0 = TimelineMath.XAtTicks(c.TimelineStart.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            double x1 = TimelineMath.XAtTicks(c.TimelineEnd.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            ClipDragMode m = TimelineMath.HitMode(p.X, x0, x1, EdgeGrip);
            if (m != ClipDragMode.None)
            {
                clip = c;
                mode = m;
            }
        }
        return clip is not null;
    }

    // A held clip's drag baseline (PLAN.md step 43): its independent hold duration and frozen source time —
    // trimming a held clip edits the hold duration (no media clamp) and slipping moves the frozen frame.
    private long _dragOrigHoldDur;
    private long _dragOrigHoldAt;
    private long _dragOrigDur; // clip.Duration at drag start (≠ out−in for a held clip)

    private void BeginClipDrag(Clip clip, ClipDragMode mode, Point p)
    {
        _dragClip = clip;
        _dragSourceTrack = TrackOf(clip);
        _dragMode = mode;
        _dragPressTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        _dragOrigIn = clip.SourceIn;
        _dragOrigOut = clip.SourceOut;
        _dragOrigStart = clip.TimelineStart;
        _dragOrigHoldDur = clip.HoldDuration.Ticks;
        _dragOrigHoldAt = clip.HoldFrameAt?.Ticks ?? 0;
        _dragOrigDur = clip.Duration.Ticks;
        _snapPoints = BuildSnapPoints(clip);
        _movePreview = false;
        _dragKind = DragKind.None;

        // Capture the other clips a Select-tool move shifts with the primary: the rest of a multi-selection
        // (the set moves rigidly, PLAN.md step 54) plus, with Linked on, every member's linked companions —
        // deduplicated, so the whole group shifts by one locked delta.
        _dragLinked = _activeTool == EditTool.Select
            ? ClipEdits.ExpandWithLinked(
                    _project!.Timeline,
                    _selection.Contains(clip) ? _selection.Clips : [clip],
                    Linked)
                .Where(m => !ReferenceEquals(m.Clip, clip))
                .Select(m => (m.Clip, m.Clip.TimelineStart))
                .ToList()
            : [];
        _dragGroupMinStart = _dragOrigStart.Ticks;
        foreach ((Clip _, Timecode origStart) in _dragLinked)
            _dragGroupMinStart = Math.Min(_dragGroupMinStart, origStart.Ticks);

        switch (_activeTool)
        {
            case EditTool.Slip:
                _dragKind = DragKind.Slip;
                break;
            case EditTool.Ripple when mode is ClipDragMode.TrimStart or ClipDragMode.TrimEnd:
                BeginRipple(clip, mode);
                break;
            case EditTool.Roll when mode is ClipDragMode.TrimStart or ClipDragMode.TrimEnd:
                BeginRoll(clip, mode); // leaves _dragKind == None (aborts) when there is no adjacent clip
                break;
            case EditTool.Slide:
                BeginSlide(clip);
                break;
            case EditTool.Select when mode == ClipDragMode.Move:
                // The Move gesture (Select tool, clip body) previews across tracks and commits one command on
                // release — so copy + cross-track + horizontal-lock are each a single undo entry (PLAN.md step 16e).
                _dragKind = DragKind.MovePreview;
                _movePreview = true;
                _movePreviewStart = _dragOrigStart.Ticks;
                _movePreviewTrack = _dragSourceTrack;
                _movePreviewCopy = false;
                break;
            default: // Select tool on an edge → plain trim
                _dragKind = DragKind.Trim;
                break;
        }

        // Live gestures mutate the model on every move and coalesce into one undo entry; the move preview commits
        // exactly one command on release (so it opens no scope).
        _coalesce = _dragKind is DragKind.Trim or DragKind.Slip or DragKind.Ripple or DragKind.Roll or DragKind.Slide
            ? _history!.BeginCoalescing()
            : null;
    }

    private void UpdateClipDrag(Point p, KeyModifiers mods)
    {
        long pointerTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        long delta = pointerTicks - _dragPressTicks;

        switch (_dragKind)
        {
            case DragKind.Slip: UpdateSlip(delta); return;
            case DragKind.MovePreview: UpdateMovePreview(p, delta, mods); return;
            case DragKind.Ripple: UpdateRipple(pointerTicks); return;
            case DragKind.Roll: UpdateRoll(pointerTicks); return;
            case DragKind.Slide: UpdateSlide(pointerTicks); return;
            case DragKind.FadeIn: UpdateFadeHandle(pointerTicks, fadeIn: true); return;
            case DragKind.FadeOut: UpdateFadeHandle(pointerTicks, fadeIn: false); return;
            case DragKind.Band: UpdateFadeBand(p); return;
            case DragKind.None: return;
        }

        // A held clip's trim edits its independent HoldDuration — no media clamp, like a generator/still
        // (PLAN.md step 43). The frozen frame and the retained source span are untouched.
        if (_dragClip!.IsHeld)
        {
            long newDur = _dragOrigHoldDur, newHeldStart = _dragOrigStart.Ticks;
            if (_dragMode == ClipDragMode.TrimEnd)
            {
                newDur = Math.Max(_minDurTicks, _dragOrigHoldDur + delta);
                if (Snapping)
                {
                    long end = _dragOrigStart.Ticks + newDur;
                    long snapped = TimelineMath.Snap(end, _snapPoints, SnapTolerancePx, _pxPerSecond);
                    if (snapped != end)
                        newDur = Math.Max(_minDurTicks, snapped - _dragOrigStart.Ticks);
                }
            }
            else if (_dragMode == ClipDragMode.TrimStart)
            {
                long origEnd = _dragOrigStart.Ticks + _dragOrigHoldDur;
                newHeldStart = TimelineMath.ClampNonNegative(_dragOrigStart.Ticks + delta);
                if (Snapping)
                    newHeldStart = TimelineMath.Snap(newHeldStart, _snapPoints, SnapTolerancePx, _pxPerSecond);
                newHeldStart = TimelineMath.ClampNonNegative(Math.Min(newHeldStart, origEnd - _minDurTicks));
                newDur = origEnd - newHeldStart;
            }
            Execute(new TrimHeldClipCommand(_dragClip, new Timecode(newHeldStart), new Timecode(newDur), "Trim clip"));
            return;
        }

        // Otherwise an edge trim: mutate live and coalesce so the whole drag is one undo entry.
        long newIn = _dragOrigIn.Ticks, newOut = _dragOrigOut.Ticks, newStart = _dragOrigStart.Ticks;

        switch (_dragMode)
        {
            case ClipDragMode.TrimEnd:
                newOut = Math.Max(_dragOrigIn.Ticks + _minDurTicks, _dragOrigOut.Ticks + delta);
                if (Snapping)
                {
                    long end = _dragOrigStart.Ticks + (newOut - _dragOrigIn.Ticks);
                    long snapped = TimelineMath.Snap(end, _snapPoints, SnapTolerancePx, _pxPerSecond);
                    if (snapped != end)
                        newOut = Math.Max(_dragOrigIn.Ticks + _minDurTicks, _dragOrigIn.Ticks + (snapped - _dragOrigStart.Ticks));
                }
                // The out-point stops at the end of the source media, like slip/ripple/roll/slide (and every
                // major NLE); the min-duration floor wins if the media is shorter than one frame.
                newOut = Math.Max(_dragOrigIn.Ticks + _minDurTicks, Math.Min(newOut, MediaDurationTicks(_dragClip!)));
                break;

            case ClipDragMode.TrimStart:
                newStart = TimelineMath.ClampNonNegative(_dragOrigStart.Ticks + delta);
                if (Snapping)
                    newStart = TimelineMath.Snap(newStart, _snapPoints, SnapTolerancePx, _pxPerSecond);
                long deltaActual = newStart - _dragOrigStart.Ticks;
                newIn = _dragOrigIn.Ticks + deltaActual;
                if (newIn < 0) { newStart -= newIn; newIn = 0; }
                if (newIn > _dragOrigOut.Ticks - _minDurTicks)
                {
                    long over = newIn - (_dragOrigOut.Ticks - _minDurTicks);
                    newIn -= over;
                    newStart -= over;
                }
                newStart = TimelineMath.ClampNonNegative(newStart);
                break;
        }

        Execute(new SetClipPlacementCommand(
            _dragClip!, new Timecode(newIn), new Timecode(newOut), new Timecode(newStart), "Trim clip"));
    }

    // Updates the move-gesture preview (PLAN.md step 16e): snapped landing time (Shift locks it to the origin),
    // the target track under the cursor (kept on the source track when the lane is an incompatible kind), and
    // the copy flag (Alt). No model mutation — the commit happens once on pointer release.
    private void UpdateMovePreview(Point p, long delta, KeyModifiers mods)
    {
        bool lockX = mods.HasFlag(KeyModifiers.Shift);
        _movePreviewCopy = mods.HasFlag(KeyModifiers.Alt);

        if (lockX)
        {
            _movePreviewStart = _dragOrigStart.Ticks;
        }
        else
        {
            // Clamp the delta so no group member would cross t=0, then snap the primary's start.
            long clamped = Math.Max(delta, -_dragGroupMinStart);
            _movePreviewStart = SnapMove(_dragOrigStart.Ticks + clamped, _dragOrigDur);
        }

        (Track? laneTrack, _) = TrackAndKindAtY(p.Y);
        _movePreviewTrack = ClipPlacement.CompatibleTrack(_dragSourceTrack!, laneTrack) ?? _dragSourceTrack;

        Cursor = _movePreviewCopy ? new Cursor(StandardCursorType.DragCopy) : ToolCursor(_activeTool);
        InvalidateVisual();
    }

    // Commits the move gesture as exactly one undo entry on pointer release (PLAN.md step 16e):
    //  • Alt-copy → add an independent duplicate on the target track (original untouched);
    //  • cross-track move → MoveClipToTrackCommand;
    //  • same-track move → SetClipPlacementCommand;
    //  • the other moved clips — multi-selection members and linked companions alike (PLAN.md step 54) —
    //    shift in time only (they keep their own track), wrapped in a CompositeCommand (ClipEdits.MoveSet).
    private void CommitMovePreview()
    {
        if (_dragClip is null || _dragSourceTrack is null)
            return;

        Clip clip = _dragClip;
        Track src = _dragSourceTrack;
        Track dst = _movePreviewTrack ?? src;
        long newStart = _movePreviewStart;

        if (_movePreviewCopy)
        {
            Clip clone = ClipboardOps.Paste(clip, new Timecode(newStart));
            Execute(new AddClipCommand(dst, clone));
            Select(clone);
            ClipPlaced?.Invoke();
            return;
        }

        if (ClipEdits.MoveSet(clip, src, dst, newStart,
                _dragOrigIn, _dragOrigOut, _dragOrigStart.Ticks, _dragLinked) is not { } command)
        {
            // Pure click, no movement: a plain press on a member of a multi-selection kept the set for the
            // drag — releasing without moving collapses the selection to just that clip (the
            // convention in leading editors, PLAN.md step 54).
            if (_selection.Count > 1)
                Select(clip);
            return;
        }

        Execute(command);
        if (!ReferenceEquals(dst, src))
            ClipPlaced?.Invoke();
    }

    /// <summary>
    /// Slips the dragged clip's source window by <paramref name="rawDelta"/> ticks, clamped to the media so
    /// the visible content shifts but the clip neither moves nor changes duration (PLAN.md step 13). Dragging
    /// right reveals later source content. Coalesces into one undo entry like the other drag gestures.
    /// </summary>
    private void UpdateSlip(long rawDelta)
    {
        // Slip on a held clip moves the frozen frame through the source instead (PLAN.md step 43): same gesture
        // (content shifts, placement/duration fixed), applied to the single held source time, clamped to media.
        if (_dragClip!.IsHeld)
        {
            long media = MediaDurationTicks(_dragClip);
            long newHoldAt = Math.Clamp(_dragOrigHoldAt + rawDelta, 0, Math.Max(0, media - 1));
            Execute(new SetClipHoldCommand(_dragClip, new Timecode(newHoldAt), _dragClip.HoldDuration, "Slip clip"));
            return;
        }

        long mediaDuration = MediaDurationTicks(_dragClip!);
        long slip = TimelineMath.ClampSlip(_dragOrigIn.Ticks, _dragOrigOut.Ticks, mediaDuration, rawDelta);
        Execute(new SetClipPlacementCommand(
            _dragClip!,
            new Timecode(_dragOrigIn.Ticks + slip),
            new Timecode(_dragOrigOut.Ticks + slip),
            _dragOrigStart,
            "Slip clip"));
    }

    // ── Fade handles & opacity rubber-band (PLAN.md step 39) ───────────────────────────────────────

    // The clip's on-screen body rect (the same geometry DrawClips uses), or null when its track isn't visible.
    private Rect? ClipRectOf(Clip clip)
    {
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = lanes.FindIndex(l => l.track.Clips.Contains(clip));
        if (i < 0)
            return null;
        double x0 = TimelineMath.XAtTicks(clip.TimelineStart.Ticks, _pxPerSecond, _scrollX, _headerWidth);
        double x1 = TimelineMath.XAtTicks(clip.TimelineEnd.Ticks, _pxPerSecond, _scrollX, _headerWidth);
        return new Rect(x0, LaneTop(i) + 3, Math.Max(2, x1 - x0), TrackHeight - 6);
    }

    /// <summary>
    /// Starts a fade gesture when the press lands on a fade handle (top corners / ramp tops) or on the opacity
    /// rubber-band line (PLAN.md step 39). Captures the envelope at press so drag updates are computed from one
    /// stable original, and opens a coalescing scope so the whole drag is a single undo entry. Ctrl+click on
    /// the band adds a keyframe at the pointer (which the rest of the drag then moves). Returns whether a
    /// gesture began.
    /// </summary>
    private bool TryBeginFadeGesture(Clip clip, Point p, KeyModifiers mods)
    {
        if (_history is null || ClipRectOf(clip) is not { } rect)
            return false;

        AnimatableValue? opacity = FadeOps.FadeOpacity(clip);
        (long fadeIn, long fadeOut) = FadeOps.ReadFades(opacity, clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks);

        // 1. The handle triangles along the top edge.
        double inX = rect.X + TimelineMath.WidthOfTicks(fadeIn, _pxPerSecond);
        double outX = rect.Right - TimelineMath.WidthOfTicks(fadeOut, _pxPerSecond);
        FadeHandleKind handle = rect.Width >= 24
            ? TimelineMath.HitFadeHandle(p.X, p.Y, rect.Y, FadeHandleBand, inX, outX, FadeHandleGrip)
            : FadeHandleKind.None;
        if (handle != FadeHandleKind.None)
        {
            _dragClip = clip;
            _dragKind = handle == FadeHandleKind.FadeIn ? DragKind.FadeIn : DragKind.FadeOut;
            _fadeOrigOpacity = opacity;
            _fadeOrigIn = fadeIn;
            _fadeOrigOut = fadeOut;
            _fadeClipRect = rect;
            _coalesce = _history.BeginCoalescing();
            InvalidateVisual();
            return true;
        }

        // 2. The rubber-band: within a few px of the envelope line, inside the clip body.
        if (p.X < rect.X || p.X > rect.Right)
            return false;
        long pressTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        double level = opacity is null ? 1.0 : Math.Clamp(opacity.Evaluate(new Timecode(pressTicks)), 0, 1);
        double bandY = TimelineMath.FadeYAtLevel(level, rect.Y, rect.Height, FadeBandPad);
        if (Math.Abs(p.Y - bandY) > FadeBandGrip)
            return false;

        _dragClip = clip;
        _dragKind = DragKind.Band;
        _fadeClipRect = rect;
        _coalesce = _history.BeginCoalescing();

        long tolTicks = (long)(FadeHandleGrip / Math.Max(1e-6, _pxPerSecond) * Timecode.TicksPerSecond);
        if (mods.HasFlag(KeyModifiers.Control))
        {
            // Add a point at the pointer, then let the rest of the drag move it — one coalesced undo entry.
            AnimatableValue added = FadeOps.WithAddedPoint(opacity, pressTicks);
            Execute(new SetClipFadeCommand(clip, added, "Add fade point"));
            _fadeOrigOpacity = added;
            _bandGrabTimes = [pressTicks];
        }
        else
        {
            _fadeOrigOpacity = opacity;
            _bandGrabTimes = FadeOps.GrabKeyframes(opacity, pressTicks, tolTicks);
        }
        _bandPressLevel = TimelineMath.FadeLevelAtY(p.Y, rect.Y, rect.Height, FadeBandPad);
        return true;
    }

    // Drags a fade handle: the fade length follows the pointer, clamped so the two fades never cross, and the
    // envelope is rebuilt from the press-time original (interior rubber-band points preserved).
    private void UpdateFadeHandle(long pointerTicks, bool fadeIn)
    {
        Clip clip = _dragClip!;
        long start = clip.TimelineStart.Ticks, end = clip.TimelineEnd.Ticks;
        long duration = Math.Max(0, end - start);

        long newIn = _fadeOrigIn, newOut = _fadeOrigOut;
        if (fadeIn)
            newIn = Math.Clamp(pointerTicks - start, 0, duration - _fadeOrigOut);
        else
            newOut = Math.Clamp(end - pointerTicks, 0, duration - _fadeOrigIn);

        Execute(new SetClipFadeCommand(
            clip, FadeOps.BuildOpacity(_fadeOrigOpacity, start, end, newIn, newOut)));
    }

    // Drags the rubber-band vertically: the grabbed keyframes (or the flat level) move by the total level
    // delta since the press, computed against the press-time original so the drag is exact.
    private void UpdateFadeBand(Point p)
    {
        double level = TimelineMath.FadeLevelAtY(p.Y, _fadeClipRect.Y, _fadeClipRect.Height, FadeBandPad);
        Execute(new SetClipFadeCommand(
            _dragClip!, FadeOps.WithValueDelta(_fadeOrigOpacity, _bandGrabTimes, level - _bandPressLevel)));
    }

    // ── Ripple / roll / slide (PLAN.md step 22) ─────────────────────────────────────────────────────

    // Source→timeline and timeline→source conversions for a clip at the given playback speed (retime, step 21):
    // a faster clip consumes more source per timeline tick (MapToSource scales by speed), so timeline ticks are
    // source ÷ speed. The 1× case is the identity.
    private static long ToTimeline(long sourceTicks, Rational speed) => new Timecode(sourceTicks).Scale(speed.Inverse()).Ticks;
    private static long ToSource(long timelineTicks, Rational speed) => new Timecode(timelineTicks).Scale(speed).Ticks;

    // Snaps the moving reference point (anchorTick + delta) to nearby edits/playhead and returns the adjusted
    // delta; a no-op when snapping is off or nothing is within tolerance. Mirrors the edge-snap in UpdateClipDrag.
    private long SnapDelta(long anchorTick, long delta)
    {
        if (!Snapping)
            return delta;
        long moving = anchorTick + delta;
        long snapped = TimelineMath.Snap(moving, _snapPoints, SnapTolerancePx, _pxPerSecond);
        return snapped == moving ? delta : delta + (snapped - moving);
    }

    // Begins a ripple-trim gesture: the dragged clip's edge plus, when Linked is on, its companion clips' matching
    // edges. Each "unit" carries the downstream clips on its own track so every affected track stays gap-free.
    private void BeginRipple(Clip clip, ClipDragMode mode)
    {
        // A held clip's duration is independent of its source span, so the source-trim ripple math doesn't apply
        // — the gesture aborts (trim a held clip with the Select tool instead; PLAN.md step 43).
        if (clip.IsHeld)
            return;
        _rippleTrimEnd = mode == ClipDragMode.TrimEnd;
        _rippleUnits.Clear();
        _rippleUnits.Add(BuildRippleUnit(clip, _dragSourceTrack!));
        if (Linked)
            foreach ((Track ctrack, Clip cclip) in _project!.Timeline.ClipsLinkedTo(clip))
            {
                if (cclip.IsHeld)
                {
                    _rippleUnits.Clear();
                    return;
                }
                _rippleUnits.Add(BuildRippleUnit(cclip, ctrack));
            }
        _dragKind = DragKind.Ripple;
    }

    private RippleUnit BuildRippleUnit(Clip clip, Track track)
    {
        Timecode origEnd = clip.TimelineEnd;
        var downstream = new List<(Clip, Timecode)>();
        foreach (Clip c in track.Clips)
            if (!ReferenceEquals(c, clip) && c.TimelineStart >= origEnd)
                downstream.Add((c, c.TimelineStart));
        return new RippleUnit(clip, clip.SpeedRatio, MediaDurationTicks(clip), clip.SourceIn, clip.SourceOut, downstream);
    }

    private void UpdateRipple(long pointerTicks)
    {
        // The reference edge follows the cursor (relative to the grab point), then snaps to nearby edits.
        long anchor = _rippleTrimEnd ? _dragOrigStart.Ticks + (_dragOrigOut.Ticks - _dragOrigIn.Ticks) : _dragOrigStart.Ticks;
        long delta = SnapDelta(anchor, pointerTicks - _dragPressTicks);

        // Intersect every unit's allowable edge travel (companions share media/speed by construction; be safe).
        long lower = long.MinValue, upper = long.MaxValue;
        foreach (RippleUnit u in _rippleUnits)
        {
            long durTimeline = ToTimeline(u.OrigOut.Ticks - u.OrigIn.Ticks, u.Speed);
            long inHeadroom = ToTimeline(u.OrigIn.Ticks, u.Speed);
            long outHeadroom = ToTimeline(Math.Max(0, u.MediaDuration - u.OrigOut.Ticks), u.Speed);
            (long lo, long hi) = TimelineMath.RippleTrimBounds(_rippleTrimEnd, durTimeline, inHeadroom, outHeadroom, _minDurTicks);
            lower = Math.Max(lower, lo);
            upper = Math.Min(upper, hi);
        }
        if (upper < lower)
            return;
        delta = Math.Clamp(delta, lower, upper);

        var commands = new List<IEditCommand>();
        foreach (RippleUnit u in _rippleUnits)
        {
            long sourceDelta = ToSource(delta, u.Speed);
            Timecode newIn = u.OrigIn, newOut = u.OrigOut;
            long shift; // downstream shift in timeline ticks (= the clip's duration change)
            if (_rippleTrimEnd)
            {
                newOut = new Timecode(u.OrigOut.Ticks + sourceDelta);
                shift = delta;
            }
            else
            {
                newIn = new Timecode(u.OrigIn.Ticks + sourceDelta);
                shift = -delta;
            }
            commands.Add(new RippleTrimCommand(u.Clip, newIn, newOut, u.Downstream, shift));
        }
        Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Ripple trim", commands));
    }

    // Begins a roll gesture: resolves the two clips sharing the dragged cut. Leaves the gesture inert (DragKind
    // stays None, so the press aborts) when there is no adjacent clip on the other side of the cut.
    private void BeginRoll(Clip clip, ClipDragMode mode)
    {
        Track track = _dragSourceTrack!;
        Clip? left = mode == ClipDragMode.TrimEnd ? clip : AdjacentBefore(track, clip);
        Clip? right = mode == ClipDragMode.TrimEnd ? AdjacentAfter(track, clip) : clip;
        if (left is null || right is null)
            return;
        // A held clip's duration ignores its source span, so rolling its source edge can't move the cut — abort
        // (PLAN.md step 43).
        if (left.IsHeld || right.IsHeld)
            return;

        _rollLeft = left;
        _rollRight = right;
        _rollLeftSpeed = left.SpeedRatio;
        _rollRightSpeed = right.SpeedRatio;
        _rollLeftMedia = MediaDurationTicks(left);
        _rollRightMedia = MediaDurationTicks(right);
        _rollOrigLeftOut = left.SourceOut;
        _rollOrigRightIn = right.SourceIn;
        _rollOrigRightStart = right.TimelineStart;
        _rollOrigCut = left.TimelineEnd; // == right.TimelineStart
        _dragKind = DragKind.Roll;
    }

    private void UpdateRoll(long pointerTicks)
    {
        long delta = SnapDelta(_rollOrigCut.Ticks, pointerTicks - _dragPressTicks);

        long leftDur = ToTimeline(_rollOrigLeftOut.Ticks - _rollLeft!.SourceIn.Ticks, _rollLeftSpeed);
        long leftHeadroom = ToTimeline(Math.Max(0, _rollLeftMedia - _rollOrigLeftOut.Ticks), _rollLeftSpeed);
        long rightDur = ToTimeline(_rollRight!.SourceOut.Ticks - _rollOrigRightIn.Ticks, _rollRightSpeed);
        long rightHeadroom = ToTimeline(_rollOrigRightIn.Ticks, _rollRightSpeed);

        delta = TimelineMath.ClampRollDelta(delta, leftDur, leftHeadroom, rightDur, rightHeadroom, _minDurTicks);

        Execute(new RollEditCommand(
            _rollLeft, _rollRight,
            new Timecode(_rollOrigLeftOut.Ticks + ToSource(delta, _rollLeftSpeed)),
            new Timecode(_rollOrigRightIn.Ticks + ToSource(delta, _rollRightSpeed)),
            new Timecode(_rollOrigRightStart.Ticks + delta)));
    }

    // Begins a slide gesture: captures the (optional) adjacent neighbours that will absorb the clip's movement.
    private void BeginSlide(Clip clip)
    {
        Track track = _dragSourceTrack!;
        _slidePrev = AdjacentBefore(track, clip);
        _slideNext = AdjacentAfter(track, clip);
        // Sliding trims the neighbours' source edges — meaningless on a held neighbour, whose duration ignores
        // its source span (PLAN.md step 43). The slid clip itself moving is fine even when held.
        if (_slidePrev?.IsHeld == true || _slideNext?.IsHeld == true)
        {
            _slidePrev = null;
            _slideNext = null;
            return;
        }
        _slidePrevSpeed = _slidePrev?.SpeedRatio ?? Rational.One;
        _slideNextSpeed = _slideNext?.SpeedRatio ?? Rational.One;
        _slidePrevMedia = _slidePrev is null ? 0 : MediaDurationTicks(_slidePrev);
        _slideNextMedia = _slideNext is null ? 0 : MediaDurationTicks(_slideNext);
        _slideOrigPrevOut = _slidePrev?.SourceOut ?? default;
        _slideOrigNextIn = _slideNext?.SourceIn ?? default;
        _slideOrigNextStart = _slideNext?.TimelineStart ?? default;
        _dragKind = DragKind.Slide;
    }

    private void UpdateSlide(long pointerTicks)
    {
        long delta = SnapDelta(_dragOrigStart.Ticks, pointerTicks - _dragPressTicks);

        // A missing neighbour imposes no source/min-duration constraint on that side.
        const long Unbounded = long.MaxValue / 4;
        long prevDur = _slidePrev is null ? Unbounded
            : ToTimeline(_slideOrigPrevOut.Ticks - _slidePrev.SourceIn.Ticks, _slidePrevSpeed);
        long prevHeadroom = _slidePrev is null ? Unbounded
            : ToTimeline(Math.Max(0, _slidePrevMedia - _slideOrigPrevOut.Ticks), _slidePrevSpeed);
        long nextDur = _slideNext is null ? Unbounded
            : ToTimeline(_slideNext.SourceOut.Ticks - _slideOrigNextIn.Ticks, _slideNextSpeed);
        long nextHeadroom = _slideNext is null ? Unbounded
            : ToTimeline(_slideOrigNextIn.Ticks, _slideNextSpeed);

        delta = TimelineMath.ClampSlideDelta(delta, prevDur, prevHeadroom, nextDur, nextHeadroom, _minDurTicks);
        delta = Math.Max(delta, -_dragOrigStart.Ticks); // never slide the clip's start below the timeline origin

        Timecode newPrevOut = _slidePrev is null ? default : new Timecode(_slideOrigPrevOut.Ticks + ToSource(delta, _slidePrevSpeed));
        Timecode newNextIn = _slideNext is null ? default : new Timecode(_slideOrigNextIn.Ticks + ToSource(delta, _slideNextSpeed));
        Timecode newNextStart = _slideNext is null ? default : new Timecode(_slideOrigNextStart.Ticks + delta);

        Execute(new SlideClipCommand(
            _dragClip!, new Timecode(_dragOrigStart.Ticks + delta),
            _slidePrev, newPrevOut,
            _slideNext, newNextIn, newNextStart));
    }

    // The clip on a track whose timeline end butts exactly against this clip's start (its left neighbour), or null.
    private static Clip? AdjacentBefore(Track track, Clip clip)
    {
        Clip? best = null;
        foreach (Clip c in track.Clips)
            if (!ReferenceEquals(c, clip) && c.TimelineEnd == clip.TimelineStart)
                best = c;
        return best;
    }

    // The clip on a track whose timeline start butts exactly against this clip's end (its right neighbour), or null.
    private static Clip? AdjacentAfter(Track track, Clip clip)
    {
        foreach (Clip c in track.Clips)
            if (!ReferenceEquals(c, clip) && c.TimelineStart == clip.TimelineEnd)
                return c;
        return null;
    }

    /// <summary>
    /// Ripple-deletes the selected clips (Shift+Delete, as in leading editors): removes each and shifts later clips
    /// on its track left so the gaps close (cumulatively when several selected clips share a track). With Linked
    /// on, companion A/V clips are removed and their tracks rippled too — all one undo entry (steps 22/54).
    /// </summary>
    public void RippleDeleteSelected()
    {
        if (_selection.Count == 0 || _history is null || _project is null)
            return;
        if (ClipEdits.RippleDeleteAll(_project.Timeline, _selection.Clips, Linked) is not { } command)
            return;
        Execute(command);
        Select(null);
    }

    /// <summary>
    /// Blade (razor) split at the cursor: splits the clip under the pointer at <paramref name="p"/>'s timeline
    /// time (snapped to the playhead when snapping is on). With Linked on, every companion clip that also spans
    /// the cut is split too and the right-hand halves share a fresh link group — so each side stays an
    /// independently linked A/V pair. The whole cut is one undo entry. A cut on a clip's very edge is ignored.
    /// </summary>
    private void BladeClip(Clip clip, Point p)
    {
        Track? track = TrackOf(clip);
        if (track is null)
            return;

        // Same position logic as the hover cut-line (BladeCutTicks), so the cut lands exactly where the
        // preview showed. Null = the (snapped) cut would fall on/outside the clip's edges — ignored.
        long? cutTicks = TimelineMath.BladeCutTicks(
            p.X, clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks,
            Snapping, _playhead.Ticks, SnapTolerancePx, _pxPerSecond, _scrollX, _headerWidth);
        if (cutTicks is not { } atTicks)
            return;

        // The split core (companions, fresh right-hand link group, one undo entry) is shared with Split at
        // Playhead (PLAN.md step 53) — the Blade tool only adds the cursor/snap math above.
        if (ClipEdits.Split(_project!.Timeline, track, clip, new Timecode(atTicks), Linked) is not { } split)
            return;
        Execute(split.Command);
        Select(split.Right);
    }

    private Track? TrackOf(Clip clip)
    {
        foreach (Track t in _project!.Timeline.Tracks)
            if (t.Clips.Contains(clip))
                return t;
        return null;
    }

    /// <summary>An effectively-infinite source length for media with unbounded headroom (a still, PLAN.md step 42)
    /// — the same sentinel <see cref="UpdateSlide"/> uses for a missing neighbour, so trim/slip/ripple/slide let a
    /// still extend freely like a generator.</summary>
    private const long UnboundedMediaTicks = long.MaxValue / 4;

    private long MediaDurationTicks(Clip clip)
    {
        MediaRef? media = _project?.MediaPool.Get(clip.MediaRefId);
        // A still has one frame but unbounded on-timeline headroom — never cap its trim at the probed (default-drop)
        // duration (PLAN.md step 42).
        if (media is { HasUnboundedDuration: true })
            return UnboundedMediaTicks;
        // When the source duration is unknown (offline media), fall back to the current out-point so slip is
        // a no-op rather than running off an unknown end.
        return media is { Info.Duration.Ticks: > 0 } ? media.Info.Duration.Ticks : clip.SourceOut.Ticks;
    }

    private long SnapMove(long newStart, long dur)
    {
        if (!Snapping)
            return newStart;
        long snapStart = TimelineMath.Snap(newStart, _snapPoints, SnapTolerancePx, _pxPerSecond);
        if (snapStart != newStart)
            return snapStart;
        long end = newStart + dur;
        long snapEnd = TimelineMath.Snap(end, _snapPoints, SnapTolerancePx, _pxPerSecond);
        return snapEnd != end ? TimelineMath.ClampNonNegative(snapEnd - dur) : newStart;
    }

    private IReadOnlyList<long> BuildSnapPoints(Clip dragged)
    {
        var points = new List<long> { 0, _playhead.Ticks };
        foreach (Track track in _project!.Timeline.Tracks)
            foreach (Clip c in track.Clips)
            {
                if (ReferenceEquals(c, dragged))
                    continue;
                points.Add(c.TimelineStart.Ticks);
                points.Add(c.TimelineEnd.Ticks);
            }
        return points;
    }

    // ── Drag-and-drop (media bin → lane, effect → clip) ─────────────────────────────────────────────

    /// <summary>Raised when a clip is placed by a media-bin drop, so the shell can refresh the timeline header.</summary>
    public event Action? ClipPlaced;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool media = e.DataTransfer.Contains(DragFormats.MediaRefId);
        bool effect = e.DataTransfer.Contains(DragFormats.EffectId);
        bool transition = e.DataTransfer.Contains(DragFormats.TransitionId);
        if (!media && !effect && !transition)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropPreview();
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        // Show the indicator at the snapped drop start (media), the nearest cut (transition), or just the cursor.
        Point pos = e.GetPosition(this);
        if (transition && TransitionDropPreviewX(pos) is { } cutX)
            _dropPreviewX = cutX;
        else
            _dropPreviewX = pos.X < _headerWidth ? null : pos.X;
        InvalidateVisual();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearDropPreview();
        if (_project is null || _history is null)
            return;

        Point p = e.GetPosition(this);
        if (p.X < _headerWidth)
            return;

        if (e.DataTransfer.Contains(DragFormats.MediaRefId))
            DropMedia(e.DataTransfer.TryGetValue(DragFormats.MediaRefId), p);
        else if (e.DataTransfer.Contains(DragFormats.EffectId))
            DropEffect(e.DataTransfer.TryGetValue(DragFormats.EffectId), p);
        else if (e.DataTransfer.Contains(DragFormats.TransitionId))
            DropTransition(e.DataTransfer.TryGetValue(DragFormats.TransitionId), p);
    }

    /// <summary>The X of the cut nearest the cursor on the hovered track, or null when none is near (so the drop
    /// preview snaps to a real cut while a transition is dragged, PLAN.md step 25).</summary>
    private double? TransitionDropPreviewX(Point p)
    {
        if (_project is null || p.X < _headerWidth)
            return null;
        if (TrackAndKindAtY(p.Y).track is not { } track)
            return null;
        long tTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        return TryFindCutNear(track, tTicks, out Timecode cut)
            ? TimelineMath.XAtTicks(cut.Ticks, _pxPerSecond, _scrollX, _headerWidth)
            : null;
    }

    /// <summary>Applies the dropped transition at the cut nearest the cursor on the hovered track (PLAN.md step 25).</summary>
    private void DropTransition(string? transitionId, Point p)
    {
        if (string.IsNullOrEmpty(transitionId) || _project is null)
            return;
        if (TrackAndKindAtY(p.Y).track is not { } track)
            return;
        long tTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        if (TryFindCutNear(track, tTicks, out Timecode cut))
            ApplyTransitionAt(track, cut, transitionId);
        else
            Status?.Invoke("Drop a transition on a cut between two clips.");
    }

    private void ClearDropPreview()
    {
        if (_dropPreviewX is null)
            return;
        _dropPreviewX = null;
        InvalidateVisual();
    }

    /// <summary>Places a clip for the dropped source on the lane under the cursor (with a linked companion on
    /// the first track of the other kind when the source has both A/V), via <see cref="ClipPlacement"/>.</summary>
    private void DropMedia(string? idText, Point p)
    {
        if (!Guid.TryParse(idText, out Guid guid))
            return;
        MediaRef? media = _project!.MediaPool.Get(new MediaRefId(guid));
        if (media is null)
            return;

        (Track? dropped, bool isVideoLane) = TrackAndKindAtY(p.Y);
        VideoTrack? videoTarget = dropped as VideoTrack ?? _project.Timeline.VideoTracks.FirstOrDefault();
        AudioTrack? audioTarget = dropped as AudioTrack ?? _project.Timeline.AudioTracks.FirstOrDefault();
        bool primaryIsVideo = dropped is VideoTrack || (dropped is null && media.Info.HasVideo);

        long dropTicks = TimelineMath.TicksAtX(p.X, _pxPerSecond, _scrollX, _headerWidth);
        long durationTicks = media.Info.Duration.Ticks;
        long start = ClipPlacement.SnapStart(
            dropTicks, durationTicks, DropSnapPoints(), Snapping, SnapTolerancePx, _pxPerSecond);

        ClipPlacement.PlacementResult? result = ClipPlacement.BuildPlaceCommand(
            media, videoTarget, audioTarget, start, Linked, primaryIsVideo);
        if (result is null)
            return;

        Execute(result.Value.Command);
        Select(result.Value.PrimaryClip);
        ClipPlaced?.Invoke();
    }

    /// <summary>Appends the dropped effect to the clip under the cursor (PLAN.md step 16b).</summary>
    private void DropEffect(string? effectId, Point p)
    {
        if (string.IsNullOrEmpty(effectId))
            return;
        if (!TryHitClip(p, out Clip? clip, out _) || clip is null)
            return;

        EffectInstance instance = EffectCatalog.Find(effectId)?.CreateInstance() ?? new EffectInstance(effectId);
        Execute(new AddEffectCommand(clip, instance));
        Select(clip);
    }

    private (Track? track, bool isVideo) TrackAndKindAtY(double y)
    {
        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(y);
        return i >= 0 && i < lanes.Count ? lanes[i] : (null, false);
    }

    // Snap candidates for a drop: every clip edge plus the playhead and the origin (no clip is being dragged).
    private IReadOnlyList<long> DropSnapPoints()
    {
        var points = new List<long> { 0, _playhead.Ticks };
        foreach (Track track in _project!.Timeline.Tracks)
            foreach (Clip c in track.Clips)
            {
                points.Add(c.TimelineStart.Ticks);
                points.Add(c.TimelineEnd.Ticks);
            }
        return points;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private void Execute(IEditCommand command) => _history!.Execute(command); // re-render happens via Changed

    /// <summary>Replaces the selection with <paramref name="clip"/> (null clears it) — the plain-click rule.
    /// Multi-membership gestures (Ctrl/Shift-click, marquee, Select All) call the <see cref="ClipSelection"/>
    /// mutators directly and funnel through <see cref="OnSelectionMutated"/> like this does.</summary>
    private void Select(Clip? clip) => OnSelectionMutated(_selection.Replace(clip));

    /// <summary>
    /// The single sink every selection mutation flows through (PLAN.md step 54): syncs the cached primary
    /// (<see cref="_selected"/>), raises <see cref="SelectedClipChanged"/> when the primary changed, clears a
    /// transition selection when clips are selected (the two are mutually exclusive), and repaints.
    /// </summary>
    private void OnSelectionMutated(bool changed)
    {
        if (_selection.Primary is not null && _selectedTransition is not null)
        {
            _selectedTransition = null;
            _selectedTransitionTrack = null;
            changed = true;
        }
        if (!ReferenceEquals(_selection.Primary, _selected))
        {
            _selected = _selection.Primary;
            SelectedClipChanged?.Invoke(_selected);
            changed = true;
        }
        if (changed)
            InvalidateVisual();
    }

    /// <summary>Selects a transition (clearing any clip selection), or clears the transition selection when null.</summary>
    private void SelectTransition(Transition? transition, Track? track)
    {
        if (transition is not null)
            OnSelectionMutated(_selection.Clear());
        _selectedTransition = transition;
        _selectedTransitionTrack = transition is null ? null : track;
        InvalidateVisual();
    }

    /// <summary>Hit-tests the transition overlays on the lane under <paramref name="p"/> (last drawn wins).</summary>
    private bool TryHitTransition(Point p, out Transition? transition, out Track? track)
    {
        transition = null;
        track = null;
        if (p.X < _headerWidth || p.Y < RulerHeight)
            return false;

        List<(Track track, bool isVideo)> lanes = Lanes();
        int i = LaneAtY(p.Y);
        if (i < 0 || i >= lanes.Count)
            return false;

        Track t = lanes[i].track;
        double top = LaneTop(i) + 3, h = TrackHeight - 6;
        if (p.Y < top || p.Y > top + h)
            return false;

        foreach (Transition tr in t.Transitions)
        {
            double x0 = TimelineMath.XAtTicks(tr.Start.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            double x1 = TimelineMath.XAtTicks(tr.End.Ticks, _pxPerSecond, _scrollX, _headerWidth);
            if (p.X >= x0 && p.X <= Math.Max(x0 + 6, x1))
            {
                transition = tr;
                track = t;
            }
        }
        return transition is not null;
    }

    private void SeekToX(double x)
    {
        long ticks = TimelineMath.ClampNonNegative(TimelineMath.TicksAtX(x, _pxPerSecond, _scrollX, _headerWidth));
        Timecode t = new(ticks);
        if (_engine is not null)
            _engine.SeekTo(t); // engine echoes PositionChanged → playhead + redraw
        else
        {
            _playhead = t;
            InvalidateVisual();
        }
    }

    private void SetZoom(double pxPerSecond, double anchorX)
    {
        double clamped = Math.Clamp(pxPerSecond, MinPxPerSecond, MaxPxPerSecond);
        if (Math.Abs(clamped - _pxPerSecond) < 1e-6)
            return;
        // Keep the tick under anchorX fixed across the zoom.
        long anchorTicks = TimelineMath.TicksAtX(anchorX, _pxPerSecond, _scrollX, _headerWidth);
        _pxPerSecond = clamped;
        _scrollX = Math.Max(0, _headerWidth - anchorX + TimelineMath.WidthOfTicks(anchorTicks, _pxPerSecond));
        ClampScroll();
        InvalidateVisual();
    }

    private void ClampScroll()
    {
        if (_project is null)
            return;
        double content = TimelineMath.WidthOfTicks(_project.Timeline.Duration.Ticks, _pxPerSecond) + 200;
        double view = Math.Max(0, Bounds.Width - _headerWidth);
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, content - view));
    }

    private string ClipName(Clip clip)
    {
        switch (clip.Kind)
        {
            case ClipKind.Adjustment:
                return "Adjustment Layer";
            case ClipKind.Generator when clip.Generator is not null:
                // Prefer a title's text, else the generator's display name.
                string text = clip.Generator.GetString(GeneratorParamNames.Text);
                return string.IsNullOrEmpty(text) ? GeneratorCatalog.DisplayName(clip.Generator.GeneratorTypeId) : text;
            case ClipKind.Sequence:
                // A nested-sequence clip is labelled with the child sequence's name; a dangling reference
                // (the child was deleted) falls back to a neutral label (it renders as nothing, §15).
                return (clip.SourceSequenceId is { } sid ? _project?.GetSequence(sid)?.Name : null) ?? "Nested sequence";
            case ClipKind.Multicam:
                // A multicam clip is labelled with its source name + the active angle's name (the live angle).
                MulticamSource? mc = clip.SourceMulticamId is { } mid ? _project?.GetMulticam(mid) : null;
                string angle = mc?.AngleAt(clip.ActiveAngle)?.Name ?? $"Angle {clip.ActiveAngle + 1}";
                return mc is null ? "Multicam" : $"{mc.Name} · {angle}";
        }
        MediaRef? media = _project?.MediaPool.Get(clip.MediaRefId);
        return media is null ? "clip" : System.IO.Path.GetFileName(media.AbsolutePath);
    }

    private static string TrackName(Track track, bool isVideo) =>
        string.IsNullOrEmpty(track.Name) ? (isVideo ? "V" : "A") : track.Name;

    private static string TimeLabel(long ticks)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, (double)ticks / Timecode.TicksPerSecond));
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
    }

    private static FormattedText Label(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
