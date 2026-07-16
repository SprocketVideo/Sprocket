using SkiaSharp;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Pure geometry tests for the monitor's safe-area guides (<see cref="MonitorOverlay.ComputeSafeAreas"/>,
/// PLAN.md step 17), plus a CPU-raster containment test of <see cref="MonitorOverlay.Draw"/> (the portrait
/// regression); the rendered look otherwise rests on manual verification.
/// </summary>
public sealed class MonitorOverlayTests
{
    [Fact]
    public void SafeAreas_Are_Concentric_And_Inset_By_The_Documented_Fractions()
    {
        var frame = SKRect.Create(0, 0, 1000, 600);
        (SKRect action, SKRect title) = MonitorOverlay.ComputeSafeAreas(frame);

        // Action-safe = 93% of frame (3.5% inset each side); title-safe = 90% (5% inset each side).
        Assert.Equal(1000 * MonitorOverlay.ActionSafeInset, action.Left, 3);
        Assert.Equal(600 * MonitorOverlay.ActionSafeInset, action.Top, 3);
        Assert.Equal(1000 * (1 - 2 * MonitorOverlay.ActionSafeInset), action.Width, 3);
        Assert.Equal(600 * (1 - 2 * MonitorOverlay.ActionSafeInset), action.Height, 3);

        Assert.Equal(1000 * MonitorOverlay.TitleSafeInset, title.Left, 3);
        Assert.Equal(600 * (1 - 2 * MonitorOverlay.TitleSafeInset), title.Height, 3);

        // Title-safe is the tighter (inner) rectangle.
        Assert.True(title.Left > action.Left && title.Width < action.Width);

        // Both are concentric with the frame.
        Assert.Equal(frame.MidX, action.MidX, 3);
        Assert.Equal(frame.MidY, title.MidY, 3);
    }

    [Fact]
    public void SafeAreas_Are_Empty_For_A_Degenerate_Frame()
    {
        (SKRect action, SKRect title) = MonitorOverlay.ComputeSafeAreas(SKRect.Create(0, 0, 0, 100));
        Assert.Equal(SKRect.Empty, action);
        Assert.Equal(SKRect.Empty, title);
    }

    /// <summary>
    /// The portrait-sequence regression (memory: the guides looked unclipped for non-16:9 frames): every stroke
    /// of the overlay must land inside the frame rectangle, and the frame boundary itself must be stroked so
    /// the guides visibly terminate at the frame edge. Rendered on a CPU surface so the test needs no GPU.
    /// </summary>
    [Fact]
    public void Draw_Stays_Inside_The_Frame_And_Strokes_Its_Boundary()
    {
        // A portrait (9:16) frame centred in a landscape surface — the shape that exposed the bug.
        var frame = SKRect.Create(170, 20, 146, 260);
        using var surface = SKSurface.Create(new SKImageInfo(480, 300, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        MonitorOverlay.Draw(surface.Canvas, frame, thirds: true, safeAreas: true);

        using SKImage snap = surface.Snapshot();
        using SKBitmap bmp = SKBitmap.FromImage(snap);

        bool anyDrawn = false, boundaryDrawn = false;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y) is not { Red: > 0 } px || px.Green == 0 || px.Blue == 0)
                    continue; // untouched background
                anyDrawn = true;

                // Nothing may draw outside the frame (half-open pixel coverage of the rect).
                Assert.True(x >= frame.Left && x < frame.Right && y >= frame.Top && y < frame.Bottom,
                    $"overlay pixel ({x},{y}) is outside the frame {frame}");

                if (x == (int)frame.Left && y == (int)(frame.MidY))
                    boundaryDrawn = true; // left frame edge is stroked at mid-height
            }
        }

        Assert.True(anyDrawn, "the overlay drew nothing");
        Assert.True(boundaryDrawn, "the frame boundary is not stroked");
    }
}
