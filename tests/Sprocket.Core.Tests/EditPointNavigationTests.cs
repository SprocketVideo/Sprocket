using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary><see cref="EditPointNavigation"/>: previous / next clip start or end on a set of tracks.</summary>
public class EditPointNavigationTests
{
    private static Timecode S(double seconds) => Timecode.FromSeconds(seconds);

    private static VideoTrack Track(params (double Start, double Length)[] clips)
    {
        var track = new VideoTrack();
        foreach ((double start, double length) in clips)
            track.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, S(length), S(start)));
        return track;
    }

    [Fact]
    public void Finds_Starts_And_Ends_Strictly_Either_Side()
    {
        VideoTrack v1 = Track((0, 4), (6, 2));

        Assert.Equal(S(4), EditPointNavigation.Next([v1], S(0)));
        Assert.Equal(S(6), EditPointNavigation.Next([v1], S(4)));
        Assert.Equal(S(8), EditPointNavigation.Next([v1], S(7)));
        Assert.Null(EditPointNavigation.Next([v1], S(8)));
        Assert.Equal(S(6), EditPointNavigation.Previous([v1], S(8)));
        Assert.Equal(S(0), EditPointNavigation.Previous([v1], S(4)));
        Assert.Null(EditPointNavigation.Previous([v1], S(0)));
    }

    [Fact]
    public void Considers_Only_The_Given_Tracks()
    {
        VideoTrack v1 = Track((0, 4));
        VideoTrack v2 = Track((1, 1));

        Assert.Equal(S(4), EditPointNavigation.Next([v1], S(0)));
        Assert.Equal(S(1), EditPointNavigation.Next([v1, v2], S(0)));
        Assert.Null(EditPointNavigation.Next([], S(0)));
    }
}
