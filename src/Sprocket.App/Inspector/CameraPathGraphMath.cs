using System;
using System.Collections.Generic;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure geometry for the Inspector's camera-path graph (plan/features/stabilization.md phase 6): mapping a
/// per-frame value series into a lane's polyline points, and choosing each lane's value range. Kept Avalonia-free
/// (plain doubles and a <see cref="Pt"/> tuple) so it is unit-testable without a UI, like
/// <see cref="KeyframeGraphMath"/>.
/// </summary>
public static class CameraPathGraphMath
{
    /// <summary>A polyline vertex in pixel space.</summary>
    public readonly record struct Pt(double X, double Y);

    /// <summary>
    /// The value range to plot a channel over: the combined min/max of the raw and smoothed series, padded by 5%
    /// of the span (and by a small floor so a dead-flat channel still renders as a centred line rather than a
    /// degenerate 0-height lane). Returns (0,0)→(-0.5,0.5)-style safe defaults for empty input.
    /// </summary>
    public static (double Min, double Max) Range(IReadOnlyList<double> raw, IReadOnlyList<double> smoothed)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(smoothed);

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double v in raw) { if (v < min) min = v; if (v > max) max = v; }
        foreach (double v in smoothed) { if (v < min) min = v; if (v > max) max = v; }

        if (double.IsInfinity(min) || double.IsInfinity(max)) // no samples
            return (-0.5, 0.5);

        double span = max - min;
        double pad = Math.Max(span * 0.05, 1e-4);
        return (min - pad, max + pad);
    }

    /// <summary>
    /// Maps <paramref name="values"/> to polyline vertices inside the lane rect [<paramref name="x"/>,
    /// <paramref name="x"/>+<paramref name="width"/>] × [<paramref name="y"/>, <paramref name="y"/>+<paramref
    /// name="height"/>], evenly spaced along X, with the value axis inverted (max at the top) and clamped to the
    /// lane. A single value is placed at the lane's left edge. Empty input yields no points.
    /// </summary>
    public static Pt[] Polyline(
        IReadOnlyList<double> values, double x, double width, double y, double height, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = values.Count;
        if (n == 0)
            return [];

        var pts = new Pt[n];
        double range = max - min;
        if (range <= 0)
            range = 1; // avoid divide-by-zero; a flat channel then sits at the lane bottom→clamped mid below
        double stepX = n > 1 ? width / (n - 1) : 0;
        for (int i = 0; i < n; i++)
        {
            double norm = (values[i] - min) / range;      // 0..1 up
            norm = Math.Clamp(norm, 0, 1);
            double py = y + (1 - norm) * height;           // invert: max at top
            pts[i] = new Pt(x + i * stepX, py);
        }
        return pts;
    }
}
