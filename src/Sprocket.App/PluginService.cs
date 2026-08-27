using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.Core.Audio;
using Sprocket.Render;

namespace Sprocket.App;

/// <summary>
/// The composition-root wiring for the plugin host (PLAN.md steps 33 + 58, ARCHITECTURE.md §13). Owns the
/// single process-wide <see cref="PluginManager"/> and supplies its side effects: the GPU shader registration
/// (<see cref="SkiaEffectPipeline"/>, so preview and export render plugin video effects) and the disabled-plugin
/// list persistence (in <see cref="UserSettings.DisabledPlugins"/>). The audio side reaches back through
/// <see cref="AudioEffectFactory"/>, which the mixer/export call. Per §15 every failure is logged and skipped;
/// a broken plugin can never stop the editor from starting, and a disabled/uninstalled plugin's effects pass
/// through in any project that still references them.
/// </summary>
internal static class PluginService
{
    private static PluginManager? _manager;

    /// <summary>The plugin manager, once <see cref="Initialize"/> has run (for the Edit ▸ Plugins… window).</summary>
    public static PluginManager? Manager => _manager;

    /// <summary>
    /// The mixer's effect factory: plugin-contributed audio effect types first, then the built-ins
    /// (unknown ids stay pass-through). Safe to use before <see cref="Initialize"/> (no plugins yet).
    /// </summary>
    public static IAudioEffect? AudioEffectFactory(string effectTypeId) =>
        _manager?.CreateAudioEffect(effectTypeId) ?? Sprocket.Audio.Effects.BuiltInAudioEffects.Create(effectTypeId);

    /// <summary>Loads and registers all enabled plugins. Call once at startup, before any project opens.</summary>
    public static void Initialize()
    {
        if (_manager is not null)
            return;

        var manager = new PluginManager(
            PluginDirectories(),
            loadDisabled: () => UserSettingsFile.Load().DisabledPlugins,
            saveDisabled: SaveDisabled,
            registerShader: SkiaEffectPipeline.RegisterEffect,
            unregisterShader: id => SkiaEffectPipeline.UnregisterEffect(id),
            log: (message, ex) => CrashLog.Write(message, ex),
            ladspaDirectories: Sprocket.Plugins.Ladspa.LadspaHost.DefaultSearchDirectories());

        manager.Initialize();
        _manager = manager;
    }

    /// <summary>Persists the disabled-plugin list, preserving every other setting (load-modify-save). The list
    /// is owned here (the Preferences dialog never touches it), so this is the only writer of the field.</summary>
    private static void SaveDisabled(IReadOnlyCollection<string> disabled)
    {
        UserSettings current = UserSettingsFile.Load();
        UserSettingsFile.Save(current with { DisabledPlugins = disabled.ToArray() });
    }

    /// <summary>
    /// Where plugins are discovered: <c>Plugins/</c> next to the executable (bundled/portable, read-only) and
    /// the per-user <c>Sprocket/Plugins</c> under app-data (user-installed, writable, survives app upgrades).
    /// </summary>
    private static IReadOnlyList<PluginManager.PluginDirectory> PluginDirectories()
    {
        var directories = new List<PluginManager.PluginDirectory>
        {
            new(Path.Combine(AppContext.BaseDirectory, "Plugins"), IsUserWritable: false),
        };
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            directories.Add(new(Path.Combine(appData, "Sprocket", "Plugins"), IsUserWritable: true));
        return directories;
    }
}
