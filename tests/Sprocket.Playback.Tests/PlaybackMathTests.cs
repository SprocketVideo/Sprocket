using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>Pure-logic tests for the pump's timing decisions (<see cref="PlaybackMath"/>).</summary>
public class PlaybackMathTests
{
    private static readonly Timecode Duration = Timecode.FromSeconds(10);

    [Theory]
    [InlineData(-5, 0)]   // negative clamps to zero
    [InlineData(0, 0)]
    [InlineData(4, 4)]    // in range is unchanged
    [InlineData(10, 10)]  // end is inclusive
    [InlineData(15, 10)]  // past end clamps to duration
    public void ClampToTimeline_Clamps_To_Range(double posSeconds, double expectedSeconds)
    {
        Timecode result = PlaybackMath.ClampToTimeline(Timecode.FromSeconds(posSeconds), Duration);
        Assert.Equal(Timecode.FromSeconds(expectedSeconds).Ticks, result.Ticks);
    }

    [Fact]
    public void ReachedEnd_Is_True_At_Or_Past_Duration()
    {
        Assert.False(PlaybackMath.ReachedEnd(Timecode.FromSeconds(9.99), Duration));
        Assert.True(PlaybackMath.ReachedEnd(Duration, Duration));
        Assert.True(PlaybackMath.ReachedEnd(Timecode.FromSeconds(11), Duration));
    }

    [Fact]
    public void ReachedEnd_Is_False_For_Empty_Timeline()
    {
        Assert.False(PlaybackMath.ReachedEnd(Timecode.Zero, Timecode.Zero));
    }

    [Fact]
    public void ShouldPromote_When_Frame_Is_Due()
    {
        Timecode target = Timecode.FromSeconds(2);
        Assert.True(PlaybackMath.ShouldPromote(Timecode.FromSeconds(1.5), target, forcePresent: false)); // before target → due
        Assert.True(PlaybackMath.ShouldPromote(target, target, forcePresent: false));                    // exactly at target → due
        Assert.False(PlaybackMath.ShouldPromote(Timecode.FromSeconds(2.5), target, forcePresent: false)); // after target → hold
    }

    [Fact]
    public void ShouldPromote_Forces_The_Post_Seek_Frame()
    {
        Timecode target = Timecode.FromSeconds(2);
        // A frame just past the target normally holds, but after a seek it is force-presented.
        Assert.True(PlaybackMath.ShouldPromote(Timecode.FromSeconds(2.5), target, forcePresent: true));
    }
}
