using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Round-trip tests for multiple sequences + nested-sequence clips (PLAN.md step 23). The multi-sequence wire
/// shape is additive: a single-sequence project with no nesting serializes byte-identically to a pre-step-23
/// file (no schema bump), while a project with extra sequences or a nested clip writes the full sequence list.
/// </summary>
public class SequencePersistenceTests
{
    private static Project RoundTrip(Project project)
        => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

    /// <summary>A project whose active "Parent" sequence nests a "Child" sequence (with a media clip) as a clip.</summary>
    private static Project ParentNestingChild(out SequenceId childId, out MediaRefId childMedia)
    {
        childMedia = MediaRefId.New();
        var childTimeline = new Timeline(new Rational(30, 1), new Resolution(1280, 720), 48000);
        var childTrack = new VideoTrack { Name = "V1" };
        childTrack.Clips.Add(new Clip(childMedia, Timecode.Zero, Timecode.FromSeconds(8), Timecode.Zero));
        childTimeline.Tracks.Add(childTrack);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);
        childId = child.Id;

        var parentTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var parent = new Sequence(SequenceId.New(), "Parent", parentTimeline);
        var project = new Project(parent);
        project.Sequences.Add(child);

        var parentTrack = new VideoTrack { Name = "V1" };
        parentTrack.Clips.Add(Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(8), Timecode.FromSeconds(2)));
        parentTimeline.Tracks.Add(parentTrack);
        return project;
    }

    [Fact]
    public void Multi_Sequence_And_Nested_Clip_Round_Trip()
    {
        Project loaded = RoundTrip(ParentNestingChild(out SequenceId childId, out MediaRefId childMedia));

        Assert.Equal(2, loaded.Sequences.Count);
        Assert.Equal("Parent", loaded.ActiveSequence.Name);

        // The nested clip resolves to the loaded child sequence by its preserved id.
        Clip nest = loaded.Timeline.VideoTracks.First().Clips.Single();
        Assert.Equal(ClipKind.Sequence, nest.Kind);
        Assert.Equal(childId, nest.SourceSequenceId);
        Assert.Equal(Timecode.FromSeconds(2), nest.TimelineStart);

        Sequence? child = loaded.GetSequence(childId);
        Assert.NotNull(child);
        Assert.Equal("Child", child!.Name);
        Assert.Equal(new Resolution(1280, 720), child.Timeline.Resolution);
        Assert.Equal(childMedia, child.Timeline.VideoTracks.First().Clips.Single().MediaRefId);
    }

    [Fact]
    public void Active_Sequence_Selection_Round_Trips()
    {
        Project project = ParentNestingChild(out _, out _);
        // Switch the active sequence to the child, save, reload: the same sequence is active.
        project.ActiveSequence = project.Sequences[1];
        SequenceId activeId = project.ActiveSequence.Id;

        Project loaded = RoundTrip(project);
        Assert.Equal(activeId, loaded.ActiveSequence.Id);
    }

    [Fact]
    public void Single_Sequence_Project_Omits_The_Sequences_Array()
    {
        // A plain single-sequence project with no nesting keeps the legacy Timeline-only shape (additive, no bump).
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        project.Timeline.Tracks.Add(new VideoTrack { Name = "V1" });

        string json = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("\"sequences\"", json);
        Assert.DoesNotContain("\"activeSequenceId\"", json);
        Assert.DoesNotContain("\"sourceSequenceId\"", json);
        Assert.Contains("\"timeline\"", json);
    }

    [Fact]
    public void Nested_Project_Writes_The_Sequences_Shape()
    {
        string json = ProjectSerializer.Serialize(ParentNestingChild(out _, out _));
        Assert.Contains("\"sequences\"", json);
        Assert.Contains("\"activeSequenceId\"", json);
        Assert.Contains("\"sourceSequenceId\"", json);
    }
}

/// <summary>
/// Portrait/social sequence formats and the per-clip conform mode round-trip additively: any width × height
/// persists through the existing ResolutionDto, and a clip's Fill conform writes a nullable field that Fit
/// clips omit — so untouched projects serialize byte-identically and older files load as Fit.
/// </summary>
public class ConformPersistenceTests
{
    private static Project RoundTrip(Project project)
        => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

    private static Project PortraitProjectWithClip(out Clip clip)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1080, 1920), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Portrait_Resolution_Round_Trips()
    {
        Project loaded = RoundTrip(PortraitProjectWithClip(out _));
        Assert.Equal(new Resolution(1080, 1920), loaded.Timeline.Resolution);
    }

    [Fact]
    public void Fill_Conform_Round_Trips()
    {
        Project project = PortraitProjectWithClip(out Clip clip);
        clip.ConformMode = ClipConformMode.Fill;
        Project loaded = RoundTrip(project);
        Clip restored = Assert.Single(Assert.IsType<VideoTrack>(loaded.Timeline.Tracks[0]).Clips);
        Assert.Equal(ClipConformMode.Fill, restored.ConformMode);
    }

    [Fact]
    public void Fit_Conform_Writes_No_Field_And_Loads_As_Fit()
    {
        Project project = PortraitProjectWithClip(out _);
        string json = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("conformMode", json, StringComparison.OrdinalIgnoreCase);

        Clip restored = Assert.Single(
            Assert.IsType<VideoTrack>(ProjectSerializer.Deserialize(json).Timeline.Tracks[0]).Clips);
        Assert.Equal(ClipConformMode.Fit, restored.ConformMode);
    }

    [Fact]
    public void Fit_Project_Serializes_Byte_Identically_After_A_Fill_Round_Trip_Reset()
    {
        // Setting Fill and back to Fit must leave no wire-format residue.
        Project project = PortraitProjectWithClip(out Clip clip);
        string before = ProjectSerializer.Serialize(project);
        clip.ConformMode = ClipConformMode.Fill;
        clip.ConformMode = ClipConformMode.Fit;
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }
}
