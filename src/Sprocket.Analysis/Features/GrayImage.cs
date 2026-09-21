namespace Sprocket.Analysis.Features;

/// <summary>
/// A read-only view over an 8-bit grayscale image (GRAY8). A borrowed span plus dimensions — it owns
/// no memory, so it never allocates and is cheap to pass around. The backing memory is typically a
/// pooled native or managed buffer supplied by the caller (ARCHITECTURE.md §1: no per-frame managed
/// pixel allocation).
/// </summary>
public readonly ref struct GrayImage
{
    /// <summary>Row-major pixel bytes; row <c>y</c> starts at <c>y * Stride</c>.</summary>
    public readonly ReadOnlySpan<byte> Pixels;

    /// <summary>Image width in pixels.</summary>
    public readonly int Width;

    /// <summary>Image height in pixels.</summary>
    public readonly int Height;

    /// <summary>Distance in bytes between the start of consecutive rows (may exceed <see cref="Width"/>).</summary>
    public readonly int Stride;

    public GrayImage(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (stride < width)
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride must be at least the width.");
        if (pixels.Length < (long)stride * (height - 1) + width)
            throw new ArgumentException("Pixel span is too small for the given dimensions.", nameof(pixels));

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    /// <summary>The pixel at integer coordinates (no bounds clamping — caller must stay in range).</summary>
    public byte this[int x, int y] => Pixels[y * Stride + x];

    /// <summary>
    /// Bilinearly sampled intensity at a sub-pixel location, with edge clamping. Returns a float so
    /// the tracker can compute sub-pixel residuals.
    /// </summary>
    public float SampleBilinear(float x, float y)
    {
        // Clamp the sample centre so the 2x2 neighbourhood stays inside the image.
        if (x < 0f) x = 0f;
        if (y < 0f) y = 0f;
        float maxX = Width - 1f;
        float maxY = Height - 1f;
        if (x > maxX) x = maxX;
        if (y > maxY) y = maxY;

        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = x0 < Width - 1 ? x0 + 1 : x0;
        int y1 = y0 < Height - 1 ? y0 + 1 : y0;
        float fx = x - x0;
        float fy = y - y0;

        ReadOnlySpan<byte> p = Pixels;
        int r0 = y0 * Stride;
        int r1 = y1 * Stride;
        float top = p[r0 + x0] + (p[r0 + x1] - p[r0 + x0]) * fx;
        float bot = p[r1 + x0] + (p[r1 + x1] - p[r1 + x0]) * fx;
        return top + (bot - top) * fy;
    }
}
