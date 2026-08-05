using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sprocket.App.Proxy;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// The Proxy window (View ▸ Proxy, PLAN.md step 18): a live view of the whole proxy pipeline — which source is
/// building, how far along it is and its ETA, what each source's proxy state and on-disk size are — plus the
/// controls that used to have no UI at all: the on/off toggle, the resolution tier, pause/resume, and per-file or
/// wholesale deletion.
/// </summary>
/// <remarks>
/// <para>Modelled on Final Cut Pro's <b>Background Tasks</b> window (per-task progress while you keep editing) and
/// its delete-generated-media commands, plus DaVinci Resolve's per-asset proxy status and per-project tier. The
/// window is a thin view over <see cref="ProxyService"/> — it owns no proxy logic; it subscribes to
/// <see cref="ProxyService.ProgressChanged"/> (which fires on the worker thread) and marshals refreshes onto the UI
/// thread, exactly as <see cref="ExportQueueWindow"/> does. Rows are diffed by <see cref="MediaRefId"/> and updated
/// in place so a progress tick doesn't rebuild the visual tree.</para>
/// <para>The two settings are <em>not</em> written here: they are handed out to the composition root as callbacks so
/// they route through <see cref="ProxySettingsOps"/> + the edit history (undoable, and marking the document dirty so
/// they actually persist). Built in code against the shared dark <see cref="Palette"/> like the other dialogs; the
/// look rests on manual verification (the App is a UI-bound WinExe).</para>
/// <para><b>Scope:</b> everything here concerns the <b>Program</b> monitor. The Source monitor deliberately opens
/// originals (proxying it is deferred, PLAN.md step 18), so a Source preview not switching is not a regression.</para>
/// </remarks>
internal sealed class ProxyStatusWindow : Window
{
    private readonly ProxyService _proxy;
    private readonly Project _project;
    private readonly Action<bool> _setEnabled;
    private readonly Action<ProxyTier> _setTier;

    private readonly TextBlock _stateLabel, _summary;
    private readonly CheckBox _enableToggle;
    private readonly ComboBox _tierBox;
    private readonly Button _pauseButton, _rebuildButton, _deleteAllButton;

    private readonly TextBlock _buildingName, _buildingDetail;
    private readonly ProgressBar _buildingBar;
    private readonly Border _buildingPanel;

    private readonly StackPanel _list;
    private readonly TextBlock _emptyHint;
    private readonly List<MediaRefId> _renderedOrder = new();
    private readonly Dictionary<MediaRefId, AssetRow> _rows = new();

    // Suppresses the control-changed handlers while Refresh writes the controls' state back from the service.
    private bool _syncing;

    // ETA is derived from progress velocity across refreshes — the same delta technique PlaybackStatsOverlay uses
    // for its rates. Re-seeded whenever the building source changes or its progress goes backwards (a restart).
    private MediaRefId? _etaId;
    private long _etaSeedMs;
    private double _etaSeedProgress;

    public ProxyStatusWindow(ProxyService proxy, Project project, Action<bool> setEnabled, Action<ProxyTier> setTier)
    {
        _proxy = proxy;
        _project = project;
        _setEnabled = setEnabled;
        _setTier = setTier;

        Title = "Proxy";
        Icon = AppIcon.Window;
        Width = 600;
        Height = 480;
        MinWidth = 460;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.WindowBgBrush;

        _stateLabel = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _summary = new TextBlock { FontSize = 11, Foreground = Palette.FaintTextBrush };

        _enableToggle = new CheckBox { Content = "Use proxies", Foreground = Palette.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        _enableToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing)
                _setEnabled(_enableToggle.IsChecked == true);
        };

        _tierBox = new ComboBox
        {
            ItemsSource = Enum.GetValues<ProxyTier>().Select(t => (object)ProxySettingsOps.TierLabel(t)).ToList(),
            MinWidth = 170,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _tierBox.SelectionChanged += (_, _) => OnTierSelected();

        _pauseButton = ToolButton("Pause", accent: false);
        _rebuildButton = ToolButton("Rebuild All", accent: true);
        _deleteAllButton = ToolButton("Delete All Proxies", accent: false);
        _pauseButton.Click += (_, _) => _proxy.SetPaused(!_proxy.Paused);
        _rebuildButton.Click += (_, _) => _proxy.RebuildAll();
        _rebuildButton.SetValue(ToolTip.TipProperty,
            "Builds every missing or failed proxy. Ready proxies are left alone — use Delete All Proxies first to re-encode those.");
        _deleteAllButton.Click += (_, _) => _ = DeleteAllAsync();

        var header = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16, 14, 16, 10),
            Children =
            {
                new StackPanel { Children = { _stateLabel, _summary } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        _enableToggle,
                        new TextBlock { Text = "Resolution", Foreground = Palette.MutedTextBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                        _tierBox,
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _pauseButton, _rebuildButton, _deleteAllButton },
                },
            },
        };

        _buildingName = new TextBlock { Foreground = Palette.TextBrush, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        _buildingDetail = new TextBlock { Foreground = Palette.AccentBrush, FontSize = 12 };
        _buildingBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        _buildingPanel = new Border
        {
            Background = Palette.SectionBgBrush,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(16, 0, 16, 12),
            Child = new StackPanel
            {
                Margin = new Thickness(12, 10),
                Children = { _buildingName, _buildingDetail, _buildingBar },
            },
        };

        _list = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 12) };
        _emptyHint = new TextBlock
        {
            Text = "No media in this project needs a proxy. Sources at 1080p or smaller preview in real time on the original.",
            Foreground = Palette.MutedTextBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4, 16, 0),
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _emptyHint, _list } },
        };

        Content = new DockPanel
        {
            Children = { header.DockTop(), _buildingPanel.DockTop(), scroller },
        };

        _proxy.ProgressChanged += OnProxyChanged;
        Rebuild();
    }

    protected override void OnClosed(EventArgs e)
    {
        _proxy.ProgressChanged -= OnProxyChanged;
        base.OnClosed(e);
    }

    private void OnProxyChanged()
    {
        // ProgressChanged fires on the proxy worker thread; hop to the UI thread to touch controls.
        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>
    /// Applies a tier pick. Confirms first, because changing the tier re-keys the proxy cache and therefore
    /// rebuilds every proxy — the same warning DaVinci Resolve gives. Declining puts the dropdown back.
    /// </summary>
    private async void OnTierSelected()
    {
        if (_syncing || _tierBox.SelectedIndex < 0)
            return;
        var picked = (ProxyTier)_tierBox.SelectedIndex;
        if (picked == _proxy.Tier)
            return;

        bool confirmed = await ConfirmDialog.Show(this, "Change Proxy Resolution",
            $"Build proxies at {ProxySettingsOps.TierLabel(picked)}?\n\n" +
            "The resolution is part of each proxy's cache key, so existing proxies stop applying and rebuild in " +
            "the background. The preview falls back to originals until they finish.", "Change", "Cancel");
        if (!confirmed)
        {
            Refresh(); // put the dropdown back where the service still is
            return;
        }
        _setTier(picked);
        Refresh();
    }

    private async System.Threading.Tasks.Task DeleteAllAsync()
    {
        bool confirmed = await ConfirmDialog.Show(this, "Delete All Proxies",
            "Delete every cached proxy file?\n\nThe preview falls back to originals. Proxies rebuild on Rebuild " +
            "All, on the next project load, or when proxies are switched back on.", "Delete", "Cancel");
        if (confirmed)
            _proxy.DeleteAllProxies();
    }

    /// <summary>Rebuilds the row tree only when the tracked source set/order changed; otherwise updates in place.</summary>
    private void Refresh()
    {
        IReadOnlyList<ProxySnapshot> rows = _proxy.Snapshot();
        // Structural changes are a new/removed source (order) *or* a source crossing the NotNeeded line, which a
        // tier change can do — those get/lose a row rather than just new text.
        bool sameShape = rows.Count == _renderedOrder.Count;
        for (int i = 0; sameShape && i < rows.Count; i++)
        {
            sameShape = rows[i].Id == _renderedOrder[i]
                && _rows.ContainsKey(rows[i].Id) == (rows[i].State != ProxyState.NotNeeded);
        }

        if (!sameShape)
        {
            Rebuild();
            return; // Rebuild refreshes the header + building panel itself
        }

        foreach (ProxySnapshot row in rows)
            if (_rows.TryGetValue(row.Id, out AssetRow? view))
                view.Update(row, _proxy.Enabled);

        UpdateHeader(rows);
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();
        _renderedOrder.Clear();

        IReadOnlyList<ProxySnapshot> rows = _proxy.Snapshot();
        // Sources that can never want a proxy would only be noise here — the dialog is about proxy work.
        _emptyHint.IsVisible = rows.All(r => r.State == ProxyState.NotNeeded);

        foreach (ProxySnapshot row in rows)
        {
            _renderedOrder.Add(row.Id); // every tracked source, so the order diff stays aligned with Snapshot()
            if (row.State == ProxyState.NotNeeded)
                continue;
            MediaRefId id = row.Id;
            var view = new AssetRow(DisplayName(id), () => _proxy.DeleteProxy(id), () => _proxy.Generate(id));
            _rows[id] = view;
            _list.Children.Add(view.Root);
            view.Update(row, _proxy.Enabled);
        }

        UpdateHeader(rows);
    }

    private void UpdateHeader(IReadOnlyList<ProxySnapshot> rows)
    {
        _syncing = true;
        try
        {
            _enableToggle.IsChecked = _proxy.Enabled;
            _tierBox.SelectedIndex = (int)_proxy.Tier;
        }
        finally
        {
            _syncing = false;
        }

        (_stateLabel.Text, _stateLabel.Foreground) = (_proxy.Enabled, _proxy.Paused) switch
        {
            (false, _) => ("Proxies off — previewing originals", Palette.MutedTextBrush),
            (true, true) => ("Proxies on · generation paused", Palette.WarnBrush),
            _ => ("Proxies on", Palette.GoodBrush),
        };

        long totalSize = rows.Sum(r => r.SizeBytes);
        int ready = rows.Count(r => r.State == ProxyState.Ready);
        int pending = rows.Count(r => r.State is ProxyState.Queued or ProxyState.Building);
        int missing = rows.Count(r => r.State == ProxyState.NotGenerated);
        int failed = rows.Count(r => r.State == ProxyState.Failed);
        _summary.Text = string.Create(CultureInfo.InvariantCulture,
            $"{ready} ready · {pending} pending · {missing} not generated · {failed} failed  ·  " +
            $"{PreferencesFormat.Bytes(totalSize)} on disk");

        _pauseButton.Content = _proxy.Paused ? "Resume" : "Pause";
        _pauseButton.IsEnabled = _proxy.Enabled;
        _rebuildButton.IsEnabled = _proxy.Enabled && (missing > 0 || failed > 0);
        _deleteAllButton.IsEnabled = ready > 0;
        _tierBox.IsEnabled = true; // the tier is meaningful even while off — it decides what a later build targets

        UpdateBuildingPanel(rows);
    }

    private void UpdateBuildingPanel(IReadOnlyList<ProxySnapshot> rows)
    {
        ProxySnapshot? active = null;
        foreach (ProxySnapshot row in rows)
            if (row.State == ProxyState.Building)
                active = row;

        if (active is not { } building)
        {
            _buildingPanel.IsVisible = false;
            _etaId = null;
            return;
        }

        _buildingPanel.IsVisible = true;
        _buildingName.Text = DisplayName(building.Id);
        _buildingBar.Value = building.Progress;

        string eta = EtaText(building.Id, building.Progress);
        _buildingDetail.Text = string.Create(CultureInfo.InvariantCulture,
            $"Building {building.Target.Width}×{building.Target.Height} · {building.Progress * 100:0}%{eta}");
    }

    /// <summary>
    /// The " · about Ns left" suffix, derived from how much progress has accrued since the build was first seen
    /// (velocity × remaining fraction). Empty until there is enough movement to extrapolate from, so the readout
    /// never flickers a wild first estimate — and re-seeded when the source changes or its progress resets.
    /// </summary>
    private string EtaText(MediaRefId id, double progress)
    {
        long now = Environment.TickCount64;
        if (_etaId != id || progress < _etaSeedProgress)
        {
            _etaId = id;
            _etaSeedMs = now;
            _etaSeedProgress = progress;
            return "";
        }

        double elapsedSeconds = (now - _etaSeedMs) / 1000.0;
        double gained = progress - _etaSeedProgress;
        if (elapsedSeconds < 1.0 || gained < 0.01)
            return "";

        double remainingSeconds = (1.0 - progress) * (elapsedSeconds / gained);
        if (remainingSeconds is <= 0 or > 24 * 3600)
            return "";
        return remainingSeconds < 90
            ? string.Create(CultureInfo.InvariantCulture, $" · about {remainingSeconds:0}s left")
            : string.Create(CultureInfo.InvariantCulture, $" · about {remainingSeconds / 60:0} min left");
    }

    private string DisplayName(MediaRefId id) =>
        _project.MediaPool.Get(id) is { } media ? Path.GetFileName(media.AbsolutePath) : id.ToString();

    private static Button ToolButton(string text, bool accent) => new()
    {
        Content = text,
        Padding = new Thickness(14, 5),
        Foreground = accent ? Brushes.White : Palette.TextBrush,
        Background = accent ? Palette.AccentBrush : Palette.PanelBgBrush,
        CornerRadius = new CornerRadius(5),
    };

    /// <summary>One source's row: name, target resolution + state + on-disk size, and Delete / Generate.</summary>
    private sealed class AssetRow
    {
        public Border Root { get; }
        private readonly TextBlock _status;
        private readonly ProgressBar _bar;
        private readonly Button _delete, _generate;

        public AssetRow(string name, Func<bool> delete, Action generate)
        {
            var title = new TextBlock
            {
                Text = name,
                Foreground = Palette.TextBrush,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _status = new TextBlock { FontSize = 12 };
            _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0, 6, 0, 0) };

            _delete = RowButton("Delete", Palette.MutedTextBrush);
            _generate = RowButton("Generate", Palette.TextBrush);
            _delete.Click += (_, _) => delete();
            _generate.Click += (_, _) => generate();
            // Deletion is not persisted in the project, so the honest promise is that it comes back.
            _delete.SetValue(ToolTip.TipProperty,
                "Deletes the proxy file. It will rebuild on Generate, when proxies are switched back on, or on the next project load.");

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Children = { _generate, _delete },
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(12, 10),
            };
            grid.Children.Add(new StackPanel { Children = { title, _status, _bar } });
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            Root = new Border
            {
                Background = Palette.RaisedBgBrush,
                CornerRadius = new CornerRadius(6),
                BorderBrush = Palette.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = grid,
            };
        }

        public void Update(ProxySnapshot row, bool serviceEnabled)
        {
            _bar.Value = row.Progress;
            _bar.IsVisible = row.State == ProxyState.Building;

            string size = row.SizeBytes > 0 ? $" · {PreferencesFormat.Bytes(row.SizeBytes)}" : "";
            string target = row.Target.Width > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{row.Target.Width}×{row.Target.Height} · ")
                : "";

            // Same state→colour mapping the Export Queue's rows use (Palette health tokens).
            (_status.Text, _status.Foreground) = row.State switch
            {
                ProxyState.Ready => ($"{target}Ready{size}", Palette.GoodBrush),
                ProxyState.Building => (string.Create(CultureInfo.InvariantCulture, $"{target}Building… {row.Progress * 100:0}%"), Palette.AccentBrush),
                ProxyState.Queued => ($"{target}Queued", Palette.MutedTextBrush),
                ProxyState.NotGenerated => ($"{target}Not generated", Palette.MutedTextBrush),
                ProxyState.Failed => ($"{target}Failed — previewing the original", Palette.BadBrush),
                _ => ($"{target}{row.State}", Palette.MutedTextBrush),
            };

            _delete.IsEnabled = row.State == ProxyState.Ready;
            // Generating while proxies are off would build a file nothing would open, so the service no-ops there.
            _generate.IsEnabled = serviceEnabled && row.State is ProxyState.NotGenerated or ProxyState.Failed;
        }

        private static Button RowButton(string text, IBrush foreground) => new()
        {
            Content = text,
            Padding = new Thickness(10, 4),
            FontSize = 12,
            Foreground = foreground,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(4),
        };
    }
}
