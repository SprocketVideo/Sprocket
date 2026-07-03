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
    private static readonly IBrush TextBrush = Palette.TextBrush;
    private static readonly IBrush MutedText = Palette.MutedTextBrush;
    private static readonly IBrush FaintText = Palette.FaintTextBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;

    private Project? _project;
    private EditHistory? _history;
    private Func<Timecode> _playhead = () => Timecode.Zero;

    private Clip? _clip;
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
        foreach (Expander section in _body.Children.OfType<Expander>())
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

    /// <summary>Shows the given clip's properties (or the empty state when <see langword="null"/>).</summary>
    public void SetSelectedClip(Clip? clip)
    {
        if (!ReferenceEquals(_clip, clip))
            _effectExpanded.Clear();
        _clip = clip;
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
        _valueRefreshers.Clear();
        _body.Children.Clear();

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

        foreach (EffectInstance effect in _clip.Effects)
            _body.Children.Add(BuildEffectSection(_clip, effect));

        _body.Children.Add(BuildAddEffectBar(_clip));
        RefreshValues();
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
            BorderBrush = Edge,
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
            BorderBrush = Edge,
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
            BorderBrush = Edge,
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
    private static Control LabeledRow(string label, Control editor)
    {
        var row = new DockPanel();
        DockPanel.SetDock(editor, Dock.Right);
        row.Children.Add(editor);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = FaintText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private Control BuildEffectSection(Clip clip, EffectInstance effect)
    {
        EffectDescriptor? descriptor = EffectCatalog.Find(effect.EffectTypeId);
        string title = descriptor?.DisplayName ?? effect.EffectTypeId;

        var rows = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4, 4, 4, 2) };

        // Factory presets (PLAN.md step 41): descriptors that carry presets get a picker above the parameter
        // rows; applying one is a single undoable composite parameter edit.
        if (descriptor is { Presets.Count: > 0 })
            rows.Children.Add(BuildPresetRow(effect, descriptor));

        // Heavy-chain hint (PLAN.md step 41): long-tailed DSP is worth pre-rendering rather than recomputing
        // every playback pass — point at the freeze command instead of silently burning CPU.
        if (Sprocket.Core.Audio.AudioEffectTraits.IsHeavy(effect.EffectTypeId))
            rows.Children.Add(new TextBlock
            {
                Text = "CPU-heavy tail — Sequence ▸ Freeze Clip Audio pre-renders it.",
                FontSize = 11,
                Foreground = FaintText,
                TextWrapping = TextWrapping.Wrap,
            });

        IReadOnlyList<EffectParameterDescriptor> parameters =
            descriptor?.Parameters ?? FallbackDescriptors(effect);
        if (parameters.Count == 0)
            rows.Children.Add(new TextBlock { Text = "No editable parameters.", FontSize = 11, Foreground = FaintText });

        // The input color transform's profile is an enum, not a slider scalar — a dropdown over the
        // known log profiles (PLAN.md step 37) replaces the auto-generated numeric row.
        if (effect.EffectTypeId == EffectTypeIds.ColorTransform)
            rows.Children.Add(BuildColorProfileRow(effect));
        else
            foreach (EffectParameterDescriptor p in parameters)
                rows.Children.Add(BuildParamRow(effect, p));
        // Dim the parameter rows (rather than disabling input) when the effect is off, so values stay
        // visible and editable while previewing without it.
        rows.Opacity = effect.Enabled ? 1.0 : 0.5;

        // Header with an enable/disable toggle (eye icon, matching the track-header convention in
        // TimelineControl) and a remove (x) button. Dense icon tier: these sit inside a 12px-text strip.
        var header = new DockPanel { Background = Brushes.Transparent }; // hit-testable for the reorder drag
        var toggleIcon = new ShapesPath
        {
            Data = Icons.Eye,
            Stroke = effect.Enabled ? TextBrush : FaintText,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = IconSizes.Dense,
            Height = IconSizes.Dense,
            Stretch = Stretch.Uniform,
        };
        var toggle = new ToggleButton
        {
            Content = toggleIcon,
            Padding = new Avalonia.Thickness(5, 1),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = effect.Enabled,
        };
        ToolTip.SetTip(toggle, effect.Enabled ? "Disable effect" : "Enable effect");
        toggle.Click += (_, e) =>
        {
            e.Handled = true;
            _history!.Execute(new SetEffectEnabledCommand(effect, toggle.IsChecked == true));
        };
        var remove = new Button
        {
            Content = new ShapesPath
            {
                Data = Icons.Close, Stroke = FaintText, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round,
                Width = IconSizes.Dense, Height = IconSizes.Dense, Stretch = Stretch.Uniform,
            },
            Padding = new Avalonia.Thickness(5, 1),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, "Remove effect");
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            _effectExpanded.Remove(effect);
            _history!.Execute(new RemoveEffectCommand(clip, effect));
        };
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(toggle);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = effect.Enabled ? TextBrush : FaintText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Reorder within the stack (PLAN.md step 51) — stack order is processing order. Primary gesture:
        // drag the section header onto another effect section (top half = before it, bottom half = after).
        // Fallback: Move Up / Move Down on the header's context menu, clamped at the ends.
        int index = clip.Effects.IndexOf(effect);
        int count = clip.Effects.Count;
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                _reorderPressed = e;
                _reorderStart = e.GetPosition(this);
                _reorderSourceIndex = index;
            }
        };
        header.ContextMenu = BuildMoveMenu(clip, effect, index, count);

        bool expanded = !_effectExpanded.TryGetValue(effect, out bool stored) || stored;
        Control section = Section(header, rows, expanded);
        if (section is Expander expander)
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
            MoveEffect(clip, source, EffectReorder.DropIndex(source, index, bottomHalf, count));
        });
        return section;
    }

    /// <summary>The effect header's Move Up / Move Down context menu — the discoverable, non-drag reorder
    /// affordance (PLAN.md step 51). Boundary items are disabled rather than wrapping.</summary>
    private ContextMenu BuildMoveMenu(Clip clip, EffectInstance effect, int index, int count)
    {
        var up = new MenuItem { Header = "Move Up", FontSize = 12, IsEnabled = index > 0 };
        up.Click += (_, _) => MoveEffect(clip, index, EffectReorder.StepIndex(index, count, -1));
        var down = new MenuItem { Header = "Move Down", FontSize = 12, IsEnabled = index < count - 1 };
        down.Click += (_, _) => MoveEffect(clip, index, EffectReorder.StepIndex(index, count, +1));
        return new ContextMenu { ItemsSource = new[] { up, down } };
    }

    /// <summary>Executes a stack reorder as one undoable <see cref="MoveChainEffectCommand"/>; a same-index
    /// gesture is skipped so no-ops don't pollute the undo history.</summary>
    private void MoveEffect(Clip clip, int fromIndex, int toIndex)
    {
        if (_history is null || fromIndex == toIndex ||
            fromIndex < 0 || fromIndex >= clip.Effects.Count)
            return;
        _history.Execute(new MoveChainEffectCommand(clip.Effects, clip.Effects[fromIndex], toIndex));
    }

    /// <summary>The input-transform profile dropdown (PLAN.md step 37): selects which log encoding the source
    /// was shot in (<see cref="ColorProfiles"/>), committing the effect's numeric
    /// <see cref="EffectParamNames.SourceProfile"/> index through the command stack. Auto-detected clips arrive
    /// pre-set; this row is the manual per-clip tag / override.</summary>
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

    private Control BuildColorProfileRow(EffectInstance effect)
    {
        var combo = new ComboBox
        {
            ItemsSource = ColorProfiles.DisplayNames,
            FontSize = 11,
            MinHeight = 24,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppress || combo.SelectedIndex < 0)
                return;
            ExecuteParam(effect, EffectParamNames.SourceProfile,
                AnimatableValue.Constant(combo.SelectedIndex), coalescing: false);
        };
        _valueRefreshers.Add(() =>
        {
            _suppress = true;
            double current = effect.Parameters.TryGetValue(EffectParamNames.SourceProfile, out AnimatableValue? v)
                ? v.Evaluate(Sprocket.Core.Timing.Timecode.Zero) : 0.0;
            combo.SelectedIndex = Math.Clamp((int)Math.Round(current), 0, ColorProfiles.All.Count - 1);
            _suppress = false;
        });
        return LabeledRow("Source Profile", combo);
    }

    private Control BuildParamRow(EffectInstance effect, EffectParameterDescriptor p) =>
        BuildAnimatableRow(
            p,
            () => ParamValue(effect, p),
            (next, coalescing) => ExecuteParam(effect, p.Name, next, coalescing));

    /// <summary>
    /// One animatable numeric parameter row (slider + numeric box + keyframe toggle + keyframe lane), driven
    /// by delegates so effect parameters (<see cref="EffectInstance.Parameters"/>) and generator parameters
    /// (<see cref="GeneratorSpec.Parameters"/>, PLAN.md step 40) share the identical editing UI: <paramref
    /// name="get"/> reads the current <see cref="AnimatableValue"/>, <paramref name="execute"/> runs an edit
    /// through the command stack (coalescing mid-drag).
    /// </summary>
    private Control BuildAnimatableRow(
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
            BorderBrush = Edge,
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

        // Slider drag → coalesced edits (one undo entry); numeric box → a single discrete edit.
        void Commit(double value, bool coalescing) =>
            execute(AnimatableEditing.SetValueAt(get(), _playhead(), value), coalescing);
        void CommitBox()
        {
            if (_suppress)
                return;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
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
                : AnimatableEditing.EnableKeyframing(current, t), false);
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
            if (value.IsAnimated && _clip is { } clip)
                lane.Update(value, clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks, _playhead().Ticks, p.Min, p.Max);
        });

        return stack;
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

    private static Control Section(object header, Control content, bool expanded) => new Expander
    {
        // A plain-string title (Clip, Multicam, the TEXT sections) would render at the Expander's default
        // header font — visibly larger than the effect sections' hand-built 12px semibold headers. Wrap it
        // so every section header reads at the same size.
        Header = header is string title
            ? new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : header,
        Content = content,
        IsExpanded = expanded,
        Margin = new Avalonia.Thickness(8, 6, 8, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

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
}
