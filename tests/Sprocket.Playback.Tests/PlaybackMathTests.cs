using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>Pure-logic tests for the pump's timing decisions (<see cref="PlaybackMath"/>).</summary>
public class PlaybackMathTests
{
    private static readonly Timecode Duration = Timecode.FromSeconds(10);
    private static readonly Rational Fps30 = new(30, 1);

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

    [Fact]
    public void StepFrame_Advances_And_Retreats_One_Frame()
    {
        // Frame 30 of 30 fps = 1.0 s. Stepping ±1 lands exactly on the neighbouring frame boundaries.
        Timecode at1s = Timecode.FromFrames(30, Fps30);
        Assert.Equal(Timecode.FromFrames(31, Fps30).Ticks, PlaybackMath.StepFrame(at1s, Fps30, +1, Duration).Ticks);
        Assert.Equal(Timecode.FromFrames(29, Fps30).Ticks, PlaybackMath.StepFrame(at1s, Fps30, -1, Duration).Ticks);
    }

    [Fact]
    public void StepFrame_Snaps_A_Mid_Frame_Position_To_The_Frame_Grid()
    {
        // A position partway through frame 10 floors to frame 10, then steps to 11 / 9.
        Timecode midFrame10 = Timecode.FromFrames(10, Fps30) + new Timecode(123);
        Assert.Equal(Timecode.FromFrames(11, Fps30).Ticks, PlaybackMath.StepFrame(midFrame10, Fps30, +1, Duration).Ticks);
        Assert.Equal(Timecode.FromFrames(9, Fps30).Ticks, PlaybackMath.StepFrame(midFrame10, Fps30, -1, Duration).Ticks);
    }

    [Fact]
    public void StepFrame_Clamps_At_Both_Ends()
    {
        Assert.Equal(Timecode.Zero.Ticks, PlaybackMath.StepFrame(Timecode.Zero, Fps30, -1, Duration).Ticks);

        // 10 s at 30 fps = frame 300; stepping forward at the end clamps to the duration.
        Timecode end = Timecode.FromFrames(300, Fps30);
        Assert.Equal(Duration.Ticks, PlaybackMath.StepFrame(end, Fps30, +1, Duration).Ticks);
    }

    [Fact]
    public void StepFrame_Is_A_Clamped_NoOp_For_A_Degenerate_Frame_Rate()
    {
        Timecode pos = Timecode.FromSeconds(3);
        Assert.Equal(pos.Ticks, PlaybackMath.StepFrame(pos, new Rational(0, 1), +1, Duration).Ticks);
    }
}
