using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// "Interpret Footage" (PLAN.md step 42, Premiere's "Assume this frame rate"): pick a new frame rate to
/// reinterpret a source at. The same frames stay selected — clip timeline durations stretch/squeeze — so this is
/// the frame-rate re-time for image sequences and the whole-clip "shoot on twos" lever. Offers the common
/// broadcast rates plus the source's current rate, defaulting to the current rate.
/// </summary>
internal static class InterpretFootageDialog
{
    private static readonly Rational[] Presets =
    [
        new(12, 1), new(15, 1), new(24000, 1001), new(24, 1), new(25, 1),
        new(30000, 1001), new(30, 1), new(50, 1), new(60000, 1001), new(60, 1),
    ];

    public static Task<Rational?> Show(Window owner, string mediaName, Rational currentFps)
    {
        var rates = new List<Rational>();
        foreach (Rational r in Presets)
            if (!rates.Contains(r))
                rates.Add(r);
        if (currentFps.Num > 0 && !rates.Contains(currentFps))
            rates.Add(currentFps);

        var fps = new ComboBox
        {
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 140,
        };
        foreach (Rational r in rates)
            fps.Items.Add(new ComboBoxItem { Content = FormatFps(r), Tag = r, Foreground = Palette.TextBrush });
        fps.SelectedIndex = rates.IndexOf(currentFps);
        if (fps.SelectedIndex < 0)
            fps.SelectedIndex = 0;

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
            Title = "Interpret Footage",
            Icon = AppIcon.Window,
            Width = 440,
            Height = 210,
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
                    new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = mediaName,
                                Foreground = Palette.TextBrush,
                                FontSize = 14,
                                FontWeight = FontWeight.SemiBold,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                            new TextBlock
                            {
                                Text = $"Currently {FormatFps(currentFps)}. Assume this frame rate:",
                                Foreground = Palette.MutedTextBrush,
                                FontSize = 12,
                            },
                            fps,
                        },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
            dialog.Close(fps.SelectedItem is ComboBoxItem { Tag: Rational r } ? r : (Rational?)null);
        cancel.Click += (_, _) => dialog.Close((Rational?)null);

        return dialog.ShowDialog<Rational?>(owner);
    }

    private static string FormatFps(Rational r) =>
        r.Den == 1
            ? $"{r.Num} fps"
            : $"{((double)r.Num / r.Den).ToString("0.###", CultureInfo.InvariantCulture)} fps";
}
