using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Sprocket.App;

/// <summary>How the user left the update-available dialog.</summary>
internal enum UpdateDialogResult
{
    /// <summary>Just closed ("Later") — the badge keeps showing until this version is skipped or installed.</summary>
    Close,

    /// <summary>"Skip This Version" — persist the version so the badge stays hidden for it.</summary>
    Skip,
}

/// <summary>
/// The "update available" box (PLAN.md steps 36 + 45), for Velopack-installed builds: says what version
/// is available and installs it in place — "Install &amp; Restart" downloads the update (delta when
/// possible) with a progress bar, then applies it and restarts the app. Opened only from the status-bar
/// badge or Help ▸ Check for Updates, never on launch (the VS Code restart-nag being the deliberate
/// non-example). A download/apply failure is shown in the dialog and the session continues unharmed.
/// </summary>
internal static class UpdateAvailableDialog
{
    public static Task<UpdateDialogResult> Show(Window owner, UpdateService service)
    {
        var install = new Button
        {
            Content = "Install & Restart",
            Padding = new Thickness(18, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var releaseNotes = new Button
        {
            Content = "Full Release Notes",
            Padding = new Thickness(14, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };
        var skip = new Button
        {
            Content = "Skip This Version",
            Padding = new Thickness(14, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };
        var later = new Button
        {
            Content = "Later",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            IsVisible = false,
        };
        var status = new TextBlock
        {
            Text = "The update downloads in the background and applies on restart.",
            Foreground = Palette.MutedTextBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };

        // "What's new", when the release was packed with notes (Velopack carries them on the feed). Rendered
        // as lightly-softened plain text — no markdown NuGet, keeping the managed footprint small. Absent
        // notes → the region collapses and the "Full Release Notes" button (GitHub) is the only path.
        string notes = SoftenMarkdown(service.AvailableNotes);
        bool hasNotes = notes.Length > 0;
        var notesRegion = new Border
        {
            [DockPanel.DockProperty] = Dock.Top,
            IsVisible = hasNotes,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 10),
            Background = Palette.SectionBgBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new SelectableTextBlock
                {
                    Text = notes,
                    Foreground = Palette.TextBrush,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var dialog = new Window
        {
            Title = "Update Available",
            Icon = AppIcon.Window,
            Width = 640,
            Height = hasNotes ? 440 : 240,
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
                        Children = { skip, later, releaseNotes, install },
                    },
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Top,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Sprocket {service.AvailableVersion} is available.",
                                Foreground = Palette.TextBrush,
                                FontSize = 14,
                                FontWeight = FontWeight.SemiBold,
                            },
                            new TextBlock
                            {
                                Text = $"You are running {Program.AppVersion}.",
                                Foreground = Palette.MutedTextBrush,
                                FontSize = 12,
                            },
                        },
                    },
                    // Bottom-docked so it sits just above the buttons; the notes region (Dock.Top) then fills
                    // the middle. Order in the tree matters for DockPanel — last un-docked child fills.
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Spacing = 8,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { status, progress },
                    },
                    notesRegion,
                },
            },
        };

        install.Click += async (_, _) =>
        {
            install.IsEnabled = skip.IsEnabled = later.IsEnabled = false;
            progress.IsVisible = true;
            status.Text = "Downloading update…";
            try
            {
                // Progress arrives on a worker thread; marshal onto the UI thread for the bar.
                await service.DownloadAsync(p => Dispatcher.UIThread.Post(() => progress.Value = p));
                status.Text = "Restarting to apply the update…";
                service.ApplyAndRestart(); // exits the process on success
            }
            catch (Exception ex)
            {
                CrashLog.Write("Update download/apply failed", ex);
                status.Text = $"The update failed: {ex.Message} You can download it manually from the releases page.";
                progress.IsVisible = false;
                install.IsEnabled = skip.IsEnabled = later.IsEnabled = true;
            }
        };
        releaseNotes.Click += async (_, _) => await OpenAsync(dialog, UpdateService.ReleasesPageUrl);
        skip.Click += (_, _) => dialog.Close(UpdateDialogResult.Skip);
        later.Click += (_, _) => dialog.Close(UpdateDialogResult.Close);

        return dialog.ShowDialog<UpdateDialogResult>(owner);
    }

    /// <summary>
    /// Turns release-notes Markdown into readable plain text for the inline "what's new" box — we render
    /// with a plain <see cref="SelectableTextBlock"/> (no markdown NuGet), so soften the common syntax:
    /// strip heading hashes and bullet/emphasis markers, drop leading/trailing blank lines, and normalize
    /// line endings. Not a full parser — just enough that raw Markdown doesn't read as noise.
    /// </summary>
    internal static string SoftenMarkdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md))
            return "";
        var sb = new System.Text.StringBuilder(md.Length);
        foreach (string rawLine in md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.TrimStart();
            // Headings: drop the leading '#'s, keep the text (as its own line).
            if (trimmed.StartsWith('#'))
                trimmed = trimmed.TrimStart('#').TrimStart();
            // Bullets: normalize '*'/'+'/'-' list markers to a bullet glyph, preserving indentation.
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
                trimmed = "• " + trimmed[2..];
            // Inline emphasis/code markers: strip the noise characters (leave the words).
            trimmed = trimmed.Replace("**", "").Replace("`", "");
            sb.Append(trimmed).Append('\n');
        }
        return sb.ToString().Trim('\n');
    }

    /// <summary>Best-effort browser launch, like About's Open Logs Folder: no launcher / a malformed
    /// URL must not crash.</summary>
    internal static async Task OpenAsync(Window dialog, string url)
    {
        try
        {
            if (!string.IsNullOrEmpty(url) && TopLevel.GetTopLevel(dialog)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(url));
        }
        catch
        {
        }
    }
}

/// <summary>Shown when Help ▸ Check for Updates runs in a build that isn't a Velopack install
/// (portable zip / dev run) — those can't self-update, so deep-link to the releases page instead.</summary>
internal static class UpdateNotInstalledDialog
{
    public static Task Show(Window owner)
    {
        var open = new Button
        {
            Content = "Open Releases Page",
            Padding = new Thickness(16, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var close = new Button
        {
            Content = "Close",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        var dialog = new Window
        {
            Title = "Check for Updates",
            Icon = AppIcon.Window,
            Width = 460,
            Height = 190,
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
                        Children = { close, open },
                    },
                    new TextBlock
                    {
                        Text = $"You are running {Program.AppVersion}. This is a portable or development " +
                               "build, so it doesn't update itself — new releases can be downloaded from " +
                               "the Sprocket releases page.",
                        Foreground = Palette.TextBrush,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        open.Click += async (_, _) => await UpdateAvailableDialog.OpenAsync(dialog, UpdateService.ReleasesPageUrl);
        close.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(owner);
    }
}
