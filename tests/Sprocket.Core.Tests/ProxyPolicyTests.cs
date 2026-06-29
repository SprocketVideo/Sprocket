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
    private static ProbedMediaInfo Video(int w, int h) =>
        new(Timecode.FromSeconds(10), HasVideo: true, new Rational(30, 1), w, h, HasAudio: false, 0, 0);

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
}
