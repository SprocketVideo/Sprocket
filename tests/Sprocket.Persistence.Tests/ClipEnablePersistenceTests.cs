using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// The clip Enable toggle on the wire (PLAN.md step 53): <c>Enabled=false</c> round-trips, the field is absent
/// for the common enabled case so a pre-53 file loads enabled and untouched projects serialize byte-identically,
/// and — because the render-cache hash covers the clip DTO — toggling invalidates cached renders (step 32).
/// </summary>
public class ClipEnablePersistenceTests
{
    private static Project BuildProject(out Clip clip)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        var spec = new GeneratorSpec(GeneratorTypeIds.SolidColor).SetString(GeneratorParamNames.Color, "#FF203040");
        clip = Clip.CreateGenerator(spec, Timecode.FromSeconds(2), Timecode.Zero);
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Disabled_Round_Trips_And_Enabled_Writes_Nothing()
    {
        Project project = BuildProject(out Clip clip);
        string enabledJson = ProjectSerializer.Serialize(project);

        clip.Enabled = false;
        string disabledJson = ProjectSerializer.Serialize(project);
        Assert.NotEqual(enabledJson, disabledJson);
        Assert.False(ProjectSerializer.Deserialize(disabledJson).Timeline.VideoTracks.First().Clips[0].Enabled);

        // Re-enabling restores the exact pre-toggle bytes: the enabled case writes no field at all (the
        // absent-means-default idiom), so untouched projects keep the pre-53 wire shape.
        clip.Enabled = true;
        Assert.Equal(enabledJson, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void A_Pre53_File_Without_The_Field_Loads_Enabled()
    {
        // Serialize an enabled project (which omits the field — the exact pre-53 wire shape) and load it back.
        Project loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(BuildProject(out _)));
        Assert.True(loaded.Timeline.VideoTracks.First().Clips[0].Enabled);
    }

    [Fact]
    public void Toggling_Enabled_Changes_The_Render_Cache_Hash_And_Undo_Restores_It()
    {
        Project project = BuildProject(out Clip clip);
        string Hash() => RenderCacheHasher.ComputeHash(
            project, project.ActiveSequence.Id, Timecode.Zero, Timecode.FromSeconds(2), RenderCacheScope.Video);

        string before = Hash();
        clip.Enabled = false;
        Assert.NotEqual(before, Hash());

        clip.Enabled = true; // the undo of the toggle
        Assert.Equal(before, Hash());
    }
}
