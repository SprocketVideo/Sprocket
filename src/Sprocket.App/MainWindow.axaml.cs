using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sprocket.Core.Timing;
using Sprocket.Playback;

namespace Sprocket.App;

public partial class MainWindow : Window
{
    private readonly PlaybackEngine? _engine;
    private bool _suppressSeek;   // guards programmatic scrubber updates from re-triggering a seek

    // Parameterless ctor for the XAML designer / tooling.
    public MainWindow() : this(null, string.Empty) { }

    public MainWindow(PlaybackEngine? engine, string status)
    {
        AvaloniaXamlLoader.Load(this);
        _engine = engine;

        var statusText = this.FindControl<TextBlock>("StatusText")!;
        var playPause = this.FindControl<Button>("PlayPauseButton")!;
        var scrubber = this.FindControl<Slider>("Scrubber")!;
        var positionText = this.FindControl<TextBlock>("PositionText")!;
        var durationText = this.FindControl<TextBlock>("DurationText")!;
        var preview = this.FindControl<PreviewSurface>("Preview")!;

        statusText.Text = status;

        if (_engine is null)
        {
            playPause.IsEnabled = false;
            scrubber.IsEnabled = false;
            return;
        }

        preview.Attach(_engine);

        Timecode duration = _engine.Duration;
        scrubber.Maximum = Math.Max(1, duration.Ticks);
        durationText.Text = FormatTime(duration);
        positionText.Text = FormatTime(Timecode.Zero);

        playPause.Click += (_, _) => _engine.TogglePlayPause();

        scrubber.ValueChanged += (_, e) =>
        {
            if (_suppressSeek)
                return;
            _engine.SeekTo(new Timecode((long)e.NewValue));
        };

        _engine.PositionChanged += pos => Dispatcher.UIThread.Post(() =>
        {
            _suppressSeek = true;
            scrubber.Value = Math.Clamp(pos.Ticks, 0, scrubber.Maximum);
            _suppressSeek = false;
            positionText.Text = FormatTime(pos);
        });

        _engine.StateChanged += state => Dispatcher.UIThread.Post(() =>
            playPause.Content = state == PlaybackState.Playing ? "❚❚" : "▶");

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                _engine.TogglePlayPause();
                e.Handled = true;
            }
        };

        // Optional timed auto-exit for unattended profiling runs: SPROCKET_APP_SECONDS=12
        if (int.TryParse(Environment.GetEnvironmentVariable("SPROCKET_APP_SECONDS"), out int seconds) && seconds > 0)
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(seconds));
    }

    private static string FormatTime(Timecode t)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, t.ToSeconds()));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }
}
