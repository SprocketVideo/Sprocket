using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Offscreen-raster goldens for the rich title renderer (PLAN.md step 40): bundled deterministic fonts,
/// multi-line wrap, alignment, stroke/shadow/background box, the lower third template, the typewriter
/// reveal, and the Roll/Crawl scroll driven by clip-local progress. Follows the step-19 generator-test
/// discipline: render on a CPU surface, read pixels back, probe.
/// </summary>
public class TitleRendererTests
{
    private const int W = 128;
    private const int H = 128;

    private static ResolvedGenerator Title(
        Dictionary<string, string>? strings = null,
        Dictionary<string, double>? values = null,
        double progress = 0.0,
        string typeId = GeneratorTypeIds.Title)
    {
        var s = strings ?? new Dictionary<string, string>();
        s.TryAdd(GeneratorParamNames.Text, "X");
        s.TryAdd(GeneratorParamNames.Color, "#FFFFFFFF");
        var v = values ?? new Dictionary<string, double>();
        v.TryAdd(GeneratorParamNames.FontSize, 0.3);
        return new ResolvedGenerator(typeId, s, v, progress);
    }

    private static SKBitmap Render(ResolvedGenerator generator)
    {
        var bitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        TitleRenderer.Draw(canvas, generator, W, H);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Bounding box of pixels with alpha above a threshold, or null when none.</summary>
    private static SKRectI? LitBounds(SKBitmap bmp, byte minAlpha = 32)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha >= minAlpha)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
        return left == int.MaxValue ? null : new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static int LitCount(SKBitmap bmp, byte minAlpha = 32)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha >= minAlpha)
                    n++;
        return n;
    }

    [Fact]
    public void Single_Line_Centres_In_The_Frame()
    {
        using SKBitmap bmp = Render(Title());
        SKRectI bounds = LitBounds(bmp)!.Value;
        Assert.InRange(bounds.MidX, W / 2 - 6, W / 2 + 6);
        Assert.InRange(bounds.MidY, H / 2 - 8, H / 2 + 8);
    }

    [Fact]
    public void MultiLine_Wraps_Taller_Than_Single_Line()
    {
        using SKBitmap one = Render(Title(new() { [GeneratorParamNames.Text] = "XX" }));
        using SKBitmap two = Render(Title(new() { [GeneratorParamNames.Text] = "XX\nXX" }));
        Assert.True(LitBounds(two)!.Value.Height > LitBounds(one)!.Value.Height * 1.5,
            "two hard lines should stack vertically");
    }

    [Fact]
    public void Long_Text_Word_Wraps_Within_The_Frame()
    {
        // Six words that cannot fit one line at this size must wrap instead of overflowing horizontally.
        using SKBitmap bmp = Render(Title(
            new() { [GeneratorParamNames.Text] = "XX XX XX XX XX XX" },
            new() { [GeneratorParamNames.FontSize] = 0.2 }));
        SKRectI bounds = LitBounds(bmp)!.Value;
        Assert.True(bounds.Width <= W * 0.95, "wrapped text stays inside the frame width");
        Assert.True(bounds.Height > 0.2 * H * 1.5, "wrapping produced multiple lines");
    }

    [Fact]
    public void Alignment_Left_And_Right_Shift_The_Short_Line()
    {
        var text = new Dictionary<string, string> { [GeneratorParamNames.Text] = "XXXX\nX" };
        using SKBitmap left = Render(Title(new(text) { [GeneratorParamNames.Alignment] = "left" }));
        using SKBitmap right = Render(Title(new(text) { [GeneratorParamNames.Alignment] = "right" }));

        // Probe the short line's band (below the block middle): its lit span sits left vs right of centre.
        static int RowMid(SKBitmap bmp, int y0, int y1)
        {
            int min = int.MaxValue, max = int.MinValue;
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).Alpha >= 32)
                    {
                        min = Math.Min(min, x);
                        max = Math.Max(max, x);
                    }
            return (min + max) / 2;
        }

        SKRectI bounds = LitBounds(left)!.Value;
        int bandTop = bounds.Top + bounds.Height / 2;
        Assert.True(RowMid(left, bandTop, bounds.Bottom) < RowMid(right, bandTop, bounds.Bottom) - 10,
            "the short second line lands left-aligned vs right-aligned");
    }

    [Fact]
    public void Stroke_Adds_Coloured_Outline_Pixels()
    {
        using SKBitmap bmp = Render(Title(
            new()
            {
                [GeneratorParamNames.Color] = "#FFFFFFFF",
                [GeneratorParamNames.StrokeColor] = "#FFFF0000",
            },
            new() { [GeneratorParamNames.StrokeWidth] = 0.02 }));

        bool sawRed = false;
        for (int y = 0; y < H && !sawRed; y++)
            for (int x = 0; x < W && !sawRed; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                sawRed = c.Alpha > 128 && c.Red > 180 && c.Green < 80;
            }
        Assert.True(sawRed, "a red outline should surround the white glyph");
    }

    [Fact]
    public void Shadow_Extends_The_Lit_Area_Down_Right()
    {
        using SKBitmap plain = Render(Title());
        using SKBitmap shadowed = Render(Title(
            new() { [GeneratorParamNames.ShadowColor] = "#FF000000" },
            new()
            {
                [GeneratorParamNames.ShadowOffsetX] = 0.03,
                [GeneratorParamNames.ShadowOffsetY] = 0.03,
                [GeneratorParamNames.ShadowBlur] = 0.01,
            }));

        SKRectI p = LitBounds(plain)!.Value;
        SKRectI s = LitBounds(shadowed)!.Value;
        Assert.True(s.Right > p.Right + 1 && s.Bottom > p.Bottom + 1, "the offset shadow extends the coverage");
    }

    [Fact]
    public void Background_Box_Fills_A_Padded_Rect_Behind_The_Text()
    {
        using SKBitmap plain = Render(Title());
        using SKBitmap boxed = Render(Title(
            new() { [GeneratorParamNames.BoxColor] = "#FF0000FF" },
            new() { [GeneratorParamNames.BoxPadding] = 0.05 }));

        SKRectI p = LitBounds(plain)!.Value;
        SKRectI b = LitBounds(boxed)!.Value;
        Assert.True(b.Left < p.Left - 2 && b.Right > p.Right + 2, "the box pads beyond the glyphs");
        // A corner of the box (outside the glyph) is the box colour.
        SKColor corner = boxed.GetPixel(b.Left + 1, b.Top + 1);
        Assert.True(corner.Blue > 180 && corner.Red < 80, "box corner is the box colour");
    }

    [Fact]
    public void Position_Moves_The_Block()
    {
        using SKBitmap low = Render(Title(
            values: new()
            {
                [GeneratorParamNames.FontSize] = 0.2,
                [GeneratorParamNames.PositionX] = 0.25,
                [GeneratorParamNames.PositionY] = 0.8,
            }));
        SKRectI bounds = LitBounds(low)!.Value;
        Assert.True(bounds.MidX < W / 2 && bounds.MidY > H / 2, "block follows positionX/Y");
    }

    [Fact]
    public void LowerThird_Template_Renders_Two_Fields_Over_A_Bar()
    {
        GeneratorSpec spec = GeneratorCatalog.BuiltIns.Single(d => d.Id == GeneratorTypeIds.LowerThird).CreateSpec();
        ResolvedGenerator resolved = RenderGraph.ResolveGenerator(spec, Sprocket.Core.Timing.Timecode.Zero);
        using SKBitmap bmp = Render(resolved);
        SKRectI bounds = LitBounds(bmp)!.Value;
        Assert.True(bounds.MidY > H / 2, "the lower third sits in the lower half");
        Assert.True(bounds.MidX < W / 2, "anchored left of centre");
    }

    // ── Typewriter reveal ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reveal_Zero_Draws_Nothing_And_Half_Draws_Less_Than_Full()
    {
        var text = new Dictionary<string, string> { [GeneratorParamNames.Text] = "XXXXXXXX" };
        using SKBitmap none = Render(Title(new(text), new() { [GeneratorParamNames.RevealFraction] = 0.0, [GeneratorParamNames.FontSize] = 0.15 }));
        using SKBitmap half = Render(Title(new(text), new() { [GeneratorParamNames.RevealFraction] = 0.5, [GeneratorParamNames.FontSize] = 0.15 }));
        using SKBitmap full = Render(Title(new(text), new() { [GeneratorParamNames.FontSize] = 0.15 }));

        Assert.Null(LitBounds(none));
        int h = LitCount(half), f = LitCount(full);
        Assert.True(h > 0 && h < f * 0.75, $"half reveal ({h}) should draw materially less than full ({f})");
    }

    // ── Roll / Crawl scroll (clip-local progress drives the offset) ─────────────────────────────────

    private static ResolvedGenerator Roll(double progress) => Title(
        new() { [GeneratorParamNames.ScrollMode] = TitleScrollModes.Roll },
        new() { [GeneratorParamNames.FontSize] = 0.2 },
        progress,
        GeneratorTypeIds.Roll);

    [Fact]
    public void Roll_Starts_And_Ends_OffScreen_And_Passes_The_Middle()
    {
        using SKBitmap start = Render(Roll(0.0));
        using SKBitmap mid = Render(Roll(0.5));
        using SKBitmap end = Render(Roll(1.0));

        Assert.Null(LitBounds(start));
        Assert.Null(LitBounds(end));
        SKRectI bounds = LitBounds(mid)!.Value;
        Assert.InRange(bounds.MidY, H / 2 - 12, H / 2 + 12);
    }

    [Fact]
    public void Roll_Moves_Up_As_Progress_Advances()
    {
        using SKBitmap early = Render(Roll(0.4));
        using SKBitmap late = Render(Roll(0.6));
        Assert.True(LitBounds(late)!.Value.MidY < LitBounds(early)!.Value.MidY - 4,
            "later progress places the block higher");
    }

    [Fact]
    public void Crawl_Moves_Right_To_Left()
    {
        static ResolvedGenerator Crawl(double p) => Title(
            new() { [GeneratorParamNames.ScrollMode] = TitleScrollModes.Crawl, [GeneratorParamNames.Text] = "XX XX" },
            new() { [GeneratorParamNames.FontSize] = 0.2 },
            p,
            GeneratorTypeIds.Crawl);

        using SKBitmap start = Render(Crawl(0.0));
        using SKBitmap early = Render(Crawl(0.4));
        using SKBitmap late = Render(Crawl(0.6));
        using SKBitmap end = Render(Crawl(1.0));

        Assert.Null(LitBounds(start));
        Assert.Null(LitBounds(end));
        Assert.True(LitBounds(late)!.Value.MidX < LitBounds(early)!.Value.MidX - 4,
            "later progress places the line further left");
    }

    [Fact]
    public void Roll_Not_OffScreen_Rests_At_Its_Position_At_The_Ends()
    {
        static ResolvedGenerator RollAnchored(double p) => Title(
            new()
            {
                [GeneratorParamNames.ScrollMode] = TitleScrollModes.Roll,
                [GeneratorParamNames.ScrollOffscreen] = "false",
            },
            new() { [GeneratorParamNames.FontSize] = 0.2 },
            p,
            GeneratorTypeIds.Roll);

        using SKBitmap start = Render(RollAnchored(0.0));
        using SKBitmap end = Render(RollAnchored(1.0));
        // Start == end == the resting position (no off-screen travel when both flags are off).
        Assert.Equal(LitBounds(start)!.Value.MidY, LitBounds(end)!.Value.MidY);
        Assert.InRange(LitBounds(start)!.Value.MidY, H / 2 - 10, H / 2 + 10);
    }

    [Fact]
    public void Bold_Renders_Heavier_Than_Regular()
    {
        using SKBitmap regular = Render(Title());
        using SKBitmap bold = Render(Title(new() { [GeneratorParamNames.Bold] = "true" }));
        Assert.True(LitCount(bold) > LitCount(regular), "bold coverage exceeds regular");
    }

    [Fact]
    public void Tracking_Widens_The_Line()
    {
        var text = new Dictionary<string, string> { [GeneratorParamNames.Text] = "XXXX" };
        using SKBitmap normal = Render(Title(new(text)));
        using SKBitmap tracked = Render(Title(new(text), new()
        {
            [GeneratorParamNames.FontSize] = 0.3,
            [GeneratorParamNames.Tracking] = 0.3,
        }));
        Assert.True(LitBounds(tracked)!.Value.Width > LitBounds(normal)!.Value.Width + 8,
            "positive tracking spreads the glyphs");
    }
}
