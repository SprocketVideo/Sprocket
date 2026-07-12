using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

public class RationalTests
{
    [Fact]
    public void Reduces_To_Lowest_Terms()
    {
        var r = new Rational(60000, 2002);
        Assert.Equal(30000, r.Num);
        Assert.Equal(1001, r.Den);
    }

    [Fact]
    public void Normalises_Sign_Onto_Numerator()
    {
        var r = new Rational(1, -2);
        Assert.Equal(-1, r.Num);
        Assert.Equal(2, r.Den);
    }

    [Fact]
    public void Equality_Is_Value_Equality_Of_Reduced_Form()
    {
        Assert.Equal(new Rational(30000, 1001), new Rational(60000, 2002));
    }

    [Fact]
    public void Zero_Denominator_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(1, 0));
    }

    [Fact]
    public void Comparison_Does_Not_Overflow()
    {
        // Large num/den that would overflow int multiplication if compared naively.
        Assert.True(new Rational(2_000_000_000, 3) > new Rational(1_000_000_000, 3));
    }
}

public class TimecodeTests
{
    [Fact]
    public void Arithmetic_Is_Exact_In_Ticks()
    {
        var a = new Timecode(100);
        var b = new Timecode(40);
        Assert.Equal(140, (a + b).Ticks);
        Assert.Equal(60, (a - b).Ticks);
    }

    [Fact]
    public void Frame_RoundTrip_Is_Stable_For_Ntsc()
    {
        var fps = new Rational(30000, 1001); // 29.97
        for (long frame = 0; frame < 5000; frame++)
        {
            Timecode tc = Timecode.FromFrames(frame, fps);
            Assert.Equal(frame, tc.ToFrameIndex(fps));
        }
    }

    [Fact]
    public void Frame_Index_Floors_Within_A_Frame()
    {
        var fps = new Rational(30, 1);
        Timecode frame10 = Timecode.FromFrames(10, fps);
        // A tick past the start of frame 10 is still frame 10.
        Assert.Equal(10, (frame10 + new Timecode(1)).ToFrameIndex(fps));
        // A tick before is frame 9.
        Assert.Equal(9, (frame10 - new Timecode(1)).ToFrameIndex(fps));
    }

    [Theory]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(30, 1)]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    public void SnapToFrame_Is_Identity_On_A_Frame_Boundary(int num, int den)
    {
        var fps = new Rational(num, den);
        Timecode boundary = Timecode.FromFrames(1234, fps);
        Assert.Equal(boundary, boundary.SnapToFrame(fps));
    }

    [Theory]
    [InlineData(24, 1)]
    [InlineData(30, 1)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    public void SnapToFrame_Floors_Mid_Frame_Ticks_To_The_Frame_Start(int num, int den)
    {
        var fps = new Rational(num, den);
        Timecode start = Timecode.FromFrames(500, fps);
        Timecode next = Timecode.FromFrames(501, fps);

        // Anywhere inside the frame — including one tick before the next boundary — snaps back to its start.
        Assert.Equal(start, (start + new Timecode(1)).SnapToFrame(fps));
        Assert.Equal(start, (next - new Timecode(1)).SnapToFrame(fps));
        Assert.Equal(500, (next - new Timecode(1)).SnapToFrame(fps).ToFrameIndex(fps));
    }

    [Fact]
    public void SnapToFrame_Is_Exact_Hours_Into_An_Ntsc_Timeline()
    {
        var fps = new Rational(30000, 1001);
        long frame = 3 * 60 * 60 * 30; // ≈3 hours in — where double-seconds math would have drifted
        Timecode deep = Timecode.FromFrames(frame, fps) + new Timecode(1);
        Assert.Equal(Timecode.FromFrames(frame, fps), deep.SnapToFrame(fps));
        Assert.Equal(frame, deep.SnapToFrame(fps).ToFrameIndex(fps));
    }

    [Fact]
    public void SnapToFrame_With_A_Non_Positive_Rate_Is_Identity()
    {
        var t = new Timecode(123457);
        Assert.Equal(t, t.SnapToFrame(Rational.Zero));
        Assert.Equal(t, t.SnapToFrame(new Rational(-30, 1)));
    }

    [Fact]
    public void Sample_RoundTrip_Is_Stable()
    {
        const int sr = 48000;
        for (long sample = 0; sample < 100000; sample += 137)
        {
            Timecode tc = Timecode.FromSamples(sample, sr);
            Assert.Equal(sample, tc.ToSampleIndex(sr));
        }
    }

    [Fact]
    public void One_Second_Is_TicksPerSecond()
    {
        Assert.Equal(Timecode.TicksPerSecond, Timecode.FromSeconds(1.0).Ticks);
        Assert.Equal(48000, Timecode.FromSeconds(1.0).ToSampleIndex(48000));
        Assert.Equal(30, Timecode.FromSeconds(1.0).ToFrameIndex(new Rational(30, 1)));
    }

    [Fact]
    public void Min_Max_Comparison()
    {
        var a = new Timecode(10);
        var b = new Timecode(20);
        Assert.Equal(a, Timecode.Min(a, b));
        Assert.Equal(b, Timecode.Max(a, b));
        Assert.True(a < b);
        Assert.True(b >= a);
    }
}
