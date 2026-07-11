using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The clip Enable toggle (PLAN.md step 53): a disabled clip renders nothing and contributes no audio while
/// keeping its place — the render graph skips it (including a transition side, which falls back like a missing
/// clip, §15), <see cref="Clip.CloneContentForSpan"/> copies the flag so blade/duplicate/paste preserve it, and
/// the toggle rides the generic <see cref="SetPropertyCommand{T}"/> so it undoes/redoes like every model edit.
/// </summary>
public class ClipEnableTests
{
    private static Project ProjectWithVideoClip(out VideoTrack track, out Clip clip)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        track = new VideoTrack();
        clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void Enabled_Defaults_True()
    {
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero);
        Assert.True(clip.Enabled);
    }

    [Fact]
    public void CloneContentForSpan_Copies_The_Enabled_Flag()
    {
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.Zero)
        {
            Enabled = false,
        };
        Clip copy = clip.CloneContentForSpan(clip.SourceIn, clip.SourceOut, Timecode.FromSeconds(5));
        Assert.False(copy.Enabled);

        clip.Enabled = true;
        Assert.True(clip.CloneContentForSpan(clip.SourceIn, clip.SourceOut, Timecode.Zero).Enabled);
    }

    [Fact]
    public void PlanVideoFrame_Yields_No_Layer_For_A_Disabled_Clip()
    {
        Project project = ProjectWithVideoClip(out _, out Clip clip);
        Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1)).Layers);

        clip.Enabled = false;
        Assert.Empty(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1)).Layers);
    }

    [Fact]
    public void A_Transition_With_A_Disabled_Side_Falls_Back_Like_A_Missing_Clip()
    {
        // Two adjacent clips with a centred cross-dissolve on the cut; disabling the incoming side makes the
        // transition invalid (a side resolves to nothing, §15), so the track renders its single active clip.
        Project project = ProjectWithVideoClip(out VideoTrack track, out Clip a);
        var mediaB = MediaRefId.New();
        var b = new Clip(mediaB, Timecode.Zero, Timecode.FromSeconds(4), Timecode.FromSeconds(4)) { Enabled = false };
        track.Clips.Add(b);
        track.Transitions.Add(new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2)));

        // 3.5s: inside the [3,5) window, before the cut — the active clip is A, rendered plainly.
        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(3.5)).Layers);
        Assert.Equal(LayerKind.Media, layer.Kind);
        Assert.Equal(a.MediaRefId, layer.MediaRefId);

        // 4.5s: past the cut, the active clip is the disabled B — nothing renders at all.
        Assert.Empty(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(4.5)).Layers);
    }

    [Fact]
    public void PlanAudioBuffer_Skips_A_Disabled_Clip()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var track = new AudioTrack();
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);

        Assert.Single(RenderGraph.PlanAudioBuffer(project, Timecode.FromSeconds(1), Timecode.FromSeconds(0.02)).Layers);

        clip.Enabled = false;
        Assert.Empty(RenderGraph.PlanAudioBuffer(project, Timecode.FromSeconds(1), Timecode.FromSeconds(0.02)).Layers);
    }

    [Fact]
    public void The_Toggle_Undoes_And_Redoes_Through_The_Command_Stack()
    {
        Project project = ProjectWithVideoClip(out _, out Clip clip);
        var history = new EditHistory();

        history.Execute(SetPropertyCommand<bool>.Create(
            "Disable clips", () => clip.Enabled, v => clip.Enabled = v, false));
        Assert.False(clip.Enabled);

        history.Undo();
        Assert.True(clip.Enabled);
        history.Redo();
        Assert.False(clip.Enabled);
    }
}
