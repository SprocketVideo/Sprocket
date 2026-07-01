using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>Transitions (PLAN.md step 25): the window/progress math, render-graph resolution of overlapping-clip
/// blends, and the undoable commands.</summary>
public class TransitionTests
{
    // Two adjacent 4-second clips meeting at a cut at t = 4s, each drawing from its own source from 0.
    private static Project TwoAdjacentClips(out VideoTrack track, out Clip a, out Clip b, out MediaRefId mediaA, out MediaRefId mediaB)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        mediaA = MediaRefId.New();
        mediaB = MediaRefId.New();
        track = new VideoTrack();
        a = new Clip(mediaA, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        b = new Clip(mediaB, Timecode.Zero, Timecode.FromSeconds(4), Timecode.FromSeconds(4));
        track.Clips.Add(a);
        track.Clips.Add(b);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    // ── Window / progress math ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Centered_Window_Straddles_The_Cut()
    {
        var t = new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2));
        Assert.Equal(Timecode.FromSeconds(3), t.Start);
        Assert.Equal(Timecode.FromSeconds(5), t.End);
        Assert.True(t.Contains(Timecode.FromSeconds(3)));
        Assert.True(t.Contains(Timecode.FromSeconds(4.999)));
        Assert.False(t.Contains(Timecode.FromSeconds(5)));        // end is exclusive
        Assert.False(t.Contains(Timecode.FromSeconds(2.999)));
    }

    [Fact]
    public void End_And_Start_Alignment_Sit_On_Their_Side_Of_The_Cut()
    {
        Timecode cut = Timecode.FromSeconds(4), dur = Timecode.FromSeconds(2);
        var endAt = new Transition(TransitionTypeIds.CrossDissolve, cut, dur, TransitionAlignment.EndAtCut);
        Assert.Equal(Timecode.FromSeconds(2), endAt.Start);
        Assert.Equal(cut, endAt.End);

        var startAt = new Transition(TransitionTypeIds.CrossDissolve, cut, dur, TransitionAlignment.StartAtCut);
        Assert.Equal(cut, startAt.Start);
        Assert.Equal(Timecode.FromSeconds(6), startAt.End);
    }

    [Fact]
    public void Progress_Ramps_Zero_To_One_Across_The_Window()
    {
        var t = new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2));
        Assert.Equal(0.0, t.ProgressAt(t.Start), 6);
        Assert.Equal(0.5, t.ProgressAt(Timecode.FromSeconds(4)), 6); // the cut is the midpoint when centred
        Assert.Equal(1.0, t.ProgressAt(t.End), 6);
        Assert.Equal(0.0, t.ProgressAt(Timecode.FromSeconds(1)), 6); // clamped before the window
        Assert.Equal(1.0, t.ProgressAt(Timecode.FromSeconds(9)), 6); // clamped after the window
    }

    [Fact]
    public void Constructor_Rejects_Non_Positive_Duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Transition(TransitionTypeIds.CrossDissolve, Timecode.Zero, Timecode.Zero));
    }

    // ── Render-graph resolution ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolves_A_Transition_Layer_Inside_The_Window_Blending_Both_Clips()
    {
        Project project = TwoAdjacentClips(out VideoTrack track, out _, out _, out MediaRefId mediaA, out MediaRefId mediaB);
        track.Transitions.Add(new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2)));

        // 4.5s: inside [3,5), past the cut. From = clip A sampled into its handles (source 4.5s, past its 4s out),
        // To = clip B at source 0.5s. Progress = (4.5-3)/2 = 0.75.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(4.5));
        VideoLayer layer = Assert.Single(plan.Layers);

        Assert.Equal(LayerKind.Transition, layer.Kind);
        ResolvedTransition tr = Assert.IsType<ResolvedTransition>(layer.Transition);
        Assert.Equal(TransitionTypeIds.CrossDissolve, tr.TransitionTypeId);
        Assert.Equal(0.75, tr.Progress, 6);
        Assert.Equal(mediaA, tr.From.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(4.5), tr.From.SourceTime); // handle frame, past A's trimmed out-point
        Assert.Equal(mediaB, tr.To.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(0.5), tr.To.SourceTime);
    }

    [Fact]
    public void Carries_Track_Opacity_And_Blend_On_The_Transition_Layer()
    {
        Project project = TwoAdjacentClips(out VideoTrack track, out _, out _, out _, out _);
        track.Opacity = 0.4;
        track.BlendMode = BlendMode.Screen;
        track.Transitions.Add(new Transition(TransitionTypeIds.Wipe, Timecode.FromSeconds(4), Timecode.FromSeconds(2)));

        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(4)).Layers);
        Assert.Equal(LayerKind.Transition, layer.Kind);
        Assert.Equal(0.4, layer.Opacity, 6);
        Assert.Equal(BlendMode.Screen, layer.BlendMode);
        // The two sides composite at unity / normal — the track's compositing is applied once, on the blend.
        Assert.Equal(1.0, layer.Transition!.From.Opacity, 6);
        Assert.Equal(BlendMode.Normal, layer.Transition.To.BlendMode);
    }

    [Fact]
    public void Outside_The_Window_Resolves_The_Single_Active_Clip()
    {
        Project project = TwoAdjacentClips(out VideoTrack track, out _, out _, out MediaRefId mediaA, out _);
        track.Transitions.Add(new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2)));

        // 1s: well before the [3,5) window → a plain media layer of clip A, not a transition.
        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1)).Layers);
        Assert.Equal(LayerKind.Media, layer.Kind);
        Assert.Equal(mediaA, layer.MediaRefId);
    }

    [Fact]
    public void A_Transition_With_No_Second_Clip_Falls_Back_To_Normal_Resolution()
    {
        // One clip only; a transition sitting at its tail has no incoming clip → not a real cut, render normally.
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var track = new VideoTrack();
        var media = MediaRefId.New();
        track.Clips.Add(new Clip(media, Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero));
        track.Transitions.Add(new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2)));
        project.Timeline.Tracks.Add(track);

        // 3.5s is inside the window but inside clip A with no clip after it: resolves as plain media, not a transition.
        VideoLayer layer = Assert.Single(RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(3.5)).Layers);
        Assert.Equal(LayerKind.Media, layer.Kind);
        Assert.Equal(media, layer.MediaRefId);
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddTransition_Applies_And_Reverts()
    {
        Project project = TwoAdjacentClips(out VideoTrack track, out _, out _, out _, out _);
        var transition = new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2));
        var history = new EditHistory();

        history.Execute(new AddTransitionCommand(track, transition));
        Assert.Contains(transition, track.Transitions);
        history.Undo();
        Assert.DoesNotContain(transition, track.Transitions);
        history.Redo();
        Assert.Contains(transition, track.Transitions);
    }

    [Fact]
    public void RemoveTransition_Restores_At_Original_Index()
    {
        Project project = TwoAdjacentClips(out VideoTrack track, out _, out _, out _, out _);
        var first = new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2));
        var second = new Transition(TransitionTypeIds.Wipe, Timecode.FromSeconds(8), Timecode.FromSeconds(1));
        track.Transitions.Add(first);
        track.Transitions.Add(second);
        var history = new EditHistory();

        history.Execute(new RemoveTransitionCommand(track, first));
        Assert.Equal(new[] { second }, track.Transitions);
        history.Undo();
        Assert.Equal(new[] { first, second }, track.Transitions); // index 0 restored
    }

    [Fact]
    public void SetTransitionWindow_Applies_Reverts_And_Coalesces()
    {
        var transition = new Transition(TransitionTypeIds.CrossDissolve, Timecode.FromSeconds(4), Timecode.FromSeconds(2));
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SetTransitionWindowCommand(transition, Timecode.FromSeconds(3), TransitionAlignment.EndAtCut));
            history.Execute(new SetTransitionWindowCommand(transition, Timecode.FromSeconds(1), TransitionAlignment.EndAtCut));
        }
        Assert.Equal(Timecode.FromSeconds(1), transition.Duration);
        Assert.Equal(TransitionAlignment.EndAtCut, transition.Alignment);

        history.Undo(); // one coalesced entry → back to the original 2s / centred
        Assert.Equal(Timecode.FromSeconds(2), transition.Duration);
        Assert.Equal(TransitionAlignment.CenterOnCut, transition.Alignment);
    }
}
