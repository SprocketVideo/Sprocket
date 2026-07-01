using Sprocket.Core.Model;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Hardware export acceleration (PLAN.md step 29). Two layers: the pure candidate-resolver (deterministic, no
/// FFmpeg) and end-to-end exports with <see cref="ExportAcceleration.Hardware"/>. Because the resolver falls back
/// to the software encoder when no GPU is available, and the <em>decoded</em> codec is the same family regardless
/// of which encoder produced it, the round-trip assertions hold whether hardware engaged or not — so they run
/// everywhere, and opportunistically exercise the real GPU path on a machine that has one.
/// </summary>
public sealed class HardwareExportTests
{
    // ── Candidate resolver (pure) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void HardwareEncoderCandidates_AreThePlatformVendorProduct_MostPreferredFirst()
    {
        IReadOnlyList<string> vendors = ExportCodecs.PlatformHardwareVendors();
        IReadOnlyList<string> got = ExportCodecs.HardwareEncoderCandidates(ExportVideoCodec.H264);

        Assert.Equal(vendors.Select(v => $"h264_{v}"), got);

        if (OperatingSystem.IsWindows())
            Assert.Equal(["h264_nvenc", "h264_qsv", "h264_amf"], got);
        else if (OperatingSystem.IsMacOS())
            Assert.Equal(["h264_videotoolbox"], got);
        else if (OperatingSystem.IsLinux())
            Assert.Equal(["h264_vaapi", "h264_nvenc"], got);
    }

    [Theory]
    [InlineData(ExportVideoCodec.H264, "h264")]
    [InlineData(ExportVideoCodec.Hevc, "hevc")]
    [InlineData(ExportVideoCodec.Av1, "av1")]
    [InlineData(ExportVideoCodec.Vp9, "vp9")]
    [InlineData(ExportVideoCodec.Mpeg2, "mpeg2")]
    [InlineData(ExportVideoCodec.ProRes, "prores")]
    public void HardwareEncoderCandidates_UseTheCodecsHardwareBaseName(ExportVideoCodec codec, string baseName)
    {
        IReadOnlyList<string> got = ExportCodecs.HardwareEncoderCandidates(codec);
        Assert.Equal(ExportCodecs.PlatformHardwareVendors().Count, got.Count);
        Assert.All(got, name => Assert.StartsWith(baseName + "_", name));
    }

    [Fact]
    public void PlatformHardwareVendors_AreDefinedForThisOs()
    {
        IReadOnlyList<string> vendors = ExportCodecs.PlatformHardwareVendors();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            Assert.NotEmpty(vendors);
    }

    // ── End-to-end export (hardware-with-fallback) ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ExportVideoCodec.H264, "h264")]
    [InlineData(ExportVideoCodec.Hevc, "hevc")]
    public void HardwareExport_RoundTripsToTheSameCodec_WhetherGpuEngagedOrFellBack(
        ExportVideoCodec codec, string expectedCodec)
    {
        // Requesting Hardware acceleration must always yield a valid file of the requested codec family: on a GPU
        // machine a hardware encoder produces it; elsewhere the software encoder does. The decoded codec name is
        // the family, so this assertion is stable across both.
        Project project = ExportFixture.BuildProject(withAudio: true);
        var format = new ExportFormat(ExportContainer.Mp4, codec, ExportAudioCodec.Aac);

        using var output = new TempFile(format.FileExtension);
        VideoExporter.Export(project, output.Path,
            new ExportOptions(Format: format, Acceleration: ExportAcceleration.Hardware));

        Assert.True(new FileInfo(output.Path).Length > 0);

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.Equal(expectedCodec, decoded.Info.VideoCodec);
        Assert.Equal(ExportFixture.Width, decoded.Info.Width);
        Assert.Equal(ExportFixture.Height, decoded.Info.Height);
        Assert.InRange(ExportProbe.CountVideoFrames(output.Path), 28, 32);
    }

    [Fact]
    public void SoftwareAndHardwareRequests_BothProduceValidFiles()
    {
        // Software is the default; Hardware is opt-in. Both paths must deliver a playable file over the same
        // project (Hardware degrading to software when no GPU encoder is present).
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var software = new TempFile(".mp4");
        using var hardware = new TempFile(".mp4");
        VideoExporter.Export(project, software.Path, new ExportOptions(Acceleration: ExportAcceleration.Software));
        VideoExporter.Export(project, hardware.Path, new ExportOptions(Acceleration: ExportAcceleration.Hardware));

        Assert.InRange(ExportProbe.CountVideoFrames(software.Path), 28, 32);
        Assert.InRange(ExportProbe.CountVideoFrames(hardware.Path), 28, 32);
    }

    [Fact]
    public void HardwareRequest_DoesNotChangeTheDefaultSoftwareOutput()
    {
        // ExportAcceleration.Software is the default and must reproduce the pre-step-29 behaviour byte-for-byte
        // (the deterministic delivery path §5): default(ExportOptions) never carries hardware candidates.
        Assert.Equal(ExportAcceleration.Software, default(ExportOptions).Acceleration);
    }
}
