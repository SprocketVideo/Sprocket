using System;
using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for <see cref="ClipEdits"/> (PLAN.md step 53): the split / duplicate / enable-toggle command
/// builders behind Split at Playhead (Ctrl+K), Duplicate, and the Enable toggle (Shift+E). The context-menu and
/// pointer wiring in <see cref="MainWindow"/>/<c>TimelineControl</c> rest on these + manual verification (the
/// App is a UI-bound WinExe), the <see cref="ClipboardOpsTests"/> idiom.
/// </summary>
public class ClipEditsTests
{
    /// <summary>A timeline with a linked A/V pair: a 4 s video clip and its 4 s audio companion, both at 2 s.</summary>
    private static Timeline LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out Clip a)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var group = Guid.NewGuid();
        video = new VideoTrack();
        audio = new AudioTrack();
        v = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.FromSeconds(2)) { LinkGroupId = group };
        a = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.FromSeconds(2)) { LinkGroupId = group };
        video.Clips.Add(v);
        audio.Clips.Add(a);
        timeline.Tracks.Add(video);
        timeline.Tracks.Add(audio);
        return timeline;
    }

    // ── Split ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_Cuts_At_The_Given_Time_And_Selects_The_Right_Half()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out _, out Clip v, out _);
        var history = new EditHistory();

        ClipEdits.SplitResult? split = ClipEdits.Split(timeline, video, v, Timecode.FromSeconds(3), linked: false);
        Assert.NotNull(split);
        history.Execute(split.Value.Command);

        Assert.Equal(2, video.Clips.Count);
        Assert.Equal(Timecode.FromSeconds(3), v.TimelineEnd);
        Assert.Same(video.Clips[1], split.Value.Right);
        Assert.Equal(Timecode.FromSeconds(3), split.Value.Right.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(6), split.Value.Right.TimelineEnd);
    }

    [Fact]
    public void Split_NoOps_On_An_Edge_Or_Outside_The_Clip()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out _, out Clip v, out _);

        Assert.Null(ClipEdits.Split(timeline, video, v, v.TimelineStart, linked: true));      // on the left edge
        Assert.Null(ClipEdits.Split(timeline, video, v, v.TimelineEnd, linked: true));        // on the right edge
        Assert.Null(ClipEdits.Split(timeline, video, v, Timecode.FromSeconds(1), linked: true));  // before
        Assert.Null(ClipEdits.Split(timeline, video, v, Timecode.FromSeconds(10), linked: true)); // after
        Assert.Single(video.Clips); // nothing changed
    }

    [Fact]
    public void Split_With_Linked_Cuts_Companions_Into_A_Fresh_Right_Hand_Group_As_One_Undo_Entry()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out Clip a);
        Guid originalGroup = v.LinkGroupId!.Value;
        var history = new EditHistory();

        ClipEdits.SplitResult? split = ClipEdits.Split(timeline, video, v, Timecode.FromSeconds(3), linked: true);
        history.Execute(split!.Value.Command);

        Assert.Equal(2, video.Clips.Count);
        Assert.Equal(2, audio.Clips.Count);
        // The left halves keep the original group; the right halves share a fresh one.
        Assert.Equal(originalGroup, v.LinkGroupId);
        Assert.Equal(originalGroup, a.LinkGroupId);
        Guid? rightGroup = video.Clips[1].LinkGroupId;
        Assert.NotNull(rightGroup);
        Assert.NotEqual(originalGroup, rightGroup);
        Assert.Equal(rightGroup, audio.Clips[1].LinkGroupId);

        history.Undo(); // one entry restores both tracks
        Assert.Single(video.Clips);
        Assert.Single(audio.Clips);
        Assert.Equal(Timecode.FromSeconds(6), v.TimelineEnd);
    }

    // ── Duplicate ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_Places_The_Copy_At_The_Original_TimelineEnd_With_Effects_And_Markers()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out _, out Clip v, out _);
        v.LinkGroupId = null; // a lone clip
        v.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.3));
        v.Markers.Add(new Marker(Timecode.FromSeconds(1), "beat"));
        var history = new EditHistory();

        (IEditCommand command, Clip copy) = ClipEdits.Duplicate(timeline, video, v, linked: true);
        history.Execute(command);

        Assert.Equal(2, video.Clips.Count);
        Assert.Equal(v.TimelineEnd, copy.TimelineStart); // butted after the original
        Assert.Equal(v.Duration, copy.Duration);
        Assert.Null(copy.LinkGroupId);
        Assert.Single(copy.Effects);
        Assert.NotSame(v.Effects[0], copy.Effects[0]); // cloned, not shared
        Assert.Single(copy.Markers);

        history.Undo();
        Assert.Single(video.Clips);
    }

    [Fact]
    public void Duplicate_With_Linked_Copies_The_Pair_Under_A_Fresh_Group_As_One_Undo_Entry()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out Clip a);
        Guid originalGroup = v.LinkGroupId!.Value;
        var history = new EditHistory();

        (IEditCommand command, Clip copy) = ClipEdits.Duplicate(timeline, video, v, linked: true);
        history.Execute(command);

        Assert.Equal(2, video.Clips.Count);
        Assert.Equal(2, audio.Clips.Count);
        Assert.Equal(v.TimelineEnd, copy.TimelineStart);
        Assert.Equal(a.TimelineEnd, audio.Clips[1].TimelineStart); // relative offsets preserved
        Guid? newGroup = copy.LinkGroupId;
        Assert.NotNull(newGroup);
        Assert.NotEqual(originalGroup, newGroup);
        Assert.Equal(newGroup, audio.Clips[1].LinkGroupId);
        Assert.Equal(originalGroup, v.LinkGroupId); // the originals keep theirs

        history.Undo(); // one entry removes both copies
        Assert.Single(video.Clips);
        Assert.Single(audio.Clips);
    }

    [Fact]
    public void Duplicate_Without_Linked_Copies_Only_The_Addressed_Clip()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out _);
        var history = new EditHistory();

        (IEditCommand command, Clip copy) = ClipEdits.Duplicate(timeline, video, v, linked: false);
        history.Execute(command);

        Assert.Equal(2, video.Clips.Count);
        Assert.Single(audio.Clips);
        Assert.Null(copy.LinkGroupId); // a lone copy is independent
    }

    // ── Enable toggle ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToggleEnabled_Flips_Linked_Pairs_Together_As_One_Undo_Entry()
    {
        Timeline timeline = LinkedPair(out _, out _, out Clip v, out Clip a);
        var history = new EditHistory();

        history.Execute(ClipEdits.ToggleEnabled(timeline, v, linked: true));
        Assert.False(v.Enabled);
        Assert.False(a.Enabled);

        history.Undo(); // one entry restores both
        Assert.True(v.Enabled);
        Assert.True(a.Enabled);

        history.Redo();
        history.Execute(ClipEdits.ToggleEnabled(timeline, v, linked: true));
        Assert.True(v.Enabled);
        Assert.True(a.Enabled);
    }

    [Fact]
    public void ToggleEnabled_Sets_Companions_To_The_Same_State_Rather_Than_Flipping_Each()
    {
        // A mixed group (video disabled, audio still enabled) converges on the primary's new state.
        Timeline timeline = LinkedPair(out _, out _, out Clip v, out Clip a);
        v.Enabled = false;
        var history = new EditHistory();

        history.Execute(ClipEdits.ToggleEnabled(timeline, v, linked: true));
        Assert.True(v.Enabled);
        Assert.True(a.Enabled); // set to the target state, not flipped to false

        history.Undo();
        Assert.False(v.Enabled);
        Assert.True(a.Enabled); // undo restores each member's prior state
    }

    [Fact]
    public void ToggleEnabled_Without_Linked_Touches_Only_The_Addressed_Clip()
    {
        Timeline timeline = LinkedPair(out _, out _, out Clip v, out Clip a);
        var history = new EditHistory();

        history.Execute(ClipEdits.ToggleEnabled(timeline, v, linked: false));
        Assert.False(v.Enabled);
        Assert.True(a.Enabled);
    }

    // ── Batch operations over the multi-selection (PLAN.md step 54) ─────────────────────────────────

    private static Clip AddClip(Track track, double startSeconds, double durationSeconds = 2)
    {
        var clip = new Clip(
            MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(durationSeconds),
            Timecode.FromSeconds(startSeconds));
        track.Clips.Add(clip);
        return clip;
    }

    [Fact]
    public void ExpandWithLinked_Dedupes_A_Wholly_Selected_Linked_Pair()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out Clip a);

        var members = ClipEdits.ExpandWithLinked(timeline, [v, a], linked: true);

        Assert.Equal(2, members.Count); // v + a once each, not four entries
        Assert.Contains(members, m => ReferenceEquals(m.Clip, v) && ReferenceEquals(m.Track, video));
        Assert.Contains(members, m => ReferenceEquals(m.Clip, a) && ReferenceEquals(m.Track, audio));

        Assert.Single(ClipEdits.ExpandWithLinked(timeline, [v], linked: false)); // no expansion when Linked is off
    }

    [Fact]
    public void DeleteAll_Removes_The_Selection_Plus_Companions_As_One_Undo_Entry()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out _);
        Clip lone = AddClip(video, 8);
        var history = new EditHistory();

        history.Execute(ClipEdits.DeleteAll(timeline, [v, lone], linked: true)!);
        Assert.Empty(video.Clips);
        Assert.Empty(audio.Clips); // v's linked companion went too

        history.Undo(); // one entry restores everything
        Assert.Equal(2, video.Clips.Count);
        Assert.Single(audio.Clips);
    }

    [Fact]
    public void RippleDeleteAll_Closes_Both_Gaps_For_A_Downstream_Survivor()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var video = new VideoTrack();
        timeline.Tracks.Add(video);
        Clip first = AddClip(video, 0, 2);
        Clip second = AddClip(video, 3, 2);
        Clip survivor = AddClip(video, 6, 2);
        var history = new EditHistory();

        history.Execute(ClipEdits.RippleDeleteAll(timeline, [first, second], linked: true)!);

        Assert.Single(video.Clips);
        // The survivor shifts by the COMBINED removed duration (2 s + 2 s), not just the nearer gap's.
        Assert.Equal(Timecode.FromSeconds(2), survivor.TimelineStart);

        history.Undo(); // one entry restores removals and placement
        Assert.Equal(3, video.Clips.Count);
        Assert.Equal(Timecode.FromSeconds(6), survivor.TimelineStart);
    }

    [Fact]
    public void NudgeAll_Shifts_The_Set_Rigidly_And_Clamps_At_The_Origin()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var video = new VideoTrack();
        timeline.Tracks.Add(video);
        Clip early = AddClip(video, 1);
        Clip late = AddClip(video, 5);
        var history = new EditHistory();
        long twoSeconds = Timecode.FromSeconds(2).Ticks;

        // A −2 s nudge is clamped to −1 s (the earliest member would cross the origin); both shift by the
        // same clamped delta as one undo entry.
        history.Execute(ClipEdits.NudgeAll(timeline, [early, late], -twoSeconds, linked: true)!);
        Assert.Equal(Timecode.Zero, early.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(4), late.TimelineStart);

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(1), early.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(5), late.TimelineStart);

        // Already at the origin: the clamp leaves nothing to move — no command at all.
        early.TimelineStart = Timecode.Zero;
        Assert.Null(ClipEdits.NudgeAll(timeline, [early, late], -twoSeconds, linked: true));
    }

    [Fact]
    public void ToggleEnabledAll_Converges_A_Mixed_Selection_On_The_Primary_Target()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out _, out Clip v, out Clip a);
        Clip lone = AddClip(video, 8);
        lone.Enabled = false; // mixed: primary v enabled, lone disabled
        var history = new EditHistory();

        history.Execute(ClipEdits.ToggleEnabledAll(timeline, v, [v, lone], linked: true)!);
        Assert.False(v.Enabled);
        Assert.False(a.Enabled);    // linked companion follows
        Assert.False(lone.Enabled); // set to the target, not flipped back on

        history.Undo(); // one entry restores each member's prior state
        Assert.True(v.Enabled);
        Assert.True(a.Enabled);
        Assert.False(lone.Enabled);
    }

    [Fact]
    public void MoveSet_Shifts_Every_Member_Rigidly_As_One_Undo_Entry()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out Clip a);
        _ = timeline;
        var history = new EditHistory();
        long newStart = Timecode.FromSeconds(5).Ticks; // v moves 2 s → 5 s: delta +3 s

        IEditCommand? command = ClipEdits.MoveSet(
            v, video, video, newStart, v.SourceIn, v.SourceOut, v.TimelineStart.Ticks,
            [(a, a.TimelineStart)]);
        Assert.NotNull(command);
        history.Execute(command);

        Assert.Equal(Timecode.FromSeconds(5), v.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(5), a.TimelineStart); // the companion shifted by the same delta

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(2), v.TimelineStart);
        Assert.Equal(Timecode.FromSeconds(2), a.TimelineStart);
    }

    [Fact]
    public void MoveSet_Moves_The_Primary_Across_Tracks_And_Others_In_Time_Only()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out AudioTrack audio, out Clip v, out Clip a);
        var upper = new VideoTrack();
        timeline.Tracks.Add(upper);
        var history = new EditHistory();
        long newStart = Timecode.FromSeconds(3).Ticks;

        history.Execute(ClipEdits.MoveSet(
            v, video, upper, newStart, v.SourceIn, v.SourceOut, v.TimelineStart.Ticks,
            [(a, a.TimelineStart)])!);

        Assert.Empty(video.Clips);
        Assert.Contains(v, upper.Clips);                        // the primary changed lanes
        Assert.Equal(Timecode.FromSeconds(3), v.TimelineStart);
        Assert.Contains(a, audio.Clips);                        // the companion kept its own track
        Assert.Equal(Timecode.FromSeconds(3), a.TimelineStart);

        history.Undo();
        Assert.Contains(v, video.Clips);
        Assert.Equal(Timecode.FromSeconds(2), a.TimelineStart);
    }

    [Fact]
    public void MoveSet_Is_Null_For_A_Pure_Click()
    {
        Timeline timeline = LinkedPair(out VideoTrack video, out _, out Clip v, out Clip a);
        _ = timeline;
        Assert.Null(ClipEdits.MoveSet(
            v, video, video, v.TimelineStart.Ticks, v.SourceIn, v.SourceOut, v.TimelineStart.Ticks,
            [(a, a.TimelineStart)]));
    }
}
