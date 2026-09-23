using Sprocket.App.MediaBrowser;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The Looks tab's pure helpers (plan/features/looks-browser.md): row badge, search filter, grouping, LUT-import
/// look, status text, and the shared apply path the browser's double-click and the timeline's drop both take.
/// The row / dialog wiring rests on these plus manual verification.
/// </summary>
public class LooksBrowserModelTests
{
    private static Clip NewClip() =>
        new(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);

    private static Look UserLook(string name, params LookEntry[] entries) =>
        new(Look.NewUserId(), name, Look.UserGroup, null, entries);

    private static LookEntry ColorEntry() =>
        new(EffectTypeIds.Color, new Dictionary<string, double> { [EffectParamNames.Saturation] = 0.5 });

    // ── Badge ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Badge_Names_A_Single_Creative_Lut_And_Counts_Everything_Else()
    {
        Assert.Equal("LUT", LooksBrowserModel.Badge(LooksBrowserModel.FromLutFile("/luts/film.cube")));
        Assert.Equal("1 effect", LooksBrowserModel.Badge(UserLook("One", ColorEntry())));
        Assert.Equal("2 effects", LooksBrowserModel.Badge(UserLook("Two", ColorEntry(), ColorEntry())));
    }

    // ── Matches / Grouped ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("   ", true)]
    [InlineData("teal", true)]              // name, case-insensitive
    [InlineData("cinematic", true)]         // group
    [InlineData("blockbuster split", true)] // every term, any field (description)
    [InlineData("teal vintage", false)]     // one term misses
    public void Matches_Requires_Every_Term_In_Name_Group_Or_Description(string? search, bool expected)
    {
        var look = new Look("builtin.look.x", "Teal & Orange", "Cinematic", "The blockbuster split.", [ColorEntry()]);
        Assert.Equal(expected, LooksBrowserModel.Matches(look, search));
    }

    [Fact]
    public void Grouped_Lists_Built_In_Groups_In_Catalog_Order_Then_User_Group()
    {
        Look mine = UserLook("Mine", ColorEntry());
        var groups = LooksBrowserModel.Grouped([.. LooksCatalog.BuiltIns, mine], search: null);

        Assert.Equal([.. LooksCatalog.Groups, Look.UserGroup], groups.Select(g => g.Group));
        Assert.Equal([mine], groups[^1].Looks);
    }

    [Fact]
    public void Grouped_Omits_Empty_Built_In_Groups_But_Keeps_The_Empty_User_Group()
    {
        var groups = LooksBrowserModel.Grouped(LooksCatalog.BuiltIns, search: "zzz-no-such-look");
        var only = Assert.Single(groups);
        Assert.Equal(Look.UserGroup, only.Group);
        Assert.Empty(only.Looks);
    }

    // ── FromLutFile ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FromLutFile_Makes_A_Full_Intensity_Creative_Lut_User_Look()
    {
        string path = Path.Combine("luts", "Warm Print.cube");
        Look look = LooksBrowserModel.FromLutFile(path);

        Assert.StartsWith(Look.UserPrefix, look.Id);
        Assert.Equal("Warm Print", look.Name);
        Assert.Equal(Look.UserGroup, look.Group);
        LookEntry entry = Assert.Single(look.Entries);
        Assert.Equal(EffectTypeIds.CreativeLut, entry.EffectTypeId);
        Assert.Equal(1.0, entry.Values[EffectParamNames.Mix]);
        Assert.Equal(path, entry.Assets![EffectParamNames.LutFile]);
    }

    // ── Apply / ApplyStatus ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_Adds_The_Looks_Effects_As_One_Undo_Step()
    {
        Look look = LooksCatalog.BuiltIns[0];
        Clip clip = NewClip();
        var history = new EditHistory();

        LookApplyResult result = LooksBrowserModel.Apply(look, clip, history);

        Assert.NotNull(result.Command);
        Assert.Equal(look.Entries.Select(e => e.EffectTypeId), clip.Effects.Select(e => e.EffectTypeId));
        Assert.True(history.Undo());
        Assert.Empty(clip.Effects);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Apply_Executes_Nothing_When_No_Entry_Survives()
    {
        Look look = UserLook("Ghost", new LookEntry("plugin.not-installed", new Dictionary<string, double>()));
        Clip clip = NewClip();
        var history = new EditHistory();

        LookApplyResult result = LooksBrowserModel.Apply(look, clip, history);

        Assert.Null(result.Command);
        Assert.Empty(clip.Effects);
        Assert.False(history.CanUndo);
        Assert.StartsWith("Couldn't apply look Ghost", LooksBrowserModel.ApplyStatus(look, result, "a.mp4"));
    }

    [Fact]
    public void ApplyStatus_Names_Skipped_Effect_Types()
    {
        Look look = UserLook("Mixed", ColorEntry(), new LookEntry("plugin.gone", new Dictionary<string, double>()));
        LookApplyResult result = LookApplication.Build(look, NewClip());

        Assert.Equal("Applied look Mixed to a.mp4. Skipped 1 unavailable effect (plugin.gone).",
            LooksBrowserModel.ApplyStatus(look, result, "a.mp4"));
    }

    [Fact]
    public void ApplyToClip_Refuses_A_Clip_On_An_Audio_Track()
    {
        var project = new Project();
        Clip clip = NewClip();
        var audio = new AudioTrack();
        audio.Clips.Add(clip);
        project.Timeline.Tracks.Add(audio);
        var history = new EditHistory();

        Assert.Equal(LooksBrowserModel.AudioClipRefusal,
            LooksBrowserModel.ApplyToClip(LooksCatalog.BuiltIns[0], clip, project, history));
        Assert.Empty(clip.Effects);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ClipName_Uses_The_Source_File_Name_Else_Clip()
    {
        var project = new Project();
        var media = new MediaRef(MediaRefId.New(), Path.Combine("media", "shot01.mp4"),
            new ProbedMediaInfo(Timecode.FromSeconds(1), true, new Rational(30, 1), 1920, 1080, false, 0, 0));
        project.MediaPool.Add(media);
        var clip = new Clip(media.Id, Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero);

        Assert.Equal("shot01.mp4", LooksBrowserModel.ClipName(project, clip));
        Assert.Equal("clip", LooksBrowserModel.ClipName(project, NewClip()));
    }
}
