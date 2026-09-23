using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// <see cref="RenderGraph.HasAudibleAudio"/> (export-speed phase 1): the export's "does this need audio work?" gate
/// must admit exactly what <see cref="RenderGraph.PlanAudioBuffer(Project, Sequence, Timecode, Timecode, AudioPlanScope?)"/>
/// admits — mute / solo / enabled, disabled clips, nested sequences, and the multicam active angle.
/// </summary>
public class HasAudibleAudioTests
{
    private static readonly Rational Fps30 = new(30, 1);

    private static ProbedMediaInfo Info(bool hasAudio) =>
        new(Timecode.FromSeconds(10), HasVideo: true, Fps30, 640, 480, HasAudio: hasAudio, hasAudio ? 48000 : 0, hasAudio ? 2 : 0);

    private static MediaRefId AddMedia(Project project, bool hasAudio)
    {
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, Path.Combine(Path.GetTempPath(), $"{id}.mp4"), Info(hasAudio)));
        return id;
    }

    private static Clip MediaClip(MediaRefId id) =>
        new(id, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero);

    /// <summary>A project whose active timeline has one audio track carrying one audible media clip.</summary>
    private static Project OneAudibleTrack(out AudioTrack track, out Clip clip)
    {
        var project = new Project(new Timeline(Fps30, new Resolution(640, 480), 48000));
        track = new AudioTrack { Name = "A1" };
        clip = MediaClip(AddMedia(project, hasAudio: true));
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void HasAudibleAudio_AudibleClip_ReturnsTrue()
    {
        Project project = OneAudibleTrack(out _, out _);
        Assert.True(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_MutedOnly_ReturnsFalse()
    {
        Project project = OneAudibleTrack(out AudioTrack track, out _);
        track.Muted = true;
        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_DisabledTrack_ReturnsFalse()
    {
        Project project = OneAudibleTrack(out AudioTrack track, out _);
        track.Enabled = false;
        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_SourceWithoutAudio_ReturnsFalse()
    {
        var project = new Project(new Timeline(Fps30, new Resolution(640, 480), 48000));
        var track = new AudioTrack();
        track.Clips.Add(MediaClip(AddMedia(project, hasAudio: false)));
        project.Timeline.Tracks.Add(track);
        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_EmptyAudibleTrack_ReturnsFalse()
    {
        var project = new Project(new Timeline(Fps30, new Resolution(640, 480), 48000));
        project.Timeline.Tracks.Add(new AudioTrack());
        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_SoloExcludesOtherwiseAudibleTrack()
    {
        // A2 carries the only audible media; soloing the silent A1 excludes A2 exactly as the planner does.
        var project = new Project(new Timeline(Fps30, new Resolution(640, 480), 48000));
        var a1 = new AudioTrack { Name = "A1", Solo = true };
        a1.Clips.Add(MediaClip(AddMedia(project, hasAudio: false)));
        var a2 = new AudioTrack { Name = "A2" };
        a2.Clips.Add(MediaClip(AddMedia(project, hasAudio: true)));
        project.Timeline.Tracks.Add(a1);
        project.Timeline.Tracks.Add(a2);

        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));

        a2.Solo = true;
        Assert.True(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_SoloOnDisabledTrack_DoesNotExcludeOthers()
    {
        // The planner's anySolo only counts enabled tracks — a soloed-but-disabled track doesn't silence the rest.
        Project project = OneAudibleTrack(out _, out _);
        var disabledSolo = new AudioTrack { Enabled = false, Solo = true };
        project.Timeline.Tracks.Add(disabledSolo);
        Assert.True(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_DisabledClip_ReturnsFalse()
    {
        Project project = OneAudibleTrack(out _, out Clip clip);
        clip.Enabled = false;
        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_NestedSequence_Recurses()
    {
        var project = new Project(new Timeline(Fps30, new Resolution(640, 480), 48000));
        var childTimeline = new Timeline(Fps30, new Resolution(640, 480), 48000);
        var childTrack = new AudioTrack { Name = "A1" };
        childTrack.Clips.Add(MediaClip(AddMedia(project, hasAudio: true)));
        childTimeline.Tracks.Add(childTrack);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);
        project.Sequences.Add(child);

        var parentTrack = new AudioTrack { Name = "A1" };
        parentTrack.Clips.Add(Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(10), Timecode.Zero));
        project.Timeline.Tracks.Add(parentTrack);

        Assert.True(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));

        // Muting inside the child silences the nest, as in the planner's sub-mix.
        childTrack.Muted = true;
        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }

    [Fact]
    public void HasAudibleAudio_NestedCycle_TerminatesFalse()
    {
        var project = new Project(new Timeline(Fps30, new Resolution(640, 480), 48000));
        var childTimeline = new Timeline(Fps30, new Resolution(640, 480), 48000);
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);
        project.Sequences.Add(child);
        // child nests itself — the planner's cycle guard contributes nothing.
        var selfTrack = new AudioTrack();
        selfTrack.Clips.Add(Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(10), Timecode.Zero));
        childTimeline.Tracks.Add(selfTrack);

        Assert.False(RenderGraph.HasAudibleAudio(project, child));
    }

    [Fact]
    public void HasAudibleAudio_Multicam_UsesActiveAngleAudio()
    {
        var project = new Project(new Timeline(Fps30, new Resolution(640, 480), 48000));
        MediaRefId silentCam = AddMedia(project, hasAudio: false);
        MediaRefId cam2Video = AddMedia(project, hasAudio: false);
        MediaRefId cam2Audio = AddMedia(project, hasAudio: true); // dual-system sound on angle 2
        var source = new MulticamSource(MulticamId.New(), "Multicam 1",
        [
            new MulticamAngle("Cam 1", silentCam),
            new MulticamAngle("Cam 2", cam2Video, Timecode.Zero, cam2Audio),
        ]);
        project.MulticamSources.Add(source);

        var track = new AudioTrack { Name = "A1" };
        Clip clip = Clip.CreateMulticamClip(source.Id, 0, Timecode.FromSeconds(10), Timecode.Zero);
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);

        Assert.False(RenderGraph.HasAudibleAudio(project, project.ActiveSequence)); // angle 0 has no audio

        clip.ActiveAngle = 1;
        Assert.True(RenderGraph.HasAudibleAudio(project, project.ActiveSequence));
    }
}
