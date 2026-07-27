using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sprocket.App.Controls;
using Sprocket.App.Inspector;
using Sprocket.Audio.Loudness;
using Sprocket.Core.Audio;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;

namespace Sprocket.App.Mixer;

/// <summary>
/// The Project panel's <b>Audio</b> tab brought to editorial completeness (PLAN.md step 30, UI.md §3.3): a mixer
/// with a live master loudness read-out (EBU R128 integrated / short-term / momentary + true peak + L/R channel
/// meters), a channel strip per audio track (gain fader, pan/balance, mute, solo), and loudness-normalization to a
/// chosen target at track and master scope. Every edit routes through the <see cref="EditHistory"/> so it is
/// undoable; the meters poll the audio engine's <see cref="AudioEngine.CurrentLoudness"/> only while this tab is on
/// screen. Each strip also carries its <b>insert chain</b> (PLAN.md step 31, the channel-strip insert
/// convention professional audio mixers use): the track's pre-fader inserts, plus a Sequence Bus strip and the master panel's inserts —
/// add / enable / reorder / remove here, deep parameter editing in the Inspector via
/// <see cref="InspectChainRequested"/>.
/// </summary>
public sealed class MixerView : UserControl
{
    // The fader's travel. The bottom is MixerFormat.SilenceFloorDb so the readout's "-∞ dB" sentinel and the
    // slider's minimum are the same value — typing "-inf" lands exactly on the floor.
    private const double GainMinDb = MixerFormat.SilenceFloorDb, GainMaxDb = 12;
    private const double PanMin = -1, PanMax = 1;
    private const double GainStepDb = 0.5, PanStep = 0.05;

    private Project? _project;
    private EditHistory? _history;
    private Func<LoudnessSnapshot>? _readLoudness;
    private Func<AudioTrack, LoudnessMeasurement>? _measureTrack;
    private Func<LoudnessMeasurement>? _measureMaster;

    private double _targetLufs = LoudnessNormalization.StreamingMinus14Lufs;

    /// <summary>The loudness target currently selected in the mixer, so other normalize actions (e.g. Clip ▸
    /// Normalize Audio) use the same target the user picked here (PLAN.md step 30).</summary>
    public double TargetLufs => _targetLufs;

    private readonly DispatcherTimer _timer;
    private bool _suppress;                 // guards programmatic widget updates from re-issuing commands
    private IDisposable? _dragScope;        // open coalescing scope for the active fader drag

    // Master read-out widgets.
    private readonly TextBlock _integratedText = Metric();
    private readonly TextBlock _shortTermText = Metric();
    private readonly TextBlock _momentaryText = Metric();
    private readonly TextBlock _truePeakText = Metric();
    private readonly MeterBar _meterL = new();
    private readonly MeterBar _meterR = new();
    private Slider _masterSlider = null!;
    private readonly TextBox _masterGainLabel = ValueField(62);

    private readonly StackPanel _strips = new() { Spacing = 6 };
    private readonly List<AudioTrack> _builtOrder = new();
    private readonly Dictionary<AudioTrack, StripWidgets> _stripWidgets = new();

    // Insert chains (PLAN.md step 31): the master panel's insert rows (rebuilt with the strips), and the
    // chain snapshot the last build reflected — compared on history changes so a fader drag's command
    // stream doesn't rebuild the strips mid-gesture, but any chain edit (add/remove/move/toggle) does.
    private readonly StackPanel _masterInserts = new() { Spacing = 2 };
    private List<object> _builtChainSignature = new();

    /// <summary>Raised when the user asks to edit a chain's effects (clicks an insert row / the chain's edit
    /// affordance) — the window shows it in the Inspector, where the full parameter/keyframe UI lives.</summary>
    public event Action<AudioChainTarget>? InspectChainRequested;

    public MixerView()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) }; // ~15 Hz while visible
        _timer.Tick += (_, _) => UpdateMeters();
        Content = BuildLayout();
        AttachedToVisualTree += (_, _) => { if (_readLoudness is not null) _timer.Start(); UpdateMeters(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Binds the mixer to a session. <paramref name="readLoudness"/> supplies the live master meter (null when the
    /// session has no audio device); the two <c>measure</c> delegates measure a scope's raw loudness for
    /// normalization (null hides the Normalize buttons). Called on attach and re-called on File ▸ New/Open.
    /// </summary>
    public void Attach(
        Project project, EditHistory history,
        Func<LoudnessSnapshot>? readLoudness,
        Func<AudioTrack, LoudnessMeasurement>? measureTrack,
        Func<LoudnessMeasurement>? measureMaster)
    {
        if (_history is not null) _history.Changed -= OnHistoryChanged;
        _project = project;
        _history = history;
        _readLoudness = readLoudness;
        _measureTrack = measureTrack;
        _measureMaster = measureMaster;
        _history.Changed += OnHistoryChanged;

        RebuildStrips();
        RefreshMaster();
        if (_readLoudness is not null && IsEffectivelyVisible) _timer.Start();
    }

    private void OnHistoryChanged()
    {
        // A gain/pan drag issues a stream of commands; only rebuild when the track set or an insert chain
        // actually changed, otherwise just refresh values so an in-progress fader isn't torn out from under
        // the pointer.
        if (_project is null) return;
        List<AudioTrack> now = _project.Timeline.AudioTracks.ToList();
        if (!now.SequenceEqual(_builtOrder) ||
            !AudioChainTarget.Signature(_project).SequenceEqual(_builtChainSignature))
            RebuildStrips();
        else
            RefreshValues();
        RefreshMaster();
    }

    // ── layout ────────────────────────────────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        var header = new TextBlock { Text = "Mixer", FontWeight = FontWeight.SemiBold, Foreground = Palette.TextBrush };

        var stripScroll = new ScrollViewer
        {
            Content = _strips,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var root = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        Control master = BuildMasterPanel();
        DockPanel.SetDock(master, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(master);
        root.Children.Add(stripScroll);
        return root;
    }

    private Control BuildMasterPanel()
    {
        _integratedText.FontSize = 20;
        _integratedText.FontWeight = FontWeight.SemiBold;

        var numbers = new StackPanel { Spacing = 2 };
        numbers.Children.Add(Row("Integrated", _integratedText));
        numbers.Children.Add(Row("Short-term", _shortTermText));
        numbers.Children.Add(Row("Momentary", _momentaryText));
        numbers.Children.Add(Row("True peak", _truePeakText));

        var meters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        meters.Children.Add(LabeledMeter("L", _meterL));
        meters.Children.Add(LabeledMeter("R", _meterR));

        _masterSlider = Fader();
        _masterSlider.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        _masterSlider.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        _masterSlider.ValueChanged += (_, e) =>
        {
            if (_suppress || _project is null || _history is null) return;
            _history.Execute(SetPropertyCommand<double>.Create(
                "Master gain", () => _project.Settings.MasterGainDb, v => _project.Settings.MasterGainDb = v,
                e.NewValue, mergeKey: "master.gain"));
            _masterGainLabel.Text = MixerFormat.GainDbLabel(e.NewValue);
        };
        WireMasterGainField();

        var fader = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Bottom };
        fader.Children.Add(new TextBlock { Text = "Master", Foreground = Palette.MutedTextBrush, FontSize = 11 });
        fader.Children.Add(_masterSlider);
        fader.Children.Add(_masterGainLabel);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        right.Children.Add(meters);
        right.Children.Add(fader);

        var normalize = new Button { Content = "Normalize", Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Bottom };
        normalize.Click += (_, _) => NormalizeMaster();
        right.Children.Add(normalize);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(numbers, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(numbers);
        grid.Children.Add(right);

        var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        targetRow.Children.Add(new TextBlock { Text = "Normalize to", Foreground = Palette.MutedTextBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        targetRow.Children.Add(BuildTargetPicker());

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 8) };
        panel.Children.Add(grid);
        panel.Children.Add(targetRow);
        panel.Children.Add(_masterInserts); // populated by RebuildStrips once a project is attached

        return new Border
        {
            Background = Palette.PanelBgBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 6, 0, 8),
            Child = panel,
        };
    }

    private ComboBox BuildTargetPicker()
    {
        var picker = new ComboBox { MinWidth = 150 };
        (string label, double lufs)[] targets =
        [
            ("-14 LUFS (streaming)", LoudnessNormalization.StreamingMinus14Lufs),
            ("-16 LUFS", LoudnessNormalization.StreamingMinus16Lufs),
            ("-23 LUFS (broadcast)", LoudnessNormalization.BroadcastMinus23Lufs),
        ];
        foreach ((string label, double lufs) in targets)
            picker.Items.Add(new ComboBoxItem { Content = label, Tag = lufs });
        picker.SelectedIndex = 0;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: double lufs })
                _targetLufs = lufs;
        };
        return picker;
    }

    // ── channel strips ─────────────────────────────────────────────────────────────────────────────────

    private void RebuildStrips()
    {
        _strips.Children.Clear();
        _stripWidgets.Clear();
        _builtOrder.Clear();
        _masterInserts.Children.Clear();
        if (_project is null) return;
        _builtChainSignature = AudioChainTarget.Signature(_project);

        foreach (AudioTrack track in _project.Timeline.AudioTracks)
        {
            _builtOrder.Add(track);
            _strips.Children.Add(BuildStrip(track));
        }
        if (_builtOrder.Count == 0)
            _strips.Children.Add(new TextBlock
            {
                Text = "No audio tracks. Add one with + Track.",
                Foreground = Palette.FaintTextBrush,
                Margin = new Thickness(2, 8),
            });

        // The sequence output bus reads as one more strip after the tracks (signal flows tracks → bus →
        // master), and the master chain's inserts sit inside the master panel above (PLAN.md step 31).
        _strips.Children.Add(BuildBusStrip());
        _masterInserts.Children.Add(BuildInsertsBlock(AudioChainTarget.ForMaster(_project.Settings)));
    }

    /// <summary>The Sequence Bus pseudo-strip: no fader of its own (the sequence mix is shaped by the track
    /// faders below and the master above) — it exists to carry the bus insert chain.</summary>
    private Control BuildBusStrip()
    {
        var name = new TextBlock
        {
            Text = "Sequence Bus",
            Foreground = Palette.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(name, "The sequence's output bus — its inserts process the summed sequence mix before the project master chain.");

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(name);
        body.Children.Add(BuildInsertsBlock(
            AudioChainTarget.ForSequenceBus(_project!.Timeline, _project.ActiveSequence.Name)));

        return new Border
        {
            Background = Palette.RaisedBgBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Child = body,
        };
    }

    private Control BuildStrip(AudioTrack track)
    {
        var name = new TextBlock
        {
            Text = string.IsNullOrEmpty(track.Name) ? "Audio" : track.Name,
            Foreground = Palette.TextBrush, Width = 90, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Slider pan = Balance();
        pan.Value = track.Pan;
        WirePanFader(pan, track);
        // 44px, not the label's old 34: the field has to fit a typed "-100" plus the box's padding.
        TextBox panLabel = ValueField(44);
        panLabel.Text = MixerFormat.PanLabel(track.Pan);
        WirePanField(panLabel, pan, track);

        Slider gain = Fader(horizontal: true);
        gain.Value = track.GainDb;
        WireGainFader(gain, track);
        TextBox gainLabel = ValueField(62);
        gainLabel.Text = MixerFormat.GainDbLabel(track.GainDb);
        WireGainField(gainLabel, gain, track);

        var mute = ToggleBox("M", track.Muted);
        mute.Click += (_, _) =>
        {
            if (_suppress) return;
            Execute(SetPropertyCommand<bool>.Create("Toggle mute", () => track.Muted, v => track.Muted = v, mute.IsChecked == true));
        };
        var solo = ToggleBox("S", track.Solo);
        solo.Click += (_, _) =>
        {
            if (_suppress) return;
            Execute(SetPropertyCommand<bool>.Create("Toggle solo", () => track.Solo, v => track.Solo = v, solo.IsChecked == true));
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(name);
        row.Children.Add(new TextBlock { Text = "Pan", Foreground = Palette.MutedTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(pan);
        row.Children.Add(panLabel);
        row.Children.Add(new TextBlock { Text = "Gain", Foreground = Palette.MutedTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(gain);
        row.Children.Add(gainLabel);
        row.Children.Add(mute);
        row.Children.Add(solo);

        Button? normalize = null;
        if (_measureTrack is not null)
        {
            normalize = new Button { Content = "Norm", Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center };
            normalize.Click += (_, _) => NormalizeTrack(track);
            row.Children.Add(normalize);
        }

        _stripWidgets[track] = new StripWidgets(gain, gainLabel, pan, panLabel, mute, solo);

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(row);
        body.Children.Add(BuildInsertsBlock(AudioChainTarget.ForTrack(track)));

        return new Border
        {
            Background = Palette.RaisedBgBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Child = body,
        };
    }

    // ── insert chains (PLAN.md step 31) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A strip's insert-chain block, following the standard audio-mixer per-strip effect-slot convention: an
    /// "Inserts" header with a "+" flyout of the catalog's audio effects, then one compact row per effect —
    /// enable LED, name (click opens the chain in the Inspector for parameter editing), and remove. Reorder
    /// via the row's context menu (Move Up / Move Down, the step-51 fallback affordance). All edits are
    /// undoable chain commands.
    /// </summary>
    private Control BuildInsertsBlock(AudioChainTarget target)
    {
        var block = new StackPanel { Spacing = 2 };

        var add = new Button
        {
            Content = "+",
            FontSize = 11,
            Width = 22,
            Padding = new Thickness(0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(add, "Add an insert effect");
        var items = new List<MenuItem>();
        foreach (EffectDescriptor descriptor in EffectRelevance.ForAudioChain())
        {
            var item = new MenuItem { Header = descriptor.DisplayName, FontSize = 12 };
            item.Click += (_, _) => Execute(new AddChainEffectCommand(target.Chain, descriptor.CreateInstance()));
            items.Add(item);
        }
        add.Flyout = new MenuFlyout { ItemsSource = items };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = "Inserts",
            Foreground = Palette.MutedTextBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(add);
        block.Children.Add(header);

        for (int i = 0; i < target.Chain.Count; i++)
            block.Children.Add(BuildInsertRow(target, target.Chain[i], i));
        return block;
    }

    private Control BuildInsertRow(AudioChainTarget target, EffectInstance effect, int index)
    {
        string title = EffectCatalog.Find(effect.EffectTypeId)?.DisplayName ?? effect.EffectTypeId;

        // Enable LED (the Inspector's audio-rack convention: green = active, grey = bypassed).
        var led = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = effect.Enabled ? Palette.GoodBrush : Palette.EdgeBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toggle = new Button
        {
            Content = led,
            Padding = new Thickness(4, 4),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(toggle, effect.Enabled ? "Disable effect" : "Enable effect");
        toggle.Click += (_, _) => Execute(new SetEffectEnabledCommand(effect, !effect.Enabled));

        var name = new Button
        {
            Content = title,
            FontSize = 11,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = effect.Enabled ? Palette.TextBrush : Palette.FaintTextBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(name, "Edit parameters in the Inspector");
        name.Click += (_, _) => InspectChainRequested?.Invoke(target);

        var remove = new Button
        {
            Content = "×",
            FontSize = 11,
            Padding = new Thickness(5, 1),
            Background = Brushes.Transparent,
            Foreground = Palette.FaintTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, "Remove effect");
        remove.Click += (_, _) => Execute(new RemoveChainEffectCommand(target.Chain, effect));

        int count = target.Chain.Count;
        var up = new MenuItem { Header = "Move Up", FontSize = 12, IsEnabled = index > 0 };
        up.Click += (_, _) => MoveInsert(target, index, EffectReorder.StepIndex(index, count, -1));
        var down = new MenuItem { Header = "Move Down", FontSize = 12, IsEnabled = index < count - 1 };
        down.Click += (_, _) => MoveInsert(target, index, EffectReorder.StepIndex(index, count, +1));

        var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(8, 0, 0, 0) };
        row.ContextMenu = new ContextMenu { ItemsSource = new[] { up, down } };
        DockPanel.SetDock(toggle, Dock.Left);
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(remove);
        row.Children.Add(name);
        return row;
    }

    /// <summary>A same-index move is skipped so no-ops don't pollute the undo history (PLAN.md step 51).</summary>
    private void MoveInsert(AudioChainTarget target, int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || fromIndex >= target.Chain.Count)
            return;
        Execute(new MoveChainEffectCommand(target.Chain, target.Chain[fromIndex], toIndex));
    }

    private void WireGainFader(Slider gain, AudioTrack track)
    {
        gain.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        gain.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        gain.ValueChanged += (_, e) =>
        {
            if (_suppress || _history is null) return;
            _history.Execute(SetPropertyCommand<double>.Create(
                "Track gain", () => track.GainDb, v => track.GainDb = v, e.NewValue, mergeKey: (track, "GainDb")));
            if (_stripWidgets.TryGetValue(track, out StripWidgets? w)) w.GainLabel.Text = MixerFormat.GainDbLabel(e.NewValue);
        };
    }

    private void WirePanFader(Slider pan, AudioTrack track)
    {
        pan.AddHandler(PointerPressedEvent, (_, _) => BeginDrag(), RoutingStrategies.Tunnel);
        pan.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
        pan.ValueChanged += (_, e) =>
        {
            if (_suppress || _history is null) return;
            _history.Execute(SetPropertyCommand<double>.Create(
                "Track pan", () => track.Pan, v => track.Pan = v, e.NewValue, mergeKey: (track, "Pan")));
            if (_stripWidgets.TryGetValue(track, out StripWidgets? w)) w.PanLabel.Text = MixerFormat.PanLabel(e.NewValue);
        };
    }

    /// <summary>The master fader's counterpart to <see cref="WireGainField"/>, over
    /// <see cref="ProjectSettings.MasterGainDb"/>.</summary>
    private void WireMasterGainField()
    {
        void Commit(double db)
        {
            if (_project is null || _history is null)
                return;
            db = Math.Clamp(db, GainMinDb, GainMaxDb);
            _history.Execute(SetPropertyCommand<double>.Create(
                "Master gain", () => _project.Settings.MasterGainDb, v => _project.Settings.MasterGainDb = v,
                db, mergeKey: "master.gain"));
            _suppress = true;
            try
            {
                _masterSlider.Value = db;
                _masterGainLabel.Text = MixerFormat.GainDbLabel(db);
            }
            finally { _suppress = false; }
        }

        void CommitText()
        {
            if (_suppress || _project is null)
                return;
            if (!MixerFormat.TryParseGainDb(_masterGainLabel.Text, out double db))
            {
                _masterGainLabel.Text = MixerFormat.GainDbLabel(_project.Settings.MasterGainDb);
                return;
            }
            Commit(db);
        }

        _masterGainLabel.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitText(); e.Handled = true; } };
        _masterGainLabel.LostFocus += (_, _) => CommitText();
        DragNumber.Attach(_masterGainLabel, new DragNumberOptions(
            Get: () => _project?.Settings.MasterGainDb ?? 0.0, Set: (v, _) => Commit(v),
            Min: GainMinDb, Max: GainMaxDb, Step: GainStepDb,
            BeginDrag: BeginDrag, EndDrag: EndDrag));
        ToolTip.SetTip(_masterGainLabel, "Drag to scrub (Shift = coarse, Ctrl = fine) · click to type a dB value");
    }

    /// <summary>
    /// Makes a strip's gain read-out an input: type an exact dB value (Enter or blur commits) or drag over it
    /// to scrub, the two numeric gestures every professional mixer offers on its level field. A typed value is
    /// one discrete undo entry; a scrub coalesces to one for the whole drag, like the fader itself. An
    /// unparseable entry reverts rather than committing, so a typo can't silently mute a track.
    /// </summary>
    private void WireGainField(TextBox field, Slider fader, AudioTrack track)
    {
        // Whether an edit collapses into the previous undo entry is decided by the open coalescing scope
        // (BeginDrag/EndDrag) exactly as it is for the fader, so the commit itself is the same either way.
        void Commit(double db)
        {
            if (_history is null)
                return;
            db = Math.Clamp(db, GainMinDb, GainMaxDb);
            _history.Execute(SetPropertyCommand<double>.Create(
                "Track gain", () => track.GainDb, v => track.GainDb = v, db, mergeKey: (track, "GainDb")));
            _suppress = true;
            try
            {
                fader.Value = db;
                field.Text = MixerFormat.GainDbLabel(db);
            }
            finally { _suppress = false; }
        }

        void CommitText()
        {
            if (_suppress)
                return;
            if (!MixerFormat.TryParseGainDb(field.Text, out double db))
            {
                field.Text = MixerFormat.GainDbLabel(track.GainDb); // revert to the model value
                return;
            }
            Commit(db);
        }

        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitText(); e.Handled = true; } };
        field.LostFocus += (_, _) => CommitText();
        DragNumber.Attach(field, new DragNumberOptions(
            Get: () => track.GainDb, Set: (v, _) => Commit(v),
            Min: GainMinDb, Max: GainMaxDb, Step: GainStepDb,
            BeginDrag: BeginDrag, EndDrag: EndDrag));
        ToolTip.SetTip(field, "Drag to scrub (Shift = coarse, Ctrl = fine) · click to type a dB value");
    }

    /// <summary>The pan counterpart of <see cref="WireGainField"/>: accepts <c>"C"</c> / <c>"L50"</c> /
    /// <c>"R25"</c> or a bare -100..100, and scrubs over the same [-1, 1] the balance slider spans.</summary>
    private void WirePanField(TextBox field, Slider fader, AudioTrack track)
    {
        void Commit(double pan)
        {
            if (_history is null)
                return;
            pan = Math.Clamp(pan, PanMin, PanMax);
            _history.Execute(SetPropertyCommand<double>.Create(
                "Track pan", () => track.Pan, v => track.Pan = v, pan, mergeKey: (track, "Pan")));
            _suppress = true;
            try
            {
                fader.Value = pan;
                field.Text = MixerFormat.PanLabel(pan);
            }
            finally { _suppress = false; }
        }

        void CommitText()
        {
            if (_suppress)
                return;
            if (!MixerFormat.TryParsePan(field.Text, out double pan))
            {
                field.Text = MixerFormat.PanLabel(track.Pan);
                return;
            }
            Commit(pan);
        }

        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitText(); e.Handled = true; } };
        field.LostFocus += (_, _) => CommitText();
        DragNumber.Attach(field, new DragNumberOptions(
            Get: () => track.Pan, Set: (v, _) => Commit(v),
            Min: PanMin, Max: PanMax, Step: PanStep,
            BeginDrag: BeginDrag, EndDrag: EndDrag));
        ToolTip.SetTip(field, "Drag to scrub · click to type C, L50, R25 or -100..100");
    }

    private void RefreshValues()
    {
        _suppress = true;
        try
        {
            foreach ((AudioTrack track, StripWidgets w) in _stripWidgets)
            {
                w.Gain.Value = track.GainDb;
                w.GainLabel.Text = MixerFormat.GainDbLabel(track.GainDb);
                w.Pan.Value = track.Pan;
                w.PanLabel.Text = MixerFormat.PanLabel(track.Pan);
                w.Mute.IsChecked = track.Muted;
                w.Solo.IsChecked = track.Solo;
            }
        }
        finally { _suppress = false; }
    }

    private void RefreshMaster()
    {
        if (_project is null) return;
        _suppress = true;
        try
        {
            _masterSlider.Value = _project.Settings.MasterGainDb;
            _masterGainLabel.Text = MixerFormat.GainDbLabel(_project.Settings.MasterGainDb);
        }
        finally { _suppress = false; }
    }

    // ── normalization ────────────────────────────────────────────────────────────────────────────────

    private void NormalizeTrack(AudioTrack track)
    {
        if (_measureTrack is null || _history is null) return;
        LoudnessMeasurement m = _measureTrack(track);
        double gain = LoudnessNormalization.ComputeGainDb(m.IntegratedLufs, m.TruePeakDbtp, _targetLufs);
        if (double.IsNegativeInfinity(m.IntegratedLufs)) return; // silent track: nothing to normalize
        _history.Execute(SetPropertyCommand<double>.Create(
            $"Normalize {track.Name}", () => track.GainDb, v => track.GainDb = v, gain));
    }

    private void NormalizeMaster()
    {
        if (_measureMaster is null || _project is null || _history is null) return;
        LoudnessMeasurement m = _measureMaster();
        if (double.IsNegativeInfinity(m.IntegratedLufs)) return;
        double gain = LoudnessNormalization.ComputeGainDb(m.IntegratedLufs, m.TruePeakDbtp, _targetLufs);
        _history.Execute(SetPropertyCommand<double>.Create(
            "Normalize master", () => _project.Settings.MasterGainDb, v => _project.Settings.MasterGainDb = v, gain));
    }

    // ── meters ────────────────────────────────────────────────────────────────────────────────────────

    private void UpdateMeters()
    {
        if (_readLoudness is null) return;
        LoudnessSnapshot s = _readLoudness();
        _integratedText.Text = MixerFormat.LufsLabel(s.IntegratedLufs);
        _shortTermText.Text = MixerFormat.LufsLabel(s.ShortTermLufs);
        _momentaryText.Text = MixerFormat.LufsLabel(s.MomentaryLufs);
        _truePeakText.Text = MixerFormat.DbtpLabel(s.TruePeakDbtp);
        _meterL.SetLevel(MixerFormat.MeterFillFraction(s.PeakDbLeft));
        _meterR.SetLevel(MixerFormat.MeterFillFraction(s.PeakDbRight));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

    private void BeginDrag() { _dragScope ??= _history?.BeginCoalescing(); }
    private void EndDrag() { _dragScope?.Dispose(); _dragScope = null; }
    private void Execute(IEditCommand command) => _history?.Execute(command);

    private static Slider Fader(bool horizontal = true) => new()
    {
        Minimum = GainMinDb, Maximum = GainMaxDb, Value = 0,
        Width = horizontal ? 150 : double.NaN,
        Height = horizontal ? double.NaN : 120,
        Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
        VerticalAlignment = VerticalAlignment.Center,
        SmallChange = 0.5, LargeChange = 3,
    };

    private static Slider Balance() => new()
    {
        Minimum = -1, Maximum = 1, Value = 0, Width = 80,
        VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.05, LargeChange = 0.2,
    };

    private static TextBlock Metric() => new() { Foreground = Palette.TextBrush, FontFamily = new FontFamily("Consolas, monospace") };

    /// <summary>
    /// The strip's editable value read-out: a compact <see cref="TextBox"/> rather than a label, so a level or
    /// balance can be typed exactly instead of only dragged — the click-and-type dB field every professional
    /// mixer has. Styled like the Inspector's numeric box (STYLE_GUIDE.md: PanelBg fill, InputEdge border,
    /// which is the pairing that stays visible on a card).
    /// </summary>
    private static TextBox ValueField(double width) => new()
    {
        Width = width,
        Foreground = Palette.MutedTextBrush,
        FontSize = 11,
        // Defeat the Fluent theme's 32px MinHeight so the field matches the fader's height.
        MinHeight = 22,
        Height = 22,
        Padding = new Thickness(6, 2),
        Background = Palette.PanelBgBrush,
        BorderBrush = Palette.InputEdgeBrush,
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static Control Row(string label, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, Foreground = Palette.MutedTextBrush, Width = 84, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(value);
        return row;
    }

    private static Control LabeledMeter(string label, MeterBar bar)
    {
        var col = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(bar);
        col.Children.Add(new TextBlock { Text = label, Foreground = Palette.MutedTextBrush, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
        return col;
    }

    private static ToggleButton ToggleBox(string glyph, bool on) => new()
    {
        Content = glyph, IsChecked = on, Width = 26, Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private sealed record StripWidgets(Slider Gain, TextBox GainLabel, Slider Pan, TextBox PanLabel, ToggleButton Mute, ToggleButton Solo);

    /// <summary>A vertical peak meter: an instantaneous fill (green→amber→red) plus a slowly-decaying peak-hold line.</summary>
    private sealed class MeterBar : Control
    {
        private double _level;
        private double _peakHold;

        public MeterBar() { Width = 14; Height = 120; }

        public void SetLevel(double level)
        {
            _level = Math.Clamp(level, 0, 1);
            _peakHold = Math.Max(_level, _peakHold - 0.02); // ~3 s fall from full
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            ctx.FillRectangle(Palette.WindowBgBrush, new Rect(0, 0, w, h), 2);
            double fill = _level * h;
            if (fill > 0)
                ctx.FillRectangle(BrushFor(_level), new Rect(0, h - fill, w, fill), 2);
            if (_peakHold > 0)
            {
                double y = h - _peakHold * h;
                ctx.DrawLine(new Pen(Palette.TextBrush, 1), new Point(0, y), new Point(w, y));
            }
        }

        private static IBrush BrushFor(double level) =>
            level > 0.95 ? Palette.BadBrush : level > 0.8 ? Palette.WarnBrush : Palette.GoodBrush;
    }
}
