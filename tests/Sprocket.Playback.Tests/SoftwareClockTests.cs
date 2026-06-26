using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Deterministic behaviour of <see cref="SoftwareClock"/> driven by a controllable elapsed source — no real
/// time, no sleeping. Covers the start-paused, advance-while-running, freeze-on-pause, seek, and rate cases.
/// </summary>
public class SoftwareClockTests
{
    private static (SoftwareClock clock, Action<double> setSeconds) NewClock()
    {
        var elapsed = TimeSpan.Zero;
        var clock = new SoftwareClock(() => elapsed);
        return (clock, s => elapsed = TimeSpan.FromSeconds(s));
    }

    [Fact]
    public void Starts_Paused_At_Zero()
    {
        (SoftwareClock clock, _) = NewClock();
        Assert.False(clock.IsRunning);
        Assert.Equal(0, clock.Now.Ticks);
    }

    [Fact]
    public void Does_Not_Advance_While_Paused()
    {
        (SoftwareClock clock, Action<double> setSeconds) = NewClock();
        setSeconds(5.0);
        Assert.Equal(0, clock.Now.Ticks); // paused: elapsed wall time is ignored
    }

    [Fact]
    public void Advances_In_Real_Time_While_Running()
    {
        (SoftwareClock clock, Action<double> setSeconds) = NewClock();
        clock.Start();
        setSeconds(2.0);
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, clock.Now.Ticks);
    }

    [Fact]
    public void Pause_Freezes_At_Current_Position()
    {
        (SoftwareClock clock, Action<double> setSeconds) = NewClock();
        clock.Start();
        setSeconds(1.5);
        clock.Pause();
        setSeconds(10.0); // further wall time must not move a paused clock
        Assert.Equal(Timecode.FromSeconds(1.5).Ticks, clock.Now.Ticks);
    }

    [Fact]
    public void Seek_Sets_Position_And_Keeps_Running_State()
    {
        (SoftwareClock clock, Action<double> setSeconds) = NewClock();
        clock.Start();
        setSeconds(1.0);
        clock.Seek(Timecode.FromSeconds(10.0));
        Assert.True(clock.IsRunning);
        Assert.Equal(Timecode.FromSeconds(10.0).Ticks, clock.Now.Ticks);

        setSeconds(1.5); // half a second more wall time after the seek anchor
        Assert.Equal(Timecode.FromSeconds(10.5).Ticks, clock.Now.Ticks);
    }

    [Fact]
    public void Rate_Scales_Advance()
    {
        (SoftwareClock clock, Action<double> setSeconds) = NewClock();
        clock.Start();
        clock.Rate = 2.0;
        setSeconds(2.0);
        Assert.Equal(Timecode.FromSeconds(4.0).Ticks, clock.Now.Ticks);
    }
}
