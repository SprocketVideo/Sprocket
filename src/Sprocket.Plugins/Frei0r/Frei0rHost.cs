using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Plugins.Frei0r;

/// <summary>The result of loading one frei0r library (PLAN.md step 59): the CPU effect it contributes (null for a
/// source/mixer), its catalog descriptor, per-plugin warnings, and the plugin version for the Plugin Manager row.</summary>
public sealed record Frei0rLibraryLoad(
    string Path,
    ICpuVideoEffect? Effect,
    IReadOnlyList<string> Warnings,
    string? Version)
{
    /// <summary>The descriptor the composition root registers (null when nothing is hostable).</summary>
    public EffectDescriptor? Descriptor => Effect?.Descriptor;
}

/// <summary>
/// Discovers and hosts frei0r video plugins (PLAN.md step 59) on the CPU-effect seam: scans the standard frei0r
/// locations (<c>FREI0R_PATH</c> plus per-OS defaults such as <c>/usr/lib/frei0r-1</c>), loads each library, and
/// exposes its filter as an <see cref="ICpuVideoEffect"/> for the composition root to register with the render
/// pipeline. Every per-file failure is recorded in <see cref="Errors"/> rather than thrown (§15). Loaded modules
/// stay mapped for the session (see <see cref="Frei0rLibrary"/>).
/// </summary>
public sealed class Frei0rHost
{
    private readonly List<Frei0rLibrary> _libraries = [];
    private readonly List<PluginLoadError> _errors = [];

    internal IReadOnlyList<Frei0rLibrary> Libraries => _libraries;

    /// <summary>Everything that went wrong while loading; loading never throws.</summary>
    public IReadOnlyList<PluginLoadError> Errors => _errors;

    /// <summary>Loads every frei0r library under <paramref name="directory"/>; returns how many contributed a filter.</summary>
    public int LoadDirectory(string directory)
    {
        int loaded = 0;
        foreach (string file in EnumerateLibraryFiles(directory))
            if (Load(file) is { Effect: not null })
                loaded++;
        return loaded;
    }

    /// <summary>Loads one library file; returns null (with an <see cref="Errors"/> entry) if it cannot be loaded or is
    /// not a frei0r library. A file already loaded from the same full path returns the existing result.</summary>
    public Frei0rLibraryLoad? Load(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            _errors.Add(new PluginLoadError(path, $"{ex.GetType().Name}: {ex.Message}"));
            return null;
        }

        Frei0rLibrary? existing = _libraries.FirstOrDefault(l => string.Equals(l.Path, full, StringComparison.Ordinal));
        if (existing is not null)
            return ToResult(existing);

        try
        {
            Frei0rLibrary library = Frei0rLibrary.Load(full);
            _libraries.Add(library);
            foreach (string note in library.Skipped)
                _errors.Add(new PluginLoadError(full, note));
            return ToResult(library);
        }
        catch (Exception ex)
        {
            _errors.Add(new PluginLoadError(full, $"{ex.GetType().Name}: {ex.Message}"));
            return null;
        }
    }

    /// <summary>Registers an already-built library (the in-process test fixture path).</summary>
    internal Frei0rLibraryLoad Add(Frei0rLibrary library)
    {
        _libraries.Add(library);
        foreach (string note in library.Skipped)
            _errors.Add(new PluginLoadError(library.Path, note));
        return ToResult(library);
    }

    private static Frei0rLibraryLoad ToResult(Frei0rLibrary library) =>
        new(library.Path, library.Effect, library.Skipped, library.Info.Version);

    /// <summary>The hosted CPU effect for a frei0r effect type id, or null.</summary>
    public ICpuVideoEffect? FindEffect(string effectTypeId)
    {
        foreach (Frei0rLibrary library in _libraries)
            if (library.Effect is { } effect && effect.Descriptor.Id == effectTypeId)
                return effect;
        return null;
    }

    /// <summary>Drops the host's reference to a library (the module stays mapped) and its recorded errors.</summary>
    public void Forget(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }
        _libraries.RemoveAll(l => string.Equals(l.Path, full, StringComparison.Ordinal));
        _errors.RemoveAll(e => string.Equals(e.Source, full, StringComparison.Ordinal));
    }

    /// <summary>Clears the library and error lists (modules stay mapped). Used by a rescan.</summary>
    public void Clear()
    {
        _libraries.Clear();
        _errors.Clear();
    }

    /// <summary>The frei0r library files under <paramref name="directory"/> for the current OS.</summary>
    public static IEnumerable<string> EnumerateLibraryFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            yield break;

        string pattern = OperatingSystem.IsWindows() ? "*.dll"
            : OperatingSystem.IsMacOS() ? "*.dylib"
            : "*.so";
        foreach (string file in Directory.EnumerateFiles(directory, pattern))
            yield return file;
    }

    /// <summary>
    /// The frei0r search directories in priority order: <c>FREI0R_PATH</c> (platform path separator) then the
    /// conventional locations from the frei0r specification (<c>~/.frei0r-1/lib</c>, <c>/usr/local/lib/frei0r-1</c>,
    /// <c>/usr/lib/frei0r-1</c>; macOS adds <c>/opt/local/lib/frei0r-1</c>; Windows has no convention beyond the
    /// environment variable and the per-user Sprocket plugins folder the composition root appends).
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchDirectories()
    {
        var dirs = new List<string>();

        string? env = Environment.GetEnvironmentVariable("FREI0R_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            dirs.AddRange(env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrEmpty(home))
                dirs.Add(Path.Combine(home, ".frei0r-1", "lib"));
            dirs.Add("/usr/local/lib/frei0r-1");
            dirs.Add("/usr/lib/frei0r-1");
            if (OperatingSystem.IsMacOS())
                dirs.Add("/opt/local/lib/frei0r-1");
            else
            {
                dirs.Add("/usr/lib/x86_64-linux-gnu/frei0r-1");
                dirs.Add("/usr/lib/aarch64-linux-gnu/frei0r-1");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && seen.Add(d)).ToList();
    }
}
