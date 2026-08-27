using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Plugins;

namespace Sprocket.App;

/// <summary>Which plugin standard a discovered plugin file belongs to (PLAN.md steps 58 + 59). Determines how it
/// is loaded, registered, and torn down, and gives the Plugin Manager a per-format label.</summary>
public enum PluginFormat
{
    /// <summary>A managed Sprocket effect assembly loaded into a collectible <c>AssemblyLoadContext</c> (step 33).</summary>
    Managed,

    /// <summary>A native LADSPA audio plugin library discovered on the LADSPA search path (step 59).</summary>
    Ladspa,

    /// <summary>A native LV2 audio plugin bundle (a <c>*.lv2</c> directory) discovered on the LV2 search path (step 59).</summary>
    Lv2,

    /// <summary>A native frei0r video plugin library discovered on the frei0r search path (step 59); hosted on the
    /// CPU-effect readback seam.</summary>
    Frei0r,
}

/// <summary>The runtime state of one discovered plugin file, as the Plugin Manager UI shows it (PLAN.md step 58).</summary>
public enum PluginStatus
{
    /// <summary>Loaded and its effects registered — live in the catalog / render pipeline.</summary>
    Enabled,

    /// <summary>Present on disk but skipped by the user (in <see cref="UserSettings.DisabledPlugins"/>); not loaded.</summary>
    Disabled,

    /// <summary>Could not be loaded, or loaded but every effect faulted; <see cref="PluginEntry.Message"/> says why.</summary>
    Error,
}

/// <summary>
/// One discovered plugin assembly and its live state (PLAN.md step 58): where it came from, whether it is
/// bundled or user-installed, its version, the effects it contributes, and its <see cref="PluginStatus"/>.
/// Mutated only by <see cref="PluginManager"/> (on the model-owning thread); the UI reads it.
/// </summary>
public sealed class PluginEntry
{
    internal PluginEntry(string assemblyPath, bool isUserPlugin, PluginFormat format = PluginFormat.Managed)
    {
        AssemblyPath = assemblyPath;
        IsUserPlugin = isUserPlugin;
        Format = format;
    }

    /// <summary>Full path of the plugin assembly (managed), library file (LADSPA / frei0r) or bundle directory (LV2).</summary>
    public string AssemblyPath { get; }

    /// <summary>Which plugin standard this entry belongs to (managed assembly vs. a native open standard).</summary>
    public PluginFormat Format { get; }

    /// <summary>Whether the plugin lives in the user-writable plugins folder (so it can be uninstalled), as
    /// opposed to the read-only bundled <c>&lt;exe&gt;/Plugins</c> location.</summary>
    public bool IsUserPlugin { get; }

    /// <summary>Display name — the assembly file name without extension.</summary>
    public string Name => Path.GetFileNameWithoutExtension(AssemblyPath);

    /// <summary>The assembly version once it has been loaded at least once this session; <see langword="null"/>
    /// for a plugin that has only ever been seen disabled.</summary>
    public string? Version { get; internal set; }

    /// <summary>The plugin's current state.</summary>
    public PluginStatus Status { get; internal set; }

    /// <summary>An error / warning detail for an <see cref="PluginStatus.Error"/> row (or a partial-load
    /// warning on an otherwise-enabled row); <see langword="null"/> when there is nothing to report.</summary>
    public string? Message { get; internal set; }

    /// <summary>The effect descriptors this plugin has registered while enabled; empty while disabled or errored.</summary>
    public IReadOnlyList<EffectDescriptor> Effects { get; internal set; } = [];

    internal LoadedPlugin? Loaded { get; set; }
}

/// <summary>
/// The user-facing plugin management surface (PLAN.md step 58): discovers plugin assemblies in the bundled and
/// user directories, tracks each one's live state, and lets the user enable/disable, install, uninstall, and
/// rescan them over the existing managed <see cref="PluginHost"/> (step 33) — the DAW convention (REAPER's
/// browser + rescan, Ableton's per-plugin enable) rather than the filesystem-only NLE approach.
/// </summary>
/// <remarks>
/// <para>Registration into <see cref="EffectCatalog"/> is done here (pure Core, so this is headlessly testable);
/// the GPU shader compile/registration is injected as a callback (<c>registerShader</c>/<c>unregisterShader</c>,
/// wired to <c>SkiaEffectPipeline</c> by <see cref="PluginService"/>) so tests need no GPU. The disabled-plugin
/// list is read/written through injected callbacks so the persistence store stays this class's business only at
/// the composition root. Audio effect providers are served live from the host, so unloading a plugin stops it
/// providing with no separate unregister step.</para>
/// <para>Enable = load + register; disable = unregister + unload the collectible context (§13). Projects keep
/// their effect instances either way: a disabled/uninstalled plugin's effects pass through (§15) and revive on
/// re-enable. Not thread-safe — all methods run on the model-owning thread (startup, then the UI thread).</para>
/// </remarks>
public sealed class PluginManager
{
    /// <summary>One directory to scan, and whether the user may write to it (install / uninstall).</summary>
    public readonly record struct PluginDirectory(string Path, bool IsUserWritable);

    private readonly PluginHost _host = new();
    private readonly Sprocket.Plugins.Ladspa.LadspaHost _ladspa = new();
    private readonly Sprocket.Plugins.Lv2.Lv2Host _lv2 = new();
    private readonly Sprocket.Plugins.Frei0r.Frei0rHost _frei0r = new();
    private readonly IReadOnlyList<PluginDirectory> _directories;
    private readonly IReadOnlyList<string> _ladspaDirectories;
    private readonly IReadOnlyList<string> _lv2Directories;
    private readonly IReadOnlyList<string> _frei0rDirectories;
    private readonly Action<IVideoEffect> _registerShader;
    private readonly Action<ICpuVideoEffect> _registerCpuEffect;
    private readonly Action<string> _unregisterShader;
    private readonly Action<IReadOnlyCollection<string>> _saveDisabled;
    private readonly Action<string, Exception?> _log;

    private readonly List<PluginEntry> _entries = [];
    private readonly HashSet<string> _disabled;

    /// <param name="directories">The directories to scan, in priority order (bundled first, then user).</param>
    /// <param name="loadDisabled">Reads the persisted set of disabled plugin paths.</param>
    /// <param name="saveDisabled">Persists the set of disabled plugin paths.</param>
    /// <param name="registerShader">Compiles + registers a video effect's shader for the render pipeline
    /// (may throw on bad SkSL — the manager records that and unregisters the descriptor).</param>
    /// <param name="unregisterShader">Removes a video effect's shader from the render pipeline (no-op for
    /// audio ids or ids already gone).</param>
    /// <param name="log">Diagnostics sink for non-fatal load problems.</param>
    /// <param name="ladspaDirectories">The LADSPA library search directories (PLAN.md step 59); null uses none
    /// (tests pass an explicit set so discovery is deterministic and never picks up the host's real plugins).</param>
    /// <param name="lv2Directories">The LV2 bundle search directories (step 59); null uses none.</param>
    /// <param name="frei0rDirectories">The frei0r library search directories (step 59); null uses none.</param>
    /// <param name="registerCpuEffect">Registers a CPU (readback) video effect with the render pipeline (step 59);
    /// null = no-op (headless tests). Removal goes through <paramref name="unregisterShader"/>, which covers both.</param>
    public PluginManager(
        IReadOnlyList<PluginDirectory> directories,
        Func<IReadOnlyCollection<string>> loadDisabled,
        Action<IReadOnlyCollection<string>> saveDisabled,
        Action<IVideoEffect> registerShader,
        Action<string> unregisterShader,
        Action<string, Exception?> log,
        IReadOnlyList<string>? ladspaDirectories = null,
        IReadOnlyList<string>? lv2Directories = null,
        IReadOnlyList<string>? frei0rDirectories = null,
        Action<ICpuVideoEffect>? registerCpuEffect = null)
    {
        _directories = directories;
        _ladspaDirectories = ladspaDirectories ?? [];
        _lv2Directories = lv2Directories ?? [];
        _frei0rDirectories = frei0rDirectories ?? [];
        _saveDisabled = saveDisabled;
        _registerShader = registerShader;
        _registerCpuEffect = registerCpuEffect ?? (_ => { });
        _unregisterShader = unregisterShader;
        _log = log;
        _disabled = new HashSet<string>(loadDisabled(), StringComparer.Ordinal);
    }

    /// <summary>Every discovered plugin, in scan order (bundled directory first).</summary>
    public IReadOnlyList<PluginEntry> Entries => _entries;

    /// <summary>The current set of disabled plugin paths — lets the composition root keep its in-memory
    /// <see cref="UserSettings"/> copy in step so a later settings save doesn't clobber the persisted list.</summary>
    public IReadOnlyList<string> DisabledPaths => _disabled.ToArray();

    /// <summary>The user-writable plugins folder (install target + "Open Plugins Folder"), or
    /// <see langword="null"/> if none was configured.</summary>
    public string? UserPluginDirectory =>
        _directories.FirstOrDefault(d => d.IsUserWritable) is { IsUserWritable: true } dir ? dir.Path : null;

    /// <summary>Scans every configured directory and loads the enabled plugins, skipping the disabled ones and
    /// recording per-file errors. Call once at startup; use <see cref="Rescan"/> to re-run later.</summary>
    public void Initialize()
    {
        _entries.Clear();
        foreach (PluginDirectory dir in _directories)
        {
            IEnumerable<string> candidates;
            try
            {
                candidates = PluginHost.EnumeratePluginFiles(dir.Path).ToArray();
            }
            catch (Exception ex)
            {
                _log($"Plugin scan failed for '{dir.Path}'", ex);
                continue;
            }

            foreach (string candidate in candidates)
            {
                string full = Path.GetFullPath(candidate);
                var entry = new PluginEntry(full, dir.IsUserWritable);
                _entries.Add(entry);

                if (_disabled.Contains(full))
                    entry.Status = PluginStatus.Disabled;
                else
                    LoadAndRegister(entry);
            }
        }

        // The native open standards (PLAN.md step 59): one row per LADSPA library file, LV2 bundle directory,
        // and frei0r library file, each deduplicated by full path across its search directories.
        InitializeNative(PluginFormat.Ladspa, "LADSPA", _ladspaDirectories, Sprocket.Plugins.Ladspa.LadspaHost.EnumerateLibraryFiles);
        InitializeNative(PluginFormat.Lv2, "LV2", _lv2Directories, Sprocket.Plugins.Lv2.Lv2Host.EnumerateBundles);
        InitializeNative(PluginFormat.Frei0r, "frei0r", _frei0rDirectories, Sprocket.Plugins.Frei0r.Frei0rHost.EnumerateLibraryFiles);
    }

    /// <summary>Discovers one native plugin format's files/bundles on its search path and loads the enabled ones,
    /// skipping the disabled — one entry per discovered path, deduplicated across directories.</summary>
    private void InitializeNative(PluginFormat format, string label, IReadOnlyList<string> directories, Func<string, IEnumerable<string>> enumerate)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in directories)
        {
            IEnumerable<string> paths;
            try
            {
                paths = enumerate(dir).ToArray();
            }
            catch (Exception ex)
            {
                _log($"{label} scan failed for '{dir}'", ex);
                continue;
            }

            foreach (string path in paths)
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!seen.Add(full))
                    continue;

                var entry = new PluginEntry(full, isUserPlugin: false, format);
                _entries.Add(entry);

                if (_disabled.Contains(full))
                    entry.Status = PluginStatus.Disabled;
                else
                    LoadAndRegisterNative(entry);
            }
        }
    }

    /// <summary>The mixer's plugin audio effect factory: returns a fresh DSP instance for a plugin-contributed
    /// effect type id (managed first, then LADSPA, then LV2), or <see langword="null"/> when none provides it.</summary>
    public IAudioEffect? CreateAudioEffect(string effectTypeId) =>
        _host.CreateAudioEffect(effectTypeId) ?? _ladspa.CreateAudioEffect(effectTypeId) ?? _lv2.CreateAudioEffect(effectTypeId);

    /// <summary>The hosted frei0r CPU effect for an effect type id, or <see langword="null"/> (for tests / diagnostics;
    /// the render pipeline reaches CPU effects through its own registry).</summary>
    public ICpuVideoEffect? FindCpuEffect(string effectTypeId) => _frei0r.FindEffect(effectTypeId);

    /// <summary>
    /// Enables or disables one plugin and persists the choice. Enabling a not-loaded plugin loads and registers
    /// it; disabling unregisters its effects and unloads its collectible context. Returns whether anything changed.
    /// </summary>
    public bool SetEnabled(PluginEntry entry, bool enable)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (enable)
        {
            _disabled.Remove(entry.AssemblyPath);
            _saveDisabled(_disabled.ToArray());
            if (entry.Status == PluginStatus.Enabled)
                return false; // already active
            if (entry.Format == PluginFormat.Managed)
                LoadAndRegister(entry);
            else
                LoadAndRegisterNative(entry);
            return true;
        }

        _disabled.Add(entry.AssemblyPath);
        _saveDisabled(_disabled.ToArray());
        Unregister(entry);
        entry.Status = PluginStatus.Disabled;
        entry.Message = null;
        return true;
    }

    /// <summary>
    /// Installs a plugin by copying <paramref name="sourceFilePath"/> into the user plugins folder and loading
    /// it. Returns an error message, or <see langword="null"/> on success (the new row's own status then reflects
    /// whether it loaded). Refuses to overwrite an already-installed plugin of the same file name.
    /// </summary>
    public string? Install(string sourceFilePath)
    {
        if (UserPluginDirectory is not { } userDir)
            return "No user plugins folder is configured.";
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            return "The selected file does not exist.";

        string dest = Path.Combine(userDir, Path.GetFileName(sourceFilePath));
        string destFull = Path.GetFullPath(dest);
        if (_entries.Any(e => string.Equals(e.AssemblyPath, destFull, StringComparison.Ordinal)) || File.Exists(destFull))
            return $"A plugin named \"{Path.GetFileName(sourceFilePath)}\" is already installed.";

        try
        {
            Directory.CreateDirectory(userDir);
            File.Copy(sourceFilePath, destFull);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not copy the plugin: {ex.Message}";
        }

        var entry = new PluginEntry(destFull, isUserPlugin: true);
        _entries.Add(entry);
        _disabled.Remove(destFull); // a freshly installed plugin starts enabled
        _saveDisabled(_disabled.ToArray());
        LoadAndRegister(entry);
        return null;
    }

    /// <summary>
    /// Uninstalls a user-installed plugin: unregisters + unloads it, then deletes its file. Returns an error
    /// message, or <see langword="null"/> on success. Bundled plugins cannot be uninstalled.
    /// </summary>
    public string? Uninstall(PluginEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsUserPlugin)
            return "Bundled plugins cannot be uninstalled.";

        Unregister(entry);
        _disabled.Remove(entry.AssemblyPath);
        _saveDisabled(_disabled.ToArray());

        string? deleteError = TryDeletePluginFile(entry.AssemblyPath);
        if (deleteError is not null)
            return deleteError; // leave the row in place; the file is still there

        _entries.Remove(entry);
        return null;
    }

    /// <summary>Unloads everything and re-scans from disk — picks up newly dropped files and drops removed ones,
    /// re-reading the disabled list. Equivalent to a fresh <see cref="Initialize"/>.</summary>
    public void Rescan()
    {
        foreach (PluginEntry entry in _entries.ToArray())
            Unregister(entry);
        // Drop discovery references (native modules stay mapped for the session, step 59).
        _ladspa.Clear();
        _lv2.Clear();
        _frei0r.Clear();
        Initialize();
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads one plugin and registers its effects, filling in the entry's version / status / message.</summary>
    private void LoadAndRegister(PluginEntry entry)
    {
        int errorsBefore = _host.Errors.Count;
        LoadedPlugin? plugin = _host.Load(entry.AssemblyPath);
        if (plugin is null)
        {
            entry.Status = PluginStatus.Error;
            entry.Message = FirstErrorSince(entry.AssemblyPath, errorsBefore) ?? "Failed to load.";
            return;
        }

        entry.Loaded = plugin;
        entry.Version = plugin.Version;
        Register(entry, plugin);
    }

    /// <summary>Registers a loaded plugin's effects into the catalog + render pipeline, mirroring the step-33
    /// composition-root rules (reserved/duplicate ids rejected; a bad shader unregisters just that effect).</summary>
    private void Register(PluginEntry entry, LoadedPlugin plugin)
    {
        var registered = new List<EffectDescriptor>();
        var problems = new List<string>();

        foreach (IVideoEffect effect in plugin.VideoEffects)
        {
            if (!EffectCatalog.Register(effect.Descriptor))
            {
                _log($"Plugin '{plugin.Name}': effect id '{effect.Descriptor.Id}' already registered — skipped", null);
                problems.Add($"'{effect.Descriptor.Id}' already registered");
                continue;
            }
            try
            {
                _registerShader(effect); // compiles the SkSL; throws on a broken program
                registered.Add(effect.Descriptor);
            }
            catch (Exception ex)
            {
                EffectCatalog.Unregister(effect.Descriptor.Id);
                _log($"Plugin '{plugin.Name}': effect '{effect.Descriptor.Id}' failed to compile", ex);
                problems.Add($"'{effect.Descriptor.Id}' failed to compile");
            }
        }

        foreach (IAudioEffectProvider provider in plugin.AudioEffectProviders)
        {
            if (provider.Descriptor.Category != EffectCategory.Audio)
            {
                _log($"Plugin '{plugin.Name}': audio effect '{provider.Descriptor.Id}' must use EffectCategory.Audio — skipped", null);
                problems.Add($"'{provider.Descriptor.Id}' is not an Audio effect");
                continue;
            }
            if (!EffectCatalog.Register(provider.Descriptor))
            {
                _log($"Plugin '{plugin.Name}': effect id '{provider.Descriptor.Id}' already registered — skipped", null);
                problems.Add($"'{provider.Descriptor.Id}' already registered");
                continue;
            }
            registered.Add(provider.Descriptor);
        }

        entry.Effects = registered;
        if (registered.Count > 0)
        {
            entry.Status = PluginStatus.Enabled;
            entry.Message = problems.Count > 0 ? string.Join("; ", problems) : null;
        }
        else
        {
            entry.Status = PluginStatus.Error;
            entry.Message = problems.Count > 0 ? string.Join("; ", problems) : "No usable effects.";
        }
    }

    /// <summary>Loads one native plugin file/bundle through its format's host and registers what it contributes,
    /// filling in the entry's version / status / message (PLAN.md step 59). Native modules stay mapped; catalog
    /// (and, for frei0r, render-pipeline) registration is what makes the effects live.</summary>
    private void LoadAndRegisterNative(PluginEntry entry)
    {
        IReadOnlyList<EffectDescriptor> audioDescriptors = [];
        ICpuVideoEffect? cpuEffect = null;
        IReadOnlyList<string> warnings = [];
        string? version = null;
        string? failure = null;

        switch (entry.Format)
        {
            case PluginFormat.Ladspa:
            {
                int before = _ladspa.Errors.Count;
                if (_ladspa.Load(entry.AssemblyPath) is { } load)
                    (audioDescriptors, warnings) = (load.Descriptors, load.Warnings);
                else
                    failure = FirstErrorSince(_ladspa.Errors, entry.AssemblyPath, before) ?? "Failed to load.";
                break;
            }
            case PluginFormat.Lv2:
            {
                int before = _lv2.Errors.Count;
                if (_lv2.Load(entry.AssemblyPath) is { } load)
                    (audioDescriptors, warnings, version) = (load.Descriptors, load.Warnings, load.Version);
                else
                    failure = FirstErrorSince(_lv2.Errors, entry.AssemblyPath, before) ?? "Failed to load.";
                break;
            }
            case PluginFormat.Frei0r:
            {
                int before = _frei0r.Errors.Count;
                if (_frei0r.Load(entry.AssemblyPath) is { } load)
                    (cpuEffect, warnings, version) = (load.Effect, load.Warnings, load.Version);
                else
                    failure = FirstErrorSince(_frei0r.Errors, entry.AssemblyPath, before) ?? "Failed to load.";
                break;
            }
            default:
                throw new InvalidOperationException($"{entry.Format} is not a native plugin format.");
        }

        if (failure is not null)
        {
            entry.Status = PluginStatus.Error;
            entry.Message = failure;
            return;
        }
        entry.Version = version;

        var registered = new List<EffectDescriptor>();
        var problems = new List<string>();
        foreach (EffectDescriptor descriptor in audioDescriptors)
        {
            if (EffectCatalog.Register(descriptor))
                registered.Add(descriptor);
            else
            {
                _log($"{entry.Format} '{entry.Name}': effect id '{descriptor.Id}' already registered — skipped", null);
                problems.Add($"'{descriptor.Id}' already registered");
            }
        }
        if (cpuEffect is not null)
        {
            if (!EffectCatalog.Register(cpuEffect.Descriptor))
            {
                _log($"frei0r '{entry.Name}': effect id '{cpuEffect.Descriptor.Id}' already registered — skipped", null);
                problems.Add($"'{cpuEffect.Descriptor.Id}' already registered");
            }
            else
            {
                try
                {
                    _registerCpuEffect(cpuEffect);
                    registered.Add(cpuEffect.Descriptor);
                }
                catch (Exception ex)
                {
                    EffectCatalog.Unregister(cpuEffect.Descriptor.Id);
                    _log($"frei0r '{entry.Name}': effect '{cpuEffect.Descriptor.Id}' failed to register", ex);
                    problems.Add($"'{cpuEffect.Descriptor.Id}' failed to register");
                }
            }
        }
        problems.AddRange(warnings);

        entry.Effects = registered;
        if (registered.Count > 0)
        {
            entry.Status = PluginStatus.Enabled;
            entry.Message = problems.Count > 0 ? string.Join("; ", problems) : null;
        }
        else
        {
            entry.Status = PluginStatus.Error;
            entry.Message = problems.Count > 0 ? string.Join("; ", problems) : "No hostable effects.";
        }
    }

    /// <summary>Unregisters a plugin's effects everywhere and unloads its context; safe to call on any entry.</summary>
    private void Unregister(PluginEntry entry)
    {
        foreach (EffectDescriptor descriptor in entry.Effects)
        {
            EffectCatalog.Unregister(descriptor.Id);
            _unregisterShader(descriptor.Id); // no-op for audio ids / already-removed ids
        }
        entry.Effects = [];

        switch (entry.Format)
        {
            // Native modules stay mapped; forgetting just stops the host creating their effects.
            case PluginFormat.Ladspa: _ladspa.Forget(entry.AssemblyPath); return;
            case PluginFormat.Lv2: _lv2.Forget(entry.AssemblyPath); return;
            case PluginFormat.Frei0r: _frei0r.Forget(entry.AssemblyPath); return;
        }

        if (entry.Loaded is { } plugin)
        {
            _host.Unload(plugin);
            entry.Loaded = null;
        }
    }

    /// <summary>The first native-host load error whose source is <paramref name="path"/> since index
    /// <paramref name="fromIndex"/>, or <see langword="null"/>.</summary>
    private static string? FirstErrorSince(IReadOnlyList<PluginLoadError> errors, string path, int fromIndex)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = fromIndex; i < errors.Count; i++)
            if (string.Equals(errors[i].Source, full, StringComparison.Ordinal))
                return errors[i].Message;
        return null;
    }

    /// <summary>The first recorded error whose source is <paramref name="path"/> since index
    /// <paramref name="fromIndex"/> (the failure that just happened), or <see langword="null"/>.</summary>
    private string? FirstErrorSince(string path, int fromIndex)
    {
        IReadOnlyList<PluginLoadError> errors = _host.Errors;
        for (int i = fromIndex; i < errors.Count; i++)
            if (string.Equals(errors[i].Source, path, StringComparison.Ordinal))
                return errors[i].Message;
        return null;
    }

    /// <summary>
    /// Deletes an unloaded plugin file, tolerating the brief window in which a just-unloaded collectible
    /// <c>AssemblyLoadContext</c> still holds the DLL on Windows: a bounded GC-then-retry, exactly the pattern
    /// the host's unload test relies on. Returns an error message if the file still can't be removed.
    /// </summary>
    private static string? TryDeletePluginFile(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                    return $"The plugin was disabled, but its file could not be deleted (it may be in use): {ex.Message}";
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
