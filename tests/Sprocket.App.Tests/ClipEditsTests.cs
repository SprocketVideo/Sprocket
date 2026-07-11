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
}
