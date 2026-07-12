using Sprocket.App;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests the pure Fill Height math (<see cref="FramingOps.FillHeightScale"/>): the Transform scale — relative
/// to the clip's conformed base rectangle — that makes the picture span the full canvas height.
/// </summary>
public class FramingOpsTests
{
    [Fact]
    public void Landscape_Media_In_A_Portrait_Canvas_Under_Fit_Scales_Up_To_Full_Height()
    {
        // 1920×1080 fit into 1080×1920 renders 1080×607.5 (width-limited): fit scale = 1080/1920 = 0.5625,
        // height wants 1920/1080 ≈ 1.7778 → Transform scale ≈ 3.1605.
        double? scale = FramingOps.FillHeightScale(new Resolution(1080, 1920), 1920, 1080, ClipConformMode.Fit);
        Assert.NotNull(scale);
        Assert.Equal((1920.0 / 1080.0) / (1080.0 / 1920.0), scale!.Value, 6);
    }

    [Fact]
    public void Fill_Conform_Already_Spans_The_Height_When_The_Crop_Is_Horizontal()
    {
        // 1920×1080 filling 1080×1920 scales by max(0.5625, 1.7778) = height-driven → already full height.
        double? scale = FramingOps.FillHeightScale(new Resolution(1080, 1920), 1920, 1080, ClipConformMode.Fill);
        Assert.Equal(1.0, scale!.Value, 6);
    }

    [Fact]
    public void Portrait_Media_In_A_Landscape_Canvas_Under_Fit_Is_Already_Full_Height()
    {
        // 1080×1920 fit into 1920×1080 is height-limited — Fill Height is a no-op scale of 1.
        double? scale = FramingOps.FillHeightScale(new Resolution(1920, 1080), 1080, 1920, ClipConformMode.Fit);
        Assert.Equal(1.0, scale!.Value, 6);
    }

    [Fact]
    public void Matching_Aspect_Is_A_No_Op_Under_Both_Modes()
    {
        Assert.Equal(1.0, FramingOps.FillHeightScale(new Resolution(1920, 1080), 3840, 2160, ClipConformMode.Fit)!.Value, 6);
        Assert.Equal(1.0, FramingOps.FillHeightScale(new Resolution(1920, 1080), 3840, 2160, ClipConformMode.Fill)!.Value, 6);
    }

    [Theory]
    [InlineData(0, 1080, 1920, 1080)]  // degenerate canvas
    [InlineData(1920, 1080, 0, 1080)]  // unknown source width
    [InlineData(1920, 1080, 1920, -1)] // unknown source height
    public void Unknown_Dimensions_Return_Null(int canvasW, int canvasH, int sourceW, int sourceH)
    {
        Assert.Null(FramingOps.FillHeightScale(new Resolution(canvasW, canvasH), sourceW, sourceH, ClipConformMode.Fit));
    }
}
