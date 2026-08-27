using Sprocket.Core.Audio;
using Sprocket.Core.Model;

namespace Sprocket.Plugins.Lv2;

/// <summary>The result of loading one LV2 bundle (PLAN.md step 59): the catalog descriptors it contributes plus
/// per-plugin warnings, and the bundle version string for the Plugin Manager row.</summary>
public sealed record Lv2BundleLoad(
    string Path,
    IReadOnlyList<EffectDescriptor> Descriptors,
    IReadOnlyList<string> Warnings,
    string? Version);

/// <summary>
/// Discovers and hosts LV2 audio plugins (PLAN.md step 59) — the core-subset host beside <c>LadspaHost</c> on the
/// same <c>IAudioEffectProvider</c> seam. Scans the standard LV2 locations (<c>LV2_PATH</c> plus per-OS defaults)
/// for <c>*.lv2</c> bundle directories, reads each bundle's Turtle metadata, loads its binaries, and exposes the
/// hostable plugins. Every per-bundle failure is recorded in <see cref="Errors"/> rather than thrown (§15).
/// </summary>
/// <remarks>Discovery only — registering the descriptors is the composition root's job. Loaded binaries stay
/// mapped for the session (see <see cref="Lv2Library"/>).</remarks>
public sealed class Lv2Host
{
    private readonly List<Lv2Library> _libraries = [];
    private readonly List<PluginLoadError> _errors = [];

    internal IReadOnlyList<Lv2Library> Libraries => _libraries;

    /// <summary>Everything that went wrong while loading, for logging / the Plugin Manager; loading never throws.</summary>
    public IReadOnlyList<PluginLoadError> Errors => _errors;

    /// <summary>Loads every bundle under <paramref name="directory"/>; returns how many contributed ≥1 effect.</summary>
    public int LoadDirectory(string directory)
    {
        int loaded = 0;
        foreach (string bundle in EnumerateBundles(directory))
            if (Load(bundle) is { Descriptors.Count: > 0 })
                loaded++;
        return loaded;
    }

    /// <summary>Loads one bundle directory; returns null (with an <see cref="Errors"/> entry) on failure. A bundle
    /// already loaded from the same full path returns the existing result.</summary>
    public Lv2BundleLoad? Load(string bundleDirectory)
    {
        string full;
        try
        {
            full = Path.GetFullPath(bundleDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            _errors.Add(new PluginLoadError(bundleDirectory, $"{ex.GetType().Name}: {ex.Message}"));
            return null;
        }

        Lv2Library? existing = _libraries.FirstOrDefault(l => string.Equals(l.Path, full, StringComparison.Ordinal));
        if (existing is not null)
            return ToResult(existing);

        try
        {
            Lv2Library library = Lv2Library.Load(full);
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

    private static Lv2BundleLoad ToResult(Lv2Library library) =>
        new(library.Path,
            library.Providers.Select(p => p.Descriptor).ToArray(),
            library.Skipped,
            library.Providers.Count > 0 ? library.Providers[0].Info.Version : null);

    /// <summary>Creates a fresh DSP instance for an LV2 effect type id, or null if no loaded bundle provides it.</summary>
    public IAudioEffect? CreateAudioEffect(string effectTypeId)
    {
        foreach (Lv2Library library in _libraries)
            foreach (Lv2EffectProvider provider in library.Providers)
                if (provider.Descriptor.Id == effectTypeId)
                    return provider.CreateEffect();
        return null;
    }

    /// <summary>Drops the host's reference to a bundle (binaries stay mapped) and its recorded errors.</summary>
    public void Forget(string bundleDirectory)
    {
        string full;
        try { full = Path.GetFullPath(bundleDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { full = bundleDirectory; }
        _libraries.RemoveAll(l => string.Equals(l.Path, full, StringComparison.Ordinal));
        _errors.RemoveAll(e => string.Equals(e.Source, full, StringComparison.Ordinal));
    }

    /// <summary>Clears the host's bundle and error lists (binaries stay mapped). Used by a rescan.</summary>
    public void Clear()
    {
        _libraries.Clear();
        _errors.Clear();
    }

    /// <summary>The <c>*.lv2</c> bundle directories under <paramref name="directory"/> that carry a manifest.</summary>
    public static IEnumerable<string> EnumerateBundles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            yield break;
        foreach (string sub in Directory.EnumerateDirectories(directory, "*.lv2"))
            if (File.Exists(Path.Combine(sub, "manifest.ttl")))
                yield return sub;
    }

    /// <summary>
    /// The LV2 search directories in priority order: <c>LV2_PATH</c> (platform path separator) then the per-OS
    /// conventional locations from the LV2 specification (<c>~/.lv2</c>, <c>/usr/lib/lv2</c>, …; macOS
    /// <c>~/Library/Audio/Plug-Ins/LV2</c>; Windows <c>%APPDATA%\LV2</c>, <c>%COMMONPROGRAMFILES%\LV2</c>).
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchDirectories()
    {
        var dirs = new List<string>();

        string? env = Environment.GetEnvironmentVariable("LV2_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            dirs.AddRange(env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                dirs.Add(Path.Combine(appData, "LV2"));
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            if (!string.IsNullOrEmpty(common))
                dirs.Add(Path.Combine(common, "LV2"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (!string.IsNullOrEmpty(home))
                dirs.Add(Path.Combine(home, "Library", "Audio", "Plug-Ins", "LV2"));
            dirs.Add("/Library/Audio/Plug-Ins/LV2");
            dirs.Add("/usr/local/lib/lv2");
            dirs.Add("/usr/lib/lv2");
        }
        else
        {
            if (!string.IsNullOrEmpty(home))
                dirs.Add(Path.Combine(home, ".lv2"));
            dirs.Add("/usr/local/lib/lv2");
            dirs.Add("/usr/lib/lv2");
            dirs.Add("/usr/lib/x86_64-linux-gnu/lv2");
            dirs.Add("/usr/lib/aarch64-linux-gnu/lv2");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && seen.Add(d)).ToList();
    }
}
