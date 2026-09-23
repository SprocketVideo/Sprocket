using Sprocket.App.MediaBrowser;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The Looks browser's app-wide library (plan/features/looks-browser.md): built-ins-then-user listing, persistence
/// of every mutation to its looks file, unique naming, and read-only built-ins. Each test uses its own temp file.
/// </summary>
public sealed class LooksLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sprocket-looks-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "looks.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static Look Draft(string name) => new("draft", name, "Cinematic", null,
        [new LookEntry(EffectTypeIds.Color, new Dictionary<string, double> { [EffectParamNames.Contrast] = 1.2 })]);

    [Fact]
    public void Missing_File_Loads_Only_The_Built_Ins()
    {
        var library = new LooksLibrary(FilePath);
        Assert.Empty(library.UserLooks);
        Assert.Empty(library.LoadWarnings);
        Assert.Equal(LooksCatalog.BuiltIns, library.All);
    }

    [Fact]
    public void Add_Files_Under_My_Looks_With_A_Fresh_User_Id_Saves_And_Raises_Changed()
    {
        var library = new LooksLibrary(FilePath);
        int changed = 0;
        library.Changed += () => changed++;

        Look stored = library.Add(Draft("Punchy"));

        Assert.StartsWith(Look.UserPrefix, stored.Id);
        Assert.Equal(Look.UserGroup, stored.Group);
        Assert.Equal(1, changed);
        Assert.False(library.LastSaveFailed);
        Assert.Same(stored, library.Find(stored.Id));
        Assert.Equal(stored, library.All[^1]);

        // A fresh library (the next window / launch) reads it back.
        Look reloaded = Assert.Single(new LooksLibrary(FilePath).UserLooks);
        Assert.Equal(stored.Id, reloaded.Id);
        Assert.Equal("Punchy", reloaded.Name);
    }

    [Fact]
    public void Duplicate_Names_Get_A_Numeric_Suffix_Case_Insensitively()
    {
        var library = new LooksLibrary(FilePath);
        library.Add(Draft("Warm"));
        Assert.Equal("warm 2", library.Add(Draft("warm")).Name);
        Assert.Equal("Warm 3", library.Add(Draft("  Warm  ")).Name);
    }

    [Fact]
    public void Rename_Keeps_Its_Own_Name_Free_And_Rejects_Blank_Or_Built_In()
    {
        var library = new LooksLibrary(FilePath);
        Look a = library.Add(Draft("A"));
        library.Add(Draft("B"));

        Assert.Equal("A", library.Rename(a.Id, "A")!.Name); // renaming to itself is not a clash
        Assert.Equal("B 2", library.Rename(a.Id, "B")!.Name);
        Assert.Null(library.Rename(a.Id, "   "));
        Assert.Null(library.Rename(LooksCatalog.BuiltIns[0].Id, "Hijack"));
        Assert.Equal("B 2", new LooksLibrary(FilePath).Find(a.Id)!.Name);
    }

    [Fact]
    public void Remove_Deletes_User_Looks_Only()
    {
        var library = new LooksLibrary(FilePath);
        Look a = library.Add(Draft("A"));

        Assert.False(library.Remove(LooksCatalog.BuiltIns[0].Id));
        Assert.NotNull(library.Find(LooksCatalog.BuiltIns[0].Id));
        Assert.True(library.Remove(a.Id));
        Assert.False(library.Remove(a.Id));
        Assert.Empty(new LooksLibrary(FilePath).UserLooks);
    }

    [Fact]
    public void Garbage_File_Loads_Empty_Without_Throwing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "not json {");
        Assert.Empty(new LooksLibrary(FilePath).UserLooks);
    }

    [Fact]
    public void An_Unreadable_File_Is_Backed_Up_Before_The_First_Save_Overwrites_It()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "not json {");
        var library = new LooksLibrary(FilePath);
        Assert.NotEmpty(library.LoadWarnings);
        Assert.False(File.Exists(library.BackupPath)); // nothing is copied until a save would overwrite it

        library.Add(Draft("New"));

        Assert.Equal("not json {", File.ReadAllText(library.BackupPath));
        Assert.Single(new LooksLibrary(FilePath).UserLooks);
    }
}
