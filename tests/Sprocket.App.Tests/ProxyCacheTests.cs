using Sprocket.App.Proxy;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the proxy cache's pure key helper (PLAN.md step 18): the cache file name is a stable
/// function of source identity (path + size + modified time) and target size, so a cached proxy is reused only
/// when nothing relevant changed and any change forks to a new file (no stale reuse). The proxy worker, decode,
/// and feed-switch rest on these plus manual verification (the App is a UI-bound WinExe).
/// </summary>
public class ProxyCacheTests
{
    [Fact]
    public void KeyFileName_Is_Stable_For_Identical_Inputs()
    {
        string a = ProxyCache.KeyFileName(@"C:\media\clip.mp4", 1000, 12345, 1920, 1080);
        string b = ProxyCache.KeyFileName(@"C:\media\clip.mp4", 1000, 12345, 1920, 1080);
        Assert.Equal(a, b);
        Assert.EndsWith(".mp4", a);
    }

    [Fact]
    public void KeyFileName_Is_Case_And_Separator_Insensitive_For_The_Same_File()
    {
        // The same file referenced with different case / separators must not fork the cache.
        string a = ProxyCache.KeyFileName(@"C:\Media\Clip.mp4", 1000, 12345, 1920, 1080);
        string b = ProxyCache.KeyFileName("c:/media/clip.mp4", 1000, 12345, 1920, 1080);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(@"C:\media\other.mp4", 1000, 12345, 1920, 1080)] // different path
    [InlineData(@"C:\media\clip.mp4", 2000, 12345, 1920, 1080)]  // different size (re-encoded source)
    [InlineData(@"C:\media\clip.mp4", 1000, 99999, 1920, 1080)]  // different modified time
    [InlineData(@"C:\media\clip.mp4", 1000, 12345, 960, 540)]    // different target tier
    public void KeyFileName_Changes_When_Any_Identity_Component_Changes(
        string path, long length, long ticks, int w, int h)
    {
        string baseline = ProxyCache.KeyFileName(@"C:\media\clip.mp4", 1000, 12345, 1920, 1080);
        string other = ProxyCache.KeyFileName(path, length, ticks, w, h);
        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Directory_Is_Non_Empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProxyCache.Directory()));
    }
}
