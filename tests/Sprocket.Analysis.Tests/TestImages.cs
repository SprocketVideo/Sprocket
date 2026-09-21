using Sprocket.Analysis.Features;

namespace Sprocket.Analysis.Tests;

/// <summary>
/// A packed grayscale buffer (stride == width) that hands out <see cref="GrayImage"/> views. Kept as a
/// class because <see cref="GrayImage"/> is a ref struct and cannot be stored or returned directly.
/// </summary>
internal sealed class GrayBuffer(byte[] data, int width, int height)
{
    public byte[] Data { get; } = data;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public GrayImage View => new(Data, Width, Height, Width);
}

/// <summary>
/// Deterministic procedural test imagery: a smooth, corner-rich analytic texture and helpers that
/// render it under a known similarity or homography. Because both the reference frame and the warped
/// frame are sampled from the same continuous function, the warp carries no resampling bias — the
/// estimator is measured against the exact ground-truth transform.
/// </summary>
internal static class TestImages
{
    /// <summary>Continuous texture value at a real-valued coordinate, clamped to [0,255].</summary>
    public static double Sample(double x, double y)
    {
        double v = 128.0
            + 45.0 * Math.Sin(0.20 * x + 0.10 * y)
            + 40.0 * Math.Cos(0.15 * y - 0.05 * x)
            + 35.0 * Math.Sin(0.09 * (x + y))
            + 25.0 * Math.Cos(0.31 * x) * Math.Sin(0.27 * y);
        if (v < 0) v = 0;
        if (v > 255) v = 255;
        return v;
    }

    /// <summary>Renders the texture straight onto an integer grid (the reference frame).</summary>
    public static GrayBuffer Reference(int width, int height)
    {
        byte[] data = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                data[y * width + x] = (byte)Math.Round(Sample(x, y));
        return new GrayBuffer(data, width, height);
    }

    /// <summary>
    /// Renders the texture warped by a similarity (scale, rotation, translation) so a feature at
    /// <c>q</c> in the reference lands at <c>S(q)</c> in this frame. The estimator, tracking
    /// reference→warped, should therefore recover <c>S</c>.
    /// </summary>
    public static GrayBuffer WarpSimilarity(int width, int height, double scale, double angleRad, double tx, double ty)
    {
        double a = scale * Math.Cos(angleRad);
        double b = scale * Math.Sin(angleRad);
        double det = a * a + b * b;
        byte[] data = new byte[width * height];
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                // src = M^-1 (dst - t)
                double d = px - tx, e = py - ty;
                double sx = (a * d + b * e) / det;
                double sy = (-b * d + a * e) / det;
                data[py * width + px] = (byte)Math.Round(Sample(sx, sy));
            }
        }
        return new GrayBuffer(data, width, height);
    }

    /// <summary>
    /// Renders the texture warped by a homography (reference→warped), given the inverse homography used
    /// to look up the source coordinate for each destination pixel.
    /// </summary>
    public static GrayBuffer WarpHomography(int width, int height, double[] hInverse)
    {
        byte[] data = new byte[width * height];
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                double w = hInverse[6] * px + hInverse[7] * py + hInverse[8];
                double sx = (hInverse[0] * px + hInverse[1] * py + hInverse[2]) / w;
                double sy = (hInverse[3] * px + hInverse[4] * py + hInverse[5]) / w;
                data[py * width + px] = (byte)Math.Round(Sample(sx, sy));
            }
        }
        return new GrayBuffer(data, width, height);
    }

    /// <summary>Deterministic uniform noise (used to prove the forward-backward filter drops mistracks).</summary>
    public static GrayBuffer Noise(int width, int height, ulong seed)
    {
        var rng = new XorShiftRng(seed);
        byte[] data = new byte[width * height];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(rng.Next() & 0xFF);
        return new GrayBuffer(data, width, height);
    }

    /// <summary>Inverts a 3×3 matrix (row-major, length 9). Throws if singular.</summary>
    public static double[] Invert3x3(double[] m)
    {
        double a = m[0], b = m[1], c = m[2];
        double d = m[3], e = m[4], f = m[5];
        double g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-15)
            throw new InvalidOperationException("Singular matrix.");
        double inv = 1.0 / det;
        return
        [
            (e * i - f * h) * inv, (c * h - b * i) * inv, (b * f - c * e) * inv,
            (f * g - d * i) * inv, (a * i - c * g) * inv, (c * d - a * f) * inv,
            (d * h - e * g) * inv, (b * g - a * h) * inv, (a * e - b * d) * inv,
        ];
    }
}
