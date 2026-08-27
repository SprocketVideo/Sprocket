using Sprocket.Plugins.Frei0r;
using Xunit;

namespace Sprocket.Plugins.Tests.Frei0r;

/// <summary>frei0r discovery + per-file error capture (PLAN.md step 59): loading never throws (ARCHITECTURE.md §15).</summary>
public sealed class Frei0rHostTests
{
    private static string LibExtension => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    [Fact]
    public void EnumerateLibraryFiles_returns_only_the_current_os_library_type()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-frei0r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in new[] { "a.dll", "b.so", "c.dylib", "d.txt" })
                File.WriteAllText(Path.Combine(dir, name), "x");
            string[] found = Frei0rHost.EnumerateLibraryFiles(dir).Select(Path.GetFileName).ToArray()!;
            Assert.Single(found);
            Assert.EndsWith(LibExtension, found[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_records_an_error_for_a_file_that_is_not_a_native_library()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-frei0r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string garbage = Path.Combine(dir, "garbage" + LibExtension);
        File.WriteAllText(garbage, "not a shared library");
        try
        {
            var host = new Frei0rHost();
            Assert.Null(host.Load(garbage));
            Assert.Single(host.Errors);
            Assert.Equal(Path.GetFullPath(garbage), host.Errors[0].Source);
            Assert.Equal(0, host.LoadDirectory(dir));

            host.Forget(garbage);
            Assert.Empty(host.Errors);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DefaultSearchDirectories_includes_FREI0R_PATH_entries_first_and_dedupes()
    {
        string? original = Environment.GetEnvironmentVariable("FREI0R_PATH");
        string custom = Path.Combine(Path.GetTempPath(), "sprocket-frei0r-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("FREI0R_PATH", custom + Path.PathSeparator + custom);
            IReadOnlyList<string> dirs = Frei0rHost.DefaultSearchDirectories();
            Assert.Equal(custom, dirs[0]);
            Assert.Single(dirs, d => d == custom);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FREI0R_PATH", original);
        }
    }

    [Theory]
    [InlineData("Glow", "glow")]
    [InlineData("three point balance", "threepointbalance")]
    [InlineData("!!!", "unnamed")]
    public void Sanitize_produces_id_safe_keys(string input, string expected) =>
        Assert.Equal(expected, Frei0rParameterMapping.Sanitize(input));
}
