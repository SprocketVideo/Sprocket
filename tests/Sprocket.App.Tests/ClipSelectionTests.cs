using System.Linq;
using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for <see cref="ClipSelection"/> (PLAN.md step 54): the multi-clip selection set's
/// membership and primary-tracking rules — plain-click replace, Ctrl-click toggle, Shift-click extend,
/// marquee/Select-All replace, and the undo-pruning hand-off. The pointer wiring in <c>TimelineControl</c>
/// rests on these + manual verification (the App is a UI-bound WinExe).
/// </summary>
public class ClipSelectionTests
{
    private static Clip MakeClip(double startSeconds = 0) =>
        new(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.FromSeconds(startSeconds));

    [Fact]
    public void Replace_Selects_One_And_Clears_The_Rest()
    {
        var selection = new ClipSelection();
        Clip a = MakeClip(); Clip b = MakeClip();
        selection.Toggle(a);
        selection.Toggle(b);

        Assert.True(selection.Replace(a));
        Assert.Single(selection.Clips);
        Assert.Same(a, selection.Primary);

        Assert.False(selection.Replace(a)); // already exactly this — no change
        Assert.True(selection.Replace(null));
        Assert.Empty(selection.Clips);
        Assert.Null(selection.Primary);
    }

    [Fact]
    public void Toggle_Adds_As_Primary_And_Removes_With_Primary_Handoff()
    {
        var selection = new ClipSelection();
        Clip a = MakeClip(); Clip b = MakeClip(); Clip c = MakeClip();

        selection.Toggle(a);
        selection.Toggle(b);
        selection.Toggle(c);
        Assert.Equal(3, selection.Count);
        Assert.Same(c, selection.Primary); // the last added is the primary

        selection.Toggle(c); // toggling the primary out hands the role to the most recent remaining member
        Assert.Equal(2, selection.Count);
        Assert.Same(b, selection.Primary);

        selection.Toggle(a); // toggling out a non-primary keeps the primary
        Assert.Same(b, selection.Primary);

        selection.Toggle(b); // the set empties
        Assert.Empty(selection.Clips);
        Assert.Null(selection.Primary);
    }

    [Fact]
    public void Extend_Adds_Or_Reanchors_But_Never_Removes()
    {
        var selection = new ClipSelection();
        Clip a = MakeClip(); Clip b = MakeClip();

        Assert.True(selection.Extend(a));
        Assert.True(selection.Extend(b));
        Assert.Equal(2, selection.Count);
        Assert.Same(b, selection.Primary);

        Assert.True(selection.Extend(a));  // already a member — re-anchors the primary only
        Assert.Equal(2, selection.Count);
        Assert.Same(a, selection.Primary);

        Assert.False(selection.Extend(a)); // member and already primary — no change
    }

    [Fact]
    public void SetPrimary_Requires_Membership()
    {
        var selection = new ClipSelection();
        Clip a = MakeClip(); Clip b = MakeClip(); Clip outsider = MakeClip();
        selection.Toggle(a);
        selection.Toggle(b);

        Assert.False(selection.SetPrimary(outsider)); // not a member — refused
        Assert.Same(b, selection.Primary);
        Assert.True(selection.SetPrimary(a));
        Assert.Same(a, selection.Primary);
        Assert.Equal(2, selection.Count); // membership untouched
    }

    [Fact]
    public void ReplaceAll_Dedupes_And_Keeps_A_Still_Covered_Primary()
    {
        var selection = new ClipSelection();
        Clip a = MakeClip(); Clip b = MakeClip(); Clip c = MakeClip();
        selection.Replace(b); // b is the primary going in

        Assert.True(selection.ReplaceAll([a, b, b, c]));
        Assert.Equal(3, selection.Count);       // the duplicate b collapses
        Assert.Same(b, selection.Primary);      // a marquee still covering the primary keeps it anchored

        Assert.True(selection.ReplaceAll([a, c])); // the primary fell out — the first new member takes over
        Assert.Same(a, selection.Primary);

        Assert.False(selection.ReplaceAll([a, c])); // identical set — no change
        Assert.True(selection.ReplaceAll([]));
        Assert.Null(selection.Primary);
    }

    [Fact]
    public void Prune_Drops_Dead_Members_And_Hands_Off_The_Primary()
    {
        var selection = new ClipSelection();
        Clip a = MakeClip(); Clip b = MakeClip(); Clip c = MakeClip();
        selection.ReplaceAll([a, b, c]);
        selection.SetPrimary(c);

        Assert.False(selection.Prune(_ => true)); // everything alive — no change

        Assert.True(selection.Prune(x => !ReferenceEquals(x, c))); // undo removed the primary
        Assert.Equal(2, selection.Count);
        Assert.Same(b, selection.Primary); // most recently added survivor takes over

        Assert.True(selection.Prune(_ => false));
        Assert.Empty(selection.Clips);
        Assert.Null(selection.Primary);
    }

    [Fact]
    public void SelectAll_Semantics_Cover_Every_Clip_Across_Tracks()
    {
        // Edit ▸ Select All replaces the selection with every clip on every track (PLAN.md step 54).
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var video = new VideoTrack();
        var audio = new AudioTrack();
        video.Clips.Add(MakeClip(0));
        video.Clips.Add(MakeClip(3));
        audio.Clips.Add(MakeClip(0));
        timeline.Tracks.Add(video);
        timeline.Tracks.Add(audio);

        var selection = new ClipSelection();
        selection.Replace(video.Clips[1]);
        Assert.True(selection.ReplaceAll(timeline.Tracks.SelectMany(t => t.Clips)));

        Assert.Equal(3, selection.Count);
        Assert.Same(video.Clips[1], selection.Primary); // the prior primary is covered, so it keeps the role
        Assert.All(timeline.Tracks.SelectMany(t => t.Clips), c => Assert.True(selection.Contains(c)));
    }
}
