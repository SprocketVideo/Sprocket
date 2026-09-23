using System;
using System.Globalization;
using Sprocket.Export;

namespace Sprocket.App;

/// <summary>
/// Pure, culture-invariant text for an <see cref="ExportRunSummary"/> (export-speed phase 1): the one-line form the
/// status bar and export-queue rows show, and the multi-line diagnostics block appended to the Export Complete
/// dialog. Names the encoder that <em>actually</em> ran and states a hardware → software fallback explicitly, so
/// "Hardware (if available)" is never implied to have engaged when it did not. Avalonia-free and unit-tested.
/// </summary>
internal static class ExportSummaryText
{
    /// <summary>One line, e.g. <c>Exported with h264_nvenc in 00:42</c>; a fallback adds
    /// <c>(hardware unavailable)</c>; an audio-only export omits the encoder.</summary>
    public static string Compact(ExportRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string elapsed = FormatElapsed(summary.Timings.Total);
        if (summary.IsAudioOnly)
            return $"Exported in {elapsed}";
        string fallback = summary.FellBackToSoftware ? " (hardware unavailable)" : "";
        return $"Exported with {summary.ActualVideoEncoder}{fallback} in {elapsed}";
    }

    /// <summary>The Export Complete dialog's diagnostics block: actual encoder + hardware/software, elapsed time
    /// with average export fps, and the per-stage breakdown in milliseconds.</summary>
    public static string CompletionDetails(ExportRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ExportStageTimings t = summary.Timings;
        TimeSpan audio = t.AudioMix + t.AudioEncode;

        if (summary.IsAudioOnly)
        {
            return $"Audio only · elapsed {FormatElapsed(t.Total)}\n" +
                   $"Time: total {Ms(t.Total)} · audio {Ms(audio)} (mix {Ms(t.AudioMix)}, encode {Ms(t.AudioEncode)})";
        }

        string engine = summary.HardwareVideoEngaged ? "hardware"
            : summary.FellBackToSoftware ? "software — hardware was requested but no GPU encoder opened"
            : "software";
        string fps = summary.VideoFrames > 0 && t.Total > TimeSpan.Zero
            ? string.Create(CultureInfo.InvariantCulture, $" · {summary.VideoFrames / t.Total.TotalSeconds:0.0} fps average")
            : "";

        return $"Encoder: {summary.ActualVideoEncoder} ({engine})\n" +
               $"Elapsed: {FormatElapsed(t.Total)}{fps}\n" +
               $"Time: total {Ms(t.Total)} · decode {Ms(t.VideoDecode)} · render {Ms(t.VideoRender)} · " +
               $"encode {Ms(t.VideoEncode)} · audio {Ms(audio)}";
    }

    /// <summary><c>mm:ss</c>, or <c>h:mm:ss</c> from an hour up; truncates to whole seconds.</summary>
    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    private static string Ms(TimeSpan span) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, span.TotalMilliseconds):0} ms");
}
