using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.App;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The user-facing plugin manager (PLAN.md step 58): discovery, enable/disable round-trips through the shared
/// <see cref="EffectCatalog"/>, honoring the persisted disabled list at startup, and the install / uninstall
/// file operations. Loads the REAL <c>Sprocket.TestPlugin</c> assembly through the manager's collectible-ALC
/// path (it is copied to the test output, not assembly-referenced), exactly like a third-party plugin.
/// </summary>
/// <remarks>
/// The GPU shader registration is stubbed (no-op) so the catalog registration — the observable part browsers and
/// the Inspector read — is exercised headlessly. <see cref="EffectCatalog"/> is process-global, so each test
/// cleans the test plugin's ids out again in <see cref="Dispose"/>; the ids are the test plugin's own, distinct
/// from every built-in.
/// </remarks>
public sealed class PluginManagerTests : IDisposable
{
    private const string InvertId = "plugin.test.invert";
    private const string GainId = "plugin.test.gain";

    private static string TestPluginPath => Path.Combine(AppContext.BaseDirectory, "Sprocket.TestPlugin.dll");

    private readonly List<string> _tempDirs = [];

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sprocket-mgr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Builds a manager over one user-writable directory, with an in-memory disabled-list store.</summary>
    private static PluginManager Manager(string userDir, HashSet<string> disabled) => new(
        [new PluginManager.PluginDirectory(userDir, IsUserWritable: true)],
        loadDisabled: () => disabled.ToArray(),
        saveDisabled: paths => { disabled.Clear(); foreach (string p in paths) disabled.Add(p); },
        registerShader: _ => { },       // no GPU in a headless test — catalog registration is what we assert
        unregisterShader: _ => { },
        log: (_, _) => { });

    [Fact]
    public void Initialize_Loads_And_Registers_The_Plugin_Effects()
    {
        string dir = NewDir();
        File.Copy(TestPluginPath, Path.Combine(dir, "Sprocket.TestPlugin.dll"));

        var manager = Manager(dir, []);
        manager.Initialize();

        PluginEntry entry = Assert.Single(manager.Entries);
        Assert.Equal(PluginStatus.Enabled, entry.Status);
        Assert.False(string.IsNullOrEmpty(entry.Version));
        Assert.Contains(entry.Effects, e => e.Id == InvertId);
        Assert.Contains(entry.Effects, e => e.Id == GainId);

        // The catalog every browser / the Inspector / the render graph reads now carries them.
        Assert.NotNull(EffectCatalog.Find(InvertId));
        Assert.NotNull(EffectCatalog.Find(GainId));
    }

    [Fact]
    public void Disable_Then_Enable_Unregisters_And_Reregisters()
    {
        string dir = NewDir();
        File.Copy(TestPluginPath, Path.Combine(dir, "Sprocket.TestPlugin.dll"));
        var disabled = new HashSet<string>();
        var manager = Manager(dir, disabled);
        manager.Initialize();
        PluginEntry entry = manager.Entries[0];

        Assert.True(manager.SetEnabled(entry, false));
        Assert.Equal(PluginStatus.Disabled, entry.Status);
        Assert.Null(EffectCatalog.Find(InvertId));
        Assert.Null(EffectCatalog.Find(GainId));
        Assert.Contains(entry.AssemblyPath, disabled); // persisted through the save callback

        Assert.True(manager.SetEnabled(entry, true));
        Assert.Equal(PluginStatus.Enabled, entry.Status);
        Assert.NotNull(EffectCatalog.Find(InvertId));
        Assert.NotNull(EffectCatalog.Find(GainId));
        Assert.DoesNotContain(entry.AssemblyPath, disabled);
    }

    [Fact]
    public void A_Disabled_Plugin_Is_Skipped_At_Startup()
    {
        string dir = NewDir();
        string dll = Path.Combine(dir, "Sprocket.TestPlugin.dll");
        File.Copy(TestPluginPath, dll);

        // Pre-seed the disabled list with this plugin's path, exactly as a persisted settings file would.
        var disabled = new HashSet<string> { Path.GetFullPath(dll) };
        var manager = Manager(dir, disabled);
        manager.Initialize();

        PluginEntry entry = Assert.Single(manager.Entries);
        Assert.Equal(PluginStatus.Disabled, entry.Status);
        Assert.Empty(entry.Effects);
        Assert.Null(EffectCatalog.Find(InvertId)); // never registered — the effects pass through in projects (§15)
    }

    [Fact]
    public void Install_Copies_The_File_Into_The_User_Folder_And_Loads_It()
    {
        string userDir = NewDir();
        string source = Path.Combine(NewDir(), "MyPlugin.dll");
        File.Copy(TestPluginPath, source);

        var manager = Manager(userDir, []);
        manager.Initialize();
        Assert.Empty(manager.Entries);

        string? error = manager.Install(source);

        Assert.Null(error);
        Assert.True(File.Exists(Path.Combine(userDir, "MyPlugin.dll")));
        PluginEntry entry = Assert.Single(manager.Entries);
        Assert.Equal(PluginStatus.Enabled, entry.Status);
        Assert.True(entry.IsUserPlugin);
        Assert.NotNull(EffectCatalog.Find(InvertId));
    }

    [Fact]
    public void Install_Refuses_A_Duplicate_File_Name()
    {
        string userDir = NewDir();
        File.Copy(TestPluginPath, Path.Combine(userDir, "Sprocket.TestPlugin.dll"));
        string source = Path.Combine(NewDir(), "Sprocket.TestPlugin.dll");
        File.Copy(TestPluginPath, source);

        var manager = Manager(userDir, []);
        manager.Initialize();

        string? error = manager.Install(source);
        Assert.NotNull(error);
        Assert.Contains("already installed", error);
    }

    [Fact]
    public void Uninstall_Unloads_Registers_Off_And_Deletes_The_File()
    {
        string userDir = NewDir();
        string dll = Path.Combine(userDir, "Sprocket.TestPlugin.dll");
        File.Copy(TestPluginPath, dll);
        var manager = Manager(userDir, []);
        manager.Initialize();
        PluginEntry entry = manager.Entries[0];

        string? error = manager.Uninstall(entry);

        Assert.Null(error);
        Assert.Empty(manager.Entries);
        Assert.False(File.Exists(dll));
        Assert.Null(EffectCatalog.Find(InvertId));
        Assert.Null(EffectCatalog.Find(GainId));
    }

    [Fact]
    public void A_Broken_Assembly_Becomes_An_Error_Row_Not_A_Crash()
    {
        string dir = NewDir();
        File.WriteAllBytes(Path.Combine(dir, "Broken.dll"), [0x00, 0x01, 0x02, 0x03]);

        var manager = Manager(dir, []);
        manager.Initialize();

        PluginEntry entry = Assert.Single(manager.Entries);
        Assert.Equal(PluginStatus.Error, entry.Status);
        Assert.False(string.IsNullOrEmpty(entry.Message));
    }

    [Fact]
    public void Rescan_Picks_Up_A_Newly_Added_Plugin()
    {
        string dir = NewDir();
        var manager = Manager(dir, []);
        manager.Initialize();
        Assert.Empty(manager.Entries);

        File.Copy(TestPluginPath, Path.Combine(dir, "Sprocket.TestPlugin.dll"));
        manager.Rescan();

        Assert.Single(manager.Entries);
        Assert.NotNull(EffectCatalog.Find(InvertId));
    }

    public void Dispose()
    {
        // EffectCatalog is process-global: make sure this test's plugin ids don't leak to the next test.
        EffectCatalog.Unregister(InvertId);
        EffectCatalog.Unregister(GainId);

        foreach (string dir in _tempDirs)
        {
            // A just-unloaded collectible ALC can hold a copied DLL briefly on Windows; the temp dir is disposable.
            for (int i = 0; i < 5 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                catch (UnauthorizedAccessException) { break; }
            }
        }
    }
}
