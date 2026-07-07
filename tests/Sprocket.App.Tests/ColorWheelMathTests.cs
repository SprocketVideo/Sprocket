using System;
using Sprocket.App.Inspector;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The pure puck ↔ RGB mapping behind the Color Wheels trackballs (<see cref="ColorWheelMath"/>): the puck
/// carries the zero-sum tint of the R/G/B offsets on vectorscope-style axes (R 0°, G 120°, B 240°), the mean
/// survives puck moves (so a per-channel slider tweak isn't destroyed), and the two directions round-trip.
/// The wheel control's drawing + pointer interaction rest on these + manual verification.
/// </summary>
public class ColorWheelMathTests
{
    private const int Precision = 10;

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.0)]
    [InlineData(0.0, -0.7)]
    [InlineData(-0.3, 0.4)]
    [InlineData(0.6, 0.6)] // within the unit disc (r ≈ 0.85)
    public void FromPuck_Then_ToPuck_RoundTrips(double x, double y)
    {
        (double r, double g, double b) = ColorWheelMath.FromPuck(x, y, (0, 0, 0));
        (double px, double py) = ColorWheelMath.ToPuck(r, g, b);
        Assert.Equal(x, px, Precision);
        Assert.Equal(y, py, Precision);
    }

    [Fact]
    public void FromPuck_Offsets_Are_ZeroSum_Around_The_Preserved_Mean()
    {
        (double r0, double g0, double b0) current = (0.3, 0.0, -0.15); // mean 0.05
        (double r, double g, double b) = ColorWheelMath.FromPuck(0.4, -0.2, current);
        Assert.Equal(0.05, (r + g + b) / 3.0, Precision); // mean survives the puck move
    }

    [Fact]
    public void FromPuck_Preserving_The_Mean_Keeps_The_Master_Axis_Out_Of_The_Puck()
    {
        // A pure common offset (all channels equal) is the master slider's business: puck sits centred.
        (double x, double y) = ColorWheelMath.ToPuck(0.25, 0.25, 0.25);
        Assert.Equal(0.0, x, Precision);
        Assert.Equal(0.0, y, Precision);
    }

    [Fact]
    public void Pure_Channel_Directions_Land_On_The_Expected_Axes()
    {
        // R at 0° (screen right), G at 120°, B at 240° — push the puck along each axis and the matching
        // channel rises while the other two fall symmetrically.
        (double r, double g, double b) = ColorWheelMath.FromPuck(0.8, 0.0, (0, 0, 0));
        Assert.Equal(0.8, r, Precision);
        Assert.Equal(-0.4, g, Precision);
        Assert.Equal(-0.4, b, Precision);

        (r, g, b) = ColorWheelMath.FromPuck(0.8 * -0.5, 0.8 * Math.Sqrt(3) / 2, (0, 0, 0));
        Assert.Equal(0.8, g, Precision);
        Assert.Equal(-0.4, r, Precision);
        Assert.Equal(-0.4, b, Precision);

        (r, g, b) = ColorWheelMath.FromPuck(0.8 * -0.5, 0.8 * -Math.Sqrt(3) / 2, (0, 0, 0));
        Assert.Equal(0.8, b, Precision);
        Assert.Equal(-0.4, r, Precision);
        Assert.Equal(-0.4, g, Precision);
    }

    [Fact]
    public void FromPuck_Clamps_The_Puck_To_The_Unit_Disc()
    {
        (double r, double g, double b) = ColorWheelMath.FromPuck(3.0, 0.0, (0, 0, 0));
        Assert.Equal(1.0, r, Precision); // direction kept, radius clamped to 1
        Assert.Equal(-0.5, g, Precision);
        Assert.Equal(-0.5, b, Precision);
    }

    [Fact]
    public void FromPuck_Clamps_Each_Channel_To_The_Wheel_Throw()
    {
        // A big mean pushes the keyed channel past +1: it clamps without disturbing the others.
        (double r, double g, double b) = ColorWheelMath.FromPuck(1.0, 0.0, (0.9, 0.9, 0.9));
        Assert.Equal(1.0, r, Precision);
        Assert.Equal(0.4, g, Precision);
        Assert.Equal(0.4, b, Precision);
    }

    [Fact]
    public void ToPuck_Clamps_An_Oversized_Tint_To_The_Rim()
    {
        (double x, double y) = ColorWheelMath.ToPuck(1.0, -1.0, -1.0);
        Assert.Equal(1.0, Math.Sqrt(x * x + y * y), Precision);
        Assert.True(x > 0 && Math.Abs(y) < 1e-9); // still pointing along the red axis
    }
}
