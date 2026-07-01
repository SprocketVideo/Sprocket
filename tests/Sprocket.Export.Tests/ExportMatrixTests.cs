using Sprocket.Core.Model;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Exercises the step-27 export codec/container matrix: exporting the fixture into each container × video-codec ×
/// audio-codec combination and reopening the result to assert the container muxed cleanly and the streams carry
/// the expected codecs. These are real encode→decode round-trips (libx264/libx265/libsvtav1/libvpx-vp9/mpeg2/
/// prores + aac/mp3/pcm/flac/ac3/opus), so they need the FFmpeg + SkiaSharp natives the csproj pulls in — and an
/// FFmpeg build carrying those encoders (the bundled BtbN gpl-shared build does).
/// </summary>
public sealed class ExportMatrixTests
{
    // container, video codec, audio codec, expected decoded video codec name, expected decoded audio codec name.
    [Theory]
    [InlineData(ExportContainer.Mp4, ExportVideoCodec.H264, ExportAudioCodec.Aac, "h264", "aac")]
    [InlineData(ExportContainer.Mov, ExportVideoCodec.ProRes, ExportAudioCodec.Pcm, "prores", "pcm_s16le")]
    [InlineData(ExportContainer.Mkv, ExportVideoCodec.Hevc, ExportAudioCodec.Flac, "hevc", "flac")]
    [InlineData(ExportContainer.WebM, ExportVideoCodec.Vp9, ExportAudioCodec.Opus, "vp9", "opus")]
    [InlineData(ExportContainer.Mp4, ExportVideoCodec.Av1, ExportAudioCodec.Aac, "av1", "aac")]
    [InlineData(ExportContainer.MpegTs, ExportVideoCodec.Mpeg2, ExportAudioCodec.Mp3, "mpeg2video", "mp3")]
    public void Export_InContainerCodec_RoundTripsWithExpectedStreams(
        ExportContainer container, ExportVideoCodec videoCodec, ExportAudioCodec audioCodec,
        string expectedVideo, string expectedAudio)
    {
        var format = new ExportFormat(container, videoCodec, audioCodec);
        Assert.True(format.IsValid, $"{format} should be a valid combination");

        Project project = ExportFixture.BuildProject(withAudio: true);

        using var output = new TempFile(format.FileExtension);
        VideoExporter.Export(project, output.Path, new ExportOptions(Format: format));

        Assert.True(new FileInfo(output.Path).Length > 0);

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        ProbedMediaInfo info = decoded.Info;

        Assert.True(info.HasVideo);
        Assert.Equal(ExportFixture.Width, info.Width);
        Assert.Equal(ExportFixture.Height, info.Height);
        Assert.Equal(expectedVideo, info.VideoCodec);

        Assert.True(info.HasAudio, "the matrix export should carry an audio stream");
        Assert.Equal(expectedAudio, info.AudioCodec);

        // The file genuinely decodes end to end (not merely a valid header): full frame count within tolerance.
        Assert.InRange(CountVideoFrames(output.Path), 28, 32);
    }

    [Fact]
    public void Export_ProRes_ProducesA10BitStream()
    {
        // ProRes 422 encodes 10-bit 4:2:2 (yuv422p10le) — the matrix's high-bit-depth pixel-format path. The
        // reopened file's probe must report a 10-bit source.
        var format = new ExportFormat(ExportContainer.Mov, ExportVideoCodec.ProRes, ExportAudioCodec.Pcm);
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile(format.FileExtension);
        VideoExporter.Export(project, output.Path, new ExportOptions(Format: format));

        using MediaSource decoded = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.Equal("prores", decoded.Info.VideoCodec);
        Assert.Equal(10, decoded.Info.BitDepth);
        Assert.Contains("422", decoded.Info.PixelFormatName);
    }

    [Fact]
    public void Export_InvalidContainerCodecPairing_Throws()
    {
        // WebM only carries VP9/AV1 + Opus; H.264 in WebM is not a valid combination and must be rejected up front.
        var format = new ExportFormat(ExportContainer.WebM, ExportVideoCodec.H264, ExportAudioCodec.Aac);
        Assert.False(format.IsValid);

        Project project = ExportFixture.BuildProject(withAudio: true);
        using var output = new TempFile(format.FileExtension);

        Assert.Throws<ArgumentException>(() =>
            VideoExporter.Export(project, output.Path, new ExportOptions(Format: format)));
    }

    [Fact]
    public void Export_HigherQualityTier_ProducesLargerFile()
    {
        // The CRF quality tier is honoured: a High-quality H.264 export must be larger than a Low-quality one of
        // the same source (lower CRF → more bits).
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var high = new TempFile(".mp4");
        using var low = new TempFile(".mp4");
        VideoExporter.Export(project, high.Path, new ExportOptions(Quality: ExportQuality.High));
        VideoExporter.Export(project, low.Path, new ExportOptions(Quality: ExportQuality.Low));

        long highSize = new FileInfo(high.Path).Length;
        long lowSize = new FileInfo(low.Path).Length;
        Assert.True(highSize > lowSize,
            $"High quality should produce a larger file than Low: high={highSize}, low={lowSize}");
    }

    [Theory]
    [InlineData(3840, 2160, 3840, 2160)]   // UHD 4K: unchanged
    [InlineData(4096, 2160, 4096, 2160)]   // DCI 4K: unchanged (the cap)
    [InlineData(7680, 4320, 3840, 2160)]   // 8K → scaled down to fit 4K, aspect preserved
    [InlineData(3840, 2400, 3456, 2160)]   // taller than 16:9 → height-limited (3840*2160/2400 = 3456)
    [InlineData(321, 241, 320, 240)]       // sub-4K odd size → rounded down to even, not scaled
    public void ComputeExportResolution_CapsAt4KPreservingAspectAndEvenness(int w, int h, int expW, int expH)
    {
        (int width, int height) = VideoExporter.ComputeExportResolution(w, h);
        Assert.Equal(expW, width);
        Assert.Equal(expH, height);
        Assert.True(width <= VideoExporter.MaxExportWidth && height <= VideoExporter.MaxExportHeight);
        Assert.True((width & 1) == 0 && (height & 1) == 0, "capped dimensions must be even");
    }

    private static int CountVideoFrames(string path)
    {
        using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        int count = 0;
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            using (frame)
                count++;
        }
        return count;
    }

    /// <summary>A scratch output path (with the container's extension) that deletes itself on dispose.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(string extension) =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-matrix-{Guid.NewGuid():N}{extension}");

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
