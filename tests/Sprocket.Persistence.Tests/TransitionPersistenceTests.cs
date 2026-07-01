using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>Transitions round-trip through the versioned JSON (PLAN.md step 25), additively (no schema bump): a
/// transition-free project serializes byte-identically to a pre-step-25 file.</summary>
public class TransitionPersistenceTests
{
    private static Project RoundTrip(Project project) =>
        ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

    private static Project TwoClipProject(out VideoTrack track)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero));
        track.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.FromSeconds(4)));
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Transition_Round_Trips_Field_For_Field()
    {
        Project project = TwoClipProject(out VideoTrack track);
        track.Transitions.Add(new Transition(
            TransitionTypeIds.DipToBlack, Timecode.FromSeconds(4), Timecode.FromSeconds(1.5), TransitionAlignment.EndAtCut));

        VideoTrack loaded = Assert.IsType<VideoTrack>(RoundTrip(project).Timeline.Tracks[0]);
        Transition t = Assert.Single(loaded.Transitions);
        Assert.Equal(TransitionTypeIds.DipToBlack, t.TransitionTypeId);
        Assert.Equal(Timecode.FromSeconds(4), t.CutPoint);
        Assert.Equal(Timecode.FromSeconds(1.5), t.Duration);
        Assert.Equal(TransitionAlignment.EndAtCut, t.Alignment);
    }

    [Fact]
    public void Transition_Free_Track_Omits_The_Field()
    {
        Project project = TwoClipProject(out _);
        string json = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("transitions", json);
    }

    [Fact]
    public void Pre_Step25_File_Without_Transitions_Loads_With_None()
    {
        Project project = TwoClipProject(out _);
        // A round-tripped transition-free project loads with an empty transition list, never throwing.
        VideoTrack loaded = Assert.IsType<VideoTrack>(RoundTrip(project).Timeline.Tracks[0]);
        Assert.Empty(loaded.Transitions);
    }
}
