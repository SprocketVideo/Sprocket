using SkiaSharp;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Pure scaling-math tests for <see cref="FramePresenter.ComputeFitRect"/> (the letterbox fit), exercised
/// without a canvas or the native Skia surface — just the managed <see cref="SKRect"/> arithmetic.
/// </summary>
public class FramePresenterTests
{
    [Fact]
    public void Pillarboxes_A_Wide_Frame_In_A_Narrow_Bounds()
    {
        // 16:9 frame into a 100×100 square → full width, centred vertically.
        SKRect fit = FramePresenter.ComputeFitRect(SKRect.Create(0, 0, 100, 100), 1920, 1080);
        Assert.Equal(100f, fit.Width, 3);
        Assert.Equal(56.25f, fit.Height, 3);
        Assert.Equal(0f, fit.Left, 3);
        Assert.Equal((100 - 56.25f) / 2, fit.Top, 3);
    }

    [Fact]
    public void Letterboxes_A_Tall_Frame_In_A_Wide_Bounds()
    {
        // 1:1 frame into a 200×100 bounds → full height, centred horizontally.
        SKRect fit = FramePresenter.ComputeFitRect(SKRect.Create(0, 0, 200, 100), 100, 100);
        Assert.Equal(100f, fit.Height, 3);
        Assert.Equal(100f, fit.Width, 3);
        Assert.Equal(50f, fit.Left, 3);
        Assert.Equal(0f, fit.Top, 3);
    }

    [Fact]
    public void Preserves_Aspect_Ratio_When_Fitting()
    {
        SKRect fit = FramePresenter.ComputeFitRect(SKRect.Create(0, 0, 640, 480), 1920, 1080);
        Assert.Equal(1920.0 / 1080.0, fit.Width / (double)fit.Height, 3);
        Assert.True(fit.Width <= 640f && fit.Height <= 480f);
    }

    [Theory]
    [InlineData(0, 100, 1920, 1080)]   // zero-width bounds
    [InlineData(100, 100, 0, 1080)]    // zero-width frame
    public void Returns_Empty_For_Degenerate_Inputs(float boundsW, float boundsH, int frameW, int frameH)
    {
        SKRect fit = FramePresenter.ComputeFitRect(SKRect.Create(0, 0, boundsW, boundsH), frameW, frameH);
        Assert.Equal(SKRect.Empty, fit);
    }
}
