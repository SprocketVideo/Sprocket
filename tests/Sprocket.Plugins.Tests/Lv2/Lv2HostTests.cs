using Sprocket.Plugins.Lv2;
using Xunit;

namespace Sprocket.Plugins.Tests.Lv2;

/// <summary>LV2 discovery + per-bundle error capture (PLAN.md step 59): loading never throws (ARCHITECTURE.md §15).</summary>
public sealed class Lv2HostTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-lv2host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnumerateBundles_returns_only_lv2_directories_with_a_manifest()
    {
        string root = NewDir();
        Directory.CreateDirectory(Path.Combine(root, "good.lv2"));
        File.WriteAllText(Path.Combine(root, "good.lv2", "manifest.ttl"), "");
        Directory.CreateDirectory(Path.Combine(root, "empty.lv2"));
        Directory.CreateDirectory(Path.Combine(root, "notabundle"));
        File.WriteAllText(Path.Combine(root, "notabundle", "manifest.ttl"), "");

        string[] found = Lv2Host.EnumerateBundles(root).Select(Path.GetFileName).ToArray()!;
        Assert.Equal(["good.lv2"], found);
    }

    [Fact]
    public void EnumerateBundles_missing_directory_is_empty() =>
        Assert.Empty(Lv2Host.EnumerateBundles(Path.Combine(Path.GetTempPath(), "sprocket-nope-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Load_records_an_error_for_a_bundle_whose_binary_is_not_a_library()
    {
        string root = NewDir();
        string bundle = Path.Combine(root, "amp.lv2");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), Lv2BundleReaderTests.Manifest);
        File.WriteAllText(Path.Combine(bundle, "amp.ttl"), Lv2BundleReaderTests.PluginTtl);
        File.WriteAllText(Path.Combine(bundle, "amp.so"), "not a shared library");

        var host = new Lv2Host();
        Lv2BundleLoad? result = host.Load(bundle);

        Assert.Null(result);
        Assert.Single(host.Errors);
        Assert.Equal(Path.GetFullPath(bundle), host.Errors[0].Source);
        Assert.Equal(0, host.LoadDirectory(root)); // and the directory scan survives it
    }

    [Fact]
    public void Load_records_an_error_for_a_malformed_manifest()
    {
        string root = NewDir();
        string bundle = Path.Combine(root, "bad.lv2");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), "this is not turtle at all <<<");

        var host = new Lv2Host();
        Assert.Null(host.Load(bundle));
        Assert.Contains("FormatException", host.Errors[0].Message);
    }

    [Fact]
    public void Forget_removes_the_bundle_and_its_errors()
    {
        string root = NewDir();
        string bundle = Path.Combine(root, "bad.lv2");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), "garbage <<<");
        var host = new Lv2Host();
        host.Load(bundle);
        Assert.Single(host.Errors);

        host.Forget(bundle + Path.DirectorySeparatorChar); // a trailing separator normalises away
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void CreateAudioEffect_unknown_id_is_null() =>
        Assert.Null(new Lv2Host().CreateAudioEffect("plugin.lv2.http://nope"));

    [Fact]
    public void DefaultSearchDirectories_includes_LV2_PATH_entries_first_and_dedupes()
    {
        string? original = Environment.GetEnvironmentVariable("LV2_PATH");
        string custom = Path.Combine(Path.GetTempPath(), "sprocket-lv2-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("LV2_PATH", custom + Path.PathSeparator + custom);
            IReadOnlyList<string> dirs = Lv2Host.DefaultSearchDirectories();
            Assert.Equal(custom, dirs[0]);
            Assert.Single(dirs, d => d == custom);
            Assert.True(dirs.Count > 1); // plus the OS defaults
        }
        finally
        {
            Environment.SetEnvironmentVariable("LV2_PATH", original);
        }
    }
}
