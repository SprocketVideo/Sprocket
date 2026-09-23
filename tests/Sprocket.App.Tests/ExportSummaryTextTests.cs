using System;
using Sprocket.App;
using Sprocket.Export;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for <see cref="ExportSummaryText"/> (export-speed phase 1): the status-bar / queue-row line and the
/// Export Complete diagnostics block. The key property is that the text names the encoder that actually ran and
/// states a hardware → software fallback explicitly.
/// </summary>
public class ExportSummaryTextTests
{
    private static readonly ExportStageTimings Timings = new(
        VideoDecode: TimeSpan.FromMilliseconds(1200),
        VideoRender: TimeSpan.FromMilliseconds(15000),
        VideoEncode: TimeSpan.FromMilliseconds(20000.4),
        AudioMix: TimeSpan.FromMilliseconds(300),
        AudioEncode: TimeSpan.FromMilliseconds(200),
        Total: TimeSpan.FromSeconds(42));

    private static ExportRunSummary Summary(
        ExportAcceleration requested, string actual, bool hardware, long frames = 1260, long samples = 2_016_000) =>
        new(requested, actual.Length == 0 ? "" : "libx264", actual, hardware, frames, samples,
            actual.Length == 0 ? Timings with { VideoDecode = default, VideoRender = default, VideoEncode = default } : Timings);

    [Fact]
    public void HardwareEngaged_NamesTheGpuEncoder()
    {
        ExportRunSummary s = Summary(ExportAcceleration.Hardware, "h264_nvenc", hardware: true);

        Assert.Equal("Exported with h264_nvenc in 00:42", ExportSummaryText.Compact(s));
        Assert.Equal(
            "Encoder: h264_nvenc (hardware)\n" +
            "Elapsed: 00:42 · 30.0 fps average\n" +
            "Time: total 42000 ms · decode 1200 ms · render 15000 ms · encode 20000 ms · audio 500 ms",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void SoftwareFallback_IsStatedExplicitly()
    {
        ExportRunSummary s = Summary(ExportAcceleration.Hardware, "libx264", hardware: false);

        Assert.Equal("Exported with libx264 (hardware unavailable) in 00:42", ExportSummaryText.Compact(s));
        Assert.StartsWith(
            "Encoder: libx264 (software — hardware was requested but no GPU encoder opened)\n",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void SoftwareRequested_SaysSoftware_WithoutFallbackWording()
    {
        ExportRunSummary s = Summary(ExportAcceleration.Software, "libx264", hardware: false);

        Assert.Equal("Exported with libx264 in 00:42", ExportSummaryText.Compact(s));
        Assert.StartsWith("Encoder: libx264 (software)\n", ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void AudioOnly_OmitsTheEncoderAndVideoStages()
    {
        ExportRunSummary s = Summary(ExportAcceleration.Software, "", hardware: false, frames: 0);

        Assert.Equal("Exported in 00:42", ExportSummaryText.Compact(s));
        Assert.Equal(
            "Audio only · elapsed 00:42\n" +
            "Time: total 42000 ms · audio 500 ms (mix 300 ms, encode 200 ms)",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void ZeroElapsed_OmitsFps()
    {
        ExportRunSummary s = Summary(ExportAcceleration.Software, "libx264", hardware: false) with
        {
            Timings = default,
        };
        Assert.DoesNotContain("fps", ExportSummaryText.CompletionDetails(s));
    }

    private static ExportRunSummary Fast(ExportDecodeSummary decode, string actual = "h264_nvenc", bool hardware = true) =>
        Summary(ExportAcceleration.Hardware, actual, hardware) with { Mode = ExportMode.Fast, Decode = decode };

    [Fact]
    public void FastExport_AllSourcesOnGpu_SaysFastAndGpuDecode()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(true, 2, 0, 0, "d3d11va"));

        Assert.Equal("Fast export with h264_nvenc in 00:42 · GPU decode", ExportSummaryText.Compact(s));
        Assert.StartsWith(
            "Mode: Fast Export\n" +
            "Decode: hardware (d3d11va) for 2 sources\n" +
            "Encoder: h264_nvenc (hardware)\n",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FastExport_SomeSourcesInSoftware_NamesTheSplit()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(true, 2, 1, 0, "cuda"));

        Assert.Equal("Fast export with h264_nvenc in 00:42 · 1 of 3 sources decoded in software", ExportSummaryText.Compact(s));
        Assert.Contains("Decode: hardware (cuda) for 2 of 3 sources, software for 1\n", ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FastExport_NoGpuDecoderOrEncoder_StatesBothFallbacks()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(true, 0, 1, 0, null), actual: "libx264", hardware: false);

        Assert.Equal(
            "Fast export with libx264 (hardware unavailable) in 00:42 · 1 of 1 source decoded in software",
            ExportSummaryText.Compact(s));
        string details = ExportSummaryText.CompletionDetails(s);
        Assert.Contains("Decode: software for 1 source — no GPU decoder was available\n", details);
        Assert.Contains("Encoder: libx264 (software — hardware was requested but no GPU encoder opened)\n", details);
    }

    [Fact]
    public void FastExport_MidExportFallback_IsStatedExplicitly()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(true, 1, 1, 1, "cuda"));

        Assert.Equal("Fast export with h264_nvenc in 00:42 · GPU decode failed for 1 source, finished in software",
            ExportSummaryText.Compact(s));
        Assert.Contains(
            "Decode: hardware (cuda) for 1 of 2 sources, software for 1\n" +
            "GPU decode failed during the export for 1 source — reopened in software at the same frame\n",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FastExport_EveryGpuSourceFellBack_DoesNotClaimNoDecoderWasAvailable()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(true, 0, 1, 1, "cuda"));

        string details = ExportSummaryText.CompletionDetails(s);
        Assert.DoesNotContain("no GPU decoder was available", details);
        Assert.Contains(
            "Decode: software for 1 source\n" +
            "GPU decode (cuda) failed during the export for 1 source — reopened in software at the same frame\n",
            details);
    }

    [Fact]
    public void FastExport_DisabledByEnvironment_SaysSo()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(true, 0, 2, 0, null, DisabledByUser: true));

        Assert.EndsWith(" · GPU decode off (SPROCKET_HWACCEL)", ExportSummaryText.Compact(s));
        Assert.Contains("Decode: software for 2 sources — GPU decode disabled by SPROCKET_HWACCEL\n",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FastExport_NoVideoSources_OmitsTheCompactDecodeNote()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(true, 0, 0, 0, null));

        Assert.Equal("Fast export with h264_nvenc in 00:42", ExportSummaryText.Compact(s));
        Assert.Contains("Decode: no video sources\n", ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FastExport_DefaultSoftwareDecode_AddsNoCompactNote()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(false, 0, 2, 0, null));

        Assert.Equal("Fast export with h264_nvenc in 00:42", ExportSummaryText.Compact(s));
        Assert.StartsWith(
            "Mode: Fast Export\n" +
            "Decode: software for 2 sources\n" +
            "Encoder: h264_nvenc (hardware)\n",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FastExport_SoftwareEncoderFallback_NamesTheSpeedPreset()
    {
        ExportRunSummary s = Fast(new ExportDecodeSummary(false, 0, 1, 0, null), actual: "libx264", hardware: false) with
        {
            SoftwarePreset = "veryfast",
        };

        Assert.Equal("Fast export with libx264 (veryfast — hardware unavailable) in 00:42", ExportSummaryText.Compact(s));
        Assert.Contains(
            "Encoder: libx264 (software, veryfast preset — hardware was requested but no GPU encoder opened)\n",
            ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FinalExport_DoesNotMentionTheDefaultPreset()
    {
        ExportRunSummary s = Summary(ExportAcceleration.Software, "libx264", hardware: false) with { SoftwarePreset = "medium" };

        Assert.Equal("Exported with libx264 in 00:42", ExportSummaryText.Compact(s));
        Assert.StartsWith("Encoder: libx264 (software)\n", ExportSummaryText.CompletionDetails(s));
    }

    [Fact]
    public void FinalExport_DetailsCarryNoModeOrDecodeLines()
    {
        ExportRunSummary s = Summary(ExportAcceleration.Software, "libx264", hardware: false) with
        {
            Decode = new ExportDecodeSummary(false, 0, 1, 0, null),
        };
        string details = ExportSummaryText.CompletionDetails(s);
        Assert.DoesNotContain("Mode:", details);
        Assert.DoesNotContain("Decode:", details);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59.9, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3725, "1:02:05")]
    public void FormatElapsed_UsesMinutesSeconds_ThenHours(double seconds, string expected) =>
        Assert.Equal(expected, ExportSummaryText.FormatElapsed(TimeSpan.FromSeconds(seconds)));
}
