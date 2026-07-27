using Sprocket.App.Controls;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Covers the drag-to-scrub sensitivity curve behind the Inspector's and the mixer's numeric fields — the
/// "scrubby slider" gesture Premiere / After Effects / Resolve put on every numeric field. Split out from the
/// Avalonia behaviour like <see cref="Sprocket.App.Inspector.InspectorFormat"/> so the maths is testable
/// headlessly.
/// </summary>
public class DragNumberMathTests
{
    [Fact]
    public void UnitsPerPixel_sweeps_the_full_range_over_the_nominal_travel()
    {
        // Rotation (-180..180, step 1) → 1.2°/px, so the whole range is one 300px drag rather than the 360px
        // a one-step-per-pixel rule would need. Track gain (-60..+12, step 0.5) likewise.
        Assert.Equal(360.0 / 300, DragNumberMath.UnitsPerPixel(-180, 180, 1.0), 9);
        Assert.Equal(72.0 / 300, DragNumberMath.UnitsPerPixel(-60, 12, 0.5), 9);
    }

    [Fact]
    public void UnitsPerPixel_floors_at_a_tenth_of_the_step()
    {
        // A narrow range with a comparatively coarse step would otherwise scrub so slowly that single steps
        // are unreachable. Opacity (0–1, step 0.05): the range term gives 0.0033/px, the floor 0.005/px —
        // a 200px full sweep, still comfortable, and a step is 10px.
        Assert.Equal(0.005, DragNumberMath.UnitsPerPixel(0, 1, 0.05), 9);
        // Shimmer's Interval (1..12 semitones, step 1): 0.037/px from the range, 0.1/px from the floor.
        Assert.Equal(0.1, DragNumberMath.UnitsPerPixel(1, 12, 1.0), 9);
    }

    [Fact]
    public void UnitsPerPixel_never_returns_zero_for_a_degenerate_descriptor()
    {
        Assert.Equal(1.0, DragNumberMath.UnitsPerPixel(0, 0, 0), 9);
    }

    [Fact]
    public void Apply_moves_the_value_by_the_travel()
    {
        // Opacity scrubs at 0.005/px, so 100px right is +50%.
        Assert.Equal(0.5, Apply(start: 0.0, dx: 100), 6);
        Assert.Equal(0.25, Apply(start: 0.5, dx: -50), 6);
    }

    [Fact]
    public void Apply_with_no_travel_is_the_identity()
    {
        Assert.Equal(0.42, Apply(start: 0.42, dx: 0), 6);
    }

    [Fact]
    public void Apply_honours_the_coarse_and_fine_modifiers()
    {
        Assert.Equal(0.1, Apply(start: 0.0, dx: 20), 6);
        Assert.Equal(0.5, Apply(start: 0.0, dx: 20, coarse: true), 6);   // Shift ×5
        Assert.Equal(0.02, Apply(start: 0.0, dx: 20, fine: true), 6);    // Ctrl ×0.2
    }

    [Fact]
    public void Apply_ignores_the_modifiers_when_both_are_held()
    {
        Assert.Equal(
            Apply(start: 0.0, dx: 20),
            Apply(start: 0.0, dx: 20, coarse: true, fine: true), 6);
    }

    [Theory]
    [InlineData(10000, 1.0)]     // far past the top
    [InlineData(-10000, 0.0)]    // far past the bottom
    public void Apply_clamps_to_the_range(double dx, double expected)
    {
        Assert.Equal(expected, Apply(start: 0.5, dx: dx), 6);
    }

    [Fact]
    public void Apply_snaps_integer_parameters_to_whole_units()
    {
        // Shimmer's Interval: 1..12 semitones at the 0.1/px step floor. 4px = +0.4, which rounds back to 5 —
        // a small nudge must not land the slider between ticks.
        Assert.Equal(5, DragNumberMath.Apply(5, 4, 1, 12, 1, false, false, integer: true), 6);
        // 40px = +4.0 → 9.
        Assert.Equal(9, DragNumberMath.Apply(5, 40, 1, 12, 1, false, false, integer: true), 6);
    }

    [Fact]
    public void Apply_clamps_after_snapping_so_an_integer_scrub_stays_in_range()
    {
        Assert.Equal(12, DragNumberMath.Apply(11, 5000, 1, 12, 1, false, false, integer: true), 6);
        Assert.Equal(1, DragNumberMath.Apply(2, -5000, 1, 12, 1, false, false, integer: true), 6);
    }

    // Opacity's shape — 0–1, step 0.05 — which puts UnitsPerPixel at the 0.005 step floor.
    private static double Apply(double start, double dx, bool coarse = false, bool fine = false) =>
        DragNumberMath.Apply(start, dx, 0.0, 1.0, 0.05, coarse, fine, integer: false);
}
