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
    public void Lift_Removes_The_Range_And_Leaves_A_Gap()
    {
        var (seq, track, _, _, c) = ThreeClips();
        seq.MarkIn = S(2);
        seq.MarkOut = S(6);

        IEditCommand cmd = RangeEdits.Lift(seq, S(2), S(6))!;
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

        IEditCommand cmd = RangeEdits.Extract(seq, S(2), S(6))!;
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

        RangeEdits.Extract(seq, S(4), S(8))!.Apply();

        Assert.DoesNotContain(b, track.Clips);
        Assert.Equal([(S(0), S(4)), (S(4), S(8))], Spans(track));
    }

    [Fact]
    public void Range_Inside_One_Clip_Carves_Out_The_Middle()
    {
        var (seq, track, a, _, _) = ThreeClips();

        RangeEdits.Lift(seq, S(1), S(3))!.Apply();

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

        RangeEdits.Extract(seq, S(3), S(6))!.Apply();

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

        IEditCommand cmd = RangeEdits.Extract(seq, S(3), S(5))!;
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

    [Fact]
    public void SelectionSpan_Covers_Every_Selected_Clip()
    {
        var (_, _, a, _, c) = ThreeClips();
        Assert.Equal((S(0), S(12)), RangeEdits.SelectionSpan([c, a]));
        Assert.Null(RangeEdits.SelectionSpan([]));
    }
}
