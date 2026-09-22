using System;
using System.Collections.Generic;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The Day for Night grade (plan/features/special-effects.md, phase 4), rendered on the offscreen CPU backend
/// (the same SkSL the GPU runs) like <see cref="VfxPrimitiveEffectTests"/>. Each control is isolated by starting
/// from the <em>neutral</em> setting — every control off at full Night Strength, which must be a pass-through —
/// and turning one on, so an assertion pins that control's behaviour rather than the whole look's.
/// </summary>
public sealed class DayForNightEffectTests
{
    private const int Size = 32;

    private static readonly SKColor DaylightGrey = new(150, 150, 150, 255);
    private static readonly SKColor SkyBlue = new(135, 185, 235, 255);
    private static readonly SKColor Grass = new(70, 120, 50, 255);
    private static readonly SKColor Lamp = new(255, 190, 90, 255);
    private static readonly SKColor Skin = new(220, 160, 130, 255);

    // ── Master blend and neutrality ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Zero_Night_Strength_Is_A_Pass_Through_At_The_Default_Look()
    {
        using SKBitmap source = SkyOverGround();
        AssertSameAsNoEffect(source, Defaults((EffectParamNames.NightStrength, 0.0)), tolerance: 0);
    }

    [Fact]
    public void Every_Control_Off_Is_A_Pass_Through()
    {
        using SKBitmap source = SkyOverGround();
        AssertSameAsNoEffect(source, Neutral(), tolerance: 2);
    }

    [Fact]
    public void Night_Strength_Blends_Between_Day_And_Night()
    {
        using SKBitmap source = Solid(DaylightGrey);
        byte day = Render(source, [])[0].GetPixel(Size / 2, Size / 2).Red;
        byte half = RenderOne(source, Defaults((EffectParamNames.NightStrength, 0.5))).GetPixel(Size / 2, Size / 2).Red;
        byte night = RenderOne(source, Defaults()).GetPixel(Size / 2, Size / 2).Red;

        Assert.True(night < half && half < day, $"expected day {day} > half {half} > night {night}");
    }

    // ── The default look ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_Default_Look_Darkens_And_Cools_A_Daylight_Plate()
    {
        using SKBitmap source = Solid(DaylightGrey);
        SKColor night = RenderOne(source, Defaults()).GetPixel(Size / 2, Size / 2);

        Assert.True(Luma(night) < Luma(DaylightGrey) * 0.6, $"night should be much darker (got {night})");
        Assert.True(night.Blue > night.Red, $"night should carry a cool moonlight cast (got {night})");
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Exterior Wide")]
    [InlineData("Street Scene")]
    [InlineData("Blue Moon")]
    public void Every_Look_Turns_A_Daylit_Scene_Into_A_Darker_Cooler_One(string lookName)
    {
        EffectPreset look = EffectCatalog.Find(EffectTypeIds.DayForNight)!.FindPreset(lookName)!;
        var values = new List<(string, double)>();
        foreach ((string name, double value) in look.Values)
            values.Add((name, value));

        using SKBitmap source = SkyOverGround();
        using SKBitmap night = RenderOne(source, Effect(values.ToArray()));
        SKColor sky = night.GetPixel(Size / 2, 2);
        SKColor ground = night.GetPixel(Size / 2, Size - 3);

        Assert.True(Luma(sky) < Luma(SkyBlue) * 0.5, $"{lookName}: the sky should read as night (got {sky})");
        Assert.True(Luma(ground) < Luma(Grass), $"{lookName}: the ground should darken (got {ground})");
        Assert.True(sky.Blue >= sky.Red, $"{lookName}: the sky should stay cool (got {sky})");
    }

    // ── Individual controls ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Exposure_Underexposes_In_Stops()
    {
        using SKBitmap source = Solid(DaylightGrey);
        byte one = RenderOne(source, Neutral((EffectParamNames.Exposure, -1.0))).GetPixel(0, 0).Red;
        byte two = RenderOne(source, Neutral((EffectParamNames.Exposure, -2.0))).GetPixel(0, 0).Red;

        // −1 EV halves linear light: 150 (≈0.305 linear) → ≈0.153 linear → ≈108 sRGB.
        Assert.InRange(one, 104, 112);
        Assert.True(two < one);
    }

    [Fact]
    public void Sky_Darkens_A_Bright_Sky_In_The_Upper_Frame_And_Leaves_The_Ground()
    {
        using SKBitmap source = SkyOverGround();
        using SKBitmap without = RenderOne(source, Neutral((EffectParamNames.SkyDarken, 0.0)));
        using SKBitmap with = RenderOne(source, Neutral((EffectParamNames.SkyDarken, 1.0)));

        Assert.True(with.GetPixel(Size / 2, 2).Blue < without.GetPixel(Size / 2, 2).Blue - 60,
            "the top-of-frame sky should drop well below its un-darkened level");
        Assert.InRange(Math.Abs(with.GetPixel(Size / 2, Size - 3).Green - without.GetPixel(Size / 2, Size - 3).Green), 0, 1);
    }

    [Fact]
    public void Sky_Ignores_The_Same_Blue_Low_In_The_Frame()
    {
        // The key is weighted to the upper frame — the plan's "no semantic segmentation" compromise.
        using SKBitmap source = Solid(SkyBlue);
        using SKBitmap with = RenderOne(source, Neutral((EffectParamNames.SkyDarken, 1.0)));

        Assert.True(with.GetPixel(Size / 2, 1).Blue < with.GetPixel(Size / 2, Size - 1).Blue - 60,
            "the same colour should darken at the top and be spared at the bottom");
        Assert.InRange(with.GetPixel(Size / 2, Size - 1).Blue, SkyBlue.Blue - 1, SkyBlue.Blue);
    }

    [Fact]
    public void Sky_Is_Resolution_Independent()
    {
        using SKBitmap small = SkyOverGround();
        using SKBitmap large = SkyOverGround(Size * 2);
        using SKBitmap a = RenderOne(small, Neutral((EffectParamNames.SkyDarken, 1.0)));
        using SKBitmap b = RenderOne(large, Neutral((EffectParamNames.SkyDarken, 1.0)));

        // The same fractional position lands on the same key weight at either resolution.
        for (int y = 0; y < Size / 2; y += 3)
            Assert.InRange(Math.Abs(a.GetPixel(Size / 2, y).Blue - b.GetPixel(Size, y * 2 + 1).Blue), 0, 8);
    }

    [Fact]
    public void Highlights_Roll_Off_Whites_And_Leave_The_Mids()
    {
        using SKBitmap white = Solid(SKColors.White);
        using SKBitmap mid = Solid(new SKColor(90, 90, 90, 255));

        Assert.True(RenderOne(white, Neutral((EffectParamNames.HighlightRolloff, 1.0))).GetPixel(0, 0).Red < 225,
            "white should come down under the shoulder");
        AssertSameAsNoEffect(mid, Neutral((EffectParamNames.HighlightRolloff, 1.0)), tolerance: 2);
    }

    [Fact]
    public void Saturation_Pulls_Colour_Towards_Grey_About_Luma()
    {
        using SKBitmap source = Solid(Grass);
        SKColor grey = RenderOne(source, Neutral((EffectParamNames.Saturation, 0.0))).GetPixel(0, 0);

        Assert.InRange(grey.Red - grey.Blue, -1, 1);
        Assert.InRange(grey.Green - grey.Blue, -1, 1);
    }

    [Fact]
    public void Moonlight_Tint_Cools_The_Image_Without_Changing_Its_Brightness()
    {
        using SKBitmap source = Solid(new SKColor(110, 110, 110, 255));
        SKColor tinted = RenderOne(source, Neutral((EffectParamNames.MoonlightTint, 1.0))).GetPixel(0, 0);

        Assert.True(tinted.Blue > tinted.Red + 20, $"expected a blue cast (got {tinted})");
        // Unit-luma gains: the cast is chromatic, so linear luma is kept (a few sRGB codes of rounding drift).
        Assert.InRange(LinearLuma(tinted), LinearLuma(new SKColor(110, 110, 110)) - 0.01, LinearLuma(new SKColor(110, 110, 110)) + 0.01);
    }

    [Fact]
    public void Moonlight_Hue_Picks_The_Colour_Of_The_Cast()
    {
        using SKBitmap source = Solid(new SKColor(110, 110, 110, 255));
        SKColor teal = RenderOne(source, Neutral((EffectParamNames.MoonlightTint, 1.0), (EffectParamNames.MoonlightHue, 180.0))).GetPixel(0, 0);
        SKColor violet = RenderOne(source, Neutral((EffectParamNames.MoonlightTint, 1.0), (EffectParamNames.MoonlightHue, 260.0))).GetPixel(0, 0);

        Assert.True(teal.Green > violet.Green, $"teal {teal} should be greener than violet {violet}");
        Assert.True(violet.Red > teal.Red, $"violet {violet} should be redder than teal {teal}");
    }

    [Fact]
    public void Shadow_Floor_Lifts_Black_To_A_Tinted_Floor()
    {
        using SKBitmap source = Solid(SKColors.Black);
        SKColor neutralFloor = RenderOne(source, Neutral((EffectParamNames.ShadowFloor, 0.1))).GetPixel(0, 0);
        SKColor moonFloor = RenderOne(source, Neutral((EffectParamNames.ShadowFloor, 0.1), (EffectParamNames.MoonlightTint, 1.0))).GetPixel(0, 0);

        Assert.InRange(neutralFloor.Red, 24, 27); // 10% of full scale
        Assert.True(moonFloor.Blue > moonFloor.Red, $"the floor should take the moonlight cast (got {moonFloor})");
        using SKBitmap white = Solid(SKColors.White);
        Assert.InRange(RenderOne(white, Neutral((EffectParamNames.ShadowFloor, 0.1))).GetPixel(0, 0).Red, 254, 255);
    }

    [Fact]
    public void Vignette_Darkens_The_Corners_And_Spares_The_Centre()
    {
        using SKBitmap source = Solid(DaylightGrey);
        using SKBitmap night = RenderOne(source, Neutral((EffectParamNames.VignetteAmount, 1.0)));

        Assert.InRange(night.GetPixel(Size / 2, Size / 2).Red, DaylightGrey.Red - 1, DaylightGrey.Red + 1);
        Assert.True(night.GetPixel(0, 0).Red < DaylightGrey.Red - 60, "the corner should fall off");
    }

    [Fact]
    public void Practical_Lights_Keep_A_Warm_Lamp_Lit_Through_The_Grade()
    {
        using SKBitmap source = Solid(Lamp);
        SKColor dark = RenderOne(source, Defaults((EffectParamNames.PracticalLights, 0.0))).GetPixel(Size / 2, Size / 2);
        SKColor lit = RenderOne(source, Defaults((EffectParamNames.PracticalLights, 1.0))).GetPixel(Size / 2, Size / 2);

        Assert.True(Luma(lit) > Luma(dark) + 60, $"the lamp should stay lit (dark {dark}, lit {lit})");
        Assert.InRange(lit.Red, Lamp.Red - 3, Lamp.Red);
        Assert.True(lit.Red > lit.Blue + 100, $"the lamp should stay warm (got {lit})");
    }

    [Fact]
    public void Practical_Lights_Do_Not_Relight_A_Cool_Sky()
    {
        using SKBitmap source = Solid(SkyBlue);
        using SKBitmap off = RenderOne(source, Defaults((EffectParamNames.PracticalLights, 0.0)));
        using SKBitmap on = RenderOne(source, Defaults((EffectParamNames.PracticalLights, 1.0)));
        Assert.True(MaxDifference(off, on) <= 1, "a blue sky is not a practical");
    }

    [Fact]
    public void Protect_Skin_Restores_Natural_Colour_At_The_Night_Brightness()
    {
        using SKBitmap source = Solid(Skin);
        (string, double)[] grade = [(EffectParamNames.Exposure, -1.0), (EffectParamNames.Saturation, 0.0), (EffectParamNames.MoonlightTint, 1.0)];
        SKColor bare = RenderOne(source, Neutral([.. grade, (EffectParamNames.ProtectSkin, 0.0)])).GetPixel(0, 0);
        SKColor kept = RenderOne(source, Neutral([.. grade, (EffectParamNames.ProtectSkin, 1.0)])).GetPixel(0, 0);

        Assert.True(bare.Blue > bare.Red, $"unprotected, the face goes moonlight-blue (got {bare})");
        Assert.True(kept.Red > kept.Blue + 20, $"protected, the face keeps its warmth (got {kept})");
        Assert.InRange(Luma(kept), Luma(bare) - 6, Luma(bare) + 6); // colour back, brightness still night
    }

    [Fact]
    public void Protect_Skin_Leaves_Non_Skin_Colours_Alone()
    {
        using SKBitmap source = Solid(SkyBlue);
        using SKBitmap off = RenderOne(source, Defaults((EffectParamNames.ProtectSkin, 0.0)));
        using SKBitmap on = RenderOne(source, Defaults((EffectParamNames.ProtectSkin, 1.0)));
        Assert.True(MaxDifference(off, on) <= 1, "blue is not a skin tone");
    }

    [Fact]
    public void Practical_Lights_Do_Not_Restore_A_Sunlit_Face_To_Daylight()
    {
        // A face is bright and warm too; the practical key must not pull it back to its daylight level.
        using SKBitmap source = Solid(Skin);
        using SKBitmap off = RenderOne(source, Defaults((EffectParamNames.PracticalLights, 0.0)));
        using SKBitmap on = RenderOne(source, Defaults((EffectParamNames.PracticalLights, 1.0)));
        Assert.True(MaxDifference(off, on) <= 1, "skin is not a practical");
        Assert.True(Luma(on.GetPixel(Size / 2, Size / 2)) < Luma(Skin) * 0.6, "the face should be at night level");
    }

    [Fact]
    public void Partial_Alpha_Is_Graded_Unpremultiplied_And_Keeps_Its_Coverage()
    {
        // The same colour at 50% alpha must grade to the same unpremultiplied colour as the opaque pixel.
        var half = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        half.Erase(DaylightGrey.WithAlpha(128));
        using SKBitmap premul = new(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        half.CopyTo(premul, SKColorType.Rgba8888);
        half.Dispose();
        ResolvedEffect look = Defaults((EffectParamNames.ShadowFloor, 0.2), (EffectParamNames.VignetteAmount, 0.0));

        using SKBitmap opaque = RenderOne(Solid(DaylightGrey), look);
        using SKBitmap graded = Render(premul, [look], hasAlpha: true)[0];
        SKColor a = opaque.GetPixel(Size / 2, Size / 2), b = graded.GetPixel(Size / 2, Size / 2);

        Assert.InRange(b.Alpha, 127, 129);
        // GetPixel unpremultiplies over the surface's cleared-transparent background: compare the colour itself.
        Assert.InRange(Math.Abs(a.Red - b.Red), 0, 4);
        Assert.InRange(Math.Abs(a.Blue - b.Blue), 0, 4);
    }

    [Fact]
    public void Transparent_Pixels_Stay_Transparent()
    {
        using var source = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        source.Erase(SKColors.Transparent);
        using SKBitmap night = Render(source, [Defaults((EffectParamNames.ShadowFloor, 0.2))], hasAlpha: true)[0];
        Assert.Equal(0, night.GetPixel(Size / 2, Size / 2).Alpha);
        Assert.Equal(0, night.GetPixel(Size / 2, Size / 2).Blue);
    }

    // ── Effect builders ──────────────────────────────────────────────────────────────────────────────

    private static ResolvedEffect Effect(params (string Name, double Value)[] values)
    {
        var parameters = new Dictionary<string, double>();
        foreach ((string name, double value) in values)
            parameters[name] = value;
        return new ResolvedEffect(EffectTypeIds.DayForNight, parameters);
    }

    /// <summary>The descriptor's defaults (full strength), with overrides.</summary>
    private static ResolvedEffect Defaults(params (string Name, double Value)[] overrides)
    {
        var values = new List<(string, double)>();
        foreach (EffectParameterDescriptor p in EffectCatalog.Find(EffectTypeIds.DayForNight)!.Parameters)
            values.Add((p.Name, p.Default));
        values.AddRange(overrides);
        return Effect(values.ToArray());
    }

    /// <summary>Every control off at full strength — a pass-through — with overrides.</summary>
    private static ResolvedEffect Neutral(params (string Name, double Value)[] overrides)
    {
        (string, double)[] off =
        [
            (EffectParamNames.NightStrength, 1.0), (EffectParamNames.Exposure, 0.0), (EffectParamNames.SkyDarken, 0.0),
            (EffectParamNames.HighlightRolloff, 0.0), (EffectParamNames.ShadowFloor, 0.0), (EffectParamNames.Saturation, 1.0),
            (EffectParamNames.MoonlightTint, 0.0), (EffectParamNames.MoonlightHue, 215.0), (EffectParamNames.PracticalLights, 0.0),
            (EffectParamNames.ProtectSkin, 0.0), (EffectParamNames.VignetteAmount, 0.0),
        ];
        return Effect([.. off, .. overrides]);
    }

    // ── Sources ──────────────────────────────────────────────────────────────────────────────────────

    private static SKBitmap Solid(SKColor color, int size = Size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(color);
        return bitmap;
    }

    /// <summary>A daylight exterior in miniature: blue sky over the top half, grass below.</summary>
    private static SKBitmap SkyOverGround(int size = Size)
    {
        SKBitmap bitmap = Solid(SkyBlue, size);
        for (int y = size / 2; y < size; y++)
            for (int x = 0; x < size; x++)
                bitmap.SetPixel(x, y, Grass);
        return bitmap;
    }

    // ── Rendering / measurement ──────────────────────────────────────────────────────────────────────

    private static SKBitmap RenderOne(SKBitmap source, ResolvedEffect effect) => Render(source, [effect])[0];

    private static SKBitmap[] Render(SKBitmap source, IReadOnlyList<ResolvedEffect> effects, bool hasAlpha = false)
    {
        using var pipeline = new SkiaEffectPipeline();
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(source.Width, source.Height), source.GetPixels(),
            source.RowBytes, source.Width, source.Height, effects, hasAlpha: hasAlpha);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        return [SKBitmap.FromImage(image)];
    }

    private static double Luma(SKColor c) => 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;

    private static double LinearLuma(SKColor c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
    }

    /// <summary>The largest per-pixel, per-channel difference between two renders.</summary>
    private static int MaxDifference(SKBitmap a, SKBitmap b)
    {
        int max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                max = Math.Max(max, Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue))));
            }
        return max;
    }

    private static void AssertSameAsNoEffect(SKBitmap source, ResolvedEffect effect, int tolerance)
    {
        using SKBitmap plain = Render(source, [])[0];
        using SKBitmap through = Render(source, [effect])[0];
        int diff = MaxDifference(plain, through);
        Assert.True(diff <= tolerance, $"expected a pass-through (max difference {diff})");
    }
}
