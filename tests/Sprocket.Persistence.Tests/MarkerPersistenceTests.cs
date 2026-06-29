using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Round-trip tests for markers (PLAN.md step 20). Markers serialize additively (nullable list, WhenWritingNull)
/// so a marker-less project stays byte-identical to a pre-step-20 file — verified here too.
/// </summary>
public class MarkerPersistenceTests
{
    private static Project RoundTrip(Project p) => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));

    [Fact]
    public void Round_Trips_Sequence_And_Clip_Markers()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        timeline.Markers.Add(new Marker(Timecode.FromSeconds(1), "Intro", "needs a fade", MarkerColor.Red));
        timeline.Markers.Add(new Marker(Timecode.FromSeconds(4), "B-roll", "", MarkerColor.Green,
            duration: Timecode.FromSeconds(2)));

        var track = new VideoTrack { Name = "V1" };
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(6), Timecode.Zero);
        clip.Markers.Add(new Marker(Timecode.FromSeconds(2), "focus", color: MarkerColor.Cyan));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);

        Timeline loaded = RoundTrip(project).Timeline;

        Assert.Collection(loaded.Markers,
            m =>
            {
                Assert.Equal(Timecode.FromSeconds(1), m.Time);
                Assert.Equal("Intro", m.Name);
                Assert.Equal("needs a fade", m.Comment);
                Assert.Equal(MarkerColor.Red, m.Color);
                Assert.False(m.IsSpan);
            },
            m =>
            {
                Assert.Equal("B-roll", m.Name);
                Assert.Equal(MarkerColor.Green, m.Color);
                Assert.True(m.IsSpan);
                Assert.Equal(Timecode.FromSeconds(2), m.Duration);
            });

        Marker clipMarker = Assert.Single(loaded.VideoTracks.First().Clips.Single().Markers);
        Assert.Equal("focus", clipMarker.Name);
        Assert.Equal(MarkerColor.Cyan, clipMarker.Color);
        Assert.Equal(Timecode.FromSeconds(2), clipMarker.Time);
    }

    [Fact]
    public void Marker_Less_Project_Omits_The_Markers_Field()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        timeline.Tracks.Add(new VideoTrack { Name = "V1" });

        string json = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("\"markers\"", json);
    }

    [Fact]
    public void Pre_Step20_File_Without_Markers_Loads_With_None()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "media": [],
              "timeline": { "frameRate": { "num": 30, "den": 1 }, "resolution": { "width": 1920, "height": 1080 }, "sampleRate": 48000, "tracks": [] },
              "settings": { "masterGainDb": 0.0 }
            }
            """;
        Project loaded = ProjectSerializer.Deserialize(json);
        Assert.Empty(loaded.Timeline.Markers);
    }
}
