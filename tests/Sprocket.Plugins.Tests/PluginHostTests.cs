using System.Runtime.CompilerServices;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Plugins;
using Xunit;

namespace Sprocket.Plugins.Tests;

/// <summary>
/// Exercises the plugin host (PLAN.md step 33, ARCHITECTURE.md §13) against a REAL separate plugin assembly
/// (Sprocket.TestPlugin — built alongside but not assembly-referenced, so its types only ever enter the
/// process through the host's collectible AssemblyLoadContext, exactly like a third-party plugin).
/// </summary>
public sealed class PluginHostTests
{
    private static string TestPluginPath =>
        Path.Combine(AppContext.BaseDirectory, "Sprocket.TestPlugin.dll");

    [Fact]
    public void Load_DiscoversVideoAndAudioEffects()
    {
        var host = new PluginHost();
        LoadedPlugin? plugin = host.Load(TestPluginPath);

        Assert.NotNull(plugin);
        Assert.Single(plugin!.VideoEffects);           // InvertEffect (Reserved/Throwing are rejected)
        Assert.Single(plugin.AudioEffectProviders);    // TestGainProvider

        IVideoEffect video = plugin.VideoEffects[0];
        Assert.Equal("plugin.test.invert", video.Descriptor.Id);
        Assert.Equal(EffectCategory.Color, video.Descriptor.Category);
        Assert.Contains("src.eval", video.SkslSource);

        Assert.Equal("plugin.test.gain", plugin.AudioEffectProviders[0].Descriptor.Id);
    }

    [Fact]
    public void Load_RecordsReservedIdAndThrowingCtor_AsErrors_NotFailures()
    {
        var host = new PluginHost();
        LoadedPlugin? plugin = host.Load(TestPluginPath);

        Assert.NotNull(plugin); // the good effects still load
        Assert.Contains(host.Errors, e => e.Message.Contains("builtin.hijack"));
        Assert.Contains(host.Errors, e => e.Message.Contains("ThrowingEffect"));
    }

    [Fact]
    public void PluginTypes_UnifyWithHostContracts()
    {
        var host = new PluginHost();
        LoadedPlugin? plugin = host.Load(TestPluginPath);

        // The plugin resolved Sprocket.Core from the default context, so its EffectDescriptor / IVideoEffect
        // ARE the host's types (no cross-ALC type-identity split).
        Assert.NotNull(plugin);
        Assert.IsAssignableFrom<IVideoEffect>(plugin!.VideoEffects[0]);
        Assert.Same(typeof(EffectDescriptor).Assembly, plugin.VideoEffects[0].Descriptor.GetType().Assembly);
    }

    [Fact]
    public void CreateAudioEffect_ProcessesSamples()
    {
        var host = new PluginHost();
        host.Load(TestPluginPath);

        IAudioEffect? effect = host.CreateAudioEffect("plugin.test.gain");
        Assert.NotNull(effect);

        float[] samples = [0.25f, -0.5f, 0.1f, 0.0f];
        var parameters = new ResolvedEffect("plugin.test.gain", new Dictionary<string, double> { ["gain"] = 2.0 });
        effect!.Process(samples, frames: 2, sampleRate: 48000, channels: 2, parameters);

        Assert.Equal(0.5f, samples[0], 5);
        Assert.Equal(-1.0f, samples[1], 5);
    }

    [Fact]
    public void CreateAudioEffect_UnknownId_ReturnsNull()
    {
        var host = new PluginHost();
        host.Load(TestPluginPath);
        Assert.Null(host.CreateAudioEffect("plugin.test.doesnotexist"));
    }

    [Fact]
    public void Load_MissingFile_RecordsError_ReturnsNull()
    {
        var host = new PluginHost();
        Assert.Null(host.Load(Path.Combine(AppContext.BaseDirectory, "NoSuchPlugin.dll")));
        Assert.Single(host.Errors);
    }

    [Fact]
    public void Load_GarbageDll_RecordsError_ReturnsNull()
    {
        string garbage = Path.Combine(Path.GetTempPath(), $"sprocket-garbage-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(garbage, [0x00, 0x01, 0x02, 0x03]);
        try
        {
            var host = new PluginHost();
            Assert.Null(host.Load(garbage));
            Assert.Single(host.Errors);
        }
        finally
        {
            File.Delete(garbage);
        }
    }

    [Fact]
    public void LoadDirectory_MissingDirectory_IsNoOp()
    {
        var host = new PluginHost();
        Assert.Equal(0, host.LoadDirectory(Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}")));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void LoadDirectory_FindsTopLevelDllsAndSubdirectoryBundles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sprocket-plugins-{Guid.NewGuid():N}");
        string bundleDir = Path.Combine(dir, "BundledPlugin");
        Directory.CreateDirectory(bundleDir);
        try
        {
            File.Copy(TestPluginPath, Path.Combine(dir, "TopLevel.dll"));
            File.Copy(TestPluginPath, Path.Combine(bundleDir, "BundledPlugin.dll"));

            var host = new PluginHost();
            Assert.Equal(2, host.LoadDirectory(dir));
            Assert.Equal(2, host.Plugins.Count);
            host.UnloadAll(); // release the copies so the temp dir can be deleted
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Unload_CollectsThePluginContext()
    {
        WeakReference weak = LoadAndUnload();
        for (int i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "The collectible plugin AssemblyLoadContext should be collected after Unload.");
    }

    /// <summary>Keeps every strong reference to plugin objects inside a non-inlined frame so the caller's
    /// stack can't accidentally root them during the collection assertions.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndUnload()
    {
        var host = new PluginHost();
        LoadedPlugin? plugin = host.Load(TestPluginPath);
        Assert.NotNull(plugin);
        return host.Unload(plugin!);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // An unloading ALC may still hold the file briefly on Windows; the temp dir is disposable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
