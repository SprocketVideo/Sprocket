using System.Reflection;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Plugins;

/// <summary>One problem hit while loading plugins — surfaced, never thrown, so a broken plugin can't take the
/// editor down (ARCHITECTURE.md §15): a bad file, a type whose construction faulted, a reserved id, …</summary>
public sealed record PluginLoadError(string Source, string Message);

/// <summary>
/// One successfully loaded plugin assembly and the effect implementations discovered in it. The instances are
/// the plugin's live objects — holding this object keeps the plugin's load context alive until
/// <see cref="PluginHost.Unload"/>.
/// </summary>
public sealed class LoadedPlugin
{
    internal LoadedPlugin(
        string assemblyPath,
        PluginLoadContext context,
        IReadOnlyList<IVideoEffect> videoEffects,
        IReadOnlyList<IAudioEffectProvider> audioEffectProviders)
    {
        AssemblyPath = assemblyPath;
        Context = context;
        VideoEffects = videoEffects;
        AudioEffectProviders = audioEffectProviders;
    }

    /// <summary>Full path of the plugin assembly.</summary>
    public string AssemblyPath { get; }

    /// <summary>A display name for the plugin (the assembly file name).</summary>
    public string Name => Path.GetFileNameWithoutExtension(AssemblyPath);

    /// <summary>The shader-backed video effects the plugin contributes.</summary>
    public IReadOnlyList<IVideoEffect> VideoEffects { get; internal set; }

    /// <summary>The audio DSP effect types the plugin contributes.</summary>
    public IReadOnlyList<IAudioEffectProvider> AudioEffectProviders { get; internal set; }

    internal PluginLoadContext? Context { get; set; }
}

/// <summary>
/// The plugin host (ARCHITECTURE.md §13, PLAN.md step 33): loads effect-plugin assemblies into collectible
/// <see cref="PluginLoadContext"/>s and discovers their <see cref="IVideoEffect"/> /
/// <see cref="IAudioEffectProvider"/> implementations by interface scan (public, non-abstract, parameterless
/// ctor). Plugins run in-process and trusted (v1); every per-plugin failure is recorded in
/// <see cref="Errors"/> instead of thrown, so one bad plugin never blocks the rest. The host only
/// <b>discovers</b> — registering the found effects into <c>EffectCatalog</c> / the render pipeline is the
/// composition root's job (§2), keeping this project free of GPU/UI dependencies.
/// </summary>
public sealed class PluginHost
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<PluginLoadError> _errors = [];

    /// <summary>The successfully loaded plugins, in load order.</summary>
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>Everything that went wrong while loading, for logging/UI; loading itself never throws.</summary>
    public IReadOnlyList<PluginLoadError> Errors => _errors;

    /// <summary>
    /// Loads every plugin under <paramref name="directory"/>: top-level <c>*.dll</c> files plus one
    /// directory level of <c>&lt;Name&gt;/&lt;Name&gt;.dll</c> bundles (a plugin that ships its own managed /
    /// native dependencies beside it). A missing directory is a no-op. Returns how many plugins loaded.
    /// </summary>
    public int LoadDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return 0;

        int loaded = 0;
        foreach (string dll in Directory.EnumerateFiles(directory, "*.dll"))
            loaded += Load(dll) is not null ? 1 : 0;

        foreach (string sub in Directory.EnumerateDirectories(directory))
        {
            string candidate = Path.Combine(sub, Path.GetFileName(sub) + ".dll");
            if (File.Exists(candidate))
                loaded += Load(candidate) is not null ? 1 : 0;
        }
        return loaded;
    }

    /// <summary>
    /// Loads one plugin assembly and scans it for effect implementations. Returns the plugin, or
    /// <see langword="null"/> (with an <see cref="Errors"/> entry) if the file isn't a usable plugin —
    /// unreadable, no effect types, or every candidate type failed.
    /// </summary>
    public LoadedPlugin? Load(string assemblyPath)
    {
        string fullPath;
        PluginLoadContext? context = null;
        try
        {
            fullPath = Path.GetFullPath(assemblyPath);
            context = new PluginLoadContext(fullPath);
            Assembly assembly = context.LoadFromAssemblyPath(fullPath);

            var video = new List<IVideoEffect>();
            var audio = new List<IAudioEffectProvider>();
            foreach (Type type in GetLoadableTypes(assembly))
                ScanType(fullPath, type, video, audio);

            if (video.Count == 0 && audio.Count == 0)
            {
                _errors.Add(new PluginLoadError(fullPath, "No IVideoEffect or IAudioEffectProvider implementations found."));
                context.Unload();
                return null;
            }

            var plugin = new LoadedPlugin(fullPath, context, video, audio);
            _plugins.Add(plugin);
            return plugin;
        }
        catch (Exception ex)
        {
            _errors.Add(new PluginLoadError(assemblyPath, $"{ex.GetType().Name}: {ex.Message}"));
            context?.Unload();
            return null;
        }
    }

    /// <summary>Instantiates one candidate type into the video/audio lists, recording (not throwing) failures.</summary>
    private void ScanType(string source, Type type, List<IVideoEffect> video, List<IAudioEffectProvider> audio)
    {
        if (type.IsAbstract || !type.IsClass)
            return;
        bool isVideo = typeof(IVideoEffect).IsAssignableFrom(type);
        bool isAudio = typeof(IAudioEffectProvider).IsAssignableFrom(type);
        if (!isVideo && !isAudio)
            return;

        try
        {
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                _errors.Add(new PluginLoadError(source, $"{type.FullName}: effect types need a public parameterless constructor."));
                return;
            }

            object instance = Activator.CreateInstance(type)!;
            EffectDescriptor descriptor = isVideo
                ? ((IVideoEffect)instance).Descriptor
                : ((IAudioEffectProvider)instance).Descriptor;

            if (descriptor.Id.StartsWith("builtin.", StringComparison.Ordinal))
            {
                _errors.Add(new PluginLoadError(source, $"{type.FullName}: effect id '{descriptor.Id}' uses the reserved 'builtin.' prefix."));
                return;
            }

            if (isVideo)
                video.Add((IVideoEffect)instance);
            else
                audio.Add((IAudioEffectProvider)instance);
        }
        catch (Exception ex)
        {
            // Activator wraps a throwing constructor — report the plugin's actual exception.
            Exception cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            _errors.Add(new PluginLoadError(source, $"{type.FullName}: {cause.GetType().Name}: {cause.Message}"));
        }
    }

    /// <summary>Creates a fresh audio DSP instance for a plugin-contributed effect type id, or
    /// <see langword="null"/> if no loaded plugin provides it (the mixer then passes through).</summary>
    public IAudioEffect? CreateAudioEffect(string effectTypeId)
    {
        foreach (LoadedPlugin plugin in _plugins)
            foreach (IAudioEffectProvider provider in plugin.AudioEffectProviders)
                if (provider.Descriptor.Id == effectTypeId)
                    return provider.CreateEffect();
        return null;
    }

    /// <summary>
    /// Unloads one plugin: drops the host's references to its instances and starts the collectible context's
    /// unload. The caller must first unregister the plugin's effects everywhere it registered them. Returns a
    /// <see cref="WeakReference"/> to the load context so callers (tests) can observe collection; actual
    /// collection completes on a later GC once nothing else references plugin objects.
    /// </summary>
    public WeakReference Unload(LoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugins.Remove(plugin);
        PluginLoadContext? context = plugin.Context;
        plugin.Context = null;
        plugin.VideoEffects = [];
        plugin.AudioEffectProviders = [];
        var weak = new WeakReference(context);
        context?.Unload();
        return weak;
    }

    /// <summary>Unloads every plugin (see <see cref="Unload"/>).</summary>
    public void UnloadAll()
    {
        foreach (LoadedPlugin plugin in _plugins.ToArray())
            Unload(plugin);
    }

    /// <summary>The types of an assembly, tolerating partially-loadable assemblies (missing optional deps).</summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
