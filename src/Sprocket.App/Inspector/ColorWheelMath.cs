using System;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure puck ↔ RGB mapping for the Color Wheels trackballs (the Inspector's composite editor over the
/// step-34 Lift/Gamma/Gain parameters), split out like <see cref="InspectorFormat"/> so it's unit-testable
/// without an Avalonia surface. The disc uses the vectorscope-style channel axes — R at 0°, G at 120°,
/// B at 240° (x right, y up, unit radius) — and the puck carries only the <em>zero-sum tint</em> of the
/// R/G/B offsets: pushing toward a hue raises that channel and lowers the other two symmetrically, exactly
/// how Resolve's wheel + master ring pair resolves (the master slider carries the common component, and the
/// shader sums master + channel per channel). The common (mean) part of the current offsets is preserved
/// across puck moves so a wheel drag never destroys a per-channel slider tweak.
/// </summary>
public static class ColorWheelMath
{
    // Unit channel axes at 0° / 120° / 240°. For any puck p, offsets cᵢ = p·eᵢ sum to zero, and
    // Σ cᵢ·eᵢ = (3/2)·p — the exact inverse used by ToPuck.
    private static readonly (double X, double Y) AxisR = (1.0, 0.0);
    private static readonly (double X, double Y) AxisG = (-0.5, Math.Sqrt(3.0) / 2.0);
    private static readonly (double X, double Y) AxisB = (-0.5, -Math.Sqrt(3.0) / 2.0);

    /// <summary>
    /// The R/G/B offsets for a puck at (<paramref name="x"/>, <paramref name="y"/>) (unit disc, x right,
    /// y up), preserving the mean of <paramref name="current"/> — the part of the offsets the puck can't
    /// represent (it lives on the master slider's axis). The puck is clamped to the unit disc and each
    /// channel to the wheel's [-1, 1] throw.
    /// </summary>
    public static (double R, double G, double B) FromPuck(
        double x, double y, (double R, double G, double B) current)
    {
        double radius = Math.Sqrt(x * x + y * y);
        if (radius > 1.0)
        {
            x /= radius;
            y /= radius;
        }
        double mean = (current.R + current.G + current.B) / 3.0;
        return (
            Math.Clamp(mean + x * AxisR.X + y * AxisR.Y, -1.0, 1.0),
            Math.Clamp(mean + x * AxisG.X + y * AxisG.Y, -1.0, 1.0),
            Math.Clamp(mean + x * AxisB.X + y * AxisB.Y, -1.0, 1.0));
    }

    /// <summary>
    /// The puck position for the given R/G/B offsets: the zero-sum (tint) part projected onto the disc,
    /// clamped to the unit circle. The mean component doesn't move the puck — it belongs to the master.
    /// </summary>
    public static (double X, double Y) ToPuck(double r, double g, double b)
    {
        double mean = (r + g + b) / 3.0;
        double zr = r - mean, zg = g - mean, zb = b - mean;
        double x = (2.0 / 3.0) * (zr * AxisR.X + zg * AxisG.X + zb * AxisB.X);
        double y = (2.0 / 3.0) * (zr * AxisR.Y + zg * AxisG.Y + zb * AxisB.Y);
        double radius = Math.Sqrt(x * x + y * y);
        return radius > 1.0 ? (x / radius, y / radius) : (x, y);
    }
}
