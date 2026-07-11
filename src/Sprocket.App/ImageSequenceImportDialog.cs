using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>How the user chose to import a detected numbered image run (PLAN.md step 42).</summary>
/// <param name="AsSequence">True to import as one image-sequence clip; false to import the picked file as a
/// single still instead (the "Import as image sequence" checkbox convention, unchecked).</param>
/// <param name="FrameRate">The frame rate for the sequence (ignored when <see cref="AsSequence"/> is false).</param>
public readonly record struct SequenceImportChoice(bool AsSequence, Rational FrameRate);

/// <summary>
/// Asks whether a detected numbered image run should import as one image sequence and at what frame rate
/// (PLAN.md step 42). Follows leading NLEs convention — an explicit "Import as image sequence" checkbox — so a
/// deliberate single-still import stays possible. The frame-rate combo offers the common stop-motion presets
/// (12 / 15 / 24 fps) plus the project's timeline rate, defaulting to the project rate.
/// </summary>
internal static class ImageSequenceImportDialog
{
    public static Task<SequenceImportChoice?> Show(Window owner, ImageSequenceInfo sequence, int width, int height, Rational projectFps)
    {
        var asSequence = new CheckBox
        {
            Content = "Import as image sequence",
            IsChecked = true,
            Foreground = Palette.TextBrush,
        };

        // fps presets + the project rate (deduped), default = project rate.
        var rates = new List<Rational>();
        foreach (Rational r in new[] { new Rational(12, 1), new Rational(15, 1), new Rational(24, 1), projectFps })
            if (r.Num > 0 && !rates.Contains(r))
                rates.Add(r);

        var fps = new ComboBox
        {
            Foreground = Palette.TextBrush,
            Background = Palette.PanelBgBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 120,
        };
        foreach (Rational r in rates)
            fps.Items.Add(new ComboBoxItem { Content = FormatFps(r), Tag = r, Foreground = Palette.TextBrush });
        fps.SelectedIndex = rates.IndexOf(projectFps);
        if (fps.SelectedIndex < 0)
            fps.SelectedIndex = rates.Count - 1;

        var fpsRow = Labeled("Frame rate", fps);
        asSequence.IsCheckedChanged += (_, _) => fpsRow.IsEnabled = asSequence.IsChecked == true;

        var ok = new Button
        {
            Content = "Import",
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

        string dims = width > 0 && height > 0 ? $"  ·  {width}×{height}" : "";
        var dialog = new Window
        {
            Title = "Import Image Sequence",
            Icon = AppIcon.Window,
            Width = 460,
            Height = 240,
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
                                Text = $"Detected {sequence.FrameCount} numbered frames{dims}.",
                                Foreground = Palette.TextBrush,
                                FontSize = 14,
                                FontWeight = FontWeight.SemiBold,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = System.IO.Path.GetFileName(sequence.Pattern),
                                Foreground = Palette.MutedTextBrush,
                                FontSize = 12,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            asSequence,
                            fpsRow,
                        },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
        {
            bool seq = asSequence.IsChecked == true;
            Rational rate = fps.SelectedItem is ComboBoxItem { Tag: Rational r } ? r : projectFps;
            dialog.Close((SequenceImportChoice?)new SequenceImportChoice(seq, rate));
        };
        cancel.Click += (_, _) => dialog.Close((SequenceImportChoice?)null);

        return dialog.ShowDialog<SequenceImportChoice?>(owner);
    }

    private static string FormatFps(Rational r) =>
        r.Den == 1
            ? $"{r.Num} fps"
            : $"{((double)r.Num / r.Den).ToString("0.###", CultureInfo.InvariantCulture)} fps";

    private static StackPanel Labeled(string label, Control control) => new()
    {
        Spacing = 3,
        Children =
        {
            new TextBlock { Text = label, Foreground = Palette.MutedTextBrush, FontSize = 12 },
            control,
        },
    };
}
