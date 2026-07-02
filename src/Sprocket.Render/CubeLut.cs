using System.Globalization;
using SkiaSharp;

namespace Sprocket.Render;

/// <summary>
/// A parsed Adobe/IRIDAS <c>.cube</c> 3D LUT (PLAN.md step 37): <see cref="Size"/>³ RGB samples, red index
/// fastest per the format spec. Parsing happens once per bundled LUT (see <see cref="ColorLuts"/>) — never
/// on a render path. <see cref="ToPackedImage"/> packs the lattice into the 2D texture layout the color
/// transform shader samples (all color math stays on the GPU, ARCHITECTURE.md §1/§7).
/// </summary>
public sealed class CubeLut
{
    private CubeLut(int size, float[] data)
    {
        Size = size;
        Data = data;
    }

    /// <summary>Lattice size N (the file's <c>LUT_3D_SIZE</c>): the table holds N³ RGB samples.</summary>
    public int Size { get; }

    /// <summary>The samples as RGB triples, red index fastest then green then blue —
    /// sample (r, g, b) starts at <c>3 * (r + g*N + b*N²)</c>.</summary>
    public float[] Data { get; }

    /// <summary>The output sample at lattice indices (<paramref name="r"/>, <paramref name="g"/>,
    /// <paramref name="b"/>) — test/inspection helper.</summary>
    public (float R, float G, float B) Sample(int r, int g, int b)
    {
        int i = 3 * (r + g * Size + b * Size * Size);
        return (Data[i], Data[i + 1], Data[i + 2]);
    }

    /// <summary>
    /// Parses a <c>.cube</c> text (comments, <c>TITLE</c>, and <c>DOMAIN_MIN/MAX</c> headers are tolerated;
    /// only the identity domain [0,1] is supported — the bundled camera LUTs all use it). Throws
    /// <see cref="FormatException"/> on a malformed table so a bad asset fails loudly at load, not mid-draw.
    /// </summary>
    public static CubeLut Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int size = 0;
        float[]? data = null;
        int filled = 0;

        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = rawLine.Trim();
            if (line.IsEmpty || line[0] == '#')
                continue;

            if (char.IsLetter(line[0]))
            {
                if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> value = line["LUT_3D_SIZE".Length..].Trim();
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
                        || size is < 2 or > 256)
                        throw new FormatException($"Invalid LUT_3D_SIZE '{value}'.");
                    data = new float[3 * size * size * size];
                }
                else if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase)
                         || line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
                {
                    bool isMax = char.ToUpperInvariant(line[8]) == 'A';   // DOMAIN_M[I]N vs DOMAIN_M[A]X
                    if (!IsUniform(line[10..], isMax ? 1f : 0f))
                        throw new FormatException("Only the identity domain [0, 1] is supported.");
                }
                // TITLE / LUT_1D_SIZE etc.: 1D tables are not supported; anything else is ignored.
                else if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("1D .cube tables are not supported.");
                continue;
            }

            if (data is null)
                throw new FormatException("Sample data before LUT_3D_SIZE.");
            if (filled >= data.Length)
                throw new FormatException("More samples than LUT_3D_SIZE³.");
            ParseTriple(line, data, filled);
            filled += 3;
        }

        if (data is null)
            throw new FormatException("No LUT_3D_SIZE header found.");
        if (filled != data.Length)
            throw new FormatException($"Expected {data.Length / 3} samples, found {filled / 3}.");
        return new CubeLut(size, data);
    }

    /// <summary>
    /// Packs the lattice into a 2D RGBA F16 image for GPU sampling: the N blue slices side by side
    /// (width N·N, height N), so slice b's texel (r, g) sits at (b·N + r, g). The transform shader does
    /// hardware bilinear within a slice and lerps between the two neighbouring slices (trilinear total).
    /// F16 keeps 10-bit-source precision without banding, stays linear-filterable on every GPU, and
    /// preserves the "Not-Clipped" vendor LUTs' slightly out-of-range samples.
    /// </summary>
    public SKImage ToPackedImage()
    {
        int n = Size;
        var info = new SKImageInfo(n * n, n, SKColorType.RgbaF16, SKAlphaType.Opaque);
        byte[] pixels = new byte[info.BytesSize];
        Span<Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(pixels.AsSpan());
        Half one = (Half)1f;

        for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
                for (int r = 0; r < n; r++)
                {
                    int src = 3 * (r + g * n + b * n * n);
                    int dst = 4 * ((b * n + r) + g * n * n);   // (x = b·N + r, y = g), 4 channels
                    halves[dst] = (Half)Data[src];
                    halves[dst + 1] = (Half)Data[src + 1];
                    halves[dst + 2] = (Half)Data[src + 2];
                    halves[dst + 3] = one;
                }

        return SKImage.FromPixelCopy(info, pixels, info.RowBytes)
            ?? throw new InvalidOperationException("Failed to create the packed LUT image.");
    }

    private static void ParseTriple(ReadOnlySpan<char> line, float[] data, int at)
    {
        for (int c = 0; c < 3; c++)
        {
            line = line.TrimStart();
            int end = 0;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
                end++;
            if (end == 0
                || !float.TryParse(line[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                throw new FormatException($"Malformed sample line at entry {at / 3}.");
            data[at + c] = v;
            line = line[end..];
        }
    }

    private static bool IsUniform(ReadOnlySpan<char> values, float expected)
    {
        int seen = 0;
        foreach (Range r in values.Split(' '))
        {
            ReadOnlySpan<char> token = values[r].Trim();
            if (token.IsEmpty)
                continue;
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                || Math.Abs(v - expected) > 1e-6f)
                return false;
            seen++;
        }
        return seen == 3;
    }
}
