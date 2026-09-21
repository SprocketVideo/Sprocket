using Sprocket.App.Inspector;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Unit tests for the camera-path graph's plotting geometry (plan/features/stabilization.md phase 6): value-range
/// selection and value-series → lane-polyline mapping. Pure math, no UI (<see cref="CameraPathGraphMath"/>).
/// </summary>
public sealed class CameraPathGraphMathTests
{
    [Fact]
    public void Range_spans_both_series_with_padding()
    {
        (double min, double max) = CameraPathGraphMath.Range([0.0, 1.0], [-1.0, 0.5]);
        Assert.True(min < -1.0);   // padded below the combined minimum (-1)
        Assert.True(max > 1.0);    // padded above the combined maximum (1)
    }

    [Fact]
    public void Range_of_a_flat_channel_is_non_degenerate()
    {
        (double min, double max) = CameraPathGraphMath.Range([2.0, 2.0, 2.0], [2.0, 2.0, 2.0]);
        Assert.True(max > min); // a floor pad keeps the lane from collapsing to zero height
    }

    [Fact]
    public void Range_of_empty_input_is_a_safe_default()
    {
        (double min, double max) = CameraPathGraphMath.Range([], []);
        Assert.True(max > min);
    }

    [Fact]
    public void Polyline_maps_max_to_the_top_and_min_to_the_bottom()
    {
        // Two points at the value extremes over a lane at y=[10,10+40].
        CameraPathGraphMath.Pt[] pts = CameraPathGraphMath.Polyline([0.0, 10.0], x: 5, width: 100, y: 10, height: 40, min: 0, max: 10);

        Assert.Equal(2, pts.Length);
        Assert.Equal(5, pts[0].X, 3);       // first sample at the lane's left edge
        Assert.Equal(105, pts[1].X, 3);      // last sample at the right edge
        Assert.Equal(50, pts[0].Y, 3);       // value 0 (min) → lane bottom (y = 10 + 40)
        Assert.Equal(10, pts[1].Y, 3);       // value 10 (max) → lane top (y = 10)
    }

    [Fact]
    public void Polyline_clamps_out_of_range_values_into_the_lane()
    {
        CameraPathGraphMath.Pt[] pts = CameraPathGraphMath.Polyline([-5.0, 15.0], x: 0, width: 10, y: 0, height: 20, min: 0, max: 10);
        Assert.Equal(20, pts[0].Y, 3); // below min → clamped to the bottom
        Assert.Equal(0, pts[1].Y, 3);  // above max → clamped to the top
    }

    [Fact]
    public void Polyline_of_empty_input_is_empty()
    {
        Assert.Empty(CameraPathGraphMath.Polyline([], 0, 100, 0, 40, 0, 1));
    }
}
