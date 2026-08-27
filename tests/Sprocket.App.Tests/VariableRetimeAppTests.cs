using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless App-layer pieces of variable / reverse retime (PLAN.md step 21 remainder): the fraction ↔ ratio
/// conversion the Inspector's keyframeable Speed row commits through, and the clip badge text. The dialog /
/// inspector / timeline gestures themselves rest on these + manual verification.
/// </summary>
public class VariableRetimeAppTests
{
    [Theory]
    [InlineData(1.0, 1, 1)]
    [InlineData(0.5, 1, 2)]
    [InlineData(2.0, 2, 1)]
    [InlineData(1.5, 3, 2)]
    [InlineData(0.3333, 3333, 10000)]
    public void FromFraction_Keeps_Two_Decimals_Of_The_Percentage(double fraction, int num, int den)
    {
        Assert.Equal(new Rational(num, den), SpeedFormat.FromFraction(fraction));
    }

    [Fact]
    public void FromFraction_Clamps_Non_Positive_Input_To_The_Ramp_Floor()
    {
        Assert.Equal(SpeedFormat.FromFraction(SpeedRamp.MinSpeed), SpeedFormat.FromFraction(0));
        Assert.Equal(SpeedFormat.FromFraction(SpeedRamp.MinSpeed), SpeedFormat.FromFraction(-3));
        Assert.Equal(SpeedFormat.FromFraction(SpeedRamp.MinSpeed), SpeedFormat.FromFraction(double.NaN));
        Assert.True(SpeedFormat.FromFraction(1e-9).Num > 0);
    }

    [Fact]
    public void FromFraction_Round_Trips_Through_The_Percent_String()
    {
        foreach (Rational speed in new[] { new Rational(3, 2), new Rational(1, 4), new Rational(7, 5) })
            Assert.Equal(speed, SpeedFormat.FromFraction(speed.ToDouble()));
    }

    [Fact]
    public void Retime_Badge_Reads_Speed_Ramp_And_Direction()
    {
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero);
        Assert.Null(TimelineControl.RetimeBadgeText(clip));

        clip.SpeedRatio = new Rational(1, 2);
        Assert.Equal("50%", TimelineControl.RetimeBadgeText(clip));

        clip.Reverse = true;
        Assert.Equal("◀ 50%", TimelineControl.RetimeBadgeText(clip));

        clip.SpeedRatio = Rational.One;
        Assert.Equal("◀", TimelineControl.RetimeBadgeText(clip));

        clip.SpeedCurve = AnimatableValue.Animated([new Keyframe(Timecode.Zero, 1.0), new Keyframe(Timecode.FromSeconds(1), 2.0)]);
        Assert.Equal("◀ RAMP", TimelineControl.RetimeBadgeText(clip));
        clip.Reverse = false;
        Assert.Equal("RAMP", TimelineControl.RetimeBadgeText(clip));
    }
}
