using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Persistence;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// The user looks file (plan/features/looks-browser.md): round-trips, merges after the built-ins, and degrades a
/// hand-edited or corrupt file to "skip with a warning" rather than failing.
/// </summary>
public sealed class LooksStoreTests
{
    private static Look UserLook(string name = "Mine") => new(
        Look.NewUserId(), name, Look.UserGroup, "notes",
        [
            new LookEntry(EffectTypeIds.Color, new Dictionary<string, double> { [EffectParamNames.Contrast] = 1.3 }),
            new LookEntry(EffectTypeIds.CreativeLut,
                new Dictionary<string, double> { [EffectParamNames.Mix] = 0.75 },
                new Dictionary<string, string> { [EffectParamNames.LutFile] = "/looks/warm.cube" }),
        ]);

    [Fact]
    public void Round_Trips_Values_Assets_And_Ids()
    {
        Look look = UserLook();
        LooksLoadResult result = LooksStore.Deserialize(LooksStore.Serialize([look]));

        Assert.Empty(result.Warnings);
        Look back = Assert.Single(result.Looks);
        Assert.Equal(look.Id, back.Id);
        Assert.Equal("Mine", back.Name);
        Assert.Equal("notes", back.Description);
        Assert.Equal(Look.UserGroup, back.Group);
        Assert.Equal(look.Entries.Select(e => e.EffectTypeId), back.Entries.Select(e => e.EffectTypeId));
        Assert.Equal(1.3, back.Entries[0].Values[EffectParamNames.Contrast]);
        Assert.Null(back.Entries[0].Assets);
        Assert.Equal("/looks/warm.cube", back.Entries[1].Assets![EffectParamNames.LutFile]);
    }

    [Fact]
    public void Never_Writes_Built_Ins()
    {
        string json = LooksStore.Serialize([LooksCatalog.BuiltIns[0], UserLook()]);
        Assert.DoesNotContain(Look.BuiltInPrefix, json);
        Assert.Single(LooksStore.Deserialize(json).Looks);
    }

    [Fact]
    public void Merge_Lists_Built_Ins_First_Then_User_Looks()
    {
        Look mine = UserLook();
        IReadOnlyList<Look> all = LooksStore.BuiltInAndUser([mine]);
        Assert.Equal(LooksCatalog.BuiltIns.Count + 1, all.Count);
        Assert.Equal(LooksCatalog.BuiltIns, all.Take(LooksCatalog.BuiltIns.Count));
        Assert.Same(mine, all[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    public void Blank_Or_Corrupt_Input_Yields_No_Looks(string? json) =>
        Assert.Empty(LooksStore.Deserialize(json).Looks);

    [Fact]
    public void Hand_Edited_Entries_Are_Repaired_Or_Skipped_With_Warnings()
    {
        const string json = """
        {
          "version": 1,
          "looks": [
            { "name": "", "effects": [ { "type": "builtin.color", "values": { "contrast": 1.1 } } ] },
            { "id": "user.a", "name": "No effects", "effects": [] },
            { "id": "builtin.look.teal-orange", "name": "Impostor",
              "effects": [ { "type": "builtin.color", "values": { "saturation": 0.5 } } ] },
            { "id": "user.a", "name": "Dup A", "effects": [ { "type": "builtin.color" } ] },
            { "id": "user.a", "name": "Dup B", "effects": [ { "type": "builtin.color" } ] },
            { "id": "user.log", "name": "Log Look",
              "effects": [
                { "type": "builtin.colortransform", "values": { "sourceProfile": 1 } },
                { "type": "plugin.not.loaded", "values": { "x": 1 } },
                { "type": "builtin.curves", "values": { "curveMasterMids": 0.1 } }
              ] },
          ]
        }
        """;
        LooksLoadResult result = LooksStore.Deserialize(json);

        Assert.Equal(["Impostor", "Dup A", "Dup B", "Log Look"], result.Looks.Select(l => l.Name));
        // A built-in-prefixed id in the user file is replaced, and duplicate ids are made unique.
        Assert.All(result.Looks, l => Assert.StartsWith(Look.UserPrefix, l.Id));
        Assert.Equal(result.Looks.Count, result.Looks.Select(l => l.Id).Distinct().Count());
        Assert.Equal("user.a", result.Looks[1].Id);
        // The tier-1 transform is stripped; an unknown (not-yet-loaded plugin) type is kept for apply-time skipping.
        Assert.Equal(["plugin.not.loaded", EffectTypeIds.Curves], result.Looks[3].Entries.Select(e => e.EffectTypeId));
        Assert.Contains(result.Warnings, w => w.Contains("no name"));
        Assert.Contains(result.Warnings, w => w.Contains("No effects"));
        Assert.Contains(result.Warnings, w => w.Contains("input color transform"));
    }

    [Fact]
    public void Unknown_Effect_In_A_Saved_Look_Is_Skipped_On_Apply_Not_A_Crash()
    {
        const string json = """
        { "version": 1, "looks": [ { "id": "user.p", "name": "Plugin look", "effects": [
            { "type": "plugin.gone", "values": { "x": 1 } },
            { "type": "builtin.whitebalance", "values": { "temperature": 20 } } ] } ] }
        """;
        Look look = Assert.Single(LooksStore.Deserialize(json).Looks);
        var clip = new Clip(MediaRefId.New(), Core.Timing.Timecode.Zero, Core.Timing.Timecode.FromSeconds(1), Core.Timing.Timecode.Zero);

        LookApplyResult applied = LookApplication.Build(look, clip);

        Assert.Equal(["plugin.gone"], applied.Skipped);
        Assert.Equal(EffectTypeIds.WhiteBalance, Assert.Single(applied.Added).EffectTypeId);
    }

    [Fact]
    public void Save_And_Load_Through_A_File_Leave_No_Temp_Behind()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-looks-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "looks.json");
        try
        {
            Assert.True(LooksStore.Save(path, [UserLook("One"), UserLook("Two")]));
            Assert.True(LooksStore.Save(path, [UserLook("Only")])); // overwrite in place
            Assert.Equal(["Only"], LooksStore.Load(path).Looks.Select(l => l.Name));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Missing_File_Loads_Empty() =>
        Assert.Empty(LooksStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "looks.json")).Looks);
}
