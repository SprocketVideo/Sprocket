using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Tests for the pure proxy decisions (PLAN.md step 18): target-resolution sizing per tier (capped at the 1080p
/// preview ceiling, even dimensions, aspect preserved) and the needs-proxy "skip light-enough sources" heuristic.
/// </summary>
public class ProxyPolicyTests
{
    // Defaults describe a plain 8-bit source with no codec facts — the "light" baseline the format rules skip.
    // chroma defaults to "" (a probe recorded before the structured field existed) so the name-based fallback
    // is what most cases exercise; pass it explicitly to test the structured path.
    private static ProbedMediaInfo Video(
        int w, int h, string codec = "", string pixFmt = "", int bitDepth = 8, int fps = 30, string chroma = "") =>
        new(Timecode.FromSeconds(10), HasVideo: true, new Rational(fps, 1), w, h, HasAudio: false, 0, 0,
            VideoCodec: codec, PixelFormatName: pixFmt, BitDepth: bitDepth, ChromaSubsampling: chroma);

    [Fact]
    public void Half_Tier_Halves_A_4K_Source_Within_The_Ceiling()
    {
        // 3840×2160 → half = 1920×1080, exactly the ceiling.
        Resolution r = ProxyPolicy.TargetResolution(3840, 2160, ProxyTier.Half);
        Assert.Equal(1920, r.Width);
        Assert.Equal(1080, r.Height);
    }

    [Fact]
    public void Quarter_Tier_Goes_Smaller_Than_Half()
    {
        Resolution r = ProxyPolicy.TargetResolution(3840, 2160, ProxyTier.Quarter);
        Assert.Equal(960, r.Width);
        Assert.Equal(540, r.Height);
    }

    [Fact]
    public void FullHd_Tier_Caps_A_Large_Source_At_1080p_Preserving_Aspect()
    {
        // 8K 16:9 at the full tier still clamps under the 1080p box → 1920×1080.
        Resolution r = ProxyPolicy.TargetResolution(7680, 4320, ProxyTier.FullHd);
        Assert.Equal(1920, r.Width);
        Assert.Equal(1080, r.Height);
    }

    [Fact]
    public void Ceiling_Clamp_Dominates_When_Tier_Would_Exceed_It()
    {
        // 3000-wide source at Half = 1500 wide, still under 1920, so the tier wins (no extra clamp).
        Resolution half = ProxyPolicy.TargetResolution(3000, 1688, ProxyTier.Half);
        Assert.Equal(1500, half.Width);
        Assert.True(half.Width <= ProxyPolicy.CeilingWidth && half.Height <= ProxyPolicy.CeilingHeight);
    }

    [Fact]
    public void Target_Dimensions_Are_Even()
    {
        // An odd-ish source must still produce even target dimensions (yuv420p chroma).
        Resolution r = ProxyPolicy.TargetResolution(4099, 2161, ProxyTier.Half);
        Assert.Equal(0, r.Width % 2);
        Assert.Equal(0, r.Height % 2);
    }

    [Fact]
    public void Non_Positive_Source_Yields_Empty()
    {
        Assert.Equal(new Resolution(0, 0), ProxyPolicy.TargetResolution(0, 0, ProxyTier.Half));
    }

    [Fact]
    public void NeedsProxy_True_For_Source_Above_The_Ceiling()
    {
        Assert.True(ProxyPolicy.NeedsProxy(Video(3840, 2160), ProxyTier.Half));
    }

    [Fact]
    public void NeedsProxy_False_For_Source_At_Or_Below_1080p()
    {
        Assert.False(ProxyPolicy.NeedsProxy(Video(1920, 1080), ProxyTier.Half));
        Assert.False(ProxyPolicy.NeedsProxy(Video(1280, 720), ProxyTier.Quarter));
    }

    [Fact]
    public void NeedsProxy_False_When_Source_Has_No_Video()
    {
        var audioOnly = new ProbedMediaInfo(
            Timecode.FromSeconds(10), HasVideo: false, Rational.Zero, 0, 0, HasAudio: true, 48000, 2);
        Assert.False(ProxyPolicy.NeedsProxy(audioOnly, ProxyTier.Half));
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("av1")]
    [InlineData("vp9")]
    public void Demanding_Long_Gop_Codecs_Qualify_At_1080p(string codec)
    {
        ProxyDecision d = ProxyPolicy.Decide(Video(1920, 1080, codec), ProxyTier.Half);
        Assert.Equal(ProxyReason.DemandingCodec, d.Reason);
        Assert.True(d.Wanted);
    }

    [Fact]
    public void Demanding_Codec_Below_1080p_Class_Does_Not_Qualify()
    {
        Assert.Equal(ProxyReason.None, ProxyPolicy.Decide(Video(1280, 720, "hevc"), ProxyTier.Half).Reason);
    }

    [Fact]
    public void Vertical_1080p_Footage_Exceeds_The_Ceiling_And_Proxies_On_Resolution()
    {
        // 1080×1920 phone footage is taller than the ceiling box, so the oversize rule catches it first.
        ProxyDecision d = ProxyPolicy.Decide(Video(1080, 1920, "hevc"), ProxyTier.Half);
        Assert.Equal(ProxyReason.OversizeResolution, d.Reason);
    }

    [Fact]
    public void Ultrawide_1080p_Class_Qualifies_On_Either_Axis()
    {
        // 1920×800 scope footage sits inside the ceiling box, but its width is 1080p-class — the format
        // rules key off either axis, not both.
        ProxyDecision d = ProxyPolicy.Decide(Video(1920, 800, "hevc"), ProxyTier.Half);
        Assert.Equal(ProxyReason.DemandingCodec, d.Reason);
    }

    [Fact]
    public void Ten_Bit_H264_At_1080p_Is_Deep_Color()
    {
        ProxyDecision d = ProxyPolicy.Decide(Video(1920, 1080, "h264", "yuv420p10le", bitDepth: 10), ProxyTier.Half);
        Assert.Equal(ProxyReason.DeepColor, d.Reason);
    }

    [Fact]
    public void Eight_Bit_422_At_1080p_Is_Deep_Color()
    {
        ProxyDecision d = ProxyPolicy.Decide(Video(1920, 1080, "h264", "yuv422p"), ProxyTier.Half);
        Assert.Equal(ProxyReason.DeepColor, d.Reason);
    }

    [Fact]
    public void Plain_8_Bit_H264_At_1080p_Stays_Unproxied()
    {
        Assert.Equal(ProxyReason.None,
            ProxyPolicy.Decide(Video(1920, 1080, "h264", "yuv420p"), ProxyTier.Half).Reason);
    }

    [Fact]
    public void ProRes_Is_Exempt_From_The_Format_Rules_But_Not_The_Ceiling()
    {
        // 1080p ProRes is 10-bit 4:2:2 yet cheap to decode/seek — the easy-intra exemption beats DeepColor.
        Assert.Equal(ProxyReason.None,
            ProxyPolicy.Decide(Video(1920, 1080, "prores", "yuv422p10le", bitDepth: 10), ProxyTier.Half).Reason);

        // 4K ProRes still proxies on resolution, like any oversize source.
        Assert.Equal(ProxyReason.OversizeResolution,
            ProxyPolicy.Decide(Video(3840, 2160, "prores", "yuv422p10le", bitDepth: 10), ProxyTier.Half).Reason);
    }

    [Fact]
    public void Format_Triggered_Proxy_Follows_The_Tier_And_Allows_A_Same_Resolution_Target()
    {
        var hevc1080 = Video(1920, 1080, "hevc");

        // FullHd → a same-resolution codec-conversion proxy (the equality case the oversize rule rejects).
        ProxyDecision full = ProxyPolicy.Decide(hevc1080, ProxyTier.FullHd);
        Assert.Equal(ProxyReason.DemandingCodec, full.Reason);
        Assert.Equal(new Resolution(1920, 1080), full.Target);

        Assert.Equal(new Resolution(960, 540), ProxyPolicy.Decide(hevc1080, ProxyTier.Half).Target);
        Assert.Equal(new Resolution(480, 270), ProxyPolicy.Decide(hevc1080, ProxyTier.Quarter).Target);
    }

    [Fact]
    public void High_Frame_Rate_Alone_Is_Not_A_Static_Trigger()
    {
        // 1080p60 plain H.264 usually plays fine; HFR that struggles is caught by the runtime drop monitor.
        Assert.Equal(ProxyReason.None,
            ProxyPolicy.Decide(Video(1920, 1080, "h264", "yuv420p", fps: 60), ProxyTier.Half).Reason);
    }

    [Fact]
    public void Oversize_Sources_Report_The_Resolution_Reason()
    {
        Assert.Equal(ProxyReason.OversizeResolution, ProxyPolicy.Decide(Video(3840, 2160), ProxyTier.Half).Reason);
    }

    [Fact]
    public void NeedsProxy_Agrees_With_Decide()
    {
        var cases = new[]
        {
            Video(3840, 2160),
            Video(1920, 1080, "hevc"),
            Video(1920, 1080, "h264", "yuv420p"),
            Video(1280, 720, "hevc"),
        };
        foreach (var info in cases)
            Assert.Equal(ProxyPolicy.Decide(info, ProxyTier.Half).Wanted, ProxyPolicy.NeedsProxy(info, ProxyTier.Half));
    }

    [Theory]
    // The formats the old name-substring test missed: FFmpeg names that don't spell out their subsampling.
    [InlineData("nv16", "422")]     // 4:2:2 semi-planar
    [InlineData("gbrp", "444")]     // planar RGB — full chroma
    [InlineData("rgb24", "444")]
    [InlineData("rgba", "444")]
    [InlineData("nv24", "444")]
    public void Structured_Chroma_Catches_Formats_Whose_Names_Do_Not_Spell_It_Out(string pixFmt, string chroma)
    {
        var info = Video(1920, 1080, "h264", pixFmt, chroma: chroma);
        Assert.True(ProxyPolicy.IsFullOrHalfChroma(info));
        Assert.Equal(ProxyReason.DeepColor, ProxyPolicy.Decide(info, ProxyTier.Half).Reason);
        // The legacy name guess is exactly what could not see these.
        Assert.False(ProxyPolicy.NameSuggestsFullOrHalfChroma(pixFmt));
    }

    [Theory]
    [InlineData("420")]  // ordinary 8-bit 4:2:0
    [InlineData("400")]  // monochrome — no chroma at all, cheap
    [InlineData("411")]
    [InlineData("410")]
    public void Structured_Chroma_Leaves_Subsampled_And_Monochrome_Sources_Alone(string chroma)
    {
        var info = Video(1920, 1080, "h264", "somefmt", chroma: chroma);
        Assert.False(ProxyPolicy.IsFullOrHalfChroma(info));
        Assert.Equal(ProxyReason.None, ProxyPolicy.Decide(info, ProxyTier.Half).Reason);
    }

    [Fact]
    public void A_Probe_Without_Structured_Chroma_Falls_Back_To_The_Format_Name()
    {
        // Projects saved before the field existed still classify the common spelled-out cases without re-import.
        Assert.True(ProxyPolicy.IsFullOrHalfChroma(Video(1920, 1080, "h264", "yuv422p")));
        Assert.True(ProxyPolicy.IsFullOrHalfChroma(Video(1920, 1080, "h264", "yuv444p10le")));
        Assert.False(ProxyPolicy.IsFullOrHalfChroma(Video(1920, 1080, "h264", "yuv420p")));
    }

    [Fact]
    public void Structured_Chroma_Overrides_A_Misleading_Format_Name()
    {
        // The descriptor is authoritative; a name that happens to contain the digits does not override it.
        Assert.False(ProxyPolicy.IsFullOrHalfChroma(Video(1920, 1080, "h264", "yuv422p", chroma: "420")));
    }

    [Fact]
    public void IsDemandingFormat_Is_The_Tier_Independent_Difficulty_Predicate()
    {
        Assert.True(ProxyPolicy.IsDemandingFormat(Video(1280, 720, "hevc")));   // size-independent
        Assert.True(ProxyPolicy.IsDemandingFormat(Video(1920, 1080, "h264", "yuv420p10le", bitDepth: 10)));
        Assert.False(ProxyPolicy.IsDemandingFormat(Video(1920, 1080, "h264", "yuv420p")));
        Assert.False(ProxyPolicy.IsDemandingFormat(Video(1920, 1080, "prores", "yuv422p10le", bitDepth: 10)));
    }
}
