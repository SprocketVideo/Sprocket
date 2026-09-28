using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Toy Cassette Camera (<c>builtin.toycam</c>, plan/features/toy-cassette-camera.md phase 3) — a registry SkSL
/// stage rendered on the offscreen CPU backend like <see cref="MosaicEffectTests"/>. The layer is a 64×64
/// square, so the 4:3 picture window is 64×48 before the border inset.
/// </summary>
public sealed class ToyCamEffectTests
{
    private const int Size = 64;

    [Fact]
    public void The_Border_Is_Black_And_The_Picture_Is_Not()
    {
        using SKBitmap source = Flat(new SKColor(150, 150, 150, 255));
        using SKBitmap toy = Render(source, [ToyCam()]);

        // Corners and the top/bottom bands sit outside the 4:3 window; the centre is inside it.
        foreach ((int x, int y) in new[] { (0, 0), (Size - 1, 0), (0, Size - 1), (Size - 1, Size - 1), (Size / 2, 1), (Size / 2, Size - 2), (1, Size / 2) })
        {
            SKColor c = toy.GetPixel(x, y);
            Assert.Equal((byte)0, c.Red);
            Assert.Equal((byte)0, c.Green);
            Assert.Equal((byte)0, c.Blue);
            Assert.Equal((byte)255, c.Alpha); // an opaque layer's border stays opaque black
        }
        Assert.True(toy.GetPixel(Size / 2, Size / 2).Red > 40, "the picture window should show the image");
    }

    [Fact]
    public void Border_Size_Grows_The_Border()
    {
        using SKBitmap source = Flat(new SKColor(150, 150, 150, 255));
        using SKBitmap thin = Render(source, [ToyCam((EffectParamNames.BorderSize, 0.0), (EffectParamNames.BorderSoftness, 0.0))]);
        using SKBitmap thick = Render(source, [ToyCam((EffectParamNames.BorderSize, 0.4), (EffectParamNames.BorderSoftness, 0.0))]);

        // Border 0: the window is the full 64×48 fit, so x = 4 on the centre row is picture. Border 40%: the
        // window is 38.4 wide, so x = 4 is black.
        Assert.True(thin.GetPixel(4, Size / 2).Red > 40);
        Assert.Equal((byte)0, thick.GetPixel(4, Size / 2).Red);
    }

    [Fact]
    public void The_Output_Is_Grey()
    {
        using SKBitmap source = ColourPattern();
        using SKBitmap toy = Render(source, [ToyCam((EffectParamNames.NoiseLines, 1.0), (EffectParamNames.Dropouts, 1.0))]);
        bool anyLit = false;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                SKColor c = toy.GetPixel(x, y);
                Assert.Equal(c.Red, c.Green);
                Assert.Equal(c.Red, c.Blue);
                anyLit |= c.Red > 0;
            }
        Assert.True(anyLit);
    }

    [Fact]
    public void Same_Frame_Time_Is_Identical_And_A_Different_Time_Moves_The_Noise_Lines()
    {
        using SKBitmap source = Flat(new SKColor(90, 90, 90, 255));
        // Grain off so the only time-varying stage is the tape noise.
        (string, double)[] lines = [(EffectParamNames.NoiseLines, 1.0), (EffectParamNames.Dropouts, 0.0), (EffectParamNames.GrainAmount, 0.0)];
        using SKBitmap a = Render(source, [ToyCam(lines) with { FrameTime = Ticks(1.0) }]);
        using SKBitmap b = Render(source, [ToyCam(lines) with { FrameTime = Ticks(1.0) }]);
        using SKBitmap c = Render(source, [ToyCam(lines) with { FrameTime = Ticks(1.0 + 1.0 / 15.0) }]);

        Assert.Equal(0, Difference(a, b));
        Assert.True(Difference(a, c) > 0, "a later frame should draw its noise lines on different rows");
    }

    [Fact]
    public void Seed_Changes_The_Pattern()
    {
        using SKBitmap source = Flat(new SKColor(120, 120, 120, 255));
        using SKBitmap a = Render(source, [ToyCam((EffectParamNames.Seed, 0.0))]);
        using SKBitmap b = Render(source, [ToyCam((EffectParamNames.Seed, 1.0))]);
        using SKBitmap again = Render(source, [ToyCam((EffectParamNames.Seed, 1.0))]);
        Assert.True(Difference(a, b) > 0, "a different seed should give a different noise pattern");
        Assert.Equal(0, Difference(b, again));
    }

    [Fact]
    public void Picture_Pixels_Form_A_Grid()
    {
        // Hard pixels, no noise: across the 64-wide window, 8 picture pixels are 8 flat runs of a ramp.
        using SKBitmap source = HorizontalRamp();
        using SKBitmap toy = Render(source, [Quiet(
            (EffectParamNames.HorizontalPixels, 8.0), (EffectParamNames.VerticalPixels, 6.0),
            (EffectParamNames.BorderSize, 0.0), (EffectParamNames.Contrast, 1.0), (EffectParamNames.BlackCrush, 0.0))]);
        Assert.Equal(8, Runs(toy, Size / 2));
    }

    [Fact]
    public void Smear_Trails_Bright_Pixels_To_The_Right_Only()
    {
        // A white bar on black: with smear, the picture pixels to the bar's right light up; its left stays dark.
        using SKBitmap source = Pattern((x, _) => x is >= 24 and < 32 ? (byte)255 : (byte)0);
        (string, double)[] grid = [(EffectParamNames.HorizontalPixels, 16.0), (EffectParamNames.VerticalPixels, 12.0), (EffectParamNames.BorderSize, 0.0)];
        using SKBitmap none = Render(source, [Quiet([.. grid, (EffectParamNames.SmearLength, 0.0)])]);
        using SKBitmap smear = Render(source, [Quiet([.. grid, (EffectParamNames.SmearLength, 1.0)])]);

        int row = Size / 2;
        Assert.Equal((byte)0, none.GetPixel(42, row).Red);
        Assert.True(smear.GetPixel(42, row).Red > 60, $"the trail should reach right of the bar (got {smear.GetPixel(42, row).Red})");
        Assert.True(smear.GetPixel(34, row).Red > smear.GetPixel(50, row).Red, "the trail fades with distance");
        Assert.Equal(none.GetPixel(18, row).Red, smear.GetPixel(18, row).Red);
    }

    [Fact]
    public void Black_Crush_Takes_Dark_Greys_To_Black()
    {
        using SKBitmap source = Flat(new SKColor(60, 60, 60, 255));
        using SKBitmap mild = Render(source, [Quiet((EffectParamNames.BlackCrush, 0.0), (EffectParamNames.Contrast, 1.0))]);
        using SKBitmap crushed = Render(source, [Quiet((EffectParamNames.BlackCrush, 0.3), (EffectParamNames.Contrast, 1.0))]);
        Assert.True(mild.GetPixel(Size / 2, Size / 2).Red > 40);
        Assert.Equal((byte)0, crushed.GetPixel(Size / 2, Size / 2).Red);
    }

    [Fact]
    public void Output_Is_Premultiplied_With_Rgb_Not_Above_Alpha()
    {
        // A half-transparent white layer (straight RGBA in, as the decoder hands alpha layers over), with bloom and
        // dropouts pushing toward white: the stored premultiplied grey must never exceed its alpha.
        using SKBitmap source = new(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        source.Erase(new SKColor(255, 255, 255, 128));
        using SKBitmap toy = Render(source, [ToyCam((EffectParamNames.HighlightBloom, 1.0), (EffectParamNames.Dropouts, 1.0))], hasAlpha: true);

        ReadOnlySpan<byte> px = toy.GetPixelSpan(); // raw premultiplied RGBA8888
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = y * toy.RowBytes + x * 4;
                Assert.True(px[i] <= px[i + 3], $"premultiplied grey {px[i]} exceeds alpha {px[i + 3]} at ({x}, {y})");
            }
        int centre = Size / 2 * toy.RowBytes + Size / 2 * 4;
        Assert.InRange(px[centre + 3], 120, 136);
        Assert.True(px[centre] > 100, "the white layer should render bright, at its own alpha");
    }

    [Fact]
    public void Every_Preset_Binds_And_Renders()
    {
        using SKBitmap source = ColourPattern();
        foreach (EffectPreset preset in ToyCamPresets.All)
        {
            var overrides = preset.Values.Select(kv => (kv.Key, kv.Value)).ToArray();
            using SKBitmap toy = Render(source, [ToyCam(overrides)]); // no throw ⇒ compiles + binds + renders
            SKColor centre = toy.GetPixel(Size / 2, Size / 2);
            Assert.Equal(centre.Red, centre.Green);
            Assert.Equal((byte)0, toy.GetPixel(0, 0).Red);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static long Ticks(double seconds) => (long)Math.Round(seconds * Timecode.TicksPerSecond);

    /// <summary>The descriptor defaults, then <paramref name="values"/> on top.</summary>
    private static ResolvedEffect ToyCam(params (string Name, double Value)[] values)
    {
        EffectDescriptor descriptor = EffectCatalog.Find(EffectTypeIds.ToyCam)!;
        var parameters = descriptor.Parameters.ToDictionary(p => p.Name, p => p.Default);
        foreach ((string name, double value) in values)
            parameters[name] = value;
        return new ResolvedEffect(EffectTypeIds.ToyCam, parameters);
    }

    /// <summary>Defaults with every random and softening stage off (hard pixels, no bloom/noise/grain/feather).</summary>
    private static ResolvedEffect Quiet(params (string Name, double Value)[] values) => ToyCam(
    [
        (EffectParamNames.PixelSoftness, 0.0), (EffectParamNames.HighlightBloom, 0.0), (EffectParamNames.SmearLength, 0.0),
        (EffectParamNames.NoiseLines, 0.0), (EffectParamNames.Dropouts, 0.0), (EffectParamNames.GrainAmount, 0.0),
        (EffectParamNames.BorderSoftness, 0.0), .. values,
    ]);

    private static SKBitmap Flat(SKColor colour)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(colour);
        return bitmap;
    }

    private static SKBitmap HorizontalRamp() => Pattern((x, _) => (byte)(x * 255 / (Size - 1)));

    private static SKBitmap Pattern(Func<int, int, byte> value)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                byte v = value(x, y);
                bitmap.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        return bitmap;
    }

    /// <summary>Saturated colours varying along both axes — a colour image the stage must render grey.</summary>
    private static SKBitmap ColourPattern()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 4), (byte)(255 - y * 4), (byte)((x + y) * 2), 255));
        return bitmap;
    }

    private static SKBitmap Render(SKBitmap source, IReadOnlyList<ResolvedEffect> effects, bool hasAlpha = false)
    {
        using var pipeline = new SkiaEffectPipeline();
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(source.Width, source.Height), source.GetPixels(),
            source.RowBytes, source.Width, source.Height, effects, hasAlpha: hasAlpha);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>The number of runs of equal red value along row <paramref name="y"/>.</summary>
    private static int Runs(SKBitmap bitmap, int y)
    {
        int runs = 1;
        for (int x = 1; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Red != bitmap.GetPixel(x - 1, y).Red)
                runs++;
        return runs;
    }

    /// <summary>The number of pixels whose red channel differs between two renders.</summary>
    private static int Difference(SKBitmap a, SKBitmap b)
    {
        int count = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y).Red != b.GetPixel(x, y).Red)
                    count++;
        return count;
    }
}
