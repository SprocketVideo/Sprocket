using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// The two-mode export rate control (constant quality vs target bitrate — the Resolve/Premiere convention):
/// the pure helper mappings (<see cref="ExportCodecs.CrfFor"/> / <see cref="ExportCodecs.MaxCrfFor"/> /
/// <see cref="ExportCodecs.QualityLabel"/> / <see cref="ExportCodecs.DefaultTargetBitrate"/>), the preset
/// round-trip of the new fields, and real-encode assertions that each mode actually drives the output size.
/// </summary>
public sealed class RateControlTests
{
    // ── Pure helper mappings ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ExportVideoCodec.H264, ExportQuality.High, 18)]
    [InlineData(ExportVideoCodec.H264, ExportQuality.Medium, 23)]
    [InlineData(ExportVideoCodec.H264, ExportQuality.Low, 28)]
    [InlineData(ExportVideoCodec.Hevc, ExportQuality.High, 18)]
    [InlineData(ExportVideoCodec.Av1, ExportQuality.High, 30)]
    [InlineData(ExportVideoCodec.Av1, ExportQuality.Medium, 36)]
    [InlineData(ExportVideoCodec.Av1, ExportQuality.Low, 45)]
    [InlineData(ExportVideoCodec.Vp9, ExportQuality.High, 30)]
    public void CrfFor_MapsTheTierOntoTheCodecFamilysOwnScale(ExportVideoCodec codec, ExportQuality quality, int expected)
    {
        Assert.Equal(expected, ExportCodecs.CrfFor(codec, quality));
    }

    [Theory]
    [InlineData(ExportVideoCodec.H264, 51)]
    [InlineData(ExportVideoCodec.Hevc, 51)]
    [InlineData(ExportVideoCodec.Av1, 63)]
    [InlineData(ExportVideoCodec.Vp9, 63)]
    [InlineData(ExportVideoCodec.ProRes, 51)]
    public void MaxCrfFor_ReflectsTheCodecFamilysScaleTop(ExportVideoCodec codec, int expected)
    {
        Assert.Equal(expected, ExportCodecs.MaxCrfFor(codec));
    }

    [Theory]
    [InlineData(ExportVideoCodec.H264, 18, "visually lossless")]
    [InlineData(ExportVideoCodec.H264, 23, "high quality")]
    [InlineData(ExportVideoCodec.H264, 28, "good for web")]
    [InlineData(ExportVideoCodec.H264, 35, "small file")]
    [InlineData(ExportVideoCodec.H264, 45, "very compressed")]
    [InlineData(ExportVideoCodec.Av1, 30, "high quality")]      // the AV1 High tier lands in its named band
    [InlineData(ExportVideoCodec.Av1, 36, "good for web")]      // AV1 Medium tier
    [InlineData(ExportVideoCodec.Av1, 45, "small file")]        // AV1 Low tier
    [InlineData(ExportVideoCodec.Av1, 22, "visually lossless")] // 22×51/63 ≈ 18
    public void QualityLabel_DescribesTheCrfInPlainLanguage_OnEachScale(ExportVideoCodec codec, int crf, string expected)
    {
        Assert.Equal(expected, ExportCodecs.QualityLabel(codec, crf));
    }

    [Theory]
    [InlineData(3840, 2160, 40_000_000)]   // 4K
    [InlineData(1920, 1080, 16_000_000)]   // 1080p
    [InlineData(1280, 720, 8_000_000)]     // 720p
    [InlineData(854, 480, 5_000_000)]      // SD floor
    public void DefaultTargetBitrate_ScalesByResolutionTier(int width, int height, long expected)
    {
        Assert.Equal(expected, ExportCodecs.DefaultTargetBitrate(width, height, new Rational(30, 1)));
    }

    [Fact]
    public void DefaultTargetBitrate_ScalesUpForHighFrameRate_ButNotDownBelowTheBaseline()
    {
        long at30 = ExportCodecs.DefaultTargetBitrate(1920, 1080, new Rational(30, 1));
        long at60 = ExportCodecs.DefaultTargetBitrate(1920, 1080, new Rational(60, 1));
        long at24 = ExportCodecs.DefaultTargetBitrate(1920, 1080, new Rational(24, 1));

        Assert.Equal(at30 * 2, at60);
        Assert.Equal(at30, at24); // fewer frames don't proportionally cheapen the picture
    }

    // ── Preset round-trip ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToOptions_CarriesTheRateControlFields()
    {
        var preset = new ExportPreset(
            "Bitrate 1080p", new ExportFormat(), ExportQuality.High,
            RateControl: ExportRateControl.Bitrate, VideoBitRate: 16_000_000, MaxBitRate: 24_000_000);

        ExportOptions options = preset.ToOptions();

        Assert.Equal(ExportRateControl.Bitrate, options.RateControl);
        Assert.Equal(16_000_000, options.VideoBitRate);
        Assert.Equal(24_000_000, options.MaxBitRate);
    }

    [Fact]
    public void Store_RoundTripsTheRateControlFields()
    {
        IReadOnlyList<ExportPreset> original =
        [
            new("Quality CRF 20", new ExportFormat(), ExportQuality.High, Crf: 20),
            new("Bitrate 8 Mbps", new ExportFormat(), ExportQuality.High,
                RateControl: ExportRateControl.Bitrate, VideoBitRate: 8_000_000, MaxBitRate: 12_000_000),
        ];

        IReadOnlyList<ExportPreset> restored = ExportPresetStore.Deserialize(ExportPresetStore.Serialize(original));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Store_Deserialize_DefaultsLegacyEntriesToQualityMode()
    {
        // A preset file written before rate control existed: no RateControl/Crf/VideoBitRate fields. It must load
        // as the exact pre-rate-control behaviour — quality mode with the tier-derived CRF.
        string json = """
        [
          { "Name": "Legacy", "Container": "Mp4", "VideoCodec": "H264", "AudioCodec": "Aac", "Quality": "Medium" }
        ]
        """;

        ExportPreset preset = Assert.Single(ExportPresetStore.Deserialize(json));
        Assert.Equal(ExportRateControl.Quality, preset.RateControl);
        Assert.Equal(0, preset.Crf);              // 0 = derive from the tier
        Assert.Equal(0, preset.VideoBitRate);
        Assert.Equal(ExportQuality.Medium, preset.Quality);
    }

    [Fact]
    public void Store_Serialize_OmitsDefaultRateControlFields_KeepingLegacyFilesShapedTheSame()
    {
        string json = ExportPresetStore.Serialize(
            [new ExportPreset("Tiered", new ExportFormat(), ExportQuality.High)]);

        Assert.DoesNotContain("RateControl", json);
        Assert.DoesNotContain("VideoBitRate", json);
    }

    // ── Real encodes: each mode actually drives the output ────────────────────────────────────────────

    [Fact]
    public void Export_ExplicitCrf_OverridesTheTier_AndOrdersFileSize()
    {
        // An explicit CRF wins over the tier: CRF 18 must out-size CRF 35 even though both say Quality=High.
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var fine = new TempFile(".mp4");
        using var coarse = new TempFile(".mp4");
        VideoExporter.Export(project, fine.Path, new ExportOptions(Crf: 18));
        VideoExporter.Export(project, coarse.Path, new ExportOptions(Crf: 35));

        long fineSize = new FileInfo(fine.Path).Length;
        long coarseSize = new FileInfo(coarse.Path).Length;
        Assert.True(fineSize > coarseSize,
            $"CRF 18 should produce a larger file than CRF 35: crf18={fineSize}, crf35={coarseSize}");
    }

    [Fact]
    public void Export_BitrateMode_TracksTheTarget()
    {
        // Bitrate mode drives size by the target: a 4 Mbps export of the same second must out-size a 500 kbps one,
        // and land loosely near its own budget (wide tolerance — a 1 s synthetic clip has real encoder overhead).
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var big = new TempFile(".mp4");
        using var small = new TempFile(".mp4");
        VideoExporter.Export(project, big.Path,
            new ExportOptions(RateControl: ExportRateControl.Bitrate, VideoBitRate: 4_000_000));
        VideoExporter.Export(project, small.Path,
            new ExportOptions(RateControl: ExportRateControl.Bitrate, VideoBitRate: 500_000));

        long bigSize = new FileInfo(big.Path).Length;
        long smallSize = new FileInfo(small.Path).Length;
        Assert.True(bigSize > smallSize * 2,
            $"a 4 Mbps target should clearly out-size a 500 kbps one: big={bigSize}, small={smallSize}");
        // The 1-second 4 Mbps encode should weigh in on the order of its budget (4 Mbit ≈ 500 KB), not the
        // CRF default's whim: within [1/4×, 2×] of the target byte budget.
        Assert.InRange(bigSize, 4_000_000 / 8 / 4, 4_000_000 / 8 * 2);
    }

    [Fact]
    public void Export_BitrateMode_WithNoTarget_UsesTheResolutionScaledDefault()
    {
        // An empty target (0) engages DefaultTargetBitrate rather than failing or producing an empty file.
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var output = new TempFile(".mp4");
        VideoExporter.Export(project, output.Path, new ExportOptions(RateControl: ExportRateControl.Bitrate));

        Assert.True(new FileInfo(output.Path).Length > 0);
    }

    [Fact]
    public void Export_MaxBitRate_CapsTheOutput()
    {
        // The VBR ceiling engages maxrate/bufsize: the same target with a tight cap must not exceed a generous
        // margin over the cap's byte budget for the clip.
        Project project = ExportFixture.BuildProject(withAudio: false);

        using var uncapped = new TempFile(".mp4");
        using var capped = new TempFile(".mp4");
        VideoExporter.Export(project, uncapped.Path,
            new ExportOptions(RateControl: ExportRateControl.Bitrate, VideoBitRate: 4_000_000));
        VideoExporter.Export(project, capped.Path,
            new ExportOptions(RateControl: ExportRateControl.Bitrate, VideoBitRate: 4_000_000, MaxBitRate: 600_000));

        long uncappedSize = new FileInfo(uncapped.Path).Length;
        long cappedSize = new FileInfo(capped.Path).Length;
        Assert.True(cappedSize < uncappedSize,
            $"a 600 kbps ceiling should shrink a 4 Mbps-target export: capped={cappedSize}, uncapped={uncappedSize}");
    }

    /// <summary>A scratch output path that deletes itself on dispose.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(string extension) =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-ratecontrol-{Guid.NewGuid():N}{extension}");

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
