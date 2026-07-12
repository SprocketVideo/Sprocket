using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// The per-vendor constant-quality mapping hardware encoders use so the export dialog's quality choice is
/// honoured on the GPU too (previously the CRF was silently dropped and hardware ran bitrate-only). Pure tests
/// over <see cref="MediaEncoder.DescribeHardwareQualityOptions"/> / <see cref="MediaEncoder.NormalizeCrfTo51"/> —
/// no GPU or FFmpeg natives needed. Each vendor's option name, mechanism, and value scale differs from software
/// CRF, so the exact keys/values are pinned here.
/// </summary>
public sealed class HardwareQualityMappingTests
{
    private static Dictionary<string, string> Describe(string encoderName, int crf51)
    {
        IReadOnlyList<(string Key, string Value)>? entries = MediaEncoder.DescribeHardwareQualityOptions(encoderName, crf51);
        Assert.NotNull(entries);
        return entries.ToDictionary(e => e.Key, e => e.Value);
    }

    [Fact]
    public void Nvenc_UsesVbrConstantQuality_WithTheDocumentedOffset()
    {
        // NVENC's cq is ~5 units more generous than x264 CRF at the same number (cq 28 ≈ crf 23), so the mapping
        // offsets by +5 to roughly match the software look at the same slider position.
        Dictionary<string, string> opts = Describe("h264_nvenc", 23);
        Assert.Equal("vbr", opts["rc"]);
        Assert.Equal("28", opts["cq"]);
    }

    [Fact]
    public void Nvenc_ClampsTheOffsetAtTheTopOfTheScale()
    {
        Assert.Equal("51", Describe("hevc_nvenc", 50)["cq"]); // 50 + 5 clamps to the 0–51 scale top
    }

    [Fact]
    public void Qsv_UsesIcqViaGlobalQuality_PassedThroughUnchanged()
    {
        // QSV selects ICQ mode when global_quality is set and no bit rate is; the value is on the same 1–51 scale.
        Dictionary<string, string> opts = Describe("h264_qsv", 23);
        Assert.Equal("23", opts["global_quality"]);
        Assert.DoesNotContain("rc", opts.Keys);
    }

    [Fact]
    public void Amf_UsesConstantQp_OnAllFrameTypes()
    {
        Dictionary<string, string> opts = Describe("h264_amf", 23);
        Assert.Equal("cqp", opts["rc"]);
        Assert.Equal("23", opts["qp_i"]);
        Assert.Equal("23", opts["qp_p"]);
        Assert.Equal("23", opts["qp_b"]);
    }

    [Fact]
    public void Vaapi_UsesCqpRateModeWithQp()
    {
        Dictionary<string, string> opts = Describe("hevc_vaapi", 23);
        Assert.Equal("CQP", opts["rc_mode"]);
        Assert.Equal("23", opts["qp"]);
    }

    [Theory]
    [InlineData(18, 65)]   // visually-lossless CRF → VT q 65 (the commonly-cited high-quality value)
    [InlineData(51, 1)]    // worst CRF → the scale floor (0 would mean "unset")
    [InlineData(1, 98)]    // near-lossless CRF → near the top of VT's scale
    public void VideoToolbox_InvertsOntoItsZeroToHundredScale_ViaTheQscaleMechanism(int crf, int expectedQ)
    {
        // VT quality is 0–100 with HIGHER = better (inverted vs CRF) and is carried through the generic qscale
        // mechanism: flags=+qscale with global_quality = q × FF_QP2LAMBDA (118), not a private dict key.
        Dictionary<string, string> opts = Describe("h264_videotoolbox", crf);
        Assert.Equal("+qscale", opts["flags"]);
        Assert.Equal((expectedQ * 118).ToString(), opts["global_quality"]);
    }

    [Theory]
    [InlineData("libx264")]      // a software encoder name is not a hardware vendor
    [InlineData("h264_magic")]   // unknown vendor suffix
    [InlineData("plain")]        // no vendor suffix at all
    public void UnknownVendor_YieldsNull_SoTheCallerFallsBackToADefaultBitrate(string encoderName)
    {
        Assert.Null(MediaEncoder.DescribeHardwareQualityOptions(encoderName, 23));
    }

    [Theory]
    [InlineData("libsvtav1", 63, 51)]  // AV1's scale top maps to the x264 scale top
    [InlineData("libsvtav1", 30, 24)]  // AV1's High-tier CRF lands near x264's High territory
    [InlineData("libvpx-vp9", 45, 36)] // VP9 Low tier
    [InlineData("libx264", 23, 23)]    // already on the 0–51 domain: unchanged
    [InlineData("libx265", 18, 18)]
    public void NormalizeCrfTo51_RescalesTheAv1Vp9Domain_AndPassesOthersThrough(string codec, int crf, int expected)
    {
        Assert.Equal(expected, MediaEncoder.NormalizeCrfTo51(codec, crf));
    }
}
