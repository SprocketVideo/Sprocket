using System.Reflection;
using System.Runtime.Loader;

namespace Sprocket.Plugins;

/// <summary>
/// The collectible <see cref="AssemblyLoadContext"/> one plugin loads into (ARCHITECTURE.md §13): the
/// plugin's own dependencies resolve from its directory via <see cref="AssemblyDependencyResolver"/> (so two
/// plugins can carry conflicting versions of the same library), while the Sprocket contract assemblies are
/// deferred to the default context so the plugin's <c>IVideoEffect</c> IS the host's <c>IVideoEffect</c>.
/// Collectible, so <see cref="AssemblyLoadContext.Unload"/> releases the plugin once the host drops its
/// instance references.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base($"plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Contract/host assemblies must unify with the host's copies — a plugin that shipped its own
        // Sprocket.Core would otherwise get type identities the host can't cast to. Returning null defers
        // resolution to the default context (which also covers the shared framework).
        if (assemblyName.Name is { } name && name.StartsWith("Sprocket.", StringComparison.Ordinal))
            return null;

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
