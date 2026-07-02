using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Render-side tests for the input color transform of PLAN.md step 37: the <c>.cube</c> parser, the
/// packed-LUT texture layout, and the GPU LUT stage end-to-end on the offscreen raster path. The D-Log
/// expectations are cross-checked against DJI's published whitepaper math (linearise the log code, apply
/// the D-Gamut→Rec.709 matrix — identity on neutrals — then Rec.709-encode), with generous tolerances
/// since the vendor LUT includes its own highlight handling.
/// </summary>
public class ColorTransformRenderTests
{
    private const int Size = 8;

    // ── CubeLut parsing ─────────────────────────────────────────────────────────────────────────────

    private const string TinyCube = @"
# comment
TITLE ""tiny""
LUT_3D_SIZE 2
DOMAIN_MIN 0.0 0.0 0.0
DOMAIN_MAX 1.0 1.0 1.0
0 0 0
1 0 0
0 1 0
1 1 0
0 0 1
1 0 1
0 1 1
1 1 1
";

    [Fact]
    public void Parse_Reads_A_Tiny_Identity_Cube()
    {
        CubeLut lut = CubeLut.Parse(TinyCube);
        Assert.Equal(2, lut.Size);
        Assert.Equal((0f, 0f, 0f), lut.Sample(0, 0, 0));
        Assert.Equal((1f, 0f, 0f), lut.Sample(1, 0, 0)); // red index fastest
        Assert.Equal((0f, 1f, 0f), lut.Sample(0, 1, 0));
        Assert.Equal((0f, 0f, 1f), lut.Sample(0, 0, 1));
        Assert.Equal((1f, 1f, 1f), lut.Sample(1, 1, 1));
    }

    [Theory]
    [InlineData("0 0 0\n1 1 1\n")]                             // data with no size header
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n")]                     // too few samples
    [InlineData("LUT_3D_SIZE 2\nDOMAIN_MAX 0.9 0.9 0.9\n")]    // non-identity domain
    [InlineData("LUT_1D_SIZE 4\n")]                            // 1D tables unsupported
    [InlineData("LUT_3D_SIZE 1\n0 0 0\n")]                     // degenerate size
    public void Parse_Rejects_Malformed_Cubes(string text) =>
        Assert.Throws<FormatException>(() => CubeLut.Parse(text));

    [Fact]
    public void Packed_Image_Lays_Blue_Slices_Side_By_Side()
    {
        CubeLut lut = CubeLut.Parse(TinyCube);
        using SKImage image = lut.ToPackedImage();
        Assert.Equal(4, image.Width);  // N·N
        Assert.Equal(2, image.Height); // N
        Assert.Equal(SKColorType.RgbaF16, image.ColorType);
    }

    [Fact]
    public void Bundled_DJI_Luts_Load_And_Cache()
    {
        Assert.True(ColorLuts.TryGet(ColorProfiles.IndexOf(ColorProfiles.DjiDLog), out SKImage a, out int sizeA));
        Assert.True(ColorLuts.TryGet(ColorProfiles.IndexOf(ColorProfiles.DjiDLogM), out SKImage b, out int sizeB));
        Assert.Equal(33, sizeA); // DJI ships 33³ tables
        Assert.Equal(33, sizeB);
        Assert.True(ColorLuts.TryGet(0, out SKImage again, out _));
        Assert.Same(a, again); // process-lifetime cache, never re-decoded
        Assert.NotSame(a, b);
    }

    // ── The GPU stage on the deterministic raster path ──────────────────────────────────────────────

    [Theory]
    [InlineData(ColorProfiles.DjiDLog)]
    [InlineData(ColorProfiles.DjiDLogM)]
    public void Log_Endpoints_Map_To_Display_Black_And_White(string profile)
    {
        int p = ColorProfiles.IndexOf(profile);
        SKColor black = RenderCenter(new SKColor(0, 0, 0), Transform(p));
        SKColor white = RenderCenter(new SKColor(255, 255, 255), Transform(p));
        Assert.True(black.Red < 20, $"log 0 should map near black, got {black.Red}");
        Assert.True(white.Red > 225, $"log 1 should map near white, got {white.Red}");
    }

    [Fact]
    public void DLog_Grays_Track_The_Whitepaper_Math()
    {
        int p = ColorProfiles.IndexOf(ColorProfiles.DjiDLog);

        // 18% gray: D-Log encodes it to ≈0.399 (code 408/1023); the whitepaper math maps that back to
        // linear 0.18 → Rec.709-encoded ≈0.409. Allow a wide band for the vendor LUT's own tone handling.
        SKColor mid = RenderCenter(new SKColor(102, 102, 102), Transform(p)); // 102/255 ≈ 0.400
        Assert.InRange(mid.Red, 71, 140); // 0.28 … 0.55

        // Log lifts shadows, so the inverse transform pushes a low log code well below its input…
        SKColor shadow = RenderCenter(new SKColor(51, 51, 51), Transform(p)); // 0.2 → linear ≈0.021 → ≈0.10
        Assert.True(shadow.Red < 46, $"a low log code should darken, got {shadow.Red}");

        // …and a high log code (0.75 → linear ≈4.4, super-white) lands at/near display white.
        SKColor bright = RenderCenter(new SKColor(191, 191, 191), Transform(p));
        Assert.True(bright.Red > 217, $"a high log code should be near white, got {bright.Red}");
    }

    [Theory]
    [InlineData(ColorProfiles.DjiDLog)]
    [InlineData(ColorProfiles.DjiDLogM)]
    public void Transform_Is_Monotonic_On_Neutrals(string profile)
    {
        int p = ColorProfiles.IndexOf(profile);
        int previous = -1;
        for (int g = 0; g <= 240; g += 40)
        {
            SKColor c = RenderCenter(new SKColor((byte)g, (byte)g, (byte)g), Transform(p));
            Assert.True(c.Red + 2 >= previous, $"brightness must not decrease along the gray ramp (at {g})");
            previous = c.Red;
        }
    }

    [Fact]
    public void Transform_Preserves_Alpha_And_Chains_With_A_Grade()
    {
        // Semi-transparent source: the transform must not disturb coverage.
        SKColor c = RenderLayerCenter(new SKColor(102, 102, 102, 128), hasAlpha: true,
            Transform(ColorProfiles.IndexOf(ColorProfiles.DjiDLog)));
        Assert.Equal(128, c.Alpha);

        // Chained: transform first, then a brightness grade on the display-referred result.
        var chain = new List<ResolvedEffect>
        {
            TransformEffect(0),
            new(EffectTypeIds.Brightness, new Dictionary<string, double> { [EffectParamNames.Amount] = 0.5 }),
        };
        SKColor graded = RenderCenter(new SKColor(102, 102, 102), chain);
        SKColor ungraded = RenderCenter(new SKColor(102, 102, 102), Transform(0));
        Assert.InRange(graded.Red, ungraded.Red / 2 - 3, ungraded.Red / 2 + 3);
    }

    // ── Helpers (the RegisteredEffectTests offscreen-raster pattern) ────────────────────────────────

    private static ResolvedEffect TransformEffect(int profile) =>
        new(EffectTypeIds.ColorTransform,
            new Dictionary<string, double> { [EffectParamNames.SourceProfile] = profile });

    private static IReadOnlyList<ResolvedEffect> Transform(int profile) => [TransformEffect(profile)];

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
