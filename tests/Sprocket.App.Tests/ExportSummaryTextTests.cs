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

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59.9, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3725, "1:02:05")]
    public void FormatElapsed_UsesMinutesSeconds_ThenHours(double seconds, string expected) =>
        Assert.Equal(expected, ExportSummaryText.FormatElapsed(TimeSpan.FromSeconds(seconds)));
}
