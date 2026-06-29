using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Tests for markers &amp; comments (PLAN.md step 20): the <see cref="Marker"/> model, the add / remove / move
/// commands reversing cleanly against the real model through <see cref="EditHistory"/>, and
/// <see cref="MarkerNavigation"/>. All headless.
/// </summary>
public class MarkerTests
{
    private static Marker At(double seconds, string name = "", double spanSeconds = 0) =>
        new(Timecode.FromSeconds(seconds), name, duration: Timecode.FromSeconds(spanSeconds));

    [Fact]
    public void Marker_Span_Is_Point_When_Duration_Zero()
    {
        Marker point = At(1);
        Assert.False(point.IsSpan);
        Assert.Equal(point.Time, point.End);

        Marker span = At(1, spanSeconds: 2);
        Assert.True(span.IsSpan);
        Assert.Equal(Timecode.FromSeconds(3), span.End);
    }

    [Fact]
    public void Marker_Rejects_Negative_Span()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Marker(Timecode.FromSeconds(1), duration: Timecode.FromSeconds(-1)));
    }

    [Fact]
    public void Clone_Is_Independent()
    {
        Marker original = At(1, "intro", spanSeconds: 2);
        original.Color = MarkerColor.Red;
        Marker copy = original.Clone();
        copy.Name = "changed";
        copy.Time = Timecode.FromSeconds(9);

        Assert.Equal("intro", original.Name);
        Assert.Equal(Timecode.FromSeconds(1), original.Time);
        Assert.Equal(MarkerColor.Red, copy.Color);
    }

    [Fact]
    public void AddMarker_Applies_And_Reverts()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var history = new EditHistory();
        Marker m = At(2, "cut here");

        history.Execute(new AddMarkerCommand(timeline.Markers, m));
        Assert.Single(timeline.Markers);
        Assert.Same(m, timeline.Markers[0]);

        history.Undo();
        Assert.Empty(timeline.Markers);
        history.Redo();
        Assert.Single(timeline.Markers);
    }

    [Fact]
    public void RemoveMarker_Restores_At_Original_Index()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        Marker a = At(1), b = At(2), c = At(3);
        timeline.Markers.AddRange([a, b, c]);
        var history = new EditHistory();

        history.Execute(new RemoveMarkerCommand(timeline.Markers, b));
        Assert.Equal([a, c], timeline.Markers);

        history.Undo();
        Assert.Equal([a, b, c], timeline.Markers); // re-inserted at index 1
    }

    [Fact]
    public void MoveMarker_Applies_Reverts_And_Coalesces_Within_A_Scope()
    {
        Marker m = At(1);
        var history = new EditHistory();

        using (history.BeginCoalescing())
        {
            history.Execute(new MoveMarkerCommand(m, Timecode.FromSeconds(2)));
            history.Execute(new MoveMarkerCommand(m, Timecode.FromSeconds(5)));
        }

        Assert.Equal(Timecode.FromSeconds(5), m.Time);
        Assert.Equal(1, history.UndoCount); // the drag collapsed to one entry

        history.Undo();
        Assert.Equal(Timecode.FromSeconds(1), m.Time); // all the way back to the gesture start
    }

    [Fact]
    public void MoveMarker_Does_Not_Coalesce_Across_Different_Markers()
    {
        Marker a = At(1), b = At(2);
        var history = new EditHistory();
        using (history.BeginCoalescing())
        {
            history.Execute(new MoveMarkerCommand(a, Timecode.FromSeconds(3)));
            history.Execute(new MoveMarkerCommand(b, Timecode.FromSeconds(4)));
        }
        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void Navigation_Finds_Nearest_In_Each_Direction()
    {
        Marker a = At(1), b = At(2), c = At(3);
        Marker[] markers = [c, a, b]; // unordered on purpose

        Assert.Same(a, MarkerNavigation.Previous(markers, Timecode.FromSeconds(1.5)));
        Assert.Same(b, MarkerNavigation.Previous(markers, Timecode.FromSeconds(3)));
        Assert.Same(c, MarkerNavigation.Next(markers, Timecode.FromSeconds(2.5)));
        Assert.Same(b, MarkerNavigation.Next(markers, Timecode.FromSeconds(1)));
    }

    [Fact]
    public void Navigation_Is_Strict_And_Null_At_The_Ends()
    {
        Marker a = At(1), b = At(2);
        Marker[] markers = [a, b];

        // Sitting exactly on a marker must move off it, not stick.
        Assert.Same(a, MarkerNavigation.Previous(markers, Timecode.FromSeconds(2)));
        Assert.Same(b, MarkerNavigation.Next(markers, Timecode.FromSeconds(1)));

        Assert.Null(MarkerNavigation.Previous(markers, Timecode.FromSeconds(1))); // 1s is the first
        Assert.Null(MarkerNavigation.Next(markers, Timecode.FromSeconds(2)));     // 2s is the last
        Assert.Null(MarkerNavigation.Previous([], Timecode.FromSeconds(5)));
    }
}
