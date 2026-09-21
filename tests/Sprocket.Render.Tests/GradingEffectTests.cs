using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The colour grading toolset (PLAN.md step 34): white balance, lift/gamma/gain wheels, parametric
/// curves, the HSL qualifier — all registry-backed SkSL stages — plus the Color effect's vibrance.
/// Rendered on the offscreen CPU backend (the same SkSL the GPU runs) with centre-pixel assertions,
/// following <see cref="RegisteredEffectTests"/>.
/// </summary>
public sealed class GradingEffectTests
{
    private const int Size = 8;

    // ── White balance (builtin.whitebalance) ─────────────────────────────────────────────────────────

    [Fact]
    public void WhiteBalance_Neutral_IsPassThrough()
    {
        SKColor c = RenderCenter(new SKColor(128, 128, 128, 255), [WhiteBalance(0, 0)]);
        Assert.InRange(c.Red, 126, 130);
        Assert.InRange(c.Green, 126, 130);
        Assert.InRange(c.Blue, 126, 130);
    }

    [Fact]
    public void WhiteBalance_Warm_ShiftsRedUpBlueDown()
    {
        SKColor c = RenderCenter(new SKColor(128, 128, 128, 255), [WhiteBalance(100, 0)]);
        Assert.True(c.Red > 132, $"warm should raise red (got {c.Red})");
        Assert.True(c.Blue < 124, $"warm should lower blue (got {c.Blue})");
    }

    [Fact]
    public void WhiteBalance_Cool_ShiftsBlueUpRedDown()
    {
        SKColor c = RenderCenter(new SKColor(128, 128, 128, 255), [WhiteBalance(-100, 0)]);
        Assert.True(c.Blue > 132, $"cool should raise blue (got {c.Blue})");
        Assert.True(c.Red < 124, $"cool should lower red (got {c.Red})");
    }

    [Fact]
    public void WhiteBalance_MagentaTint_LowersGreen()
    {
        SKColor c = RenderCenter(new SKColor(128, 128, 128, 255), [WhiteBalance(0, 100)]);
        Assert.True(c.Green < 124, $"positive tint should lower green (got {c.Green})");
        Assert.True(c.Red > c.Green && c.Blue > c.Green, "positive tint pushes toward magenta");
    }

    [Fact]
    public void WhiteBalance_GainsAreLumaNormalised()
    {
        // A grey card keeps its brightness through the correction (chromatic-only gains).
        SKColor c = RenderCenter(new SKColor(128, 128, 128, 255), [WhiteBalance(100, 0)]);
        double luma = 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;
        Assert.InRange(luma, 128 - 6, 128 + 6);
    }

    // ── Lift / gamma / gain wheels (builtin.colorwheels) ─────────────────────────────────────────────

    [Fact]
    public void Wheels_Neutral_IsPassThrough()
    {
        SKColor c = RenderCenter(new SKColor(100, 100, 100, 255), [Wheels()]);
        Assert.InRange(c.Red, 98, 102);
    }

    [Fact]
    public void Wheels_Lift_RaisesBlacks_LeavesWhite()
    {
        SKColor black = RenderCenter(new SKColor(0, 0, 0, 255), [Wheels((EffectParamNames.LiftMaster, 0.2))]);
        SKColor white = RenderCenter(new SKColor(255, 255, 255, 255), [Wheels((EffectParamNames.LiftMaster, 0.2))]);
        Assert.True(black.Red > 40, $"lift should raise black ({black.Red})");
        Assert.InRange(white.Red, 253, 255); // lift pivots on white
    }

    [Fact]
    public void Wheels_Gain_ScalesHighlights_LeavesBlack()
    {
        SKColor black = RenderCenter(new SKColor(0, 0, 0, 255), [Wheels((EffectParamNames.GainMaster, -0.5))]);
        SKColor bright = RenderCenter(new SKColor(200, 200, 200, 255), [Wheels((EffectParamNames.GainMaster, -0.5))]);
        Assert.InRange(black.Red, 0, 2); // gain pivots on black
        Assert.InRange(bright.Red, 100 - 4, 100 + 4); // 200 × 0.5
    }

    [Fact]
    public void Wheels_Gamma_BrightensMids_KeepsEnds()
    {
        var gamma = Wheels((EffectParamNames.GammaMaster, 0.5));
        SKColor black = RenderCenter(new SKColor(0, 0, 0, 255), [gamma]);
        SKColor mid = RenderCenter(new SKColor(128, 128, 128, 255), [gamma]);
        SKColor white = RenderCenter(new SKColor(255, 255, 255, 255), [gamma]);
        Assert.InRange(black.Red, 0, 2);
        Assert.True(mid.Red > 140, $"positive gamma should brighten mids ({mid.Red})");
        Assert.InRange(white.Red, 253, 255);
    }

    [Fact]
    public void Wheels_ChannelComponent_GradesOnlyThatChannel()
    {
        SKColor c = RenderCenter(new SKColor(0, 0, 0, 255), [Wheels((EffectParamNames.LiftR, 0.3))]);
        Assert.True(c.Red > 50, $"red lift should raise red ({c.Red})");
        Assert.InRange(c.Green, 0, 2);
        Assert.InRange(c.Blue, 0, 2);
    }

    // ── Parametric curves (builtin.curves) ───────────────────────────────────────────────────────────

    [Fact]
    public void Curves_AllZero_IsPassThrough()
    {
        SKColor c = RenderCenter(new SKColor(90, 140, 200, 255), [Curves()]);
        Assert.InRange(c.Red, 88, 92);
        Assert.InRange(c.Green, 138, 142);
        Assert.InRange(c.Blue, 198, 202);
    }

    [Fact]
    public void Curves_MasterMids_LiftMidGray_KeepsEnds()
    {
        var mids = Curves((EffectParamNames.CurveMasterMids, 0.2));
        SKColor black = RenderCenter(new SKColor(0, 0, 0, 255), [mids]);
        SKColor mid = RenderCenter(new SKColor(128, 128, 128, 255), [mids]);
        SKColor white = RenderCenter(new SKColor(255, 255, 255, 255), [mids]);
        Assert.InRange(black.Red, 0, 2);   // the blacks point is untouched
        Assert.InRange(mid.Red, 178 - 6, 178 + 6); // 0.5 + 0.2 → ~0.7
        Assert.InRange(white.Red, 253, 255);
    }

    [Fact]
    public void Curves_ChannelCurve_GradesOnlyThatChannel()
    {
        SKColor c = RenderCenter(new SKColor(128, 128, 128, 255), [Curves((EffectParamNames.CurveBlueMids, -0.3))]);
        Assert.InRange(c.Red, 126, 130);
        Assert.InRange(c.Green, 126, 130);
        Assert.True(c.Blue < 70, $"blue mids -0.3 should darken blue ({c.Blue})");
    }

    // ── HSL qualifier (builtin.hsl.qualify) ──────────────────────────────────────────────────────────

    [Fact]
    public void Qualifier_KeyedHue_IsGraded()
    {
        // Key red (hue 0 ± 30°) and desaturate it fully.
        SKColor c = RenderCenter(new SKColor(200, 40, 40, 255), [Qualifier(
            (EffectParamNames.HueCenter, 0.0), (EffectParamNames.HueWidth, 30.0),
            (EffectParamNames.Saturation, 0.0))]);
        Assert.InRange(Math.Abs(c.Red - c.Green), 0, 8); // keyed red is now (near) neutral
        Assert.InRange(Math.Abs(c.Green - c.Blue), 0, 8);
    }

    [Fact]
    public void Qualifier_UnkeyedHue_PassesThrough()
    {
        // The same red key must not touch a blue pixel.
        SKColor c = RenderCenter(new SKColor(40, 40, 200, 255), [Qualifier(
            (EffectParamNames.HueCenter, 0.0), (EffectParamNames.HueWidth, 30.0),
            (EffectParamNames.Saturation, 0.0))]);
        Assert.InRange(c.Blue, 196, 204);
        Assert.InRange(c.Red, 36, 44);
    }

    [Fact]
    public void Qualifier_ShowMask_PreviewsTheKey()
    {
        var mask = new (string, double)[]
        {
            (EffectParamNames.HueCenter, 0.0), (EffectParamNames.HueWidth, 30.0),
            (EffectParamNames.ShowMask, 1.0),
        };
        SKColor keyed = RenderCenter(new SKColor(200, 40, 40, 255), [Qualifier(mask)]);
        SKColor unkeyed = RenderCenter(new SKColor(40, 40, 200, 255), [Qualifier(mask)]);
        Assert.True(keyed.Red > 240, $"keyed pixel should preview white ({keyed.Red})");
        Assert.InRange(unkeyed.Red, 0, 8); // outside the key previews black
    }

    [Fact]
    public void Qualifier_HueShift_RotatesOnlyKeyedPixels()
    {
        // Shift keyed red +120° → green.
        SKColor c = RenderCenter(new SKColor(200, 40, 40, 255), [Qualifier(
            (EffectParamNames.HueCenter, 0.0), (EffectParamNames.HueWidth, 30.0),
            (EffectParamNames.HueShift, 120.0))]);
        Assert.True(c.Green > c.Red && c.Green > c.Blue, $"shifted red should read green ({c.Red},{c.Green},{c.Blue})");
    }

    // ── Color vibrance (builtin.color, PLAN.md step 34 addition) ─────────────────────────────────────

    [Fact]
    public void Vibrance_BoostsMutedColorsMoreThanSaturatedOnes()
    {
        var vib = new ResolvedEffect(EffectTypeIds.Color, new Dictionary<string, double>
        {
            [EffectParamNames.Vibrance] = 1.0,
            [EffectParamNames.Contrast] = 1.0,
            [EffectParamNames.Saturation] = 1.0,
        });
        SKColor muted = RenderCenter(new SKColor(150, 120, 120, 255), [vib]);
        SKColor saturated = RenderCenter(new SKColor(255, 0, 0, 255), [vib]);

        Assert.True(muted.Red - muted.Green > 40, $"vibrance should spread a muted colour ({muted.Red},{muted.Green})");
        Assert.InRange(saturated.Red, 250, 255); // an already-saturated colour barely moves
        Assert.InRange(saturated.Green, 0, 5);
    }

    [Fact]
    public void Vibrance_Absent_IsPassThrough()
    {
        // An old project's Color instance has no vibrance parameter — the fallback must be neutral.
        var legacy = new ResolvedEffect(EffectTypeIds.Color, new Dictionary<string, double>
        {
            [EffectParamNames.Contrast] = 1.0,
            [EffectParamNames.Saturation] = 1.0,
        });
        SKColor c = RenderCenter(new SKColor(150, 120, 120, 255), [legacy]);
        Assert.InRange(c.Red, 148, 152);
        Assert.InRange(c.Green, 118, 122);
    }

    // ── Black & White, phase 1 (builtin.blackwhite) ─────────────────────────────────────────────────────

    [Fact]
    public void BlackWhite_Neutral_IsMonochromeAtRec709Luma()
    {
        var source = new SKColor(200, 40, 40, 255);
        SKColor c = RenderCenter(source, [BlackWhite()]);
        Assert.InRange(Math.Abs(c.Red - c.Green), 0, 1); // monochrome: R = G = B
        Assert.InRange(Math.Abs(c.Green - c.Blue), 0, 1);
        Assert.InRange(c.Red - MonoLuma(source), -2, 2); // matches a linear-light Rec.709 desaturation
    }

    [Fact]
    public void BlackWhite_MixZero_IsPassThrough()
    {
        var source = new SKColor(200, 40, 40, 255);
        SKColor c = RenderCenter(source, [BlackWhite((EffectParamNames.Mix, 0.0))]);
        Assert.InRange(c.Red, 198, 202);
        Assert.InRange(c.Green, 38, 42);
        Assert.InRange(c.Blue, 38, 42);
    }

    [Fact]
    public void BlackWhite_RedFilter_DarkensBlue_BrightensRed_KeepsGreyLuma()
    {
        var filter = new (string, double)[] { (EffectParamNames.FilterHue, 0.0), (EffectParamNames.FilterStrength, 0.8) };
        SKColor blue = RenderCenter(new SKColor(0, 0, 255, 255), [BlackWhite(filter)]);
        SKColor red = RenderCenter(new SKColor(255, 0, 0, 255), [BlackWhite(filter)]);
        Assert.True(blue.Red < 40, $"a red filter should darken blue ({blue.Red})");
        Assert.True(red.Red > 200, $"a red filter should brighten red ({red.Red})");

        // The filter is luma-normalised, so a neutral grey keeps its brightness.
        var grey = new SKColor(128, 128, 128, 255);
        SKColor filtered = RenderCenter(grey, [BlackWhite(filter)]);
        Assert.InRange(filtered.Red - MonoLuma(grey), -3, 3);
    }

    [Fact]
    public void BlackWhite_MixReds_LightensRed_LeavesGreyUntouched()
    {
        SKColor redNeutral = RenderCenter(new SKColor(200, 40, 40, 255), [BlackWhite()]);
        SKColor redLifted = RenderCenter(new SKColor(200, 40, 40, 255), [BlackWhite((EffectParamNames.MixReds, 100.0))]);
        Assert.True(redLifted.Red > redNeutral.Red + 10, $"MixReds +100 should lighten red ({redNeutral.Red}→{redLifted.Red})");

        // Saturation gating: a neutral grey has no hue, so the mixer must leave it alone.
        SKColor greyNeutral = RenderCenter(new SKColor(128, 128, 128, 255), [BlackWhite()]);
        SKColor greyLifted = RenderCenter(new SKColor(128, 128, 128, 255), [BlackWhite((EffectParamNames.MixReds, 100.0))]);
        Assert.InRange(greyLifted.Red - greyNeutral.Red, -1, 1);
    }

    [Theory]
    [InlineData(EffectParamNames.Contrast, 1.6)]
    [InlineData(EffectParamNames.Shadows, 0.4)]
    [InlineData(EffectParamNames.Highlights, 0.4)]
    public void BlackWhite_ToneControls_AreMonotonicOnARamp(string param, double value)
    {
        int[] steps = { 0, 64, 128, 191, 255 };
        var outputs = steps
            .Select(v => RenderCenter(new SKColor((byte)v, (byte)v, (byte)v, 255), [BlackWhite((param, value))]).Red)
            .ToArray();
        for (int i = 1; i < outputs.Length; i++)
            Assert.True(outputs[i] >= outputs[i - 1], $"{param}={value} broke monotonicity at step {i}: {string.Join(",", outputs)}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    // The neutral mono value the effect targets: Rec.709 luma computed in linear light, re-encoded to sRGB.
    private static double MonoLuma(SKColor c)
    {
        static double ToLinear(double s) => s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        static double ToSrgb(double l) => l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
        double y = 0.2126 * ToLinear(c.Red / 255.0) + 0.7152 * ToLinear(c.Green / 255.0) + 0.0722 * ToLinear(c.Blue / 255.0);
        return ToSrgb(y) * 255.0;
    }

    private static ResolvedEffect BlackWhite(params (string Name, double Value)[] values)
    {
        var parameters = new Dictionary<string, double>
        {
            [EffectParamNames.Mix] = 1.0,
            [EffectParamNames.FilterHue] = 0.0,
            [EffectParamNames.FilterStrength] = 0.0,
            [EffectParamNames.Exposure] = 0.0,
            [EffectParamNames.Contrast] = 1.0,
            [EffectParamNames.Shadows] = 0.0,
            [EffectParamNames.Highlights] = 0.0,
        };
        foreach ((string name, double value) in values)
            parameters[name] = value;
        return new ResolvedEffect(EffectTypeIds.BlackWhite, parameters);
    }

    private static ResolvedEffect WhiteBalance(double temperature, double tint) =>
        new(EffectTypeIds.WhiteBalance, new Dictionary<string, double>
        {
            [EffectParamNames.Temperature] = temperature,
            [EffectParamNames.Tint] = tint,
        });

    private static ResolvedEffect Wheels(params (string Name, double Value)[] values) =>
        Effect(EffectTypeIds.ColorWheels, values);

    private static ResolvedEffect Curves(params (string Name, double Value)[] values) =>
        Effect(EffectTypeIds.Curves, values);

    private static ResolvedEffect Qualifier(params (string Name, double Value)[] values)
    {
        // Defaults matching the descriptor, overridden by the given values.
        var parameters = new Dictionary<string, double>
        {
            [EffectParamNames.HueCenter] = 0.0,
            [EffectParamNames.HueWidth] = 60.0,
            [EffectParamNames.HueSoftness] = 20.0,
            [EffectParamNames.SatLow] = 0.0,
            [EffectParamNames.SatHigh] = 1.0,
            [EffectParamNames.LumaLow] = 0.0,
            [EffectParamNames.LumaHigh] = 1.0,
            [EffectParamNames.RangeSoftness] = 0.1,
            [EffectParamNames.HueShift] = 0.0,
            [EffectParamNames.Saturation] = 1.0,
            [EffectParamNames.Exposure] = 0.0,
            [EffectParamNames.ShowMask] = 0.0,
        };
        foreach ((string name, double value) in values)
            parameters[name] = value;
        return new ResolvedEffect(EffectTypeIds.HslQualifier, parameters);
    }

    private static ResolvedEffect Effect(string id, (string Name, double Value)[] values)
    {
        var parameters = new Dictionary<string, double>();
        foreach ((string name, double value) in values)
            parameters[name] = value;
        return new ResolvedEffect(id, parameters);
    }

    private static SKColor RenderCenter(SKColor source, IReadOnlyList<ResolvedEffect> effects)
    {
        using var pipeline = new SkiaEffectPipeline();
        using var src = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        src.Erase(source);
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(Size, Size), src.GetPixels(), src.RowBytes, Size, Size, effects, hasAlpha: false);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(image);
        return readback.GetPixel(Size / 2, Size / 2);
    }
}
