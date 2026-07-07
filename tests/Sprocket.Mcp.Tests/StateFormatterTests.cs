using System.Text.Json.Nodes;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Mcp;
using Xunit;

namespace Sprocket.Mcp.Tests;

/// <summary>The read-side JSON payloads (PLAN.md step 38): shape, ids, tick companions.</summary>
public class StateFormatterTests
{
    [Theory]
    [InlineData(0, "0:00:00.000")]
    [InlineData(240000, "0:00:01.000")]
    [InlineData(240000 * 90 + 120000, "0:01:30.500")]
    [InlineData(240000L * 3661, "1:01:01.000")]
    [InlineData(-240000, "-0:00:01.000")]
    public void TimeString_Formats_Ticks(long ticks, string expected) =>
        Assert.Equal(expected, StateFormatter.TimeString(ticks));

    [Fact]
    public void ProjectState_Carries_Ids_Format_And_History()
    {
        var session = new FakeEditorSession();
        MediaRef media = session.ImportMedia(@"C:\media\shot.mp4").Value!;
        Clip clip = session.PlaceClip(media.Id.Value, 240000, null, null, linked: true, includeVideo: true, includeAudio: true).Value!;
        session.Project.Timeline.Markers.Add(new Marker(Timecode.FromSeconds(0.5), "beat"));

        string json = StateFormatter.ProjectState(
            session.Project, session.History, @"C:\projects\demo.sprocket.json", dirty: true,
            playheadTicks: 120000, durationTicks: session.Project.Timeline.Duration.Ticks, playing: false);
        JsonNode root = JsonNode.Parse(json)!;

        Assert.Equal(240000, (long)root["ticks_per_second"]!);
        Assert.Equal(@"C:\projects\demo.sprocket.json", (string)root["project_path"]!);
        Assert.Equal("30/1", (string)root["format"]!["frame_rate"]!);
        Assert.Equal("1920x1080", (string)root["format"]!["resolution"]!);

        JsonNode mediaEntry = root["media"]!.AsArray().Single()!;
        Assert.Equal(media.Id.Value.ToString("D"), (string)mediaEntry["media_id"]!);
        Assert.Equal("shot.mp4", (string)mediaEntry["name"]!);

        JsonArray tracks = root["tracks"]!.AsArray();
        Assert.Equal(2, tracks.Count);
        JsonNode videoTrack = tracks[0]!;
        Assert.Equal("video", (string)videoTrack["kind"]!);
        Assert.Equal(RuntimeIds.IdOf(session.Project.Timeline.Tracks[0]), (int)videoTrack["track_id"]!);

        JsonNode clipEntry = videoTrack["clips"]!.AsArray().Single()!;
        Assert.Equal(RuntimeIds.IdOf(clip), (int)clipEntry["clip_id"]!);
        Assert.Equal(240000, (long)clipEntry["start_ticks"]!);
        Assert.Equal("0:00:01.000", (string)clipEntry["start"]!);
        Assert.Equal(media.Id.Value.ToString("D"), (string)clipEntry["media_id"]!);

        Assert.Equal("beat", (string)root["markers"]!.AsArray().Single()!["name"]!);
        Assert.False((bool)root["playhead"]!["playing"]!);
        Assert.Equal(120000, (long)root["playhead"]!["position_ticks"]!);
        Assert.True((bool)root["history"]!["can_undo"]!); // import + place both executed commands
        Assert.NotNull(root["history"]!["undo_label"]);
    }

    [Fact]
    public void ClipList_Filters_By_Track_And_Rejects_Unknown_Ids()
    {
        var session = new FakeEditorSession();
        MediaRef media = session.ImportMedia(@"C:\media\shot.mp4").Value!;
        session.PlaceClip(media.Id.Value, 0, null, null, linked: true, includeVideo: true, includeAudio: true);
        Track video = session.Project.Timeline.Tracks[0];

        string? filtered = StateFormatter.ClipList(session.Project, RuntimeIds.IdOf(video));
        JsonArray tracks = JsonNode.Parse(filtered!)!["tracks"]!.AsArray();
        Assert.Single(tracks);
        Assert.Equal("video", (string)tracks[0]!["kind"]!);

        Assert.Null(StateFormatter.ClipList(session.Project, int.MaxValue));

        string? all = StateFormatter.ClipList(session.Project, null);
        Assert.Equal(2, JsonNode.Parse(all!)!["tracks"]!.AsArray().Count);
    }

    [Fact]
    public void EffectTypes_Lists_The_Catalog_With_Parameters()
    {
        string json = StateFormatter.EffectTypes()!;
        JsonArray effects = JsonNode.Parse(json)!["effect_types"]!.AsArray();
        JsonNode brightness = effects.Single(e => (string)e!["type_id"]! == EffectTypeIds.Brightness)!;
        JsonNode amount = brightness["parameters"]!.AsArray()
            .Single(p => (string)p!["name"]! == EffectParamNames.Amount)!;
        Assert.NotNull(amount["min"]);
        Assert.NotNull(amount["max"]);
        Assert.NotNull(amount["default"]);
    }

    [Fact]
    public void EffectTypes_Expose_Parameter_Kinds_And_Dropdown_Choices()
    {
        string json = StateFormatter.EffectTypes()!;
        JsonArray effects = JsonNode.Parse(json)!["effect_types"]!.AsArray();

        // A continuous scalar declares its kind (and has no choices).
        JsonNode amount = effects.Single(e => (string)e!["type_id"]! == EffectTypeIds.Brightness)!
            ["parameters"]!.AsArray().Single(p => (string)p!["name"]! == EffectParamNames.Amount)!;
        Assert.Equal("continuous", (string)amount["kind"]!);
        Assert.Null(amount["choices"]);

        // A toggle is marked so a client knows it's 0/1.
        JsonNode showMask = effects.Single(e => (string)e!["type_id"]! == EffectTypeIds.HslQualifier)!
            ["parameters"]!.AsArray().Single(p => (string)p!["name"]! == EffectParamNames.ShowMask)!;
        Assert.Equal("toggle", (string)showMask["kind"]!);

        // A dropdown carries the choice labels its value indexes into.
        JsonNode profile = effects.Single(e => (string)e!["type_id"]! == EffectTypeIds.ColorTransform)!
            ["parameters"]!.AsArray().Single(p => (string)p!["name"]! == EffectParamNames.SourceProfile)!;
        Assert.Equal("dropdown", (string)profile["kind"]!);
        Assert.Equal(ColorProfiles.DisplayNames.Count, profile["choices"]!.AsArray().Count);
    }

    [Fact]
    public void HistoryState_Reports_Labels_And_The_Performed_Note()
    {
        var history = new EditHistory();
        var markers = new List<Marker>();
        history.Execute(new AddMarkerCommand(markers, new Marker(Timecode.Zero, "m")));

        JsonNode root = JsonNode.Parse(StateFormatter.HistoryState(history, "added marker"))!;
        Assert.True((bool)root["can_undo"]!);
        Assert.False((bool)root["can_redo"]!);
        Assert.Equal("added marker", (string)root["performed"]!);
    }
}
