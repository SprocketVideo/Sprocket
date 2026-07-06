using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Render-side tests for the math-based (non-DJI) input color transform profiles of PLAN.md step 52: the GPU
/// curve shader (<c>ColorTransformCurveSksl</c> in <see cref="SkiaEffectPipeline"/>) on the deterministic
/// offscreen raster path, cross-checked against the Core-side reference math
/// (<see cref="ColorProfileCurves"/>) that <see cref="Sprocket.Core.Tests.ColorTransformCurveTests"/> already
/// validates against each vendor's own published formula — this file proves the SkSL float32 port agrees
/// with that reference, not that the formulas themselves are correct.
/// </summary>
public class ColorTransformCurveRenderTests
{
    private const int Size = 8;

    private static readonly string[] MathProfiles =
    [
        ColorProfiles.ArriLogC3, ColorProfiles.ArriLogC4, ColorProfiles.SonySLog3, ColorProfiles.PanasonicVLog,
        ColorProfiles.CanonCLog3, ColorProfiles.BlackmagicFilm, ColorProfiles.FujifilmFLog2, ColorProfiles.NikonNLog,
    ];

    public static TheoryData<string> MathProfileData() => new(MathProfiles);

    [Theory]
    [MemberData(nameof(MathProfileData))]
    public void Shader_Matches_The_Core_Reference_Math_At_A_Mid_Gray_Code_Value(string profile)
    {
        const byte code = 110; // ~0.43 normalized — mid-tone territory for all of these curves
        int p = ColorProfiles.IndexOf(profile);
        SKColor rendered = RenderCenter(new SKColor(code, code, code), Transform(p));

        (byte r, byte g, byte b) = ExpectedRgb(profile, code / 255.0);

        // +/-4 code values of slack: float32 shader vs double Core reference, plus 8-bit quantization.
        Assert.InRange(rendered.Red, Math.Max(0, r - 4), Math.Min(255, r + 4));
        Assert.InRange(rendered.Green, Math.Max(0, g - 4), Math.Min(255, g + 4));
        Assert.InRange(rendered.Blue, Math.Max(0, b - 4), Math.Min(255, b + 4));
    }

    [Theory]
    [MemberData(nameof(MathProfileData))]
    public void Shader_Matches_The_Core_Reference_Math_Near_Black_And_Near_White(string profile)
    {
        int p = ColorProfiles.IndexOf(profile);
        foreach (byte code in new byte[] { 10, 245 })
        {
            SKColor rendered = RenderCenter(new SKColor(code, code, code), Transform(p));
            (byte r, byte g, byte b) = ExpectedRgb(profile, code / 255.0);
            Assert.InRange(rendered.Red, Math.Max(0, r - 4), Math.Min(255, r + 4));
            Assert.InRange(rendered.Green, Math.Max(0, g - 4), Math.Min(255, g + 4));
            Assert.InRange(rendered.Blue, Math.Max(0, b - 4), Math.Min(255, b + 4));
        }
    }

    [Theory]
    [MemberData(nameof(MathProfileData))]
    public void Transform_Is_Monotonic_On_Neutrals(string profile)
    {
        int p = ColorProfiles.IndexOf(profile);
        int previous = -1;
        for (int g = 0; g <= 240; g += 40)
        {
            SKColor c = RenderCenter(new SKColor((byte)g, (byte)g, (byte)g), Transform(p));
            Assert.True(c.Red + 2 >= previous, $"{profile}: brightness must not decrease along the gray ramp (at {g})");
            previous = c.Red;
        }
    }

    [Fact]
    public void Transform_Preserves_Alpha_And_Chains_With_A_Grade()
    {
        int p = ColorProfiles.IndexOf(ColorProfiles.ArriLogC3);

        SKColor c = RenderLayerCenter(new SKColor(110, 110, 110, 128), hasAlpha: true, Transform(p));
        Assert.Equal(128, c.Alpha);

        var chain = new List<ResolvedEffect>
        {
            TransformEffect(p),
            new(EffectTypeIds.Brightness, new Dictionary<string, double> { [EffectParamNames.Amount] = 0.5 }),
        };
        SKColor graded = RenderCenter(new SKColor(110, 110, 110), chain);
        SKColor ungraded = RenderCenter(new SKColor(110, 110, 110), Transform(p));
        Assert.InRange(graded.Red, ungraded.Red / 2 - 3, ungraded.Red / 2 + 3);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static (byte R, byte G, byte B) ExpectedRgb(string profile, double v)
    {
        double linear = ColorProfileCurves.Decode(profile, v);
        (double r, double g, double b) = ColorProfileCurves.GamutOf(profile).Apply(linear, linear, linear);
        return (ToByte(r), ToByte(g), ToByte(b));

        static byte ToByte(double linear) =>
            (byte)Math.Clamp(Math.Round(ColorProfileCurves.EncodeRec709(linear) * 255.0), 0, 255);
    }

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
