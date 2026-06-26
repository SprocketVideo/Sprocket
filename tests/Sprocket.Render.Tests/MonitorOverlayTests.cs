using SkiaSharp;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Pure geometry tests for the monitor's safe-area guides (<see cref="MonitorOverlay.ComputeSafeAreas"/>,
/// PLAN.md step 17). The drawing itself (<see cref="MonitorOverlay.Draw"/>) is canvas-bound and rests on manual
/// verification; the insets that position the guides are asserted here without a surface.
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
}
