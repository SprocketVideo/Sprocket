using Sprocket.Core.Audio;
using Sprocket.Core.Model;

namespace Sprocket.Plugins.Ladspa;

/// <summary>The result of loading one LADSPA library file (PLAN.md step 59): the catalog descriptors it
/// contributes (for the composition root to register) plus any per-plugin warnings. Exposed so the App can host
/// LADSPA plugins without seeing the internal native types.</summary>
/// <param name="Path">Full path of the loaded library file.</param>
/// <param name="Descriptors">The audio effect descriptors the library contributes, in descriptor order.</param>
/// <param name="Warnings">Notes for plugins found but not hostable (e.g. a source with no audio input).</param>
public sealed record LadspaLibraryLoad(
    string Path,
    IReadOnlyList<EffectDescriptor> Descriptors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Discovers and hosts LADSPA audio plugins (PLAN.md step 59) — the native counterpart to the managed
/// <see cref="PluginHost"/>, sitting on the same <c>IAudioEffectProvider</c> seam. It scans the standard LADSPA
/// locations (the <c>LADSPA_PATH</c> environment variable plus the per-OS default directories) for library files,
/// loads each one, and exposes the effects they contribute. Every per-file failure is recorded in
/// <see cref="Errors"/> rather than thrown, so one bad library never blocks the rest (ARCHITECTURE.md §15).
/// </summary>
/// <remarks>
/// The host only <b>discovers</b>; registering the found descriptors into <c>EffectCatalog</c> is the composition
/// root's job (as with the managed host), keeping this project free of UI/GPU/persistence dependencies (§2).
/// Loaded modules stay mapped for the session (see <see cref="LadspaLibrary"/>).
/// </remarks>
public sealed class LadspaHost
{
    private readonly List<LadspaLibrary> _libraries = [];
    private readonly List<PluginLoadError> _errors = [];

    internal IReadOnlyList<LadspaLibrary> Libraries => _libraries;

    /// <summary>Everything that went wrong while loading, for logging / the Plugin Manager; loading never throws.</summary>
    public IReadOnlyList<PluginLoadError> Errors => _errors;

    /// <summary>
    /// Loads every LADSPA library under <paramref name="directory"/> (a missing/blank directory is a no-op) and
    /// returns how many libraries loaded at least one hostable plugin. Skips a file already loaded from the same
    /// full path.
    /// </summary>
    public int LoadDirectory(string directory)
    {
        int loaded = 0;
        foreach (string file in EnumerateLibraryFiles(directory))
            if (Load(file) is { Descriptors.Count: > 0 })
                loaded++;
        return loaded;
    }

    /// <summary>
    /// Loads one LADSPA library file and enumerates its plugins. Returns the load result (its contributed
    /// descriptors + warnings), or <see langword="null"/> (with an <see cref="Errors"/> entry) if the file could
    /// not be loaded as a native module or is not a LADSPA library. A file already loaded from the same full path
    /// returns the existing result.
    /// </summary>
    public LadspaLibraryLoad? Load(string path)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            _errors.Add(new PluginLoadError(path, $"{ex.GetType().Name}: {ex.Message}"));
            return null;
        }

        LadspaLibrary? existing = _libraries.FirstOrDefault(l => string.Equals(l.Path, full, StringComparison.Ordinal));
        if (existing is not null)
            return ToResult(existing);

        try
        {
            LadspaLibrary library = LadspaLibrary.Load(full);
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

    private static LadspaLibraryLoad ToResult(LadspaLibrary library) =>
        new(library.Path, library.Providers.Select(p => p.Descriptor).ToArray(), library.Skipped);

    /// <summary>Creates a fresh DSP instance for a LADSPA effect type id, or <see langword="null"/> if no loaded
    /// library provides it (the mixer then passes through).</summary>
    public IAudioEffect? CreateAudioEffect(string effectTypeId)
    {
        foreach (LadspaLibrary library in _libraries)
            foreach (LadspaEffectProvider provider in library.Providers)
                if (provider.Descriptor.Id == effectTypeId)
                    return provider.CreateEffect();
        return null;
    }

    /// <summary>Drops the host's reference to the library loaded from <paramref name="path"/>. The native module
    /// stays mapped for the session (see <see cref="LadspaLibrary"/>); this only removes it from discovery so a
    /// rescan re-reads it and so its effects stop being created.</summary>
    public void Forget(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { full = path; }
        _libraries.RemoveAll(l => string.Equals(l.Path, full, StringComparison.Ordinal));
        // Drop this library's recorded warnings/errors too, so a re-enable (which re-Loads and re-appends them)
        // doesn't accumulate duplicates across enable→disable→enable cycles.
        _errors.RemoveAll(e => string.Equals(e.Source, full, StringComparison.Ordinal));
    }

    /// <summary>Clears the host's library and error lists (modules stay mapped). Used by a rescan.</summary>
    public void Clear()
    {
        _libraries.Clear();
        _errors.Clear();
    }

    /// <summary>
    /// The LADSPA library files under <paramref name="directory"/> for the current OS (<c>*.so</c> on Linux,
    /// <c>*.dylib</c> on macOS, <c>*.dll</c> on Windows). A missing/blank directory yields nothing.
    /// </summary>
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
    /// The LADSPA search directories, in priority order: the <c>LADSPA_PATH</c> environment variable (split on the
    /// platform path separator) followed by the per-OS conventional locations. Duplicates are removed, order
    /// preserved. This mirrors how LADSPA hosts (Ardour, Audacity) locate plugins.
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchDirectories()
    {
        var dirs = new List<string>();

        string? env = Environment.GetEnvironmentVariable("LADSPA_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            dirs.AddRange(env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            // No OS convention on Windows; Program Files is where the few Windows LADSPA builds land.
            string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
                dirs.Add(Path.Combine(programFiles, "Audacity", "Plug-Ins"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/Library/Audio/Plug-Ins/LADSPA");
            dirs.Add("/usr/local/lib/ladspa");
            if (!string.IsNullOrEmpty(home))
                dirs.Add(Path.Combine(home, "Library", "Audio", "Plug-Ins", "LADSPA"));
        }
        else
        {
            dirs.Add("/usr/lib/ladspa");
            dirs.Add("/usr/local/lib/ladspa");
            if (!string.IsNullOrEmpty(home))
                dirs.Add(Path.Combine(home, ".ladspa"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && seen.Add(d)).ToList();
    }
}
