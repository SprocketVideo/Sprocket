using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sprocket.App;

/// <summary>
/// The application Preferences dialog (PLAN.md step 38) — the user-scoped settings surface, distinct from
/// per-sequence <c>SequenceSettingsDialog</c> and the per-project <c>ProjectSettings</c>. Code-built against
/// the shared dark <see cref="Palette"/> like the other modals in <c>Dialogs.cs</c> (its own file — Dialogs.cs
/// is already large). Look/behaviour rests on manual verification; the decision logic (parsing, clamping,
/// byte formatting, token resolution, the MCP setup command) lives in the tested <see cref="PreferencesFormat"/>
/// and <see cref="UserSettingsStore"/>.
///
/// Cache "Clear" buttons act immediately (after a confirm) rather than on OK — the standard NLE behavior for
/// cache management — so Cancel does not undo them.
/// </summary>
internal static class PreferencesDialog
{
    /// <summary>
    /// Shows the dialog over <paramref name="current"/>. The cache callbacks report/clear the proxy cache
    /// (global) and the render cache (the current session's store — render files live beside the project).
    /// Returns the clamped new settings on OK, or <see langword="null"/> on cancel.
    /// </summary>
    public static Task<UserSettings?> Show(Window owner, UserSettings current,
        Func<long> proxyCacheSize, Func<int> clearProxyCache,
        Func<long> renderCacheSize, Action clearRenderCache)
    {
        // ── Caches ──────────────────────────────────────────────────────────────────────────────
        var proxySizeText = Muted(PreferencesFormat.Bytes(proxyCacheSize()));
        var renderSizeText = Muted(PreferencesFormat.Bytes(renderCacheSize()));
        Button clearProxy = SmallButton("Clear");
        Button clearRender = SmallButton("Clear");

        // ── Export metadata defaults ────────────────────────────────────────────────────────────
        TextBox title = MakeText(current.ExportTitle);
        TextBox author = MakeText(current.ExportAuthor);
        TextBox copyright = MakeText(current.ExportCopyright);
        TextBox comment = MakeText(current.ExportComment);

        // ── Autosave ────────────────────────────────────────────────────────────────────────────
        TextBox autosave = MakeText(current.AutosaveIntervalSeconds.ToString());
        autosave.Width = 80;
        autosave.HorizontalAlignment = HorizontalAlignment.Left;

        // ── Updates (PLAN.md steps 36 + 45) ─────────────────────────────────────────────────────
        var updatesEnabled = new CheckBox
        {
            Content = "Check for updates at startup",
            IsChecked = current.UpdateCheckEnabled,
            Foreground = Palette.TextBrush,
        };
        var updatesNote = new TextBlock
        {
            Text = "Sprocket tells you when a newer build is published; nothing downloads or installs " +
                   "without your say-so. Portable builds are pointed at the releases page instead.",
            Foreground = Palette.MutedTextBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };

        // ── AI control (MCP) ────────────────────────────────────────────────────────────────────
        string token = current.McpToken;
        var mcpEnabled = new CheckBox
        {
            Content = "Enable MCP server (loopback only)",
            IsChecked = current.McpEnabled,
            Foreground = Palette.TextBrush,
        };
        TextBox port = MakeText(current.McpPort.ToString());
        port.Width = 80;
        port.HorizontalAlignment = HorizontalAlignment.Left;
        var requireToken = new CheckBox
        {
            Content = "Require bearer token",
            IsChecked = current.McpRequireToken,
            Foreground = Palette.TextBrush,
        };
        var tokenText = new TextBox
        {
            Text = token,
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            FontSize = 11,
            Foreground = Palette.MutedTextBrush,
            Background = Palette.PanelBgBrush,
        };
        Button copySetup = SmallButton("Copy setup command");
        var mcpWarning = new TextBlock
        {
            Text = "While enabled, a local AI client can inspect and edit the open project.",
            Foreground = Palette.MutedTextBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };

        void RefreshMcpControls()
        {
            bool on = mcpEnabled.IsChecked == true;
            bool tokenOn = requireToken.IsChecked == true;
            port.IsEnabled = on;
            requireToken.IsEnabled = on;
            tokenText.IsVisible = on && tokenOn;
            copySetup.IsEnabled = on;
        }

        requireToken.IsCheckedChanged += (_, _) =>
        {
            token = PreferencesFormat.ResolveToken(requireToken.IsChecked == true, token, UserSettingsStore.NewToken);
            tokenText.Text = token;
            RefreshMcpControls();
        };
        mcpEnabled.IsCheckedChanged += (_, _) => RefreshMcpControls();
        RefreshMcpControls();

        // ── Buttons ─────────────────────────────────────────────────────────────────────────────
        var ok = new Button
        {
            Content = "OK",
            Padding = new Thickness(18, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        var dialog = new Window
        {
            Title = "Preferences",
            Icon = AppIcon.Window,
            Width = 460,
            Height = 640,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.WindowBgBrush,
            Content = new DockPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { cancel, ok },
                    },
                    new ScrollViewer
                    {
                        Content = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                Section("Caches"),
                                CacheRow("Proxy cache", proxySizeText, clearProxy),
                                CacheRow("Render cache (this project)", renderSizeText, clearRender),

                                Section("Export metadata defaults"),
                                Labeled("Title", title),
                                Labeled("Author", author),
                                Labeled("Copyright", copyright),
                                Labeled("Comment", comment),

                                Section("Autosave"),
                                Labeled($"Interval (seconds, {UserSettingsStore.MinAutosaveSeconds}–{UserSettingsStore.MaxAutosaveSeconds})", autosave),

                                Section("Updates"),
                                updatesEnabled,
                                updatesNote,

                                Section("AI control (MCP)"),
                                mcpEnabled,
                                Labeled("Port", port),
                                requireToken,
                                tokenText,
                                copySetup,
                                mcpWarning,
                            },
                        },
                    },
                },
            },
        };

        clearProxy.Click += async (_, _) =>
        {
            long size = proxyCacheSize();
            bool confirmed = await ConfirmDialog.Show(dialog, "Clear Proxy Cache",
                $"Delete all cached proxy files?\n\nCurrently using {PreferencesFormat.Bytes(size)}. " +
                "Proxies regenerate on demand; open projects may re-queue proxy builds.", "Clear", "Cancel");
            if (!confirmed)
                return;
            clearProxyCache();
            proxySizeText.Text = PreferencesFormat.Bytes(proxyCacheSize());
        };

        clearRender.Click += async (_, _) =>
        {
            long size = renderCacheSize();
            bool confirmed = await ConfirmDialog.Show(dialog, "Delete Render Files",
                $"Delete this project's preview render files?\n\nCurrently using {PreferencesFormat.Bytes(size)}. " +
                "Rendered previews rebuild when you render again.", "Delete", "Cancel");
            if (!confirmed)
                return;
            clearRenderCache();
            renderSizeText.Text = PreferencesFormat.Bytes(renderCacheSize());
        };

        copySetup.Click += async (_, _) =>
        {
            token = PreferencesFormat.ResolveToken(requireToken.IsChecked == true, token, UserSettingsStore.NewToken);
            tokenText.Text = token;
            string command = PreferencesFormat.McpSetupCommand(
                PreferencesFormat.ParsePort(port.Text) is { } p ? Math.Clamp(p, UserSettingsStore.MinPort, UserSettingsStore.MaxPort) : current.McpPort,
                requireToken.IsChecked == true, token);
            if (TopLevel.GetTopLevel(dialog)?.Clipboard is { } clipboard)
                await clipboard.SetValueAsync(DataFormat.Text, command);
        };

        ok.Click += (_, _) => dialog.Close(UserSettingsStore.Clamp(current with
        {
            ExportTitle = title.Text ?? "",
            ExportAuthor = author.Text ?? "",
            ExportCopyright = copyright.Text ?? "",
            ExportComment = comment.Text ?? "",
            AutosaveIntervalSeconds = PreferencesFormat.ParseSeconds(autosave.Text) ?? current.AutosaveIntervalSeconds,
            McpEnabled = mcpEnabled.IsChecked == true,
            McpPort = PreferencesFormat.ParsePort(port.Text) ?? current.McpPort,
            McpRequireToken = requireToken.IsChecked == true,
            McpToken = token,
            UpdateCheckEnabled = updatesEnabled.IsChecked == true,
        }));
        cancel.Click += (_, _) => dialog.Close((UserSettings?)null);

        return dialog.ShowDialog<UserSettings?>(owner);
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        Foreground = Palette.TextBrush,
        FontWeight = FontWeight.SemiBold,
        FontSize = 13,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = Palette.MutedTextBrush,
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button SmallButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(12, 3),
        FontSize = 12,
        Foreground = Palette.TextBrush,
        Background = Palette.PanelBgBrush,
        CornerRadius = new CornerRadius(5),
    };

    private static TextBox MakeText(string value) => new()
    {
        Text = value,
        Foreground = Palette.TextBrush,
        Background = Palette.PanelBgBrush,
        FontSize = 12,
    };

    private static StackPanel Labeled(string label, Control control) => new()
    {
        Spacing = 3,
        Children =
        {
            new TextBlock { Text = label, Foreground = Palette.MutedTextBrush, FontSize = 12 },
            control,
        },
    };

    /// <summary>"Proxy cache   1.2 GB   [Clear]" — label left, size + action pinned right.</summary>
    private static Grid CacheRow(string label, TextBlock size, Button clear)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var name = new TextBlock
        {
            Text = label,
            Foreground = Palette.TextBrush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        size.Margin = new Thickness(0, 0, 10, 0);
        name.SetValue(Grid.ColumnProperty, 0);
        size.SetValue(Grid.ColumnProperty, 1);
        clear.SetValue(Grid.ColumnProperty, 2);
        grid.Children.Add(name);
        grid.Children.Add(size);
        grid.Children.Add(clear);
        return grid;
    }
}
