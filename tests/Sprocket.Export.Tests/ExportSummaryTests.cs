using Sprocket.Core.Model;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// <see cref="VideoExporter.ExportWithSummary"/> (export-speed phase 1): the measured result reports the encoder that
/// actually opened, plausible frame / sample counts, stage timings, and skips audio work for a muted timeline. Real
/// encodes over the 1 s fixture. Hardware engagement is never asserted — CI has no GPU encoder.
/// </summary>
public sealed class ExportSummaryTests
{
    [Fact]
    public void VideoExport_ReportsActualEncoder_Counts_AndTimings()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile(".mp4");
        ExportRunSummary summary = VideoExporter.ExportWithSummary(
            project, output.Path, default, sequenceId: null, range: null);

        Assert.Equal(ExportAcceleration.Software, summary.RequestedAcceleration);
        Assert.Equal("libx264", summary.RequestedVideoEncoder);
        Assert.Equal("libx264", summary.ActualVideoEncoder);
        Assert.False(summary.HardwareVideoEngaged);
        Assert.False(summary.FellBackToSoftware);
        Assert.False(summary.IsAudioOnly);
        Assert.InRange(summary.VideoFrames, 28, 32);
        Assert.InRange(summary.AudioSampleFrames, 47000, 49000); // ~1 s at 48 kHz

        ExportStageTimings t = summary.Timings;
        Assert.True(t.Total > TimeSpan.Zero);
        Assert.True(t.VideoDecode > TimeSpan.Zero, "a media clip must attribute decode time");
        Assert.True(t.VideoEncode > TimeSpan.Zero);
        Assert.True(t.AudioMix > TimeSpan.Zero);
        Assert.True(t.VideoRender >= TimeSpan.Zero);
        Assert.True(t.VideoDecode + t.VideoRender + t.VideoEncode + t.AudioMix + t.AudioEncode <= t.Total,
            "stage times are sub-intervals of the total");
    }

    [Fact]
    public void HardwareRequest_ReportsANonEmptyEncoder_AndConsistentFallback()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile(".mp4");
        ExportRunSummary summary = VideoExporter.ExportWithSummary(
            project, output.Path, new ExportOptions(Acceleration: ExportAcceleration.Hardware),
            sequenceId: null, range: null);

        Assert.Equal(ExportAcceleration.Hardware, summary.RequestedAcceleration);
        Assert.Equal("libx264", summary.RequestedVideoEncoder);
        Assert.NotEmpty(summary.ActualVideoEncoder);
        Assert.Equal(!summary.HardwareVideoEngaged, summary.FellBackToSoftware);
        if (!summary.HardwareVideoEngaged)
            Assert.Equal("libx264", summary.ActualVideoEncoder);
    }

    [Fact]
    public void MutedAudioTrack_SkipsAudioWork()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);
        foreach (AudioTrack track in project.Timeline.AudioTracks)
            track.Muted = true;

        using var output = new TempFile(".mp4");
        ExportRunSummary summary = VideoExporter.ExportWithSummary(
            project, output.Path, default, sequenceId: null, range: null);

        Assert.Equal(0, summary.AudioSampleFrames);
        Assert.Equal(TimeSpan.Zero, summary.Timings.AudioMix);
        Assert.Equal(TimeSpan.Zero, summary.Timings.AudioEncode);
        Assert.InRange(summary.VideoFrames, 28, 32);

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.False(decoded.Info.HasAudio, "a muted-only timeline exports with no audio stream");
    }

    [Fact]
    public void AudioOnlyExport_ReturnsZeroVideoFacts()
    {
        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile(".wav");
        ExportRunSummary summary = VideoExporter.ExportWithSummary(
            project, output.Path, new ExportOptions(AudioFormat: ExportAudioFormat.WavPcm),
            sequenceId: null, range: null);

        Assert.True(summary.IsAudioOnly);
        Assert.Equal("", summary.RequestedVideoEncoder);
        Assert.Equal("", summary.ActualVideoEncoder);
        Assert.False(summary.HardwareVideoEngaged);
        Assert.Equal(0, summary.VideoFrames);
        Assert.Equal(TimeSpan.Zero, summary.Timings.VideoDecode);
        Assert.Equal(TimeSpan.Zero, summary.Timings.VideoRender);
        Assert.Equal(TimeSpan.Zero, summary.Timings.VideoEncode);
        Assert.InRange(summary.AudioSampleFrames, 47000, 49000);
        Assert.True(summary.Timings.Total > TimeSpan.Zero);
    }

    [Fact]
    public void CompatibilityWrapper_StillExports()
    {
        // The void Export overloads now delegate to ExportWithSummary and discard the result.
        Project project = ExportFixture.BuildProject(withAudio: false);
        using var output = new TempFile(".mp4");
        VideoExporter.Export(project, output.Path);
        Assert.InRange(ExportProbe.CountVideoFrames(output.Path), 28, 32);
    }
}
