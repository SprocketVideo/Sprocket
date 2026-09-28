using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Mosaic (<c>builtin.mosaic</c>, plan/features/toy-cassette-camera.md phase 1) — a registry SkSL stage
/// rendered on the offscreen CPU backend like <see cref="VfxPrimitiveEffectTests"/>, over patterned sources
/// (a flat colour hides a block grid). Also compiles the shared <c>SkslSnippets</c> fragments the later
/// toy-cassette-camera stages build on.
/// </summary>
public sealed class MosaicEffectTests
{
    private const int Size = 32;

    [Fact]
    public void SharpColors_Fills_Every_Block_With_One_Colour()
    {
        using SKBitmap source = DiagonalRamp();
        using SKBitmap mosaic = Render(source, [Mosaic(4, 4, sharp: true)]);

        // 4×4 blocks of 8×8 pixels: every pixel matches its block's top-left pixel.
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                Assert.Equal(mosaic.GetPixel(x - x % 8, y - y % 8), mosaic.GetPixel(x, y));
    }

    [Fact]
    public void Averaged_Blocks_Are_Also_Uniform_And_Hold_The_Block_Mean()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap mosaic = Render(source, [Mosaic(4, 1, sharp: false)]);

        for (int block = 0; block < 4; block++)
        {
            double mean = Enumerable.Range(block * 8, 8).Average(x => source.GetPixel(x, 0).Red);
            for (int x = block * 8; x < block * 8 + 8; x++)
                Assert.InRange(mosaic.GetPixel(x, Size / 2).Red, mean - 2, mean + 2);
        }
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(4, false)]
    [InlineData(16, false)]
    public void Block_Count_Across_A_Ramp_Matches_Horizontal_Blocks(int blocks, bool sharp)
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap mosaic = Render(source, [Mosaic(blocks, 3, sharp)]);
        Assert.Equal(blocks, Runs(mosaic, Size / 2));
    }

    [Fact]
    public void Vertical_Blocks_Divide_The_Rows()
    {
        using SKBitmap source = DiagonalRamp();
        using SKBitmap mosaic = Render(source, [Mosaic(1, 4, sharp: true)]);

        // One column of blocks, four rows: the value is constant along x and changes three times down y.
        int changes = 0;
        for (int y = 1; y < Size; y++)
        {
            Assert.Equal(mosaic.GetPixel(0, y), mosaic.GetPixel(Size - 1, y));
            if (mosaic.GetPixel(0, y) != mosaic.GetPixel(0, y - 1))
                changes++;
        }
        Assert.Equal(3, changes);
    }

    [Theory]
    [InlineData(true, 0.0)]
    [InlineData(false, 0.0)]
    [InlineData(false, 0.5)]
    public void One_Block_Per_Pixel_Is_A_Pass_Through(bool sharp, double softness)
    {
        using SKBitmap source = DiagonalRamp();
        using SKBitmap plain = Render(source, []);
        using SKBitmap mosaic = Render(source, [Mosaic(Size, Size, sharp, softness)]);
        Assert.True(MaxDifference(plain, mosaic) <= 1,
            $"a one-pixel grid should leave the image alone (max difference {MaxDifference(plain, mosaic)})");
    }

    [Fact]
    public void Edge_Softness_Blends_Across_Block_Borders()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap hard = Render(source, [Mosaic(4, 1, sharp: true)]);
        using SKBitmap soft = Render(source, [Mosaic(4, 1, sharp: true, softness: 1.0)]);

        // Hard blocks are four flat plateaus; soft ones grade between block centres, so the row has many steps.
        Assert.Equal(4, Runs(hard, Size / 2));
        Assert.True(Runs(soft, Size / 2) > 8, $"soft edges should grade across borders (got {Runs(soft, Size / 2)} runs)");
        // Either side of the 7|8 border, the soft render sits between the two hard block values.
        byte left = hard.GetPixel(7, Size / 2).Red, right = hard.GetPixel(8, Size / 2).Red;
        Assert.InRange(soft.GetPixel(7, Size / 2).Red, left + 1, right - 1);
        Assert.InRange(soft.GetPixel(8, Size / 2).Red, left + 1, right - 1);
    }

    [Fact]
    public void The_Grid_Tracks_The_Layer_Rect_Not_The_Canvas()
    {
        // The layer is drawn 8 pixels in from the canvas's left edge; the block borders must follow it.
        using SKBitmap source = HorizontalRamp();
        const int offset = 8;
        using SKBitmap mosaic = Render(source, [Mosaic(4, 1, sharp: true)], canvasWidth: Size + offset * 2,
            dest: SKRect.Create(offset, 0, Size, Size));

        for (int x = offset; x < offset + Size; x++)
        {
            int blockStart = offset + (x - offset) / 8 * 8;
            Assert.Equal(mosaic.GetPixel(blockStart, Size / 2), mosaic.GetPixel(x, Size / 2));
        }
        Assert.NotEqual(mosaic.GetPixel(offset + 7, Size / 2), mosaic.GetPixel(offset + 8, Size / 2));
    }

    [Fact]
    public void Mosaic_Is_A_Pure_Function_Of_Its_Input()
    {
        using SKBitmap source = DiagonalRamp();
        using SKBitmap a = Render(source, [Mosaic(5, 3, sharp: false, softness: 0.4)]);
        using SKBitmap b = Render(source, [Mosaic(5, 3, sharp: false, softness: 0.4)]);
        Assert.Equal(0, MaxDifference(a, b));
    }

    [Fact]
    public void Fractional_Block_Counts_Round_To_Whole_Blocks()
    {
        // An eased keyframe or an MCP value can land between counts — the grid still uses whole blocks.
        using SKBitmap source = HorizontalRamp();
        using SKBitmap mosaic = Render(source, [Mosaic(4.4, 1, sharp: true)]);
        Assert.Equal(4, Runs(mosaic, Size / 2));
    }

    [Fact]
    public void The_Shared_Sksl_Snippets_Compile_Together()
    {
        // SkslSnippets is internal to Sprocket.Render; read its fragments reflectively so every one is compiled
        // here even before a later phase's effect concatenates it.
        Type snippets = typeof(SkiaEffectPipeline).Assembly.GetType("Sprocket.Render.Effects.SkslSnippets", throwOnError: true)!;
        string[] fragments = [.. snippets.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];
        Assert.Equal(3, fragments.Length);

        string program = "uniform shader src;\n" + string.Concat(fragments) + @"
half4 main(float2 coord) {
    float4 r = float4(0.0, 0.0, 100.0, 100.0);
    float2 cell = gridClampCell(floor(gridPosition(coord, r, float2(4.0))), float2(4.0));
    float v = cellHash(cell) * rec709Luma(float3(1.0)) + REC709_LUMA.x * 0.0;
    return half4(src.eval(gridCellPoint(cell, float2(0.5), r, float2(4.0))).rgb * half(v), 1.0);
}";
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(program, out string? errors);
        Assert.True(effect is not null, $"the snippets should compile: {errors}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static ResolvedEffect Mosaic(double horizontal, double vertical, bool sharp, double softness = 0.0) =>
        new(EffectTypeIds.Mosaic, new Dictionary<string, double>
        {
            [EffectParamNames.HorizontalBlocks] = horizontal,
            [EffectParamNames.VerticalBlocks] = vertical,
            [EffectParamNames.SharpColors] = sharp ? 1.0 : 0.0,
            [EffectParamNames.EdgeSoftness] = softness,
        });

    /// <summary>A neutral left-to-right grey ramp: every column has its own value.</summary>
    private static SKBitmap HorizontalRamp() => Pattern((x, _) => (byte)(x * 255 / (Size - 1)));

    /// <summary>A grey ramp that rises along both axes, so blocks differ in x and in y.</summary>
    private static SKBitmap DiagonalRamp() => Pattern((x, y) => (byte)((x + y * 2) * 255 / (3 * (Size - 1))));

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

    private static SKBitmap Render(SKBitmap source, IReadOnlyList<ResolvedEffect> effects,
        int? canvasWidth = null, SKRect? dest = null)
    {
        using var pipeline = new SkiaEffectPipeline();
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(canvasWidth ?? source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, dest ?? SKRect.Create(source.Width, source.Height), source.GetPixels(),
            source.RowBytes, source.Width, source.Height, effects, hasAlpha: false);
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

    /// <summary>The largest per-pixel red-channel difference between two renders.</summary>
    private static int MaxDifference(SKBitmap a, SKBitmap b)
    {
        int max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                max = Math.Max(max, Math.Abs(a.GetPixel(x, y).Red - b.GetPixel(x, y).Red));
        return max;
    }
}
