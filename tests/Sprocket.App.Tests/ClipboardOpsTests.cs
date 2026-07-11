using System;
using System.Linq;
using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for <see cref="ClipboardOps"/> (PLAN.md step 16c): the cut/copy/paste clip clipboard and the
/// nudge clamp. The menu wiring + pointer interaction rest on these + manual verification (the App is a UI-bound
/// WinExe).
/// </summary>
public class ClipboardOpsTests
{
    private static Clip MakeClip(double startSeconds = 2, double durationSeconds = 3)
    {
        var clip = new Clip(
            MediaRefId.New(),
            Timecode.FromSeconds(1),
            Timecode.FromSeconds(1 + durationSeconds),
            Timecode.FromSeconds(startSeconds))
        {
            LinkGroupId = Guid.NewGuid(), // the original is linked; a copy must not be
        };
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.4));
        return clip;
    }

    [Fact]
    public void Copy_Clones_Effects_And_Clears_The_Link()
    {
        Clip original = MakeClip();

        Clip copy = ClipboardOps.Copy(original);

        Assert.Equal(original.MediaRefId, copy.MediaRefId);
        Assert.Equal(original.SourceIn, copy.SourceIn);
        Assert.Equal(original.SourceOut, copy.SourceOut);
        Assert.Null(copy.LinkGroupId);                       // independent copy
        Assert.Single(copy.Effects);
        Assert.NotSame(original.Effects[0], copy.Effects[0]); // effect instance cloned, not shared
        Assert.Equal(original.Effects[0].EffectTypeId, copy.Effects[0].EffectTypeId);
    }

    [Fact]
    public void Copy_Is_Insulated_From_Later_Edits_To_The_Original()
    {
        Clip original = MakeClip();
        Clip copy = ClipboardOps.Copy(original);

        original.Effects.Add(new EffectInstance(EffectTypeIds.Fade)); // mutate after copying

        Assert.Single(copy.Effects); // the clipboard snapshot is unaffected
    }

    [Fact]
    public void Paste_Places_At_The_Given_Time_And_Clones_Effects()
    {
        Clip snapshot = ClipboardOps.Copy(MakeClip());
        Timecode at = Timecode.FromSeconds(7);

        Clip pasted = ClipboardOps.Paste(snapshot, at);

        Assert.Equal(at, pasted.TimelineStart);
        Assert.Equal(snapshot.Duration, pasted.Duration); // span preserved
        Assert.Null(pasted.LinkGroupId);
        Assert.NotSame(snapshot.Effects[0], pasted.Effects[0]);
    }

    [Fact]
    public void Copy_And_Paste_Preserve_Content_Nature_Including_A_Frame_Hold()
    {
        // Copy/Paste use CloneContentForSpan, so speed and a frame hold (PLAN.md step 43) survive the clipboard.
        Clip original = MakeClip();
        original.SpeedRatio = new Rational(2, 1);
        original.HoldDuration = Timecode.FromSeconds(5);
        original.HoldFrameAt = Timecode.FromSeconds(1.5);

        Clip pasted = ClipboardOps.Paste(ClipboardOps.Copy(original), Timecode.FromSeconds(9));

        Assert.Equal(new Rational(2, 1), pasted.SpeedRatio);
        Assert.True(pasted.IsHeld);
        Assert.Equal(Timecode.FromSeconds(1.5), pasted.HoldFrameAt!.Value);
        Assert.Equal(Timecode.FromSeconds(5), pasted.Duration);
    }

    [Fact]
    public void Paste_Clamps_A_Negative_Time_To_The_Origin()
    {
        Clip snapshot = ClipboardOps.Copy(MakeClip());
        Clip pasted = ClipboardOps.Paste(snapshot, new Timecode(-500));
        Assert.Equal(0, pasted.TimelineStart.Ticks);
    }

    [Fact]
    public void Repeated_Pastes_Are_Independent()
    {
        Clip snapshot = ClipboardOps.Copy(MakeClip());
        Clip a = ClipboardOps.Paste(snapshot, Timecode.FromSeconds(1));
        Clip b = ClipboardOps.Paste(snapshot, Timecode.FromSeconds(5));
        Assert.NotSame(a.Effects[0], b.Effects[0]);
    }

    [Fact]
    public void Paste_Shifts_Keyframed_Effects_So_A_Copied_Fade_Tracks_The_New_Position()
    {
        // Repro of the "Alt-drag-copied clip plays black" bug: the default clip carries a fade in/out keyframed
        // over its timeline span. Effect keyframe times are absolute timeline time, so a naive clone would leave
        // the copy's fade anchored to the ORIGINAL clip's span — placed past it, the copy sits beyond the
        // fade-out's final keyframe (opacity 0) and renders fully transparent (black). Paste must shift the
        // keyframes by the placement delta so the copy fades like the original at its new position.
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity,
            AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 0.0, Interpolation.Linear),               // fade in start
                new Keyframe(Timecode.FromSeconds(1), 1.0, Interpolation.Linear),     // fully in
                new Keyframe(Timecode.FromSeconds(3), 1.0, Interpolation.Linear),     // hold until
                new Keyframe(Timecode.FromSeconds(4), 0.0, Interpolation.Linear),     // fade out end
            ])));

        Clip pasted = ClipboardOps.Paste(ClipboardOps.Copy(clip), Timecode.FromSeconds(10));

        AnimatableValue opacity = pasted.Effects.Single().Parameters[EffectParamNames.Opacity];
        // Middle of the copy (timeline 12s ⇒ between the shifted "fully in" keyframes) is fully opaque, not black.
        Assert.Equal(1.0, opacity.Evaluate(Timecode.FromSeconds(12)));
        // The copy's fade in/out land at its own edges: ~0 at its start (10s) and end (14s).
        Assert.Equal(0.0, opacity.Evaluate(Timecode.FromSeconds(10)));
        Assert.Equal(0.0, opacity.Evaluate(Timecode.FromSeconds(14)));
    }

    [Theory]
    [InlineData(1000, 5000, 1000)]   // right nudge: unaffected
    [InlineData(-1000, 5000, -1000)] // left nudge with headroom: unaffected
    [InlineData(-5000, 3000, -3000)] // left nudge beyond the group origin: clamped to -minStart
    [InlineData(-1000, 0, 0)]        // group already at t=0: a left nudge is squashed to zero
    public void ClampGroupNudge_Keeps_The_Group_Off_The_Negative_Axis(long delta, long groupMin, long expected)
    {
        Assert.Equal(expected, ClipboardOps.ClampGroupNudge(delta, groupMin));
    }

    // ── Multi-clip paste (PLAN.md step 54) ──────────────────────────────────────────────────────────

    [Fact]
    public void PasteAll_Anchors_The_Earliest_Snapshot_And_Keeps_Relative_Offsets()
    {
        var video = new Sprocket.Core.Model.VideoTrack();
        var audio = new Sprocket.Core.Model.AudioTrack();
        var history = new Sprocket.Core.Commands.EditHistory();
        Clip a = MakeClip(startSeconds: 2); // the earliest — lands at the playhead
        Clip b = MakeClip(startSeconds: 5); // +3 s offset preserved

        ClipboardOps.PasteResult? result = ClipboardOps.PasteAll(
            [(ClipboardOps.Copy(a), true), (ClipboardOps.Copy(b), false)],
            Timecode.FromSeconds(10), video, audio);

        Assert.NotNull(result);
        history.Execute(result.Value.Command);
        Assert.Single(video.Clips);
        Assert.Single(audio.Clips); // each snapshot lands on the track of its kind
        Assert.Equal(Timecode.FromSeconds(10), video.Clips[0].TimelineStart);
        Assert.Equal(Timecode.FromSeconds(13), audio.Clips[0].TimelineStart);
        Assert.Same(result.Value.Primary, video.Clips[0]); // the first snapshot's copy anchors the selection

        history.Undo(); // one entry removes the whole paste
        Assert.Empty(video.Clips);
        Assert.Empty(audio.Clips);
    }

    [Fact]
    public void PasteAll_Skips_Snapshots_Without_A_Target_Track_Kind()
    {
        var video = new Sprocket.Core.Model.VideoTrack();
        var history = new Sprocket.Core.Commands.EditHistory();

        // No audio track: the audio snapshot is skipped, the video one still pastes.
        ClipboardOps.PasteResult? result = ClipboardOps.PasteAll(
            [(ClipboardOps.Copy(MakeClip(2)), false), (ClipboardOps.Copy(MakeClip(5)), true)],
            Timecode.FromSeconds(10), video, audioTarget: null);
        Assert.NotNull(result);
        history.Execute(result.Value.Command);
        Assert.Single(video.Clips);
        // The skipped snapshot doesn't anchor the offsets — the placeable one lands at the playhead.
        Assert.Equal(Timecode.FromSeconds(10), video.Clips[0].TimelineStart);

        // Nothing placeable at all → null.
        Assert.Null(ClipboardOps.PasteAll(
            [(ClipboardOps.Copy(MakeClip(2)), false)], Timecode.FromSeconds(10), video, audioTarget: null));
    }
}
