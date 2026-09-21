namespace Sprocket.Analysis.Features;

/// <summary>
/// A fixed-depth Gaussian image pyramid (level 0 = full resolution, each higher level halved). Built
/// with a separable 5-tap Gaussian (<c>[1 4 6 4 1] / 16</c>) then decimated 2:1. Owns its level
/// buffers and reuses them across <see cref="Build"/> calls when the source dimensions are unchanged,
/// so steady-state analysis allocates nothing.
/// </summary>
public sealed class ImagePyramid
{
    /// <summary>Number of pyramid levels (level 0 plus 3 halvings), matching the tracker's depth.</summary>
    public const int LevelCount = 4;

    private readonly byte[]?[] _levels = new byte[LevelCount][];
    private readonly int[] _width = new int[LevelCount];
    private readonly int[] _height = new int[LevelCount];
    private byte[] _scratch = [];   // row-blur workspace, reused

    /// <summary>True once <see cref="Build"/> has populated the levels at least once.</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>Width of the given pyramid level.</summary>
    public int WidthAt(int level) => _width[level];

    /// <summary>Height of the given pyramid level.</summary>
    public int HeightAt(int level) => _height[level];

    /// <summary>A read-only view of the given level (0 = full resolution).</summary>
    public GrayImage Level(int level)
    {
        byte[]? data = _levels[level];
        if (data is null)
            throw new InvalidOperationException("Pyramid has not been built.");
        int w = _width[level];
        return new GrayImage(data, w, _height[level], w);
    }

    /// <summary>
    /// (Re)builds the pyramid from a full-resolution source. Level buffers are allocated on the first
    /// build (or when the source size changes) and reused otherwise.
    /// </summary>
    public void Build(GrayImage source)
    {
        EnsureBuffers(source.Width, source.Height);

        // Level 0 is a tight (stride == width) copy of the source so downsampling has a packed input.
        byte[] level0 = _levels[0]!;
        for (int y = 0; y < source.Height; y++)
            source.Pixels.Slice(y * source.Stride, source.Width).CopyTo(level0.AsSpan(y * source.Width, source.Width));

        for (int i = 1; i < LevelCount; i++)
            Downsample(_levels[i - 1]!, _width[i - 1], _height[i - 1], _levels[i]!, _width[i], _height[i]);

        IsBuilt = true;
    }

    private void EnsureBuffers(int w, int h)
    {
        if (IsBuilt && _width[0] == w && _height[0] == h)
            return;

        int scratchNeeded = w * h;
        for (int i = 0; i < LevelCount; i++)
        {
            _width[i] = w;
            _height[i] = h;
            int size = w * h;
            if (_levels[i] is null || _levels[i]!.Length < size)
                _levels[i] = new byte[size];
            // Next level halves each dimension (floor, min 1).
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        if (_scratch.Length < scratchNeeded)
            _scratch = new byte[scratchNeeded];
    }

    /// <summary>
    /// Separable 5-tap Gaussian blur of <paramref name="src"/> followed by 2:1 decimation into
    /// <paramref name="dst"/>. Horizontal pass writes into the shared scratch buffer, vertical pass
    /// reads scratch and decimates on the fly. Edges use clamped (replicated) samples.
    /// </summary>
    private void Downsample(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        byte[] tmp = _scratch;

        // Horizontal blur: full-resolution rows, packed into tmp (stride == sw).
        for (int y = 0; y < sh; y++)
        {
            int row = y * sw;
            for (int x = 0; x < sw; x++)
            {
                int xm2 = x - 2 < 0 ? 0 : x - 2;
                int xm1 = x - 1 < 0 ? 0 : x - 1;
                int xp1 = x + 1 >= sw ? sw - 1 : x + 1;
                int xp2 = x + 2 >= sw ? sw - 1 : x + 2;
                int sum = src[row + xm2] + 4 * src[row + xm1] + 6 * src[row + x] + 4 * src[row + xp1] + src[row + xp2];
                tmp[row + x] = (byte)((sum + 8) >> 4);
            }
        }

        // Vertical blur + decimate: sample every second row/column of the horizontally-blurred image.
        for (int y = 0; y < dh; y++)
        {
            int sy = y * 2;
            int ym2 = sy - 2 < 0 ? 0 : sy - 2;
            int ym1 = sy - 1 < 0 ? 0 : sy - 1;
            int yp1 = sy + 1 >= sh ? sh - 1 : sy + 1;
            int yp2 = sy + 2 >= sh ? sh - 1 : sy + 2;
            int rm2 = ym2 * sw, rm1 = ym1 * sw, r0 = sy * sw, rp1 = yp1 * sw, rp2 = yp2 * sw;
            int drow = y * dw;
            for (int x = 0; x < dw; x++)
            {
                int sx = x * 2;
                int sum = tmp[rm2 + sx] + 4 * tmp[rm1 + sx] + 6 * tmp[r0 + sx] + 4 * tmp[rp1 + sx] + tmp[rp2 + sx];
                dst[drow + x] = (byte)((sum + 8) >> 4);
            }
        }
    }
}
