using Sprocket.Plugins.Ladspa;
using Xunit;

namespace Sprocket.Plugins.Tests.Ladspa;

/// <summary>Tests for LADSPA discovery and per-file error capture (PLAN.md step 59): loading never throws, one
/// bad library is recorded and the rest survive (ARCHITECTURE.md §15).</summary>
public sealed class LadspaHostTests
{
    [Fact]
    public void EnumerateLibraryFiles_returns_only_the_current_os_library_type()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-ladspa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in new[] { "a.dll", "b.so", "c.dylib", "d.txt" })
                File.WriteAllText(Path.Combine(dir, name), "not a real library");

            string[] found = LadspaHost.EnumerateLibraryFiles(dir).Select(Path.GetFileName).ToArray()!;
            string expected = OperatingSystem.IsWindows() ? "a.dll" : OperatingSystem.IsMacOS() ? "c.dylib" : "b.so";

            Assert.Contains(expected, found);
            Assert.DoesNotContain("d.txt", found);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnumerateLibraryFiles_missing_directory_is_empty() =>
        Assert.Empty(LadspaHost.EnumerateLibraryFiles(Path.Combine(Path.GetTempPath(), "sprocket-nope-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Load_records_an_error_for_a_file_that_is_not_a_native_library()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-ladspa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ext = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        string garbage = Path.Combine(dir, "garbage" + ext);
        File.WriteAllText(garbage, "this is not a shared library");
        try
        {
            var host = new LadspaHost();
            LadspaLibraryLoad? result = host.Load(garbage);

            Assert.Null(result);
            Assert.Single(host.Errors);
            Assert.Equal(Path.GetFullPath(garbage), host.Errors[0].Source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadDirectory_missing_directory_is_a_no_op()
    {
        var host = new LadspaHost();
        Assert.Equal(0, host.LoadDirectory(Path.Combine(Path.GetTempPath(), "sprocket-nope-" + Guid.NewGuid().ToString("N"))));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void DefaultSearchDirectories_includes_LADSPA_PATH_entries_first_and_dedupes()
    {
        string? original = Environment.GetEnvironmentVariable("LADSPA_PATH");
        string custom = Path.Combine(Path.GetTempPath(), "sprocket-ladspa-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("LADSPA_PATH", custom + Path.PathSeparator + custom);
            IReadOnlyList<string> dirs = LadspaHost.DefaultSearchDirectories();

            Assert.Equal(custom, dirs[0]);
            Assert.Single(dirs, d => d == custom); // duplicate collapsed
        }
        finally
        {
            Environment.SetEnvironmentVariable("LADSPA_PATH", original);
        }
    }

    [Fact]
    public void DefaultSearchDirectories_is_never_empty_on_supported_platforms() =>
        Assert.NotEmpty(LadspaHost.DefaultSearchDirectories());
}
