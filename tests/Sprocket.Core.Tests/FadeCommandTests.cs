using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Tests for the on-timeline fade editing primitives (PLAN.md step 39): <see cref="SetClipFadeCommand"/>
/// (author the fade envelope, creating the Fade effect on demand, one coalesced undo entry per drag) and the
/// keyframe rebasing on clip <em>moves</em> — a pure move shifts the clip's effect keyframes so a fade stays
/// aligned with the clip, while trims leave them anchored to the (unmoved) timeline edges.
/// </summary>
public class FadeCommandTests
{
    private static Clip MakeClip(double startSeconds = 2, double durSeconds = 5) =>
        new(MediaRefId.New(), Timecode.FromSeconds(0), Timecode.FromSeconds(durSeconds),
            Timecode.FromSeconds(startSeconds));

    private static AnimatableValue FadeInOut(Clip clip, double fadeSeconds = 1) =>
        AnimatableValue.Animated(
        [
            new Keyframe(clip.TimelineStart, 0),
            new Keyframe(clip.TimelineStart + Timecode.FromSeconds(fadeSeconds), 1),
            new Keyframe(clip.TimelineEnd - Timecode.FromSeconds(fadeSeconds), 1),
            new Keyframe(clip.TimelineEnd, 0),
        ]);

    private static EffectInstance AddFade(Clip clip, AnimatableValue opacity)
    {
        var fade = new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity, opacity);
        clip.Effects.Add(fade);
        return fade;
    }

    private static AnimatableValue Opacity(Clip clip)
    {
        foreach (EffectInstance e in clip.Effects)
            if (e.EffectTypeId == EffectTypeIds.Fade)
                return e.Parameters[EffectParamNames.Opacity];
        Assert.Fail("No fade effect on the clip.");
        return null!;
    }

    // ── SetClipFadeCommand ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetFade_Creates_Effect_When_Missing_And_Undo_Removes_It()
    {
        Clip clip = MakeClip();
        var history = new EditHistory();
        AnimatableValue envelope = FadeInOut(clip);

        history.Execute(new SetClipFadeCommand(clip, envelope));

        EffectInstance fade = Assert.Single(clip.Effects);
        Assert.Equal(EffectTypeIds.Fade, fade.EffectTypeId);
        Assert.Same(envelope, fade.Parameters[EffectParamNames.Opacity]);

        history.Undo();
        Assert.Empty(clip.Effects);

        history.Redo();
        Assert.Single(clip.Effects);
        Assert.Same(envelope, Opacity(clip));
    }

    [Fact]
    public void SetFade_On_Existing_Effect_Undo_Restores_Old_Envelope()
    {
        Clip clip = MakeClip();
        AnimatableValue original = FadeInOut(clip);
        AddFade(clip, original);
        var history = new EditHistory();

        AnimatableValue changed = FadeInOut(clip, fadeSeconds: 2);
        history.Execute(new SetClipFadeCommand(clip, changed));
        Assert.Same(changed, Opacity(clip));

        history.Undo();
        Assert.Same(original, Opacity(clip));
        Assert.Single(clip.Effects); // the pre-existing effect is never removed
    }

    [Fact]
    public void SetFade_Drag_Coalesces_To_One_Entry_Including_Creation()
    {
        Clip clip = MakeClip();
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new SetClipFadeCommand(clip, FadeInOut(clip, 0.5)));
            history.Execute(new SetClipFadeCommand(clip, FadeInOut(clip, 1.0)));
            history.Execute(new SetClipFadeCommand(clip, FadeInOut(clip, 1.5)));
        }

        Assert.Single(history.UndoLabels);
        history.Undo(); // one undo removes the whole gesture, including the created effect
        Assert.Empty(clip.Effects);
    }

    // ── Keyframe rebasing on moves (the step-39 gap fix) ────────────────────────────────────────────

    [Fact]
    public void PureMove_Shifts_Fade_Keyframes_And_Undo_Restores_Them()
    {
        Clip clip = MakeClip(startSeconds: 2, durSeconds: 5);
        AddFade(clip, FadeInOut(clip));
        var history = new EditHistory();

        var newStart = Timecode.FromSeconds(10);
        history.Execute(new SetClipPlacementCommand(clip, clip.SourceIn, clip.SourceOut, newStart));

        Assert.Equal(newStart, clip.TimelineStart);
        Assert.Equal(newStart.Ticks, Opacity(clip).Keyframes[0].Time.Ticks);
        Assert.Equal(clip.TimelineEnd.Ticks, Opacity(clip).Keyframes[^1].Time.Ticks);
        Assert.Equal(0, Opacity(clip).Evaluate(clip.TimelineStart), 6);
        Assert.Equal(1, Opacity(clip).Evaluate(clip.TimelineStart + Timecode.FromSeconds(1)), 6);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(2), clip.TimelineStart);
        Assert.Equal(clip.TimelineStart.Ticks, Opacity(clip).Keyframes[0].Time.Ticks);
    }

    [Fact]
    public void Coalesced_Move_Drag_Keeps_Keyframes_Exact()
    {
        Clip clip = MakeClip(startSeconds: 2, durSeconds: 5);
        AddFade(clip, FadeInOut(clip));
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            foreach (double s in new[] { 3.0, 4.5, 7.25 })
                history.Execute(new SetClipPlacementCommand(
                    clip, clip.SourceIn, clip.SourceOut, Timecode.FromSeconds(s)));
        }

        Assert.Equal(clip.TimelineStart.Ticks, Opacity(clip).Keyframes[0].Time.Ticks);

        history.Undo(); // the merged entry restores start AND keyframes in one step
        Assert.Equal(Timecode.FromSeconds(2), clip.TimelineStart);
        Assert.Equal(clip.TimelineStart.Ticks, Opacity(clip).Keyframes[0].Time.Ticks);

        history.Redo();
        Assert.Equal(Timecode.FromSeconds(7.25), clip.TimelineStart);
        Assert.Equal(clip.TimelineStart.Ticks, Opacity(clip).Keyframes[0].Time.Ticks);
    }

    [Fact]
    public void Trim_Does_Not_Shift_Keyframes()
    {
        Clip clip = MakeClip(startSeconds: 2, durSeconds: 5);
        AddFade(clip, FadeInOut(clip));
        long firstKf = Opacity(clip).Keyframes[0].Time.Ticks;
        var history = new EditHistory();

        // A left-edge trim: source in and start change together — keyframes stay anchored.
        history.Execute(new SetClipPlacementCommand(
            clip, Timecode.FromSeconds(1), clip.SourceOut, Timecode.FromSeconds(3), "Trim clip"));

        Assert.Equal(firstKf, Opacity(clip).Keyframes[0].Time.Ticks);
    }

    [Fact]
    public void MoveToTrack_Shifts_Keyframes_And_Undo_Restores()
    {
        var from = new VideoTrack { Name = "V1" };
        var to = new VideoTrack { Name = "V2" };
        Clip clip = MakeClip(startSeconds: 2, durSeconds: 5);
        from.Clips.Add(clip);
        AddFade(clip, FadeInOut(clip));
        var history = new EditHistory();

        history.Execute(new MoveClipToTrackCommand(from, to, clip, Timecode.FromSeconds(8)));
        Assert.Equal(clip.TimelineStart.Ticks, Opacity(clip).Keyframes[0].Time.Ticks);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(2), clip.TimelineStart);
        Assert.Equal(clip.TimelineStart.Ticks, Opacity(clip).Keyframes[0].Time.Ticks);
    }

    [Fact]
    public void RippleTrim_Shifts_Downstream_Clip_Keyframes()
    {
        Clip trimmed = MakeClip(startSeconds: 0, durSeconds: 5);
        Clip downstream = MakeClip(startSeconds: 5, durSeconds: 5);
        AddFade(downstream, FadeInOut(downstream));
        var history = new EditHistory();

        // Pull the trimmed clip's out-point back 2 s; downstream shifts left 2 s and its fade follows.
        long shift = -Timecode.FromSeconds(2).Ticks;
        history.Execute(new RippleTrimCommand(
            trimmed, trimmed.SourceIn, Timecode.FromSeconds(3),
            [(downstream, downstream.TimelineStart)], shift));

        Assert.Equal(Timecode.FromSeconds(3), downstream.TimelineStart);
        Assert.Equal(downstream.TimelineStart.Ticks, Opacity(downstream).Keyframes[0].Time.Ticks);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(5), downstream.TimelineStart);
        Assert.Equal(downstream.TimelineStart.Ticks, Opacity(downstream).Keyframes[0].Time.Ticks);
    }

    [Fact]
    public void Slide_Shifts_Slid_Clip_Keyframes_But_Not_Trimmed_Neighbour()
    {
        Clip prev = MakeClip(startSeconds: 0, durSeconds: 2);
        Clip slid = MakeClip(startSeconds: 2, durSeconds: 3);
        Clip next = MakeClip(startSeconds: 5, durSeconds: 3);
        AddFade(slid, FadeInOut(slid));
        AddFade(next, FadeInOut(next));
        long nextFirstKf = Opacity(next).Keyframes[0].Time.Ticks;
        var history = new EditHistory();

        // Slide right 1 s: prev's out extends, next's in + start move (a trim), the slid clip purely moves.
        history.Execute(new SlideClipCommand(
            slid, Timecode.FromSeconds(3),
            prev, Timecode.FromSeconds(3),
            next, Timecode.FromSeconds(1), Timecode.FromSeconds(6)));

        Assert.Equal(slid.TimelineStart.Ticks, Opacity(slid).Keyframes[0].Time.Ticks);
        Assert.Equal(nextFirstKf, Opacity(next).Keyframes[0].Time.Ticks); // the neighbour was trimmed, not moved

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(2), slid.TimelineStart);
        Assert.Equal(slid.TimelineStart.Ticks, Opacity(slid).Keyframes[0].Time.Ticks);
    }

    [Fact]
    public void ShiftKeyframes_Leaves_Constants_Untouched_And_Shifts_Animated_InPlace()
    {
        var effect = new EffectInstance(EffectTypeIds.Brightness)
            .Set(EffectParamNames.Amount, 1.2)
            .Set("ramp", AnimatableValue.Animated([new Keyframe(Timecode.FromSeconds(1), 0.5)]));
        AnimatableValue constant = effect.Parameters[EffectParamNames.Amount];

        effect.ShiftKeyframes(Timecode.FromSeconds(2));

        Assert.Same(constant, effect.Parameters[EffectParamNames.Amount]);
        Assert.Equal(Timecode.FromSeconds(3).Ticks, effect.Parameters["ramp"].Keyframes[0].Time.Ticks);
    }
}
