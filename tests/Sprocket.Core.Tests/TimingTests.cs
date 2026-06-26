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
