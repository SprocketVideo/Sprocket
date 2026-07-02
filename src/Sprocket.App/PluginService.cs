using System;
using System.Collections.Generic;
using System.IO;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Plugins;
using Sprocket.Render;

namespace Sprocket.App;

/// <summary>
/// The composition-root wiring for the plugin host (PLAN.md step 33, ARCHITECTURE.md §13):
/// <see cref="Initialize"/> loads every plugin from the app's plugin directories, registers the discovered
/// video effects into <see cref="EffectCatalog"/> (so the Effects browser / Inspector list them) and into
/// <see cref="SkiaEffectPipeline"/>'s shader registry (so preview and export render them), and registers
/// audio effect descriptors so the render graph routes them to the mixer — whose chains reach back through
/// <see cref="AudioEffectFactory"/>. Per §15, every failure is logged and skipped; a broken plugin can never
/// stop the editor from starting. Plugins stay loaded for the app's lifetime (unload-on-the-fly is a later
/// refinement); projects referencing an uninstalled plugin still load — its effects pass through.
/// </summary>
internal static class PluginService
{
    private static PluginHost? _host;

    /// <summary>The loaded plugins (for a future Manage Plugins UI / About box).</summary>
    public static IReadOnlyList<LoadedPlugin> Plugins => _host?.Plugins ?? [];

    /// <summary>
    /// The mixer's effect factory: plugin-contributed audio effect types first, then the built-ins
    /// (unknown ids stay pass-through). Safe to use before <see cref="Initialize"/> (no plugins yet).
    /// </summary>
    public static IAudioEffect? AudioEffectFactory(string effectTypeId) =>
        _host?.CreateAudioEffect(effectTypeId) ?? Sprocket.Audio.Effects.BuiltInAudioEffects.Create(effectTypeId);

    /// <summary>Loads and registers all plugins. Call once at startup, before any project opens.</summary>
    public static void Initialize()
    {
        if (_host is not null)
            return;
        _host = new PluginHost();

        foreach (string directory in PluginDirectories())
        {
            try
            {
                _host.LoadDirectory(directory);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"Plugin scan failed for '{directory}'", ex);
            }
        }

        foreach (LoadedPlugin plugin in _host.Plugins)
        {
            foreach (IVideoEffect effect in plugin.VideoEffects)
            {
                if (!EffectCatalog.Register(effect.Descriptor))
                {
                    CrashLog.Write($"Plugin '{plugin.Name}': effect id '{effect.Descriptor.Id}' already registered — skipped", null);
                    continue;
                }
                try
                {
                    SkiaEffectPipeline.RegisterEffect(effect); // compiles the SkSL; throws on a broken program
                }
                catch (Exception ex)
                {
                    EffectCatalog.Unregister(effect.Descriptor.Id);
                    CrashLog.Write($"Plugin '{plugin.Name}': effect '{effect.Descriptor.Id}' failed to compile", ex);
                }
            }

            foreach (IAudioEffectProvider provider in plugin.AudioEffectProviders)
            {
                if (provider.Descriptor.Category != EffectCategory.Audio)
                {
                    CrashLog.Write($"Plugin '{plugin.Name}': audio effect '{provider.Descriptor.Id}' must use EffectCategory.Audio — skipped", null);
                    continue;
                }
                if (!EffectCatalog.Register(provider.Descriptor))
                    CrashLog.Write($"Plugin '{plugin.Name}': effect id '{provider.Descriptor.Id}' already registered — skipped", null);
            }
        }

        foreach (PluginLoadError error in _host.Errors)
            CrashLog.Write($"Plugin load: {error.Source}: {error.Message}", null);
    }

    /// <summary>
    /// Where plugins are discovered: <c>Plugins/</c> next to the executable (bundled/portable installs) and
    /// the per-user <c>Sprocket/Plugins</c> under app-data (user-installed, survives app upgrades).
    /// </summary>
    private static IEnumerable<string> PluginDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Plugins");
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            yield return Path.Combine(appData, "Sprocket", "Plugins");
    }
}
