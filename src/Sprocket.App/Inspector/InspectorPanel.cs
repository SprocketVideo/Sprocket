using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Sprocket.App.Mixer;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using ShapesPath = Avalonia.Controls.Shapes.Path; // aliased so it doesn't clash with System.IO.Path

namespace Sprocket.App.Inspector;

/// <summary>
/// The type-driven Inspector (PLAN.md step 16, UI.md §3.5): collapsible sections for the selected clip — a
/// read-only Clip section plus one section per effect in the stack, each built automatically from the
/// effect's <see cref="EffectParameterDescriptor"/>s (slider + numeric box + a keyframe toggle). Editing runs
/// through the step-10 command stack (a slider drag coalesces to one undo entry); the keyframe affordance
/// converts a parameter to/from animated and scrubs keyframes in at the playhead. Built entirely in code like
/// <see cref="MediaBrowser.MediaBrowserPanel"/> / <see cref="Timeline.TimelineControl"/>.
/// </summary>
public sealed class InspectorPanel : UserControl
{
    // Core tokens come from the shared Palette (Palette.cs) so this code control can't drift from the shell.
    // FaintText used to be a darker #6A7180 here, which failed WCAG AA at the 11–12px label sizes it draws
    // at; Palette.FaintText is the AA-safe value.
    private static readonly IBrush PanelBg = Palette.PanelBgBrush;
    private static readonly IBrush RaisedBg = Palette.RaisedBgBrush;
    private static readonly IBrush Edge = Palette.EdgeBrush;
    private static readonly IBrush InputEdge = Palette.InputEdgeBrush;

    // The effect headers' enable LED (component-local per the Palette remark): unlit grey, and the
    // lit state's soft halo — Palette.Good at ~45% alpha so the glow reads as light bleed, not a ring.
    private static readonly IBrush LedOffBrush = new SolidColorBrush(Color.Parse("#3A4048"));
    private static readonly Color LedGlow = Color.FromArgb(0x73, Palette.Good.R, Palette.Good.G, Palette.Good.B);
    private static readonly IBrush TextBrush = Palette.TextBrush;
    private static readonly IBrush MutedText = Palette.MutedTextBrush;
    private static readonly IBrush FaintText = Palette.FaintTextBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;

    private Project? _project;
    private EditHistory? _history;
    private Func<Timecode> _playhead = () => Timecode.Zero;
    private Func<Sprocket.Audio.AudioMixer?>? _liveMixer;

    private Clip? _clip;
    // A mixer insert chain being edited instead of a clip (PLAN.md step 31: track / bus / master scope).
    // Mutually exclusive with _clip: selecting a clip clears it, and vice versa.
    private AudioChainTarget? _chain;
    private readonly StackPanel _body;
    private readonly List<Action> _valueRefreshers = new();

    // Effect-section collapse state, keyed by instance identity (reference equality) so it survives the
    // Rebuild() that follows every add/remove/undo/redo — otherwise every effect section would reset to
    // expanded whenever any effect in the stack was added or removed.
    private readonly Dictionary<EffectInstance, bool> _effectExpanded = new(ReferenceEqualityComparer.Instance);

    private bool _suppress;          // guards programmatic slider/text updates from re-triggering edits
    private bool _editing;           // true during a drag/commit so history.Changed refreshes values, not rebuild
    private IDisposable? _dragScope; // open coalescing scope for the active slider drag

    // Pending effect-section reorder drag (PLAN.md step 51): a press on a section header arms it; the drag only
    // begins once the pointer moves past a small threshold so a plain header click still toggles the section.
    // The move handler lives on the panel (handledEventsToo) because the Expander's header ToggleButton captures
    // the pointer on press, which routes subsequent moves around the header's own children.
    private PointerPressedEventArgs? _reorderPressed;
    private Avalonia.Point _reorderStart;
    private int _reorderSourceIndex = -1;

    public InspectorPanel()
    {
        _body = new StackPanel { Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        Content = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        AddHandler(PointerMovedEvent, OnReorderPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _reorderPressed = null, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Rebuild();
    }

    /// <summary>Expands or collapses every section at once (the pane-header Expand All / Collapse All
    /// buttons). Effect sections' tracked collapse state follows via their Expanded/Collapsed handlers.</summary>
    public void SetAllSectionsExpanded(bool expanded)
    {
        foreach (Control child in _body.Children)
            if (SectionExpander(child) is { } section)
                section.IsExpanded = expanded;
    }

    private void OnReorderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_reorderPressed is not { } pressed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Avalonia.Point p = e.GetPosition(this);
        if (Math.Abs(p.X - _reorderStart.X) < 4 && Math.Abs(p.Y - _reorderStart.Y) < 4)
            return;

        _reorderPressed = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DragFormats.EffectReorderIndex,
            _reorderSourceIndex.ToString(CultureInfo.InvariantCulture)));
        _ = DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move); // fire-and-forget; the drop applies the edit
    }

    /// <summary>Binds the inspector to the project, shared edit history, and a playhead accessor. Call once.</summary>
    public void Attach(Project project, EditHistory history, Func<Timecode> playhead)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(playhead);
        _project = project;
        _history = history;
        _playhead = playhead;
        _history.Changed += OnHistoryChanged;
        Rebuild();
    }

    /// <summary>
    /// Optional accessor for the live playback mixer, so effect sections that publish a real-time readout
    /// (currently the Compressor's gain-reduction/input/output meter) can show it. Absent (or returning null)
    /// just omits the meter — audio hasn't initialized yet, or there's no project audio at all.
    /// </summary>
    public void SetLiveAudioMixer(Func<Sprocket.Audio.AudioMixer?> getMixer) => _liveMixer = getMixer;

    /// <summary>Shows the given clip's properties (or the empty state when <see langword="null"/>). A mere
    /// timeline deselect (<see langword="null"/>) keeps an active insert-chain view (PLAN.md step 31) in
    /// place — only an actual clip selection replaces it.</summary>
    public void SetSelectedClip(Clip? clip)
    {
        if (clip is not null)
            _chain = null;
        else if (_chain is not null)
        {
            _clip = null;
            Rebuild();
            return;
        }
        if (!ReferenceEquals(_clip, clip))
            _effectExpanded.Clear();
        _clip = clip;
        Rebuild();
    }

    /// <summary>
    /// Shows a mixer insert chain — a track's pre-fader inserts, the sequence bus, or the master chain
    /// (PLAN.md step 31) — with the same per-effect sections, parameter rows, and keyframe editing clip
    /// effects get. Entered from the mixer's insert rows; a later clip selection switches back.
    /// </summary>
    public void SetSelectedChain(AudioChainTarget? target)
    {
        if (!ReferenceEquals(_chain?.Chain, target?.Chain))
            _effectExpanded.Clear();
        _chain = target;
        if (target is not null)
            _clip = null;
        Rebuild();
    }

    /// <summary>Refreshes the displayed parameter values for the current playhead (animated values move with it).</summary>
    public void OnPlayheadMoved() => RefreshValues();

    private void OnHistoryChanged()
    {
        // During a live edit (slider drag / numeric commit) just refresh values so the control isn't torn down
        // mid-gesture; a structural change (add/remove effect, undo/redo) rebuilds the sections.
        if (_editing)
            RefreshValues();
        else
            Rebuild();
    }

    // ── Build ───────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        // Settle effect reference tags before drawing headers (EffectTags): the sweep runs at read points
        // rather than inside every effect-creating command, and the rebuild after each history change is
        // the UI's read point — so adds, blade-split clones, and pasted stacks all get tagged here.
        if (_project is not null)
            EffectTags.EnsureAssigned(_project);

        _valueRefreshers.Clear();
        _body.Children.Clear();

        // A stale chain target (its track removed by undo / Remove Track, or the open sequence switched)
        // is dropped rather than edited — its chain is no longer what the mixer plays.
        if (_chain is not null && _project is not null && !_chain.IsAlive(_project))
            _chain = null;

        if (_chain is not null && _project is not null && _history is not null)
        {
            BuildChainView(_chain);
            RefreshValues();
            return;
        }

        if (_clip is null || _project is null || _history is null)
        {
            _body.Children.Add(new TextBlock
            {
                Text = "No clip selected.\nSelect a clip in the timeline to edit its properties.",
                FontSize = 12,
                Foreground = FaintText,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Avalonia.Thickness(16, 24),
            });
            return;
        }

        _body.Children.Add(BuildClipSection(_clip));

        if (_clip.Kind == ClipKind.Multicam)
            _body.Children.Add(BuildMulticamSection(_clip));

        // Title-family generator clips get the TEXT sections (PLAN.md step 40): content + typography,
        // styling (stroke/shadow/box), and scroll/animate — every attribute editable post-hoc.
        if (_clip.Kind == ClipKind.Generator && _clip.Generator is { } gen && GeneratorTypeIds.IsTitle(gen.GeneratorTypeId))
        {
            _body.Children.Add(BuildTextSection(_clip, gen));
            _body.Children.Add(BuildTextStyleSection(gen));
            _body.Children.Add(BuildScrollSection(gen));
        }

        Clip clip = _clip;
        var clipContext = new ChainContext(clip.Effects, clip, e => new RemoveEffectCommand(clip, e), ClipScope: true);
        foreach (EffectInstance effect in _clip.Effects)
            _body.Children.Add(BuildEffectSection(clipContext, effect));

        _body.Children.Add(BuildAddEffectBar(_clip));
        RefreshValues();
    }

    /// <summary>
    /// How an effect section addresses the chain that carries it, shared by the clip stack and the mixer
    /// insert scopes (PLAN.md step 31): the list itself (indexing / reorder / move commands), the identity
    /// the live mixer keys the chain's DSP state by (<see cref="Sprocket.Audio.AudioMixer.TryPeekEffect"/>
    /// metering), the scope's remove command, and whether this is the clip stack (freeze hints and the fade
    /// envelope are clip-scope concepts).
    /// </summary>
    private sealed record ChainContext(
        List<EffectInstance> Effects,
        object MeterKey,
        Func<EffectInstance, IEditCommand> Remove,
        bool ClipScope);

    /// <summary>
    /// The insert-chain view (PLAN.md step 31): a heading section describing the scope's place in the signal
    /// flow, one section per effect — the same parameter/keyframe/preset rows clip effects get, with
    /// add/remove/reorder running the chain commands — and the audio "+ Effect" bar.
    /// </summary>
    private void BuildChainView(AudioChainTarget target)
    {
        var info = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };
        info.Children.Add(new TextBlock
        {
            Text = target.Description,
            FontSize = 11,
            Foreground = FaintText,
            TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(Section(target.Title, info, expanded: true));

        var context = new ChainContext(
            target.Chain, target.StateKey, e => new RemoveChainEffectCommand(target.Chain, e), ClipScope: false);
        foreach (EffectInstance effect in target.Chain)
            _body.Children.Add(BuildEffectSection(context, effect));

        if (target.Chain.Count == 0)
            _body.Children.Add(new TextBlock
            {
                Text = "No inserts yet.",
                FontSize = 11,
                Foreground = FaintText,
                Margin = new Avalonia.Thickness(16, 10, 16, 0),
            });

        _body.Children.Add(BuildAddChainEffectBar(target));
    }

    /// <summary>
    /// The horizontal span animated chain/effect parameters display (and keyframe) over: the clip's timeline
    /// span for clip effects, the whole sequence for a mixer insert chain (its keyframes are in sequence
    /// time and it has no clip bounds). Read live at refresh time so a moved/trimmed clip's lane follows.
    /// </summary>
    private (long Start, long End)? LaneRange()
    {
        if (_chain is not null && _project is not null)
            return (0, Math.Max(_project.Timeline.Duration.Ticks, Timecode.TicksPerSecond));
        if (_clip is { } clip)
            return (clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks);
        return null;
    }

    private Control BuildClipSection(Clip clip)
    {
        string name = Path.GetFileName(_project!.MediaPool.Get(clip.MediaRefId)?.AbsolutePath ?? "clip");
        var info = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };
        info.Children.Add(InfoRow("Source", name));
        info.Children.Add(InfoRow("Start", FormatSeconds(clip.TimelineStart)));
        info.Children.Add(InfoRow("Duration", FormatSeconds(clip.Duration)));
        info.Children.Add(InfoRow("Trim", $"{FormatSeconds(clip.SourceIn)} – {FormatSeconds(clip.SourceOut)}"));
        info.Children.Add(BuildSpeedRow(clip));
        // A held clip shows its frozen frame + hold span (PLAN.md step 43); the speed above is retained but
        // ignored while held. Edited via Clip ▸ Frame Hold Options / the trim handles.
        if (clip.IsHeld)
            info.Children.Add(InfoRow("Hold", $"{FormatSeconds(clip.HoldFrameAt!.Value)} for {FormatSeconds(clip.HoldDuration)}"));
        return Section("Clip", info, expanded: true);
    }

    /// <summary>An editable Speed row (retime, PLAN.md step 21): a percentage box committing a
    /// <see cref="SetClipSpeedCommand"/> on Enter/blur. Linked companions are retimed together so A/V stays in
    /// sync. The Duration row above updates on the resulting rebuild.</summary>
    private Control BuildSpeedRow(Clip clip)
    {
        var box = new TextBox
        {
            Width = 72,
            FontSize = 11,
            // Defeat the Fluent theme's 32px MinHeight so the box hugs the 11px text (otherwise the
            // top-aligned text leaves a large gap that reads as excess bottom padding).
            MinHeight = 22,
            Height = 22,
            Padding = new Avalonia.Thickness(6, 2),
            Background = PanelBg,
            BorderBrush = InputEdge,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = SpeedFormat.ToPercentString(clip.SpeedRatio),
        };
        void Commit()
        {
            if (_history is null || _project is null)
                return;
            if (!SpeedFormat.TryParsePercent(box.Text, out Rational speed))
            {
                box.Text = SpeedFormat.ToPercentString(clip.SpeedRatio);
                return;
            }
            if (speed == clip.SpeedRatio)
                return;
            var members = new List<Clip> { clip };
            members.AddRange(_project.Timeline.ClipsLinkedTo(clip).Select(l => l.Clip));
            var commands = members.Select(c => (IEditCommand)new SetClipSpeedCommand(c, speed)).ToList();
            _history.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Change speed", commands));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();

        var row = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(box);
        right.Children.Add(new TextBlock { Text = "%", FontSize = 11, Foreground = FaintText, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = "Speed", FontSize = 11, Foreground = FaintText, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>The Multicam section (PLAN.md step 24): the synced source plus one button per camera angle (the
    /// active one highlighted). Clicking sets the segment's angle via <see cref="SetClipAngleCommand"/> (the
    /// number keys do the same with a playhead cut). Shows each angle's sync offset.</summary>
    private Control BuildMulticamSection(Clip clip)
    {
        var content = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(4, 4, 4, 4) };
        MulticamSource? source = clip.SourceMulticamId is { } id ? _project!.GetMulticam(id) : null;
        if (source is null)
        {
            content.Children.Add(new TextBlock { Text = "Multicam source missing.", FontSize = 11, Foreground = FaintText });
            return Section("Multicam", content, expanded: true);
        }

        content.Children.Add(InfoRow("Source", source.Name));
        content.Children.Add(new TextBlock { Text = "Active angle", FontSize = 11, Foreground = FaintText });

        var angles = new StackPanel { Spacing = 3 };
        for (int i = 0; i < source.Angles.Count; i++)
        {
            int index = i; // capture per iteration
            MulticamAngle a = source.Angles[i];
            bool active = clip.ActiveAngle == i;
            string offset = a.SyncOffset.Ticks == 0 ? "" : $"   ({FormatSeconds(a.SyncOffset)})";
            var button = new Button
            {
                Content = $"{i + 1}.  {a.Name}{offset}",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(8, 3),
                Background = active ? Accent : RaisedBg,
                Foreground = TextBrush,
                BorderBrush = Edge,
            };
            button.Click += (_, _) =>
            {
                if (_history is null || clip.ActiveAngle == index)
                    return;
                _history.Execute(new SetClipAngleCommand(clip, index));
            };
            angles.Children.Add(button);
        }
        content.Children.Add(angles);
        return Section("Multicam", content, expanded: true);
    }

    // ── Title sections (PLAN.md step 40) ──────────────────────────────────────────────────────────────
    // Every attribute of a title-family generator is editable post-hoc: content + typography in TEXT,
    // stroke/shadow/box in TEXT STYLE, and the roll/crawl + entrance/exit animation in SCROLL & ANIMATE.
    // String attributes commit SetGeneratorStringCommand (typing coalesces to one undo entry per focus);
    // numeric ones ride the same animatable rows as effect parameters, so they keyframe (step 16d).

    private Control BuildTextSection(Clip clip, GeneratorSpec gen)
    {
        var rows = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };

        rows.Children.Add(BuildTextEntryRow(gen, "Text", GeneratorParamNames.Text, multiline: true));
        rows.Children.Add(BuildTextEntryRow(gen, "Secondary", GeneratorParamNames.Text2, multiline: false));
        rows.Children.Add(BuildFontRow(gen));
        rows.Children.Add(BuildStyleTogglesRow(gen));
        rows.Children.Add(BuildColorRow(gen, "Fill", GeneratorParamNames.Color, "#FFFFFFFF"));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.FontSize, "Size", 0.12, 0.02, 0.5, 0.005));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.FontSize2, "Size 2", 0.07, 0.02, 0.5, 0.005));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.Tracking, "Tracking", 0.0, -0.1, 0.5, 0.005));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.Leading, "Leading", 1.2, 0.8, 2.5, 0.05));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.PositionX, "Position X", 0.5, 0.0, 1.0, 0.005));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.PositionY, "Position Y", 0.5, 0.0, 1.0, 0.005));
        rows.Children.Add(BuildPresetsRow(clip, gen));

        return Section("Text", rows, expanded: true);
    }

    private Control BuildTextStyleSection(GeneratorSpec gen)
    {
        var rows = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };
        rows.Children.Add(BuildColorRow(gen, "Stroke", GeneratorParamNames.StrokeColor, "#FF000000"));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.StrokeWidth, "Stroke width", 0.0, 0.0, 0.02, 0.0005));
        rows.Children.Add(BuildColorRow(gen, "Shadow", GeneratorParamNames.ShadowColor, "#00000000"));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.ShadowOffsetX, "Shadow X", 0.004, -0.05, 0.05, 0.001));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.ShadowOffsetY, "Shadow Y", 0.004, -0.05, 0.05, 0.001));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.ShadowBlur, "Shadow blur", 0.004, 0.0, 0.05, 0.001));
        rows.Children.Add(BuildColorRow(gen, "Box", GeneratorParamNames.BoxColor, "#00000000"));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.BoxPadding, "Box padding", 0.02, 0.0, 0.1, 0.002));
        return Section("Text Style", rows, expanded: false);
    }

    private Control BuildScrollSection(GeneratorSpec gen)
    {
        var rows = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };

        // Scroll mode (Roll / Crawl — a property of the title, PLAN.md step 40): the clip's duration sets
        // the speed, so there is no speed slider — trim the clip to retime the roll.
        var mode = new ComboBox
        {
            ItemsSource = new[] { "None", "Roll", "Crawl" },
            FontSize = 11,
            MinHeight = 24,
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        mode.SelectionChanged += (_, _) =>
        {
            if (_suppress)
                return;
            string value = mode.SelectedIndex switch { 1 => TitleScrollModes.Roll, 2 => TitleScrollModes.Crawl, _ => "" };
            ExecuteGeneratorString(gen, GeneratorParamNames.ScrollMode, value, coalescing: false);
        };
        rows.Children.Add(LabeledRow("Mode", mode));

        rows.Children.Add(BuildFlagRow(gen, "Ease in", GeneratorParamNames.ScrollEaseIn, defaultOn: false));
        rows.Children.Add(BuildFlagRow(gen, "Ease out", GeneratorParamNames.ScrollEaseOut, defaultOn: false));
        rows.Children.Add(BuildFlagRow(gen, "Start / end off-screen", GeneratorParamNames.ScrollOffscreen, defaultOn: true));
        rows.Children.Add(GenRow(gen, GeneratorParamNames.RevealFraction, "Reveal", 1.0, 0.0, 1.0, 0.01));

        _valueRefreshers.Add(() =>
        {
            _suppress = true;
            mode.SelectedIndex = gen.GetString(GeneratorParamNames.ScrollMode, TitleScrollModes.None) switch
            {
                TitleScrollModes.Roll => 1,
                TitleScrollModes.Crawl => 2,
                _ => 0,
            };
            _suppress = false;
        });

        return Section("Scroll & Animate", rows, expanded: false);
    }

    /// <summary>A string attribute editor: live preview while typing, one undo entry per focus (the focus
    /// opens a coalescing scope like a slider drag; consecutive <see cref="SetGeneratorStringCommand"/>s of
    /// the same parameter merge inside it).</summary>
    private Control BuildTextEntryRow(GeneratorSpec gen, string label, string param, bool multiline)
    {
        var box = new TextBox
        {
            FontSize = 11,
            MinHeight = multiline ? 48 : 22,
            Padding = new Avalonia.Thickness(6, 4),
            Background = PanelBg,
            BorderBrush = InputEdge,
            Foreground = TextBrush,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        box.GotFocus += (_, _) => BeginDrag();
        box.LostFocus += (_, _) => EndDrag();
        box.TextChanged += (_, _) =>
        {
            if (_suppress)
                return;
            ExecuteGeneratorString(gen, param, box.Text ?? "", coalescing: _dragScope is not null);
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = FaintText });
        stack.Children.Add(box);

        _valueRefreshers.Add(() =>
        {
            string current = gen.GetString(param);
            if (box.Text == current)
                return; // don't reset the caret while the user is typing
            _suppress = true;
            box.Text = current;
            _suppress = false;
        });
        return stack;
    }

    private Control BuildFontRow(GeneratorSpec gen)
    {
        var combo = new ComboBox
        {
            ItemsSource = Sprocket.Render.TitleFonts.Families,
            FontSize = 11,
            MinHeight = 24,
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppress || combo.SelectedItem is not string family)
                return;
            ExecuteGeneratorString(gen, GeneratorParamNames.FontFamily, family, coalescing: false);
        };
        _valueRefreshers.Add(() =>
        {
            _suppress = true;
            string current = gen.GetString(GeneratorParamNames.FontFamily, Sprocket.Render.TitleFonts.DefaultFamily);
            combo.SelectedIndex = Math.Max(0, Sprocket.Render.TitleFonts.Families.ToList().IndexOf(current));
            _suppress = false;
        });
        return LabeledRow("Font", combo);
    }

    /// <summary>Bold / Italic toggles and the left/centre/right alignment radio group on one row.</summary>
    private Control BuildStyleTogglesRow(GeneratorSpec gen)
    {
        ToggleButton Make(string text, string tip)
        {
            var b = new ToggleButton
            {
                Content = text,
                FontSize = 11,
                Width = 26,
                Height = 22,
                Padding = new Avalonia.Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = RaisedBg,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }

        ToggleButton bold = Make("B", "Bold");
        bold.FontWeight = FontWeight.Bold;
        ToggleButton italic = Make("I", "Italic");
        italic.FontStyle = FontStyle.Italic;
        ToggleButton left = Make("⟸", "Align left");
        ToggleButton centre = Make("≡", "Align centre");
        ToggleButton right = Make("⟹", "Align right");

        bold.Click += (_, _) => ExecuteGeneratorString(
            gen, GeneratorParamNames.Bold, bold.IsChecked == true ? "true" : "", coalescing: false);
        italic.Click += (_, _) => ExecuteGeneratorString(
            gen, GeneratorParamNames.Italic, italic.IsChecked == true ? "true" : "", coalescing: false);
        left.Click += (_, _) => ExecuteGeneratorString(gen, GeneratorParamNames.Alignment, "left", coalescing: false);
        centre.Click += (_, _) => ExecuteGeneratorString(gen, GeneratorParamNames.Alignment, "", coalescing: false);
        right.Click += (_, _) => ExecuteGeneratorString(gen, GeneratorParamNames.Alignment, "right", coalescing: false);

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        group.Children.Add(bold);
        group.Children.Add(italic);
        group.Children.Add(new Border { Width = 6 });
        group.Children.Add(left);
        group.Children.Add(centre);
        group.Children.Add(right);

        _valueRefreshers.Add(() =>
        {
            _suppress = true;
            bold.IsChecked = gen.GetString(GeneratorParamNames.Bold) == "true";
            italic.IsChecked = gen.GetString(GeneratorParamNames.Italic) == "true";
            string align = gen.GetString(GeneratorParamNames.Alignment, "center");
            left.IsChecked = align == "left";
            right.IsChecked = align == "right";
            centre.IsChecked = align is not ("left" or "right");
            _suppress = false;
        });

        return LabeledRow("Style", group);
    }

    /// <summary>A colour attribute as a swatch + <c>#AARRGGBB</c> hex box (Enter/blur commits; invalid input reverts).</summary>
    private Control BuildColorRow(GeneratorSpec gen, string label, string param, string fallback)
    {
        var swatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new Avalonia.CornerRadius(3),
            BorderBrush = Edge,
            BorderThickness = new Avalonia.Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new TextBox
        {
            Width = 92,
            FontSize = 11,
            MinHeight = 22,
            Height = 22,
            Padding = new Avalonia.Thickness(6, 2),
            Background = PanelBg,
            BorderBrush = InputEdge,
            Foreground = TextBrush,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        void Commit()
        {
            if (_suppress)
                return;
            string text = (box.Text ?? "").Trim();
            if (text.Length > 0 && !Avalonia.Media.Color.TryParse(text, out _))
            {
                RefreshValues(); // invalid hex → revert to the model value
                return;
            }
            ExecuteGeneratorString(gen, param, text, coalescing: false);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        group.Children.Add(swatch);
        group.Children.Add(box);

        _valueRefreshers.Add(() =>
        {
            string current = gen.GetString(param, fallback);
            _suppress = true;
            box.Text = current;
            swatch.Background = Avalonia.Media.Color.TryParse(current, out Avalonia.Media.Color c)
                ? new SolidColorBrush(c)
                : Brushes.Transparent;
            _suppress = false;
        });

        return LabeledRow(label, group);
    }

    /// <summary>A boolean string flag (<c>"true"</c>/<c>"false"</c>/absent) as a checkbox. When
    /// <paramref name="defaultOn"/> the unchecked state writes an explicit <c>"false"</c> (absent = on)
    /// and the checked state clears the entry.</summary>
    private Control BuildFlagRow(GeneratorSpec gen, string label, string param, bool defaultOn)
    {
        var check = new CheckBox { MinHeight = 22, HorizontalAlignment = HorizontalAlignment.Right };
        check.Click += (_, _) =>
        {
            if (_suppress)
                return;
            bool on = check.IsChecked == true;
            string value = defaultOn ? (on ? "" : "false") : (on ? "true" : "");
            ExecuteGeneratorString(gen, param, value, coalescing: false);
        };
        _valueRefreshers.Add(() =>
        {
            _suppress = true;
            check.IsChecked = defaultOn
                ? gen.GetString(param, "true") != "false"
                : gen.GetString(param) == "true";
            _suppress = false;
        });
        return LabeledRow(label, check);
    }

    /// <summary>An animatable numeric generator parameter row — the same slider/box/keyframe-lane UI effect
    /// parameters use, running <see cref="SetGeneratorParameterCommand"/> (PLAN.md step 40).</summary>
    private Control GenRow(GeneratorSpec gen, string name, string display, double def, double min, double max, double step)
    {
        var p = new EffectParameterDescriptor(name, display, def, min, max, step);
        return BuildAnimatableRow(
            p,
            () => gen.Parameters.TryGetValue(name, out AnimatableValue? v) ? v : AnimatableValue.Constant(def),
            (next, coalescing) => ExecuteGeneratorParam(gen, name, next, coalescing));
    }

    /// <summary>
    /// The entrance/exit animation presets (PLAN.md step 40): each authors standard keyframes through the
    /// command stack — Fade rides the step-39 fade envelope, Pop/Slide author Transform keyframes, and
    /// Typewriter keyframes the title's reveal fraction. One undo entry per preset.
    /// </summary>
    private Control BuildPresetsRow(Clip clip, GeneratorSpec gen)
    {
        var button = new Button
        {
            Content = "Animate ▾",
            FontSize = 11,
            Padding = new Avalonia.Thickness(10, 3),
            Background = RaisedBg,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };
        var items = new List<MenuItem>();
        void Add(string header, Action apply)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => apply();
            items.Add(item);
        }

        long start = clip.TimelineStart.Ticks;
        long end = clip.TimelineEnd.Ticks;
        long fade = Math.Min(Timecode.FromSeconds(1).Ticks, clip.Duration.Ticks / 2);
        long pop = Math.Min(Timecode.FromSeconds(0.5).Ticks, clip.Duration.Ticks);
        long type = Math.Min(Timecode.FromSeconds(2).Ticks, clip.Duration.Ticks);

        Add("Fade In", () =>
        {
            (long _, long fadeOut) = FadeOps.ReadFades(clip);
            ExecuteEdit(new SetClipFadeCommand(
                clip,
                FadeOps.BuildOpacity(FadeOps.FadeOpacity(clip), start, end, fade, fadeOut),
                "Fade in"), coalescing: false);
        });
        Add("Fade Out", () =>
        {
            (long fadeIn, long _) = FadeOps.ReadFades(clip);
            ExecuteEdit(new SetClipFadeCommand(
                clip,
                FadeOps.BuildOpacity(FadeOps.FadeOpacity(clip), start, end, fadeIn, fade),
                "Fade out"), coalescing: false);
        });
        Add("Pop In", () => ApplyTransformPreset(clip, "Pop in", EffectParamNames.Scale, 0.2, 1.0, pop));
        Add("Slide In From Left", () => ApplyTransformPreset(clip, "Slide in", EffectParamNames.PositionX, -1.0, 0.0, pop));
        Add("Slide In From Right", () => ApplyTransformPreset(clip, "Slide in", EffectParamNames.PositionX, 1.0, 0.0, pop));
        Add("Typewriter", () => ExecuteGeneratorParam(
            gen,
            GeneratorParamNames.RevealFraction,
            AnimatableValue.Animated(
            [
                new Keyframe(new Timecode(start), 0.0),
                new Keyframe(new Timecode(start + type), 1.0),
            ]),
            coalescing: false));

        button.Flyout = new MenuFlyout { ItemsSource = items };
        return button;
    }

    /// <summary>Authors an entrance keyframe pair on the clip's Transform effect, adding the effect first when
    /// the clip has none — grouped as one undo entry (<see cref="CompositeCommand"/>).</summary>
    private void ApplyTransformPreset(Clip clip, string label, string param, double from, double to, long lengthTicks)
    {
        long start = clip.TimelineStart.Ticks;
        var value = AnimatableValue.Animated(
        [
            new Keyframe(new Timecode(start), from, Interpolation.EaseInOut),
            new Keyframe(new Timecode(start + lengthTicks), to),
        ]);

        EffectInstance? transform = clip.Effects.FirstOrDefault(e => e.EffectTypeId == EffectTypeIds.Transform);
        if (transform is not null)
        {
            ExecuteEdit(new SetEffectParameterCommand(transform, param, value), coalescing: false);
            return;
        }

        var created = new EffectInstance(EffectTypeIds.Transform);
        ExecuteEdit(new CompositeCommand(label,
        [
            new AddEffectCommand(clip, created),
            new SetEffectParameterCommand(created, param, value),
        ]), coalescing: false);
    }

    /// <summary>A label on the left with an arbitrary editor docked right.</summary>
    private static Control LabeledRow(string label, Control editor, string? tip = null)
    {
        var row = new DockPanel();
        DockPanel.SetDock(editor, Dock.Right);
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = FaintText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip is { Length: > 0 })
            ToolTip.SetTip(text, tip);
        row.Children.Add(editor);
        row.Children.Add(text);
        return row;
    }

    private Control BuildEffectSection(ChainContext context, EffectInstance effect)
    {
        EffectDescriptor? descriptor = EffectCatalog.Find(effect.EffectTypeId);
        string title = descriptor?.DisplayName ?? effect.EffectTypeId;

        var rows = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };

        // Factory presets (PLAN.md step 41): descriptors that carry presets get a picker above the parameter
        // rows; applying one is a single undoable composite parameter edit.
        if (descriptor is { Presets.Count: > 0 })
            rows.Children.Add(BuildPresetRow(effect, descriptor));

        // Live compressor metering: gain reduction + input/output peak, read straight from the playing mixer's
        // DSP state when one is attached (SetLiveAudioMixer) — omitted otherwise (no live playback yet).
        if (effect.EffectTypeId == EffectTypeIds.AudioCompressor)
            rows.Children.Add(BuildCompressorMeterRow(context, effect));

        // Heavy-chain hint (PLAN.md step 41): long-tailed DSP is worth pre-rendering rather than recomputing
        // every playback pass — point at the freeze command instead of silently burning CPU. Freeze is
        // clip-scoped, so insert chains (track/bus/master) get the plain cost warning.
        if (Sprocket.Core.Audio.AudioEffectTraits.IsHeavy(effect.EffectTypeId))
            rows.Children.Add(new TextBlock
            {
                Text = context.ClipScope
                    ? "CPU-heavy tail — Sequence ▸ Freeze Clip Audio pre-renders it."
                    : "CPU-heavy tail — recomputed on every playback pass.",
                FontSize = 11,
                Foreground = FaintText,
                TextWrapping = TextWrapping.Wrap,
            });

        IReadOnlyList<EffectParameterDescriptor> parameters =
            descriptor?.Parameters ?? FallbackDescriptors(effect);
        if (parameters.Count == 0)
            rows.Children.Add(new TextBlock { Text = "No editable parameters.", FontSize = 11, Foreground = FaintText });

        // The three-way grade is a composite control over four parameters per wheel, which the per-param
        // descriptor model can't express — so Color Wheels gets Resolve-style trackballs instead of twelve
        // stacked sliders. (If plugins ever need wheels, promote this to descriptor group metadata.)
        if (effect.EffectTypeId == EffectTypeIds.ColorWheels)
            BuildColorWheelsRows(effect, rows);
        else
            foreach (EffectParameterDescriptor p in parameters)
                rows.Children.Add(BuildParamRow(effect, p));
        // Dim the parameter rows (rather than disabling input) when the effect is off, so values stay
        // visible and editable while previewing without it.
        rows.Opacity = effect.Enabled ? 1.0 : 0.5;

        // Header: an enable/disable status LED on the left of the title (green = active, grey = bypassed —
        // the audio-rack convention, replacing the earlier eye icon), the instance's reference tag chip
        // (EffectTags, e.g. RV-1 — how AI/MCP clients address this exact instance), and a remove (x) button.
        bool expanded = !_effectExpanded.TryGetValue(effect, out bool stored) || stored;
        var header = new DockPanel { Background = Brushes.Transparent }; // hit-testable for the reorder drag
        var led = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new Avalonia.CornerRadius(4.5),
            Background = effect.Enabled ? Palette.GoodBrush : LedOffBrush,
            // A soft same-hue halo is what makes the lit state read as an LED rather than a green dot.
            BoxShadow = effect.Enabled
                ? new BoxShadows(new BoxShadow { Blur = 6, Color = LedGlow })
                : default,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // A plain Button, not a ToggleButton: Fluent paints a checked ToggleButton with the accent fill,
        // which would put a blue pill behind the LED — the LED itself is the whole state indicator.
        var toggle = new Button
        {
            Content = led,
            Padding = new Avalonia.Thickness(4, 4),
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(toggle, effect.Enabled ? "Disable effect" : "Enable effect");
        toggle.Click += (_, e) =>
        {
            e.Handled = true;
            _history!.Execute(new SetEffectEnabledCommand(effect, !effect.Enabled));
        };
        var remove = new Button
        {
            Content = new ShapesPath
            {
                Data = Icons.Close, Stroke = FaintText, StrokeThickness = 1.2, StrokeLineCap = PenLineCap.Round,
                // Close's path is a square 12x12 diagonal cross, so at Chrome (8x8) it fills the full box —
                // visually taller than Eye's flatter ~8x5.8 render (see toggleIcon above) even though both
                // nominally use the same Chrome constant. 6x6 is a hand-tuned optical match so the drawn mark
                // reads at the same size as the eye glyph next to it, not the eye's whole (larger) button.
                Width = 6, Height = 6, Stretch = Stretch.Uniform,
            },
            Padding = new Avalonia.Thickness(5, 1),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, "Remove effect");
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            _effectExpanded.Remove(effect);
            _history!.Execute(context.Remove(effect));
        };

        // The expand/collapse chevron itself is added by Section() (rightmost, shared by every section);
        // the effect-specific LED / tag chip / × set is built here.
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        DockPanel.SetDock(toggle, Dock.Left);
        header.Children.Add(toggle);
        var titleGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleGroup.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = effect.Enabled ? TextBrush : FaintText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (effect.Tag is { Length: > 0 } tag)
        {
            var chip = new Border
            {
                BorderBrush = Edge,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(3),
                Padding = new Avalonia.Thickness(5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = tag,
                    FontSize = 9,
                    Foreground = FaintText,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(chip, "Reference tag — how AI assistants (MCP) identify this effect instance.");
            titleGroup.Children.Add(chip);
        }
        header.Children.Add(titleGroup);

        // Reorder within the stack (PLAN.md step 51) — stack order is processing order. Primary gesture:
        // drag the section header onto another effect section (top half = before it, bottom half = after).
        // Fallback: Move Up / Move Down on the header's context menu, clamped at the ends.
        int index = context.Effects.IndexOf(effect);
        int count = context.Effects.Count;
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                _reorderPressed = e;
                _reorderStart = e.GetPosition(this);
                _reorderSourceIndex = index;
            }
        };
        header.ContextMenu = BuildMoveMenu(context, index, count);

        Control section = Section(header, rows, expanded);
        if (SectionExpander(section) is { } expander)
        {
            expander.Expanded += (_, _) => _effectExpanded[effect] = true;
            expander.Collapsed += (_, _) => _effectExpanded[effect] = false;
        }

        DragDrop.SetAllowDrop(section, true);
        section.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DragFormats.EffectReorderIndex)
                ? DragDropEffects.Move
                : DragDropEffects.None;
        });
        section.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(DragFormats.EffectReorderIndex) || _history is null)
                return;
            if (!int.TryParse(e.DataTransfer.TryGetValue(DragFormats.EffectReorderIndex),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int source))
                return;
            bool bottomHalf = e.GetPosition(section).Y > section.Bounds.Height / 2;
            MoveEffect(context.Effects, source, EffectReorder.DropIndex(source, index, bottomHalf, count));
        });
        return section;
    }

    /// <summary>The effect header's Move Up / Move Down context menu — the discoverable, non-drag reorder
    /// affordance (PLAN.md step 51). Boundary items are disabled rather than wrapping.</summary>
    private ContextMenu BuildMoveMenu(ChainContext context, int index, int count)
    {
        var up = new MenuItem { Header = "Move Up", FontSize = 12, IsEnabled = index > 0 };
        up.Click += (_, _) => MoveEffect(context.Effects, index, EffectReorder.StepIndex(index, count, -1));
        var down = new MenuItem { Header = "Move Down", FontSize = 12, IsEnabled = index < count - 1 };
        down.Click += (_, _) => MoveEffect(context.Effects, index, EffectReorder.StepIndex(index, count, +1));
        return new ContextMenu { ItemsSource = new[] { up, down } };
    }

    /// <summary>Executes a stack/chain reorder as one undoable <see cref="MoveChainEffectCommand"/>; a
    /// same-index gesture is skipped so no-ops don't pollute the undo history.</summary>
    private void MoveEffect(List<EffectInstance> chain, int fromIndex, int toIndex)
    {
        if (_history is null || fromIndex == toIndex ||
            fromIndex < 0 || fromIndex >= chain.Count)
            return;
        _history.Execute(new MoveChainEffectCommand(chain, chain[fromIndex], toIndex));
    }

    /// <summary>
    /// The Compressor's live meter rows: gain-reduction, input peak, and output peak, each its own labeled
    /// bar + numeric row (same <see cref="LabeledRow"/> shape every other parameter row uses, so the three
    /// stack into aligned columns), read from the playing mixer's
    /// <see cref="Sprocket.Audio.Effects.CompressorEffect"/> instance (see <see cref="SetLiveAudioMixer"/>) via
    /// <see cref="Sprocket.Audio.AudioMixer.TryPeekEffect"/>. Registered as a refresher so it updates alongside
    /// every other value on <see cref="OnPlayheadMoved"/>/history changes; reads as "—" when no live mixer is
    /// attached, the clip isn't currently playing, or a chain edit hasn't been re-mixed since (all of which
    /// mean there's nothing live to show, not an error).
    /// </summary>
    private Control BuildCompressorMeterRow(ChainContext context, EffectInstance effect)
    {
        (StackPanel Row, MiniMeterBar Bar, TextBlock Label) Meter(IBrush accent)
        {
            var bar = new MiniMeterBar(accent);
            var label = new TextBlock
            {
                FontSize = 10, Foreground = FaintText, Width = 52,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(bar);
            row.Children.Add(label);
            return (row, bar, label);
        }

        (StackPanel grRow, MiniMeterBar grBar, TextBlock grLabel) = Meter(Accent);
        (StackPanel inRow, MiniMeterBar inBar, TextBlock inLabel) = Meter(FaintText);
        (StackPanel outRow, MiniMeterBar outBar, TextBlock outLabel) = Meter(MutedText);

        var panel = new StackPanel { Spacing = 3, Margin = new Avalonia.Thickness(0, 0, 0, 2) };
        panel.Children.Add(LabeledRow("GR", grRow));
        panel.Children.Add(LabeledRow("In", inRow));
        panel.Children.Add(LabeledRow("Out", outRow));

        _valueRefreshers.Add(() =>
        {
            Sprocket.Audio.Effects.CompressorMeterSnapshot? s = TryReadCompressorMeter(context, effect);
            if (s is { } snapshot)
            {
                grBar.SetLevel(Math.Clamp(snapshot.GainReductionDb / 24.0, 0, 1));
                grLabel.Text = snapshot.GainReductionDb > 0.05 ? $"-{snapshot.GainReductionDb:0.0} dB" : "0.0 dB";
                inBar.SetLevel(MixerFormat.MeterFillFraction(snapshot.InputPeakDb));
                inLabel.Text = $"{PeakDbText(snapshot.InputPeakDb)} dB";
                outBar.SetLevel(MixerFormat.MeterFillFraction(snapshot.OutputPeakDb));
                outLabel.Text = $"{PeakDbText(snapshot.OutputPeakDb)} dB";
            }
            else
            {
                grBar.SetLevel(0);
                grLabel.Text = "—";
                inBar.SetLevel(0);
                inLabel.Text = "—";
                outBar.SetLevel(0);
                outLabel.Text = "—";
            }
        });
        return panel;
    }

    /// <summary>Looks up the live <see cref="Sprocket.Audio.Effects.CompressorEffect"/> backing this exact model
    /// instance, or <see langword="null"/> if there isn't one right now (see <see cref="BuildCompressorMeterRow"/>
    /// for why that's an expected, non-error state).</summary>
    private Sprocket.Audio.Effects.CompressorMeterSnapshot? TryReadCompressorMeter(ChainContext context, EffectInstance effect)
    {
        if (_liveMixer?.Invoke() is not { } mixer)
            return null;
        int chainIndex = EffectTypeIds.AudioChainIndexOf(context.Effects, effect);
        if (chainIndex < 0)
            return null;
        return mixer.TryPeekEffect(context.MeterKey, chainIndex) is Sprocket.Audio.Effects.CompressorEffect compressor
            ? compressor.TakeSnapshot()
            : null;
    }

    private static string PeakDbText(double db) => double.IsNegativeInfinity(db) ? "-∞" : $"{db:0.0}";

    /// <summary>The preset picker row (PLAN.md step 41): selecting a preset applies its parameter values as one
    /// <see cref="CompositeCommand"/> — a single undo entry. Parameters a preset omits (e.g. Mix) keep their
    /// current value, so switching character preserves the user's wet/dry blend.</summary>
    private Control BuildPresetRow(EffectInstance effect, EffectDescriptor descriptor)
    {
        var combo = new ComboBox
        {
            ItemsSource = descriptor.Presets.Select(p => p.Name).ToList(),
            PlaceholderText = "Choose…",
            FontSize = 11,
            MinHeight = 24,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppress || combo.SelectedIndex < 0 || combo.SelectedIndex >= descriptor.Presets.Count)
                return;
            EffectPreset preset = descriptor.Presets[combo.SelectedIndex];
            List<IEditCommand> commands = [.. preset.Values.Select(kv =>
                (IEditCommand)new SetEffectParameterCommand(effect, kv.Key, AnimatableValue.Constant(kv.Value)))];
            ExecuteEdit(new CompositeCommand($"Apply preset {preset.Name}", commands), coalescing: false);
        };
        return LabeledRow("Preset", combo);
    }

    private Control BuildParamRow(EffectInstance effect, EffectParameterDescriptor p) =>
        BuildAnimatableRow(
            p,
            () => ParamValue(effect, p),
            (next, coalescing) => ExecuteParam(effect, p.Name, next, coalescing));

    /// <summary>
    /// One parameter row, dispatched by the descriptor's <see cref="ParameterKind"/> — the continuous /
    /// integer slider UI, a checkbox for <see cref="ParameterKind.Toggle"/>, or a dropdown for
    /// <see cref="ParameterKind.Dropdown"/> — driven by delegates so effect parameters
    /// (<see cref="EffectInstance.Parameters"/>) and generator parameters
    /// (<see cref="GeneratorSpec.Parameters"/>, PLAN.md step 40) share the identical editing UI: <paramref
    /// name="get"/> reads the current <see cref="AnimatableValue"/>, <paramref name="execute"/> runs an edit
    /// through the command stack (coalescing mid-drag).
    /// </summary>
    private Control BuildAnimatableRow(
        EffectParameterDescriptor p, Func<AnimatableValue> get, Action<AnimatableValue, bool> execute)
    {
        if (p.Kind == ParameterKind.Toggle)
            return BuildToggleRow(p, get, execute);
        if (p.Kind == ParameterKind.Dropdown)
            return BuildDropdownRow(p, get, execute);
        return BuildSliderRow(p, get, execute);
    }

    /// <summary>
    /// The slider + numeric box + keyframe toggle + keyframe lane row shared by
    /// <see cref="ParameterKind.Continuous"/> and <see cref="ParameterKind.Integer"/> parameters. Integer
    /// parameters snap the slider to whole numbers, round every commit, and default new keyframes to
    /// <see cref="Interpolation.Hold"/> (still re-easable in the lane — an int glide is occasionally
    /// meaningful, unlike a fractional toggle).
    /// </summary>
    private Control BuildSliderRow(
        EffectParameterDescriptor p, Func<AnimatableValue> get, Action<AnimatableValue, bool> execute)
    {
        bool integer = p.Kind == ParameterKind.Integer;
        Interpolation newKeyMode = AnimatableEditing.DefaultInterpolation(p.Kind);
        var keyGlyph = new ShapesPath
        {
            Data = Icons.Diamond,
            Fill = Brushes.Transparent,
            Stroke = FaintText,
            StrokeThickness = 1.5,
            Width = IconSizes.Compact,
            Height = IconSizes.Compact,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var graphGlyph = new ShapesPath
        {
            Data = Icons.Activity,
            Stroke = FaintText,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = IconSizes.Default,
            Height = IconSizes.Default,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
        {
            Minimum = p.Min,
            Maximum = p.Max,
            SmallChange = p.Step,
            LargeChange = p.Step * 10,
            IsSnapToTickEnabled = integer,
            TickFrequency = integer ? 1.0 : 0.0,
            // Negative margin trims the empty track padding the Fluent slider reserves above/below the
            // 14px thumb, so each parameter block is tighter without clipping the thumb.
            Margin = new Avalonia.Thickness(0, -4, 0, -4),
        };
        var box = new TextBox
        {
            Width = 64,
            FontSize = 11,
            // See BuildSpeedRow: override the theme's 32px MinHeight and centre the text so the box is
            // compact and its content isn't top-aligned with a bottom gap.
            MinHeight = 22,
            Height = 22,
            Padding = new Avalonia.Thickness(6, 2),
            Background = PanelBg,
            BorderBrush = InputEdge,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var keyButton = new Button
        {
            Content = keyGlyph,
            Width = 24,
            Height = 22,
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(keyButton, "Toggle keyframing at the playhead");

        // Velocity-graph toggle (PLAN.md step 16d): expands the keyframe strip into the editable value graph.
        var graphButton = new Button
        {
            Content = graphGlyph,
            Width = 24,
            Height = 22,
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        ToolTip.SetTip(graphButton, "Show / hide the velocity graph");

        var label = new TextBlock
        {
            Text = p.DisplayName,
            FontSize = 11,
            Foreground = MutedText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (p.Description is { Length: > 0 } description)
            ToolTip.SetTip(label, description);

        // Slider drag → coalesced edits (one undo entry); numeric box → a single discrete edit.
        void Commit(double value, bool coalescing) =>
            execute(AnimatableEditing.SetValueAt(
                get(), _playhead(), integer ? Math.Round(value) : value, newKeyMode), coalescing);
        void CommitBox()
        {
            if (_suppress)
                return;
            // Unit-aware parse: the box displays "1.5 EV" / "90°" (InspectorFormat.Value), which a plain
            // double.TryParse rejects — committing back a displayed value must not silently revert.
            if (!InspectorFormat.TryParseValue(box.Text, p.Unit, out double v))
            {
                RefreshValues(); // revert the text to the model value
                return;
            }
            Commit(Math.Clamp(v, p.Min, p.Max), coalescing: false);
        }

        slider.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        slider.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) => EndDrag();
        slider.ValueChanged += (_, e) =>
        {
            if (_suppress)
                return;
            Commit(e.NewValue, coalescing: _dragScope is not null);
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitBox();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitBox();

        keyButton.Click += (_, _) =>
        {
            AnimatableValue current = get();
            Timecode t = _playhead();
            execute(current.IsAnimated
                ? AnimatableEditing.DisableKeyframing(current, t)
                : AnimatableEditing.EnableKeyframing(current, t, newKeyMode), false);
        };

        // Header line: label + keyframe toggle + numeric box; slider below.
        var top = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 2) };
        var rightGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        rightGroup.Children.Add(graphButton);
        rightGroup.Children.Add(keyButton);
        rightGroup.Children.Add(box);
        DockPanel.SetDock(rightGroup, Dock.Right);
        top.Children.Add(rightGroup);
        top.Children.Add(label);

        // Keyframe lane (PLAN.md step 16b/16d): shown only when the parameter is animated. It edits the same
        // AnimatableValue through the command stack; a keyframe/handle drag coalesces to one undo entry.
        var lane = new KeyframeLane { IsVisible = false };
        lane.DragStarted += BeginDrag;
        lane.DragEnded += EndDrag;
        lane.Edited += (next, coalescing) => execute(next, coalescing);

        graphButton.Click += (_, _) =>
        {
            lane.GraphMode = !lane.GraphMode;
            graphGlyph.Stroke = lane.GraphMode ? Accent : FaintText;
        };

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(slider);
        stack.Children.Add(lane);

        // Refresher: re-read the model value at the playhead and update the widgets (suppressed) + the keyframe
        // glyph + the lane.
        _valueRefreshers.Add(() =>
        {
            AnimatableValue value = get();
            double v = value.Evaluate(_playhead());
            _suppress = true;
            slider.Value = Math.Clamp(v, p.Min, p.Max);
            box.Text = InspectorFormat.Value(v, p.Unit);
            _suppress = false;
            keyGlyph.Fill = value.IsAnimated ? Accent : Brushes.Transparent;
            keyGlyph.Stroke = value.IsAnimated ? Accent : FaintText;

            graphButton.IsVisible = value.IsAnimated;
            lane.IsVisible = value.IsAnimated;
            if (value.IsAnimated && LaneRange() is { } range)
                lane.Update(value, range.Start, range.End, _playhead().Ticks, p.Min, p.Max);
        });

        return stack;
    }

    /// <summary>
    /// A <see cref="ParameterKind.Toggle"/> row: label + keyframe toggle + checkbox (in the numeric-box
    /// slot), with the keyframe lane below when animated. The value stays the 0/1 scalar the DSP reads with
    /// its ≥ 0.5 threshold; every keyframe is <see cref="Interpolation.Hold"/> (and the lane is
    /// <see cref="KeyframeLane.HoldOnly"/>) so a keyframed toggle flips hard at each key instead of
    /// interpolating through the threshold. No slider, numeric box, or velocity-graph button.
    /// </summary>
    private Control BuildToggleRow(
        EffectParameterDescriptor p, Func<AnimatableValue> get, Action<AnimatableValue, bool> execute)
    {
        var keyGlyph = new ShapesPath
        {
            Data = Icons.Diamond,
            Fill = Brushes.Transparent,
            Stroke = FaintText,
            StrokeThickness = 1.5,
            Width = IconSizes.Compact,
            Height = IconSizes.Compact,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var keyButton = new Button
        {
            Content = keyGlyph,
            Width = 24,
            Height = 22,
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(keyButton, "Toggle keyframing at the playhead");

        var check = new CheckBox
        {
            MinHeight = 22,
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.Click += (_, _) =>
        {
            if (_suppress)
                return;
            execute(AnimatableEditing.SetValueAt(
                get(), _playhead(), check.IsChecked == true ? 1.0 : 0.0, Interpolation.Hold), false);
        };

        keyButton.Click += (_, _) =>
        {
            AnimatableValue current = get();
            Timecode t = _playhead();
            execute(current.IsAnimated
                ? AnimatableEditing.DisableKeyframing(current, t)
                : AnimatableEditing.EnableKeyframing(current, t, Interpolation.Hold), false);
        };

        var label = new TextBlock
        {
            Text = p.DisplayName,
            FontSize = 11,
            Foreground = MutedText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (p.Description is { Length: > 0 } description)
            ToolTip.SetTip(label, description);

        var top = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 2) };
        var rightGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        rightGroup.Children.Add(keyButton);
        rightGroup.Children.Add(check);
        DockPanel.SetDock(rightGroup, Dock.Right);
        top.Children.Add(rightGroup);
        top.Children.Add(label);

        var lane = new KeyframeLane { IsVisible = false, HoldOnly = true };
        lane.DragStarted += BeginDrag;
        lane.DragEnded += EndDrag;
        lane.Edited += (next, coalescing) => execute(next, coalescing);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(lane);

        _valueRefreshers.Add(() =>
        {
            AnimatableValue value = get();
            _suppress = true;
            check.IsChecked = value.Evaluate(_playhead()) >= 0.5;
            _suppress = false;
            keyGlyph.Fill = value.IsAnimated ? Accent : Brushes.Transparent;
            keyGlyph.Stroke = value.IsAnimated ? Accent : FaintText;
            lane.IsVisible = value.IsAnimated;
            if (value.IsAnimated && LaneRange() is { } range)
                lane.Update(value, range.Start, range.End, _playhead().Ticks, p.Min, p.Max);
        });

        return stack;
    }

    /// <summary>
    /// A <see cref="ParameterKind.Dropdown"/> row: a combo over the descriptor's
    /// <see cref="EffectParameterDescriptor.Choices"/>, storing the selected index as a constant (dropdowns
    /// are not keyframeable — matching how professional editors treat enum parameters like input color
    /// transforms). Replaced the bespoke ColorTransform-only profile row (PLAN.md step 37) when kinds became
    /// descriptor-driven.
    /// </summary>
    private Control BuildDropdownRow(
        EffectParameterDescriptor p, Func<AnimatableValue> get, Action<AnimatableValue, bool> execute)
    {
        IReadOnlyList<string> choices = p.Choices ?? [];
        var combo = new ComboBox
        {
            ItemsSource = choices,
            FontSize = 11,
            MinHeight = 24,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppress || combo.SelectedIndex < 0)
                return;
            execute(AnimatableValue.Constant(combo.SelectedIndex), false);
        };
        _valueRefreshers.Add(() =>
        {
            _suppress = true;
            combo.SelectedIndex = choices.Count == 0
                ? -1
                : Math.Clamp((int)Math.Round(get().Evaluate(_playhead())), 0, choices.Count - 1);
            _suppress = false;
        });
        return LabeledRow(p.DisplayName, combo, p.Description);
    }

    /// <summary>
    /// The Color Wheels section body: three Resolve-style trackballs (Lift / Gamma / Gain) side by side,
    /// each with its master slider beneath and its R/G/B rows in a collapsed "Channels" expander — all
    /// twelve parameters stay individually keyframeable there. The wheel is a composite editor over the
    /// existing R/G/B params (<see cref="ColorWheelMath"/>): each puck move commits one
    /// <see cref="CompositeCommand"/> of three parameter sets, which coalesces child-wise across the drag
    /// scope, so a whole drag is a single undo entry.
    /// </summary>
    private void BuildColorWheelsRows(EffectInstance effect, StackPanel rows)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };
        (string Label, string Master, string R, string G, string B)[] wheels =
        [
            ("Lift", EffectParamNames.LiftMaster, EffectParamNames.LiftR, EffectParamNames.LiftG, EffectParamNames.LiftB),
            ("Gamma", EffectParamNames.GammaMaster, EffectParamNames.GammaR, EffectParamNames.GammaG, EffectParamNames.GammaB),
            ("Gain", EffectParamNames.GainMaster, EffectParamNames.GainR, EffectParamNames.GainG, EffectParamNames.GainB),
        ];
        for (int i = 0; i < wheels.Length; i++)
        {
            Control cell = BuildWheelGroup(effect, wheels[i].Label,
                wheels[i].Master, wheels[i].R, wheels[i].G, wheels[i].B);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        rows.Children.Add(grid);
    }

    private Control BuildWheelGroup(
        EffectInstance effect, string label, string master, string r, string g, string b)
    {
        IReadOnlyList<EffectParameterDescriptor> parameters =
            EffectCatalog.Find(effect.EffectTypeId)?.Parameters ?? FallbackDescriptors(effect);
        EffectParameterDescriptor Descriptor(string name) =>
            parameters.FirstOrDefault(p => p.Name == name)
            ?? new EffectParameterDescriptor(name, name, 0.0, -1.0, 1.0, 0.005);
        EffectParameterDescriptor rDesc = Descriptor(r), gDesc = Descriptor(g), bDesc = Descriptor(b);

        (double R, double G, double B) Offsets(Timecode t) => (
            ParamValue(effect, rDesc).Evaluate(t),
            ParamValue(effect, gDesc).Evaluate(t),
            ParamValue(effect, bDesc).Evaluate(t));

        // One puck move = one composite of the three channel sets. Composites of the same shape merge
        // child-wise (same effect instance + same param names in order), so a drag coalesces to one entry.
        void CommitOffsets((double R, double G, double B) next, bool coalescing)
        {
            Timecode t = _playhead();
            ExecuteEdit(new CompositeCommand($"{label} wheel",
            [
                new SetEffectParameterCommand(effect, r, AnimatableEditing.SetValueAt(ParamValue(effect, rDesc), t, next.R)),
                new SetEffectParameterCommand(effect, g, AnimatableEditing.SetValueAt(ParamValue(effect, gDesc), t, next.G)),
                new SetEffectParameterCommand(effect, b, AnimatableEditing.SetValueAt(ParamValue(effect, bDesc), t, next.B)),
            ]), coalescing);
        }

        var wheel = new ColorWheelControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 2, 0, 4),
        };
        wheel.DragStarted += BeginDrag;
        wheel.DragEnded += EndDrag;
        wheel.PuckChanged += (x, y) =>
        {
            if (_suppress)
                return;
            CommitOffsets(ColorWheelMath.FromPuck(x, y, Offsets(_playhead())), coalescing: _dragScope is not null);
        };
        wheel.ResetRequested += () =>
        {
            if (_suppress)
                return;
            // Recentre the tint but keep the common component (it belongs to the master slider).
            CommitOffsets(ColorWheelMath.FromPuck(0, 0, Offsets(_playhead())), coalescing: false);
        };

        var title = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = MutedText,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // The R/G/B rows keep the full slider/keyframe UI, tucked into a collapsed expander per wheel so
        // the section shows three wheels + masters by default instead of twelve stacked sliders.
        var channels = new StackPanel { Spacing = 4 };
        channels.Children.Add(BuildParamRow(effect, rDesc));
        channels.Children.Add(BuildParamRow(effect, gDesc));
        channels.Children.Add(BuildParamRow(effect, bDesc));
        var channelsExpander = new Expander
        {
            Header = new TextBlock { Text = "Channels", FontSize = 11, Foreground = FaintText },
            Content = channels,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        channelsExpander.Classes.Add("inspectorSection");

        var cell = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(2, 0, 2, 0) };
        cell.Children.Add(title);
        cell.Children.Add(wheel);
        cell.Children.Add(BuildParamRow(effect, Descriptor(master)));
        cell.Children.Add(channelsExpander);

        _valueRefreshers.Add(() =>
        {
            (double cr, double cg, double cb) = Offsets(_playhead());
            (double x, double y) = ColorWheelMath.ToPuck(cr, cg, cb);
            bool animated = ParamValue(effect, rDesc).IsAnimated
                || ParamValue(effect, gDesc).IsAnimated
                || ParamValue(effect, bDesc).IsAnimated;
            _suppress = true;
            wheel.Update(x, y, animated);
            _suppress = false;
        });

        return cell;
    }

    private Control BuildAddEffectBar(Clip clip)
    {
        var add = new Button
        {
            Content = "+ Effect",
            FontSize = 12,
            Padding = new Avalonia.Thickness(10, 4),
            Margin = new Avalonia.Thickness(8, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = RaisedBg,
            Foreground = TextBrush,
        };

        var items = new List<MenuItem>();
        foreach (EffectDescriptor descriptor in RelevantEffects(clip))
        {
            // One point below the menu default, matching the panel's dense 12–13px type scale.
            var item = new MenuItem { Header = descriptor.DisplayName, FontSize = 13 };
            // The input color transform must run before the creative grade (PLAN.md step 37), so the
            // manual-tag path inserts it at the front of the stack; everything else appends as usual.
            bool prepend = descriptor.Id == EffectTypeIds.ColorTransform;
            item.Click += (_, _) => _history!.Execute(prepend
                ? new InsertEffectAtCommand(clip, descriptor.CreateInstance(), 0)
                : new AddEffectCommand(clip, descriptor.CreateInstance()));
            items.Add(item);
        }
        add.Flyout = new MenuFlyout { ItemsSource = items };
        return add;
    }

    /// <summary>The insert-chain "+ Effect" bar (PLAN.md step 31): the catalog's audio effects, appended to
    /// the chain via <see cref="AddChainEffectCommand"/> (processing order = chain order; reorder after).</summary>
    private Control BuildAddChainEffectBar(AudioChainTarget target)
    {
        var add = new Button
        {
            Content = "+ Effect",
            FontSize = 12,
            Padding = new Avalonia.Thickness(10, 4),
            Margin = new Avalonia.Thickness(8, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = RaisedBg,
            Foreground = TextBrush,
        };
        var items = new List<MenuItem>();
        foreach (EffectDescriptor descriptor in EffectRelevance.ForAudioChain())
        {
            var item = new MenuItem { Header = descriptor.DisplayName, FontSize = 13 };
            item.Click += (_, _) => _history!.Execute(
                new AddChainEffectCommand(target.Chain, descriptor.CreateInstance()));
            items.Add(item);
        }
        add.Flyout = new MenuFlyout { ItemsSource = items };
        return add;
    }

    /// <summary>The catalog effects (built-in + plugin, PLAN.md step 33) that make sense for this clip —
    /// audio DSP stages for an audio-track clip, video/colour shader stages otherwise (<see cref="EffectRelevance"/>).</summary>
    private IEnumerable<EffectDescriptor> RelevantEffects(Clip clip) =>
        _project is null ? EffectCatalog.All : EffectRelevance.For(_project.Timeline, clip);

    // ── Editing ───────────────────────────────────────────────────────────────────────────────────────

    private void BeginDrag()
    {
        if (_history is null || _dragScope is not null)
            return;
        _editing = true;
        _dragScope = _history.BeginCoalescing();
    }

    private void EndDrag()
    {
        _dragScope?.Dispose();
        _dragScope = null;
        _editing = false;
    }

    /// <summary>
    /// Runs an edit through the command stack. <paramref name="coalescing"/> (true mid-drag) keeps the
    /// panel in editing mode so <see cref="OnHistoryChanged"/> refreshes values rather than rebuilding the
    /// section out from under an active gesture. Shared by the slider/numeric editors, the keyframe lane,
    /// and the title text/string editors (PLAN.md step 40).
    /// </summary>
    private void ExecuteEdit(IEditCommand command, bool coalescing)
    {
        if (_history is null)
            return;

        bool wasEditing = _editing;
        _editing = true; // a single discrete commit shouldn't trigger a rebuild mid-update either
        try
        {
            _history.Execute(command);
        }
        finally
        {
            _editing = coalescing && wasEditing; // stay in editing mode only while a drag/lane scope is open
        }
        RefreshValues();
    }

    private void ExecuteParam(EffectInstance effect, string name, AnimatableValue next, bool coalescing) =>
        ExecuteEdit(new SetEffectParameterCommand(effect, name, next), coalescing);

    private void ExecuteGeneratorParam(GeneratorSpec generator, string name, AnimatableValue next, bool coalescing) =>
        ExecuteEdit(new SetGeneratorParameterCommand(generator, name, next), coalescing);

    private void ExecuteGeneratorString(GeneratorSpec generator, string name, string value, bool coalescing) =>
        ExecuteEdit(new SetGeneratorStringCommand(generator, name, value), coalescing);

    private void RefreshValues()
    {
        foreach (Action refresh in _valueRefreshers)
            refresh();
    }

    private static AnimatableValue ParamValue(EffectInstance effect, EffectParameterDescriptor p) =>
        effect.Parameters.TryGetValue(p.Name, out AnimatableValue? v) ? v : AnimatableValue.Constant(p.Default);

    /// <summary>Descriptors for an unregistered (plugin) effect's existing parameters, so the Inspector still
    /// shows editable sliders with a guessed range rather than nothing.</summary>
    private static IReadOnlyList<EffectParameterDescriptor> FallbackDescriptors(EffectInstance effect)
    {
        var list = new List<EffectParameterDescriptor>();
        foreach ((string name, AnimatableValue value) in effect.Parameters)
        {
            double v = value.Evaluate(Timecode.Zero);
            double max = Math.Max(1.0, Math.Abs(v) * 2.0);
            list.Add(new EffectParameterDescriptor(name, name, v, Math.Min(0.0, -max), max, 0.01));
        }
        return list;
    }

    // ── Section / row chrome ──────────────────────────────────────────────────────────────────────────

    private static Control Section(object header, Control content, bool expanded)
    {
        // A plain-string title (Clip, Multicam, the TEXT sections) would render at the Expander's default
        // header font — visibly larger than the effect sections' hand-built 12px semibold headers. Wrap it
        // so every section header reads at the same size.
        Control headerControl = header is string title
            ? new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : (Control)header;

        // Every section draws its own expand/collapse chevron (Icons.Chevron), replacing Fluent's built-in
        // one — which is Stretch="None" and so can't be resized to match the effect headers' eye/× glyph set
        // (App.axaml collapses it to zero for Expander.inspectorSection). Purely a state indicator: the whole
        // header is already the Expander's own toggle target, so this glyph needs no click handler of its own.
        var chevron = new ShapesPath
        {
            Data = Icons.Chevron,
            Stroke = FaintText,
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            // Aspect-true box (Chevron's geometry is 12x6): Uniform stretch pins the scaled geometry to the
            // box's top-left rather than centering the slack, so a square box would seat the arrow high and
            // break the 180° center rotation.
            Width = IconSizes.Chrome,
            Height = IconSizes.Chrome * 6.0 / 12.0,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new RotateTransform(expanded ? 180 : 0),
            RenderTransformOrigin = Avalonia.RelativePoint.Center,
            Margin = new Avalonia.Thickness(3, 0, 5, 0),
        };
        var wrap = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        wrap.Children.Add(chevron);
        wrap.Children.Add(headerControl);

        var expander = new Expander
        {
            Header = wrap,
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Classes.Add("inspectorSection");
        var rotate = (RotateTransform)chevron.RenderTransform!;
        expander.Expanded += (_, _) => rotate.Angle = 180;
        expander.Collapsed += (_, _) => rotate.Angle = 0;

        // Each section reads as a card a step darker than the panel (UI.md §3.5). The Border owns the
        // whole card look — bg / edge / radius — while App.axaml's inspectorSection styles strip the
        // Fluent Expander's own header/content fills so they don't paint over it.
        return new Border
        {
            Background = Palette.SectionBgBrush,
            BorderBrush = Edge,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            ClipToBounds = true,
            Margin = new Avalonia.Thickness(8, 6, 8, 0),
            Child = expander,
        };
    }

    /// <summary>The Expander inside a <see cref="Section"/> card, or <see langword="null"/> for controls that
    /// aren't sections (the empty-state text, the add-effect bar).</summary>
    private static Expander? SectionExpander(Control section) =>
        section is Border { Child: Expander expander } ? expander : null;

    private static Control InfoRow(string label, string value)
    {
        var row = new DockPanel();
        var v = new TextBlock
        {
            Text = value,
            FontSize = 11,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = FaintText });
        return row;
    }

    private static string FormatSeconds(Timecode t) =>
        $"{t.ToSeconds().ToString("0.00", CultureInfo.InvariantCulture)}s";

    /// <summary>A compact horizontal fill meter for inline effect readouts (the Compressor's gain-reduction and
    /// input/output rows) — where the Mixer panel's tall vertical channel meter doesn't fit a single text row.
    /// Single flat accent color rather than the channel meter's green/amber/red thresholds: these are secondary,
    /// glanceable readouts, not the main level meter clipping matters for.</summary>
    private sealed class MiniMeterBar : Control
    {
        private readonly IBrush _accent;
        private double _level;

        public MiniMeterBar(IBrush accent)
        {
            _accent = accent;
            Width = 52;
            Height = 6;
        }

        public void SetLevel(double level)
        {
            double clamped = Math.Clamp(level, 0, 1);
            if (Math.Abs(clamped - _level) < 0.002)
                return;
            _level = clamped;
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            ctx.FillRectangle(Palette.WindowBgBrush, new Avalonia.Rect(0, 0, w, h), 2);
            double fill = _level * w;
            if (fill > 0.5)
                ctx.FillRectangle(_accent, new Avalonia.Rect(0, 0, fill, h), 2);
        }
    }
}
