using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// The Plugin Manager (Edit ▸ Plugins…, PLAN.md step 58): a user-facing view over the managed plugin host — one
/// row per discovered plugin with its name, version, source path, contributed effects and status, a per-row
/// Enable toggle and (for user-installed plugins) Uninstall, plus a toolbar to Install a plugin, Rescan, and
/// Open the plugins folder. Modelled on DAW plugin managers (REAPER's browser + rescan, Ableton's per-plugin
/// enable) rather than the filesystem-only NLE approach — the same conventions the plan calls for.
/// </summary>
/// <remarks>
/// A thin view over <see cref="PluginManager"/>: it owns no plugin logic and rebuilds its (short) row list after
/// each action rather than diffing. Every enable/disable/install/uninstall runs on the UI thread and is
/// immediately durable (the manager persists the disabled list); <see cref="_onChanged"/> lets the owner refresh
/// the preview so a re-registered / removed effect shows at once. Built in code against the shared dark
/// <see cref="Palette"/> like the other dialogs; the look rests on manual verification (the App is a UI WinExe).
/// Bundled (<c>&lt;exe&gt;/Plugins</c>) rows can be enabled/disabled — an in-app choice, not a filesystem change —
/// but not uninstalled, since their files are read-only shipped content.
/// </remarks>
internal sealed class PluginManagerWindow : Window
{
    private readonly PluginManager _manager;
    private readonly Action _onChanged;

    private readonly StackPanel _list;
    private readonly TextBlock _emptyHint;
    private readonly Button _installButton, _rescanButton, _openFolderButton;

    public PluginManagerWindow(PluginManager manager, Action onChanged)
    {
        _manager = manager;
        _onChanged = onChanged;

        Title = "Plugins";
        Icon = AppIcon.Window;
        Width = 640;
        Height = 520;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.WindowBgBrush;

        _installButton = ToolButton("Install Plugin…", accent: true);
        _rescanButton = ToolButton("Rescan", accent: false);
        _openFolderButton = ToolButton("Open Plugins Folder", accent: false);
        _installButton.Click += (_, _) => _ = InstallAsync();
        _rescanButton.Click += (_, _) => { _manager.Rescan(); Rebuild(); _onChanged(); };
        _openFolderButton.Click += (_, _) => OpenPluginsFolder();
        _openFolderButton.IsEnabled = _manager.UserPluginDirectory is not null;

        var header = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16, 14, 16, 10),
            Children =
            {
                new TextBlock
                {
                    Text = "Effect plugins extend Sprocket with extra video and audio effects. Disable one to stop "
                        + "loading it, or install a plugin assembly (.dll) into your user plugins folder.",
                    Foreground = Palette.MutedTextBrush,
                    FontSize = Typography.Body,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _installButton, _rescanButton, _openFolderButton },
                },
            },
        };

        _emptyHint = new TextBlock
        {
            Text = "No plugins found. Install a plugin, or drop its .dll into your user plugins folder and Rescan.",
            Foreground = Palette.MutedTextBrush,
            FontSize = Typography.Body,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4, 16, 0),
        };

        _list = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 12) };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _emptyHint, _list } },
        };

        Content = new DockPanel { Children = { header.DockTop(), scroller } };
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _emptyHint.IsVisible = _manager.Entries.Count == 0;

        foreach (PluginEntry entry in _manager.Entries)
            _list.Children.Add(BuildRow(entry));
    }

    private Border BuildRow(PluginEntry entry)
    {
        var title = new TextBlock
        {
            Text = entry.Version is { Length: > 0 } v ? $"{entry.Name}  ·  v{v}" : entry.Name,
            Foreground = Palette.TextBrush,
            FontSize = Typography.Emphasis,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        (string statusText, IBrush statusColor) = entry.Status switch
        {
            PluginStatus.Enabled => ("Enabled", Palette.GoodBrush),
            PluginStatus.Disabled => ("Disabled", Palette.MutedTextBrush),
            _ => ("Error", Palette.BadBrush),
        };
        var status = new TextBlock
        {
            Text = statusText + (entry.IsUserPlugin ? "" : " · Bundled"),
            Foreground = statusColor,
            FontSize = Typography.Body,
        };

        var path = new TextBlock
        {
            Text = entry.AssemblyPath,
            Foreground = Palette.FaintTextBrush,
            FontSize = Typography.Caption,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The effects it contributes when enabled, or the captured error/warning message otherwise.
        string detailText = entry.Status == PluginStatus.Error && entry.Message is { Length: > 0 }
            ? entry.Message
            : entry.Effects.Count > 0
                ? "Effects: " + string.Join(", ", entry.Effects.Select(e => e.DisplayName))
                : entry.Message ?? "";
        var detail = new TextBlock
        {
            Text = detailText,
            Foreground = entry.Status == PluginStatus.Error ? Palette.BadBrush : Palette.MutedTextBrush,
            FontSize = Typography.Body,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = detailText.Length > 0,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var info = new StackPanel { Children = { title, status, path, detail } };

        var enableToggle = new CheckBox
        {
            Content = "Enabled",
            Foreground = Palette.TextBrush,
            IsChecked = entry.Status != PluginStatus.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        enableToggle.IsCheckedChanged += (_, _) =>
        {
            bool enable = enableToggle.IsChecked == true;
            if (enable == (entry.Status != PluginStatus.Disabled))
                return; // no real change (e.g. our own Rebuild set the box)
            _manager.SetEnabled(entry, enable);
            Rebuild();
            _onChanged();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { enableToggle },
        };
        if (entry.IsUserPlugin)
        {
            Button uninstall = RowButton("Uninstall", Palette.MutedTextBrush);
            uninstall.Click += (_, _) => _ = UninstallAsync(entry);
            buttons.Children.Add(uninstall);
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 10),
        };
        grid.Children.Add(info);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = Palette.RaisedBgBrush,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    /// <summary>Install Plugin… — picks a .dll and copies it into the user plugins folder, then loads it.</summary>
    private async Task InstallAsync()
    {
        string sourcePath;
        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Install Plugin",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Plugin assembly") { Patterns = ["*.dll"] }, FilePickerFileTypes.All],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } picked)
                return;
            sourcePath = picked;
        }
        catch (Exception ex)
        {
            // Some Linux storage portals throw on cancel / when unavailable — a swallowed picker failure would
            // leave Install silently doing nothing (the InspectorPanel Browse handler guards the same way).
            await MessageDialog.Show(this, "Install Plugin", $"The file picker could not be opened: {ex.Message}");
            return;
        }

        string? error = _manager.Install(sourcePath);
        Rebuild();
        _onChanged();
        if (error is not null)
            await MessageDialog.Show(this, "Install Plugin", error);
    }

    /// <summary>Uninstall — confirms, then unloads + deletes a user-installed plugin's file.</summary>
    private async Task UninstallAsync(PluginEntry entry)
    {
        bool confirmed = await ConfirmDialog.Show(this, "Uninstall Plugin",
            $"Remove the plugin \"{entry.Name}\"?\n\nIts effects are unloaded and its file is deleted. Any project "
            + "using them will keep the clips but render those effects as pass-through until the plugin is reinstalled.",
            "Uninstall", "Cancel");
        if (!confirmed)
            return;

        string? error = _manager.Uninstall(entry);
        Rebuild();
        _onChanged();
        if (error is not null)
            await MessageDialog.Show(this, "Uninstall Plugin", error);
    }

    /// <summary>Opens the user plugins folder in the OS file manager, creating it first if needed.</summary>
    private void OpenPluginsFolder()
    {
        if (_manager.UserPluginDirectory is not { } dir)
            return;
        try
        {
            Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", [dir]) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", [dir]) { UseShellExecute = false });
        }
        catch { /* the file manager may be missing / locked down — opening it is a convenience */ }
    }

    private static Button ToolButton(string text, bool accent) => new()
    {
        Content = text,
        Padding = new Thickness(14, 5),
        Foreground = accent ? Brushes.White : Palette.TextBrush,
        Background = accent ? Palette.AccentBrush : Palette.PanelBgBrush,
        CornerRadius = new CornerRadius(5),
    };

    private static Button RowButton(string text, IBrush foreground) => new()
    {
        Content = text,
        Padding = new Thickness(10, 4),
        FontSize = Typography.Body,
        Foreground = foreground,
        Background = Palette.PanelBgBrush,
        CornerRadius = new CornerRadius(4),
    };
}
