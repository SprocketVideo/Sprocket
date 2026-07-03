using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sprocket.App;

/// <summary>How the user left the update-available dialog.</summary>
internal enum UpdateDialogResult
{
    /// <summary>Just closed — the badge keeps showing until this version is skipped or installed.</summary>
    Close,

    /// <summary>"Skip This Version" — persist the tag so the badge stays hidden for it.</summary>
    Skip,
}

/// <summary>
/// The "update available" box (PLAN.md step 45): says what version is available and deep-links to the
/// download — the browser does the downloading; Sprocket never installs anything. When the release has
/// no asset for this platform the Download button falls back to the release page. Follows the standard
/// notify-only convention (VS Code's "restart to update" nag being the deliberate non-example): a
/// lightweight modal opened only from the status-bar badge or Help ▸ Check for Updates, never on launch.
/// </summary>
internal static class UpdateAvailableDialog
{
    public static Task<UpdateDialogResult> Show(Window owner, UpdateInfo info)
    {
        var download = new Button
        {
            Content = "Download",
            Padding = new Thickness(18, 5),
            Foreground = Brushes.White,
            Background = Palette.AccentBrush,
            CornerRadius = new CornerRadius(5),
        };
        var releaseNotes = new Button
        {
            Content = "Release Notes",
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
        var close = new Button
        {
            Content = "Close",
            Padding = new Thickness(16, 5),
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            CornerRadius = new CornerRadius(5),
        };

        string target = info.AssetName is not null
            ? $"Download opens {info.AssetName} in your browser."
            : "No build for this platform was published — Download opens the release page instead.";

        var dialog = new Window
        {
            Title = "Update Available",
            Icon = AppIcon.Window,
            Width = 460,
            Height = 230,
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
                        Children = { skip, close, releaseNotes, download },
                    },
                    new StackPanel
                    {
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Sprocket {info.DisplayVersion} is available.",
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
                            new TextBlock
                            {
                                Text = target + " Sprocket never downloads or installs updates itself.",
                                Foreground = Palette.MutedTextBrush,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                            },
                        },
                    },
                },
            },
        };

        async Task OpenAsync(string url)
        {
            try
            {
                if (!string.IsNullOrEmpty(url) && TopLevel.GetTopLevel(dialog)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(new Uri(url));
            }
            catch
            {
                // Best-effort, like About's Open Logs Folder: no launcher / a malformed URL must not crash.
            }
        }

        download.Click += async (_, _) => await OpenAsync(info.AssetUrl ?? info.HtmlUrl);
        releaseNotes.Click += async (_, _) => await OpenAsync(info.HtmlUrl);
        skip.Click += (_, _) => dialog.Close(UpdateDialogResult.Skip);
        close.Click += (_, _) => dialog.Close(UpdateDialogResult.Close);

        return dialog.ShowDialog<UpdateDialogResult>(owner);
    }
}
