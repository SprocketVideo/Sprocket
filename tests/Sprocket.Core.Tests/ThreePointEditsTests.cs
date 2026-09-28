using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Source patching (<see cref="Sequence.ResolvePatch"/>, <see cref="SetSourcePatchCommand"/>) and the three-point
/// <see cref="ThreePointEdits.Insert"/> / <see cref="ThreePointEdits.Overwrite"/> across patched, sync-locked, and
/// locked tracks: straddling splits, tail link groups, transitions, marker ripple, and exact undo.
/// </summary>
public class ThreePointEditsTests
{
    private static Timecode S(double seconds) => Timecode.FromSeconds(seconds);

    private static MediaRef Media(bool video = true, bool audio = true, double length = 10) =>
        new(MediaRefId.New(), "/tmp/src.mp4", new ProbedMediaInfo(
            S(length), HasVideo: video, new Rational(30, 1), 1920, 1080, HasAudio: audio, 48000, 2));

    // V1 and A1, each with linked butted 4s clips A [0,4), B [4,8); plus an empty V2 and A2.
    private static (Sequence Seq, VideoTrack V1, VideoTrack V2, AudioTrack A1, AudioTrack A2) Fixture()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var v1 = new VideoTrack { Name = "V1" };
        var v2 = new VideoTrack { Name = "V2" };
        var a1 = new AudioTrack { Name = "A1" };
        var a2 = new AudioTrack { Name = "A2" };
        foreach (double start in new[] { 0.0, 4.0 })
        {
            Guid group = Guid.NewGuid();
            var id = MediaRefId.New();
            v1.Clips.Add(new Clip(id, Timecode.Zero, S(4), S(start)) { LinkGroupId = group });
            a1.Clips.Add(new Clip(id, Timecode.Zero, S(4), S(start)) { LinkGroupId = group });
        }
        timeline.Tracks.AddRange([v1, v2, a1, a2]);
        return (new Sequence(SequenceId.New(), "Seq", timeline), v1, v2, a1, a2);
    }

    private static List<(Timecode Start, Timecode End)> Spans(Track track) =>
        track.Clips.OrderBy(c => c.TimelineStart).Select(c => (c.TimelineStart, c.TimelineEnd)).ToList();

    private static string Snapshot(Sequence seq) => string.Join("|", seq.Timeline.Tracks.Select(t =>
        string.Join(",", t.Clips.Select(c => $"{c.TimelineStart.Ticks}-{c.TimelineEnd.Ticks}-{c.SourceIn.Ticks}-{c.LinkGroupId}"))
        + ";" + string.Join(",", t.Transitions.Select(x => x.CutPoint.Ticks))))
        + "|m" + string.Join(",", seq.Timeline.Markers.Select(m => m.Time.Ticks)) + $"|{seq.MarkIn}-{seq.MarkOut}";

    private static ThreePointRange Range(double srcIn, double srcOut, double recordIn) =>
        new(S(srcIn), S(srcOut), S(recordIn), []);

    [Fact]
    public void Default_Patch_Is_Bottom_Video_And_First_Audio()
    {
        var (seq, v1, _, a1, _) = Fixture();

        Assert.Equal((v1, a1), seq.ResolvePatch());
    }

    [Fact]
    public void Patch_Command_Moves_And_Unpatches_And_Undoes()
    {
        var (seq, v1, v2, a1, _) = Fixture();
        var history = new EditHistory();

        history.Execute(new SetSourcePatchCommand(seq, new SourcePatch(Video: v2, AudioUnpatched: true)));
        Assert.Equal<(VideoTrack?, AudioTrack?)>((v2, null), seq.ResolvePatch());

        history.Undo();
        Assert.Null(seq.SourcePatch);
        Assert.Equal((v1, a1), seq.ResolvePatch());
    }

    [Fact]
    public void Removing_The_Patched_Track_Falls_Back_And_Undo_Restores_It()
    {
        var (seq, v1, v2, _, _) = Fixture();
        seq.SourcePatch = new SourcePatch(Video: v2);
        var history = new EditHistory();

        history.Execute(new RemoveTrackCommand(seq.Timeline, v2));
        Assert.Same(v1, seq.ResolvePatch().Video);

        history.Undo();
        Assert.Same(v2, seq.ResolvePatch().Video);
    }

    [Fact]
    public void Overwrite_Carves_Only_The_Patched_Tracks_And_Adds_Linked_Clips()
    {
        var (seq, v1, v2, a1, _) = Fixture();
        seq.SourcePatch = new SourcePatch(Video: v1, AudioUnpatched: true);
        seq.MarkIn = S(2);
        seq.MarkOut = S(5);
        MediaRef media = Media();

        ThreePointEditResult r = ThreePointEdits.Overwrite(seq, media, Range(1, 4, 2));
        r.Command!.Apply();

        Assert.Equal([(S(0), S(2)), (S(2), S(5)), (S(5), S(8))], Spans(v1));
        Assert.Equal([(S(0), S(4)), (S(4), S(8))], Spans(a1));   // un-patched audio untouched
        Assert.Empty(v2.Clips);
        Clip added = v1.Clips.Single(c => c.MediaRefId == media.Id);
        Assert.Equal((S(1), S(4)), (added.SourceIn, added.SourceOut));
        Assert.Null(added.LinkGroupId);                            // only one stream edited in
        Assert.Equal(S(5), r.RecordOut);
        Assert.Null(seq.MarkIn);                                   // marks cleared
    }

    [Fact]
    public void Overwrite_With_Both_Streams_Links_The_New_Clips()
    {
        var (seq, v1, _, a1, _) = Fixture();
        MediaRef media = Media();

        ThreePointEdits.Overwrite(seq, media, Range(0, 2, 1)).Command!.Apply();

        Clip v = v1.Clips.Single(c => c.MediaRefId == media.Id);
        Clip a = a1.Clips.Single(c => c.MediaRefId == media.Id);
        Assert.NotNull(v.LinkGroupId);
        Assert.Equal(v.LinkGroupId, a.LinkGroupId);
    }

    [Fact]
    public void A_Drop_Build_Keeps_The_Sequence_Marks_And_Can_Leave_The_Pair_Unlinked()
    {
        var (seq, v1, v2, a1, _) = Fixture();
        seq.MarkIn = S(1);
        seq.MarkOut = S(3);
        MediaRef media = Media();

        // A timeline drop (step 61 phase 4): explicit tracks, no patch, marks untouched, the Linked toggle off.
        ThreePointEditResult r = ThreePointEdits.Build(ThreePointEditKind.Overwrite, seq, media, Range(0, 2, 6),
            v2, a1, usePatch: false, linked: false, clearSequenceMarks: false);
        r.Command!.Apply();

        Assert.Equal((S(1), S(3)), (seq.MarkIn!.Value, seq.MarkOut!.Value));
        Assert.Equal([(S(6), S(8))], Spans(v2));
        Assert.Equal([(S(0), S(4)), (S(4), S(8))], Spans(v1));   // v1 isn't a destination: untouched
        Assert.Equal([(S(0), S(4)), (S(4), S(6)), (S(6), S(8))], Spans(a1)); // A1 carved and overwritten
        Clip audio = a1.Clips.Single(c => c.MediaRefId == media.Id);
        Assert.Null(audio.LinkGroupId);
        Assert.Null(v2.Clips.Single().LinkGroupId);
    }

    [Fact]
    public void Insert_Splits_Shifts_Patched_And_SyncLocked_Tracks()
    {
        var (seq, v1, v2, a1, a2) = Fixture();
        var other = new AudioTrack { Name = "A3", SyncLocked = false };
        other.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, S(4), S(6)));
        seq.Timeline.Tracks.Add(other);
        seq.SourcePatch = new SourcePatch(Video: v2, AudioUnpatched: true); // audio not edited in
        MediaRef media = Media();

        ThreePointEditResult r = ThreePointEdits.Insert(seq, media, Range(0, 3, 2));
        r.Command!.Apply();

        // V1 and A1 aren't destinations but are sync-locked: bladed at 2s and pushed right by 3s.
        Assert.Equal([(S(0), S(2)), (S(5), S(7)), (S(7), S(11))], Spans(v1));
        Assert.Equal([(S(0), S(2)), (S(5), S(7)), (S(7), S(11))], Spans(a1));
        Assert.Equal([(S(2), S(5))], Spans(v2));
        Assert.Equal([(S(6), S(10))], Spans(other));  // not sync-locked: stays put
        Assert.Empty(a2.Clips);

        // The right halves share a fresh link group, separate from the left halves'.
        Clip vRight = v1.Clips.Single(c => c.TimelineStart == S(5));
        Clip aRight = a1.Clips.Single(c => c.TimelineStart == S(5));
        Clip vLeft = v1.Clips.Single(c => c.TimelineStart == S(0));
        Assert.NotNull(vRight.LinkGroupId);
        Assert.Equal(vRight.LinkGroupId, aRight.LinkGroupId);
        Assert.NotEqual(vLeft.LinkGroupId, vRight.LinkGroupId);
    }

    [Fact]
    public void Insert_Ripples_Markers_And_Transitions()
    {
        var (seq, v1, _, _, _) = Fixture();
        seq.Timeline.Markers.Add(new Marker(S(1)));
        seq.Timeline.Markers.Add(new Marker(S(6)));
        var atCut = new Transition(TransitionTypeIds.CrossDissolve, S(4), S(1));
        v1.Transitions.Add(atCut);

        ThreePointEdits.Insert(seq, Media(), Range(0, 2, 6)).Command!.Apply();
        Assert.Equal([S(1), S(8)], seq.Timeline.Markers.Select(m => m.Time).ToList());
        Assert.Equal(S(4), atCut.CutPoint); // before the insert point: kept

        ThreePointEdits.Insert(seq, Media(), Range(0, 1, 4)).Command!.Apply();
        Assert.Empty(v1.Transitions);        // its cut was the insert point: removed
    }

    [Fact]
    public void Insert_Leaves_Locked_Tracks_And_Notes_The_Sync_Break()
    {
        var (seq, v1, _, a1, _) = Fixture();
        a1.Locked = true;
        seq.SourcePatch = new SourcePatch(AudioUnpatched: true);

        ThreePointEditResult r = ThreePointEdits.Insert(seq, Media(), Range(0, 2, 2));
        r.Command!.Apply();

        Assert.Equal([(S(0), S(4)), (S(4), S(8))], Spans(a1));
        Assert.Equal(S(10), v1.Clips.Max(c => c.TimelineEnd));
        Assert.Single(r.Notes);
    }

    [Fact]
    public void Locked_Or_Missing_Destination_Refuses_The_Edit()
    {
        var (seq, v1, _, _, _) = Fixture();
        v1.Locked = true;
        Assert.NotNull(ThreePointEdits.Overwrite(seq, Media(), Range(0, 2, 0)).Error);

        seq.SourcePatch = new SourcePatch(VideoUnpatched: true, AudioUnpatched: true);
        ThreePointEditResult none = ThreePointEdits.Insert(seq, Media(), Range(0, 2, 0));
        Assert.Null(none.Command);
        Assert.NotNull(none.Error);
    }

    [Fact]
    public void Audio_Only_Media_Needs_Only_An_Audio_Patch()
    {
        var (seq, v1, _, a1, _) = Fixture();
        seq.SourcePatch = new SourcePatch(VideoUnpatched: true);
        MediaRef media = Media(video: false);

        ThreePointEdits.Overwrite(seq, media, Range(0, 2, 8)).Command!.Apply();

        Assert.Contains(a1.Clips, c => c.MediaRefId == media.Id);
        Assert.DoesNotContain(v1.Clips, c => c.MediaRefId == media.Id);
    }

    [Theory]
    [InlineData(ThreePointEditKind.Insert)]
    [InlineData(ThreePointEditKind.Overwrite)]
    public void Undo_And_Redo_Restore_The_Model_Exactly(ThreePointEditKind kind)
    {
        var (seq, v1, _, _, _) = Fixture();
        seq.Timeline.Markers.Add(new Marker(S(5)));
        v1.Transitions.Add(new Transition(TransitionTypeIds.CrossDissolve, S(4), S(1)));
        seq.MarkIn = S(3);
        string before = Snapshot(seq);
        var history = new EditHistory();

        history.Execute(ThreePointEdits.Build(kind, seq, Media(), Range(1, 3, 3), null, null, usePatch: true).Command!);
        string after = Snapshot(seq);
        Assert.NotEqual(before, after);

        history.Undo();
        Assert.Equal(before, Snapshot(seq));
        history.Redo();
        Assert.Equal(after, Snapshot(seq));
    }
}
