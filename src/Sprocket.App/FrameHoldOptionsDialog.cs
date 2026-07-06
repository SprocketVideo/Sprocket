using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>What Frame Hold Options chose: hold on/off and, when on, the source time to freeze at.</summary>
internal sealed record FrameHoldOptionsResult(bool Hold, Timecode HoldAt);

/// <summary>
/// "Frame Hold Options" (PLAN.md step 43, Premiere naming): freeze the whole selected clip on a single source
/// frame — at its in point, the frame under the playhead, or an explicit source time — or release the hold.
/// The clip's timeline span is unchanged either way; un-holding restores normal playback exactly (the source
/// span and speed are retained under the hold).
/// </summary>
internal static class FrameHoldOptionsDialog
{
    /// <param name="owner">The owning window.</param>
    /// <param name="clipName">Display name of the clip being held.</param>
    /// <param name="isHeld">Whether the clip is currently held (seeds the checkbox).</param>
    /// <param name="currentHoldAt">The current frozen source time when held.</param>
    /// <param name="inPoint">The clip's source in point (the "In Point" choice).</param>
    /// <param name="playheadSource">The source time under the playhead, or <see langword="null"/> when the
    /// playhead is outside the clip (the "Playhead" choice is then disabled).</param>
    public static Task<FrameHoldOptionsResult?> Show(
        Window owner, string clipName, bool isHeld, Timecode? currentHoldAt, Timecode inPoint, Timecode? playheadSource)
    {
        // Opening the dialog is the intent to hold (matching Premiere), so the box starts checked either way;
        // unchecking it on a held clip releases the hold.
        var hold = new CheckBox
        {
            Content = "Hold on frame",
            IsChecked = true,
            Foreground = Palette.TextBrush,
        };

        var inPointRadio = new RadioButton { Content = "In point", GroupName = "holdAt", Foreground = Palette.TextBrush };
        var playheadRadio = new RadioButton
        {
            Content = "Playhead",
            GroupName = "holdAt",
            Foreground = Palette.TextBrush,
            IsEnabled = playheadSource is not null,
        };
        var sourceRadio = new RadioButton { Content = "Source time (seconds):", GroupName = "holdAt", Foreground = Palette.TextBrush };
        var seconds = new TextBox
        {
            Width = 90,
            MinHeight = 24,
            Padding = new Thickness(6, 3),
            Background = Palette.PanelBgBrush,
            BorderBrush = Palette.EdgeBrush,
            Foreground = Palette.TextBrush,
            Text = (currentHoldAt ?? inPoint).ToSeconds().ToString("0.###", CultureInfo.InvariantCulture),
        };

        // Default anchor: a held clip shows its current frame (source time); a fresh hold prefers the playhead
        // frame (what the user is looking at), falling back to the in point.
        if (isHeld && currentHoldAt is not null)
            sourceRadio.IsChecked = true;
        else if (playheadSource is not null)
            playheadRadio.IsChecked = true;
        else
            inPointRadio.IsChecked = true;

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

        var sourceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { sourceRadio, seconds },
        };
        var anchors = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(24, 0, 0, 0),
            Children = { inPointRadio, playheadRadio, sourceRow },
        };
        // The anchor choice only matters while holding.
        hold.IsCheckedChanged += (_, _) => anchors.IsEnabled = hold.IsChecked == true;

        var dialog = new Window
        {
            Title = "Frame Hold Options",
            Icon = AppIcon.Window,
            Width = 440,
            Height = 280,
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
                                Text = clipName,
                                Foreground = Palette.TextBrush,
                                FontSize = 14,
                                FontWeight = FontWeight.SemiBold,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                            hold,
                            anchors,
                        },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
        {
            if (hold.IsChecked != true)
            {
                dialog.Close(new FrameHoldOptionsResult(false, default));
                return;
            }
            Timecode holdAt;
            if (playheadRadio.IsChecked == true && playheadSource is { } ps)
                holdAt = ps;
            else if (sourceRadio.IsChecked == true)
            {
                if (!double.TryParse(seconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) || s < 0)
                    return; // invalid input: keep the dialog open for correction
                holdAt = Timecode.FromSeconds(s);
            }
            else
            {
                holdAt = inPoint;
            }
            dialog.Close(new FrameHoldOptionsResult(true, holdAt));
        };
        cancel.Click += (_, _) => dialog.Close(null);

        return dialog.ShowDialog<FrameHoldOptionsResult?>(owner);
    }
}
