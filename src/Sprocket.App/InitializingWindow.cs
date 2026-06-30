using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sprocket.App;

/// <summary>
/// A lightweight, frameless splash shown at launch while the first session is built off the UI thread
/// (<see cref="App.OnFrameworkInitializationCompleted"/>). First launch generates the sample clip with the
/// ffmpeg CLI, which can take a few seconds; without visible feedback the app looks like it failed to start.
/// The indeterminate bar animates because the slow work runs on a background thread. Closed and replaced by the
/// editor shell once the session is ready.
/// </summary>
internal sealed class InitializingWindow : Window
{
    public InitializingWindow()
    {
        Title = "Sprocket";
        Icon = AppIcon.Window;
        Width = 360;
        Height = 200;
        CanResize = false;
        SystemDecorations = WindowDecorations.None; // frameless splash
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        Background = Palette.WindowBg;

        Content = new Border
        {
            BorderBrush = Palette.Accent,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(28, 24),
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Image
                    {
                        Width = 52,
                        Height = 52,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Source = AppIcon.Bitmap,
                    },
                    new TextBlock
                    {
                        Text = "Initializing Sprocket…",
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Palette.Text,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Height = 4,
                        Width = 220,
                        Foreground = Palette.Accent,
                    },
                    new TextBlock
                    {
                        Text = "Preparing the sample video — this only happens on first launch.",
                        FontSize = 11.5,
                        Foreground = Palette.MutedText,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
    }
}
