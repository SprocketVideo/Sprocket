using Sprocket.Media;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// Pure formatting for the status-bar telemetry (UI.md §3.7): the left engine-state label (playback state +
/// GPU/hardware-accel decode path) and the right <c>fps · resolution · duration</c> readout. Kept free of any
/// Avalonia control — like <see cref="MarkerListFormat"/> / <c>SpeedFormat</c> — so the strings are unit-testable
/// and the code-behind only maps them onto the <c>TextBlock</c>s and picks the state-dot colour.
/// </summary>
public static class StatusBarFormat
{
    /// <summary>
    /// The left status group's text: a friendly state word ("Ready" while parked/stopped, matching the UI.md §3.7
    /// mockup) optionally followed by the top-most decoding layer's path — <c>" · GPU · D3D11VA"</c> when
    /// hardware-accelerated, else <c>" · CPU · software"</c>. When nothing is decoding at the playhead (a gap or a
    /// generator) <paramref name="decode"/> is null and just the state word is shown.
    /// </summary>
    public static string EngineLabel(PlaybackState state, VideoDecodeInfo? decode)
    {
        string label = state switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            _ => "Ready",
        };
        if (decode is { } d)
            label += d.IsHardwareAccelerated
                ? $" · GPU · {d.HardwareDeviceName!.ToUpperInvariant()}"
                : " · CPU · software";
        return label;
    }

    /// <summary>
    /// The right telemetry cell: <c>fps · WxH · duration</c>. The rate is the measured preview rate while playing
    /// and the sequence's nominal rate at rest; the caller supplies the already-formatted duration timecode. No
    /// framework/runtime text (UI.md §3.7).
    /// </summary>
    public static string Telemetry(double fps, int width, int height, string durationText) =>
        $"{fps:0.##} fps · {width}×{height} · {durationText}";
}
