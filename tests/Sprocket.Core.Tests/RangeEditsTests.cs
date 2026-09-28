using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Sequence in/out marks and the range edits over them: <see cref="SetSequenceMarksCommand"/> apply/revert,
/// <see cref="RangeEdits.Lift"/> (gap left) and <see cref="RangeEdits.Extract"/> (ripple closed) across straddling,
/// contained, and downstream clips, linked tails, transitions, and the Mark Clip / Mark Selection spans.
/// </summary>
public class RangeEditsTests
{
    private static Timecode S(double seconds) => Timecode.FromSeconds(seconds);

    private static Clip ClipAt(double start, double length) =>
        new(MediaRefId.New(), Timecode.Zero, S(length), S(start));

    // One video track with three butted 4s clips: A [0,4), B [4,8), C [8,12).
    private static (Sequence Seq, VideoTrack Track, Clip A, Clip B, Clip C) ThreeClips()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var track = new VideoTrack();
        Clip a = ClipAt(0, 4), b = ClipAt(4, 4), c = ClipAt(8, 4);
        track.Clips.AddRange([a, b, c]);
        timeline.Tracks.Add(track);
        return (new Sequence(SequenceId.New(), "Seq", timeline), track, a, b, c);
    }

    private static List<(Timecode Start, Timecode End)> Spans(Track track) =>
        track.Clips.OrderBy(c => c.TimelineStart).Select(c => (c.TimelineStart, c.TimelineEnd)).ToList();

    [Fact]
    public void SetSequenceMarks_Applies_And_Reverts_Both_Marks()
    {
        var (seq, _, _, _, _) = ThreeClips();
        seq.MarkIn = S(1);
        var history = new EditHistory();

        history.Execute(new SetSequenceMarksCommand(seq, S(2), S(5)));
        Assert.Equal(S(2), seq.MarkIn);
        Assert.Equal(S(5), seq.MarkOut);

        history.Undo();
        Assert.Equal(S(1), seq.MarkIn);
        Assert.Null(seq.MarkOut);
    }

    [Fact]
    public void SetSourceMarks_Applies_And_Reverts_Both_Marks_On_The_Media()
    {
        var media = new MediaRef(MediaRefId.New(), "/tmp/a.mp4", new ProbedMediaInfo(
            S(10), HasVideo: true, new Rational(30, 1), 1920, 1080, HasAudio: true, 48000, 2))
        { SourceMarkIn = S(1) };
        var history = new EditHistory();

        history.Execute(new SetSourceMarksCommand(media, S(2), S(5)));
        Assert.Equal(S(2), media.SourceMarkIn);
        Assert.Equal(S(5), media.SourceMarkOut);

        history.Undo();
        Assert.Equal(S(1), media.SourceMarkIn);
        Assert.Null(media.SourceMarkOut);
    }

    [Fact]
    public void Lift_Removes_The_Range_And_Leaves_A_Gap()
    {
        var (seq, track, _, _, c) = ThreeClips();
        seq.MarkIn = S(2);
        seq.MarkOut = S(6);

        IEditCommand cmd = RangeEdits.Lift(seq, S(2), S(6))!.Command;
        cmd.Apply();

        Assert.Equal([(S(0), S(2)), (S(6), S(8)), (S(8), S(12))], Spans(track));
        Assert.Equal(S(8), c.TimelineStart);  // downstream untouched
        Assert.Null(seq.MarkIn);               // marks cleared, as leading editors do
        Assert.Null(seq.MarkOut);
    }

    [Fact]
    public void Extract_Removes_The_Range_And_Ripples_Downstream()
    {
        var (seq, track, a, b, c) = ThreeClips();

        IEditCommand cmd = RangeEdits.Extract(seq, S(2), S(6))!.Command;
        cmd.Apply();

        Assert.Equal([(S(0), S(2)), (S(2), S(4)), (S(4), S(8))], Spans(track));
        Assert.Equal(S(4), c.TimelineStart);
        // The tail of B keeps playing from where the out mark cut it (source 2s of B's 4s).
        Clip tail = track.Clips.Single(x => x.TimelineStart == S(2));
        Assert.Equal(S(2), tail.SourceIn);

        cmd.Revert();
        Assert.Equal([(S(0), S(4)), (S(4), S(8)), (S(8), S(12))], Spans(track));
        Assert.Same(a, track.Clips[0]);
        Assert.Equal(S(4), a.SourceOut);
        Assert.Equal(S(4), b.SourceOut);
        Assert.Equal(S(8), c.TimelineStart);
    }

    [Fact]
    public void Extract_Of_A_Whole_Clip_Closes_Its_Gap()
    {
        var (seq, track, _, b, _) = ThreeClips();

        RangeEdits.Extract(seq, S(4), S(8))!.Command.Apply();

        Assert.DoesNotContain(b, track.Clips);
        Assert.Equal([(S(0), S(4)), (S(4), S(8))], Spans(track));
    }

    [Fact]
    public void Range_Inside_One_Clip_Carves_Out_The_Middle()
    {
        var (seq, track, a, _, _) = ThreeClips();

        RangeEdits.Lift(seq, S(1), S(3))!.Command.Apply();

        Assert.Equal([(S(0), S(1)), (S(3), S(4)), (S(4), S(8)), (S(8), S(12))], Spans(track));
        Assert.Equal(S(1), a.SourceOut);
    }

    [Fact]
    public void Extract_Keeps_Linked_Tails_Linked_To_Each_Other_Only()
    {
        var (seq, video, _, _, _) = ThreeClips();
        var audio = new AudioTrack();
        seq.Timeline.Tracks.Add(audio);
        Guid group = Guid.NewGuid();
        Clip v = video.Clips[1], au = ClipAt(4, 4);
        v.LinkGroupId = group;
        au.LinkGroupId = group;
        audio.Clips.Add(au);

        RangeEdits.Extract(seq, S(3), S(6))!.Command.Apply();

        Clip vTail = video.Clips.Single(x => x.TimelineStart == S(3) && x.SourceIn == S(2));
        Clip aTail = audio.Clips.Single();
        Assert.Equal(S(3), aTail.TimelineStart);
        Assert.NotNull(vTail.LinkGroupId);
        Assert.Equal(vTail.LinkGroupId, aTail.LinkGroupId);
        Assert.NotEqual(group, vTail.LinkGroupId);
    }

    [Fact]
    public void Transitions_In_Range_Are_Removed_And_Downstream_Ones_Shift_On_Extract()
    {
        var (seq, track, _, _, _) = ThreeClips();
        var inside = new Transition(TransitionTypeIds.CrossDissolve, S(4), S(1));
        var after = new Transition(TransitionTypeIds.CrossDissolve, S(8), S(1));
        track.Transitions.AddRange([inside, after]);

        IEditCommand cmd = RangeEdits.Extract(seq, S(3), S(5))!.Command;
        cmd.Apply();

        Assert.Equal([after], track.Transitions);
        Assert.Equal(S(6), after.CutPoint);

        cmd.Revert();
        Assert.Equal([inside, after], track.Transitions);
        Assert.Equal(S(8), after.CutPoint);
    }

    [Fact]
    public void Empty_Or_Vacant_Range_Is_A_No_Op()
    {
        var (seq, _, _, _, _) = ThreeClips();
        Assert.Null(RangeEdits.Lift(seq, S(5), S(5)));
        Assert.Null(RangeEdits.Lift(seq, S(20), S(30)));    // past the end: nothing to lift
        Assert.Null(RangeEdits.Extract(seq, S(20), S(30))); // …and nothing downstream to ripple
    }

    [Fact]
    public void ClipSpanAt_Prefers_The_Topmost_Video_Clip()
    {
        var (seq, _, _, _, _) = ThreeClips();
        var upper = new VideoTrack();
        upper.Clips.Add(ClipAt(5, 1));
        seq.Timeline.Tracks.Add(upper);

        Assert.Equal((S(5), S(6)), RangeEdits.ClipSpanAt(seq.Timeline, S(5.5)));
        Assert.Equal((S(4), S(8)), RangeEdits.ClipSpanAt(seq.Timeline, S(7)));
        Assert.Null(RangeEdits.ClipSpanAt(seq.Timeline, S(20)));
    }

    // A second video track holding one 12s clip [0,12) above the three butted clips.
    private static VideoTrack AddUpperTrack(Sequence seq)
    {
        var upper = new VideoTrack();
        upper.Clips.Add(ClipAt(0, 12));
        seq.Timeline.Tracks.Add(upper);
        return upper;
    }

    [Fact]
    public void Lift_Leaves_Untargeted_Tracks_Alone()
    {
        var (seq, track, _, _, _) = ThreeClips();
        VideoTrack upper = AddUpperTrack(seq);
        upper.Targeted = false;

        RangeEdits.Lift(seq, S(2), S(6))!.Command.Apply();

        Assert.Equal([(S(0), S(2)), (S(6), S(8)), (S(8), S(12))], Spans(track));
        Assert.Equal([(S(0), S(12))], Spans(upper));
    }

    [Fact]
    public void Extract_Ripples_A_Sync_Locked_Untargeted_Track_Without_Carving_It()
    {
        var (seq, track, _, _, _) = ThreeClips();
        var upper = new VideoTrack { Targeted = false };
        Clip late = ClipAt(9, 2);
        upper.Clips.Add(late);
        seq.Timeline.Tracks.Add(upper);

        RangeEditResult result = RangeEdits.Extract(seq, S(2), S(6))!;
        result.Command.Apply();

        Assert.Equal(S(5), late.TimelineStart); // shifted left by the 4s range to stay in sync
        Assert.Equal(0, result.SyncBreaks);
        Assert.Equal([(S(0), S(2)), (S(2), S(4)), (S(4), S(8))], Spans(track));
    }

    [Fact]
    public void Extract_Reports_A_Sync_Break_When_A_Sync_Locked_Track_Has_Material_In_Range()
    {
        var (seq, _, _, _, _) = ThreeClips();
        VideoTrack upper = AddUpperTrack(seq);
        upper.Targeted = false;

        RangeEditResult result = RangeEdits.Extract(seq, S(2), S(6))!;
        result.Command.Apply();

        Assert.Equal(1, result.SyncBreaks);
        Assert.Equal([(S(0), S(12))], Spans(upper)); // no gap to close, so it keeps its place
    }

    [Fact]
    public void Extract_Does_Not_Shift_A_Track_With_Sync_Lock_Off()
    {
        var (seq, _, _, _, _) = ThreeClips();
        var upper = new VideoTrack { Targeted = false, SyncLocked = false };
        Clip late = ClipAt(9, 2);
        upper.Clips.Add(late);
        seq.Timeline.Tracks.Add(upper);

        RangeEditResult result = RangeEdits.Extract(seq, S(2), S(6))!;
        result.Command.Apply();

        Assert.Equal(S(9), late.TimelineStart);
        Assert.Equal(0, result.SyncBreaks);
    }

    [Fact]
    public void Locked_Tracks_Are_Never_Touched_And_Are_Reported()
    {
        var (seq, track, _, _, _) = ThreeClips();
        VideoTrack upper = AddUpperTrack(seq);
        upper.Locked = true; // still targeted + sync-locked, but the lock wins

        RangeEditResult result = RangeEdits.Extract(seq, S(2), S(6))!;
        result.Command.Apply();

        Assert.Equal([(S(0), S(12))], Spans(upper));
        Assert.Equal(1, result.LockedTracksInRange);
        Assert.Equal(0, result.SyncBreaks);
        Assert.Equal([(S(0), S(2)), (S(2), S(4)), (S(4), S(8))], Spans(track));
    }

    [Fact]
    public void HasEditableTarget_Needs_A_Targeted_Unlocked_Track()
    {
        var (seq, track, _, _, _) = ThreeClips();
        Assert.True(RangeEdits.HasEditableTarget(seq.Timeline));
        track.Locked = true;
        Assert.False(RangeEdits.HasEditableTarget(seq.Timeline));
        track.Locked = false;
        track.Targeted = false;
        Assert.False(RangeEdits.HasEditableTarget(seq.Timeline));
    }

    [Fact]
    public void ClipSpanAt_Only_Reads_Targeted_Tracks()
    {
        var (seq, track, _, _, _) = ThreeClips();
        var upper = new VideoTrack { Targeted = false };
        upper.Clips.Add(ClipAt(5, 1));
        seq.Timeline.Tracks.Add(upper);

        Assert.Equal((S(4), S(8)), RangeEdits.ClipSpanAt(seq.Timeline, S(5.5)));
        track.Targeted = false;
        Assert.Null(RangeEdits.ClipSpanAt(seq.Timeline, S(5.5)));
    }

    [Fact]
    public void ClipsLinkedTo_Skips_Locked_Tracks_Unless_Asked()
    {
        var (seq, video, _, b, _) = ThreeClips();
        var audio = new AudioTrack { Locked = true };
        seq.Timeline.Tracks.Add(audio);
        Guid group = Guid.NewGuid();
        Clip au = ClipAt(4, 4);
        b.LinkGroupId = group;
        au.LinkGroupId = group;
        audio.Clips.Add(au);

        Assert.Empty(seq.Timeline.ClipsLinkedTo(b));
        (Track linkedTrack, Clip linkedClip) = Assert.Single(seq.Timeline.ClipsLinkedTo(b, includeLocked: true));
        Assert.Same(audio, linkedTrack);
        Assert.Same(au, linkedClip);
        Assert.Same(video, seq.Timeline.TrackOf(b));
    }

    [Fact]
    public void SelectionSpan_Covers_Every_Selected_Clip()
    {
        var (_, _, a, _, c) = ThreeClips();
        Assert.Equal((S(0), S(12)), RangeEdits.SelectionSpan([c, a]));
        Assert.Null(RangeEdits.SelectionSpan([]));
    }
}
