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
    /// <c>(hardware unavailable)</c>; an audio-only export omits the encoder. A Fast Export (export-speed phase 3)
    /// reads <c>Fast export with …</c>, names the speed preset a software fallback ran with, and — when GPU decode was
    /// opted into — appends how the sources decoded, naming any software decode.</summary>
    public static string Compact(ExportRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string elapsed = FormatElapsed(summary.Timings.Total);
        if (summary.IsAudioOnly)
            return $"Exported in {elapsed}";
        bool fast = summary.Mode == ExportMode.Fast;
        string fallback = !summary.FellBackToSoftware ? ""
            : fast && summary.SoftwarePreset is { } preset ? $" ({preset} — hardware unavailable)"
            : " (hardware unavailable)";
        return fast
            ? $"Fast export with {summary.ActualVideoEncoder}{fallback} in {elapsed}{CompactDecodeNote(summary.Decode)}"
            : $"Exported with {summary.ActualVideoEncoder}{fallback} in {elapsed}";
    }

    /// <summary>The Fast Export GPU-decode suffix (only when GPU decode was opted into): <c> · GPU decode</c> when
    /// every source decoded on the GPU, otherwise what ran in software and why (disabled, failed mid-export, or no GPU
    /// decoder). Default software decode needs no note — it is the expected path.</summary>
    private static string CompactDecodeNote(ExportDecodeSummary d)
    {
        if (d.Sources == 0 || !d.HardwareRequested)
            return "";
        if (d.DisabledByUser)
            return " · GPU decode off (SPROCKET_HWACCEL)";
        if (d.FallbackSources > 0)
            return $" · GPU decode failed for {Sources(d.FallbackSources)}, finished in software";
        if (d.SoftwareSources > 0)
            return $" · {d.SoftwareSources} of {Sources(d.Sources)} decoded in software";
        return " · GPU decode";
    }

    /// <summary>The Fast Export <c>Decode:</c> line — software by default; with the GPU-decode opt-in, the device and
    /// per-source split, stating software decode and mid-export fallbacks explicitly.</summary>
    private static string DecodeLine(ExportDecodeSummary d)
    {
        if (d.Sources == 0)
            return "Decode: no video sources";
        if (!d.HardwareRequested)
            return $"Decode: software for {Sources(d.Sources)}";
        if (d.DisabledByUser)
            return $"Decode: software for {Sources(d.Sources)} — GPU decode disabled by SPROCKET_HWACCEL";
        string line = d.HardwareSources == 0 && d.FallbackSources == 0
            ? $"Decode: software for {Sources(d.Sources)} — no GPU decoder was available"
            : d.HardwareSources == 0
                ? $"Decode: software for {Sources(d.Sources)}"
            : d.SoftwareSources == 0
                ? $"Decode: hardware ({d.HardwareDevice}) for {Sources(d.Sources)}"
                : $"Decode: hardware ({d.HardwareDevice}) for {d.HardwareSources} of {Sources(d.Sources)}, software for {d.SoftwareSources}";
        if (d.FallbackSources > 0)
        {
            // Name the device here when the first line could not (every GPU source fell back).
            string device = d.HardwareSources == 0 && d.HardwareDevice is { } dev ? $" ({dev})" : "";
            line += $"\nGPU decode{device} failed during the export for {Sources(d.FallbackSources)} — reopened in software at the same frame";
        }
        return line;
    }

    private static string Sources(int count) => count == 1 ? "1 source" : $"{count} sources";

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

        // A Fast Export software fallback names its speed-first preset (a Final Export's preset is the codec default).
        string presetNote = summary.Mode == ExportMode.Fast && summary.SoftwarePreset is { } preset ? $", {preset} preset" : "";
        string engine = summary.HardwareVideoEngaged ? "hardware"
            : summary.FellBackToSoftware ? $"software{presetNote} — hardware was requested but no GPU encoder opened"
            : $"software{presetNote}";
        string fps = summary.VideoFrames > 0 && t.Total > TimeSpan.Zero
            ? string.Create(CultureInfo.InvariantCulture, $" · {summary.VideoFrames / t.Total.TotalSeconds:0.0} fps average")
            : "";

        // Fast Export (export-speed phase 3) leads with the mode and adds how the sources decoded; a Final Export's
        // block is unchanged (it always decodes in software, with the codec's default preset).
        string fastLines = summary.Mode == ExportMode.Fast
            ? $"Mode: Fast Export\n{DecodeLine(summary.Decode)}\n"
            : "";
        return fastLines +
               $"Encoder: {summary.ActualVideoEncoder} ({engine})\n" +
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
