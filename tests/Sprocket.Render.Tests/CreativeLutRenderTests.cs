using System.Text;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The Creative LUT effect's render stage (plan/features/looks-browser.md): a user <c>.cube</c> file loaded through
/// <see cref="CreativeLuts"/> and sampled by the packed-LUT shader with an Intensity blend, on the deterministic
/// offscreen raster path (the <see cref="ColorTransformRenderTests"/> pattern). A missing, relative, oversized or
/// malformed file must pass the frame through untouched (§15).
/// </summary>
public sealed class CreativeLutRenderTests : IDisposable
{
    private const int Size = 8;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sprocket-lut-tests-" + Guid.NewGuid().ToString("N"));

    public CreativeLutRenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        CreativeLuts.Clear();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // A 2³ lattice whose samples are f(r, g, b) at the corners — trilinear interpolation of a linear f is exact.
    private string WriteCube(string name, Func<float, float, float, (float, float, float)> f)
    {
        var text = new StringBuilder("TITLE \"test\"\nLUT_3D_SIZE 2\n");
        for (int b = 0; b < 2; b++)
            for (int g = 0; g < 2; g++)
                for (int r = 0; r < 2; r++)
                {
                    (float R, float G, float B) o = f(r, g, b);
                    text.Append(FormattableString.Invariant($"{o.R} {o.G} {o.B}\n"));
                }
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text.ToString());
        return path;
    }

    private string InvertCube() => WriteCube("invert.cube", (r, g, b) => (1 - r, 1 - g, 1 - b));

    [Fact]
    public void Identity_Lut_Leaves_The_Frame_Unchanged()
    {
        string path = WriteCube("identity.cube", (r, g, b) => (r, g, b));
        SKColor c = RenderCenter(new SKColor(200, 90, 30), Lut(path, 1.0));
        AssertNear(new SKColor(200, 90, 30), c, 2);
    }

    [Fact]
    public void Invert_Lut_Inverts_And_Intensity_Blends()
    {
        string path = InvertCube();
        AssertNear(new SKColor(55, 165, 225), RenderCenter(new SKColor(200, 90, 30), Lut(path, 1.0)), 2);
        // Halfway: every channel lands at mid-grey for an invert (x + (1 − x)) / 2.
        AssertNear(new SKColor(128, 128, 128), RenderCenter(new SKColor(200, 90, 30), Lut(path, 0.5)), 2);
    }

    [Fact]
    public void Zero_Intensity_Is_A_Pass_Through()
    {
        string path = InvertCube();
        AssertNear(new SKColor(200, 90, 30), RenderCenter(new SKColor(200, 90, 30), Lut(path, 0.0)), 1);
    }

    [Fact]
    public void Preserves_Alpha()
    {
        string path = InvertCube();
        SKColor c = RenderLayerCenter(new SKColor(200, 90, 30, 128), hasAlpha: true, Lut(path, 1.0));
        Assert.Equal(128, c.Alpha);
    }

    [Fact]
    public void Missing_Or_No_File_Passes_Through_And_Is_Flagged()
    {
        string missing = Path.Combine(_dir, "nope.cube");
        AssertNear(new SKColor(200, 90, 30), RenderCenter(new SKColor(200, 90, 30), Lut(missing, 1.0)), 1);
        Assert.True(CreativeLuts.HasFailed(missing));

        var noFile = new ResolvedEffect(EffectTypeIds.CreativeLut, new Dictionary<string, double> { [EffectParamNames.Mix] = 1.0 });
        AssertNear(new SKColor(200, 90, 30), RenderCenter(new SKColor(200, 90, 30), [noFile]), 1);
    }

    [Fact]
    public void Relative_Paths_Are_Refused()
    {
        Assert.False(CreativeLuts.TryGet("looks/warm.cube", out _, out _));
        Assert.True(CreativeLuts.HasFailed("looks/warm.cube"));
    }

    [Fact]
    public void Malformed_And_Oversized_Lattices_Fail_Without_Throwing()
    {
        string bad = Path.Combine(_dir, "bad.cube");
        File.WriteAllText(bad, "not a lut at all");
        Assert.False(CreativeLuts.TryGet(bad, out _, out _));

        // A header claiming a huge lattice is rejected before the table is allocated.
        string huge = Path.Combine(_dir, "huge.cube");
        File.WriteAllText(huge, "LUT_3D_SIZE 250\n0 0 0\n");
        Assert.False(CreativeLuts.TryGet(huge, out _, out _));
        Assert.Contains("LUT_3D_SIZE", CreativeLuts.Error(huge));
    }

    [Fact]
    public void Loads_Once_And_Reloads_After_Invalidate()
    {
        string path = InvertCube();
        Assert.True(CreativeLuts.TryGet(path, out SKImage a, out int size));
        Assert.Equal(2, size);
        Assert.True(CreativeLuts.TryGet(path, out SKImage again, out _));
        Assert.Same(a, again);

        CreativeLuts.Invalidate(path);
        Assert.True(CreativeLuts.TryGet(path, out SKImage reloaded, out _));
        Assert.NotSame(a, reloaded);
    }

    [Fact]
    public void Parse_Honours_The_Max_Size_Cap() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_3D_SIZE 33\n", maxSize: 17));

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ResolvedEffect> Lut(string path, double intensity) =>
    [
        new ResolvedEffect(EffectTypeIds.CreativeLut,
            new Dictionary<string, double> { [EffectParamNames.Mix] = intensity },
            new Dictionary<string, string> { [EffectParamNames.LutFile] = path }),
    ];

    private static void AssertNear(SKColor expected, SKColor actual, int tolerance)
    {
        Assert.InRange(actual.Red, expected.Red - tolerance, expected.Red + tolerance);
        Assert.InRange(actual.Green, expected.Green - tolerance, expected.Green + tolerance);
        Assert.InRange(actual.Blue, expected.Blue - tolerance, expected.Blue + tolerance);
    }

    private static SKColor RenderCenter(SKColor source, IReadOnlyList<ResolvedEffect> effects) =>
        RenderLayerCenter(source, hasAlpha: false, effects);

    private static SKColor RenderLayerCenter(SKColor source, bool hasAlpha, IReadOnlyList<ResolvedEffect> effects)
    {
        using var pipeline = new SkiaEffectPipeline();
        var alphaType = hasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
        using var src = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, alphaType));
        src.Erase(source);
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(Size, Size), src.GetPixels(), src.RowBytes, Size, Size, effects, hasAlpha: hasAlpha);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(image);
        return readback.GetPixel(Size / 2, Size / 2);
    }
}
