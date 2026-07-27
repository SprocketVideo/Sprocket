using System;

namespace Sprocket.App.Controls;

/// <summary>
/// Pure maths for the drag-to-scrub numeric field (<see cref="DragNumber"/>), split out like
/// <see cref="Inspector.InspectorFormat"/> / <see cref="Timeline.TimelineMath"/> so the sensitivity curve is
/// unit-testable without an Avalonia surface.
/// </summary>
public static class DragNumberMath
{
    /// <summary>How far the pointer must travel before a press counts as a scrub rather than a click into
    /// the field. Below this the field focuses for typing instead.</summary>
    public const double DragThresholdPx = 3.0;

    /// <summary>Shift-drag multiplier — coarse, for crossing a wide range quickly.</summary>
    public const double CoarseScale = 5.0;

    /// <summary>Ctrl-drag multiplier — fine, for trimming the last fraction.</summary>
    public const double FineScale = 0.2;

    /// <summary>Pointer travel that sweeps a wide parameter's whole range at 1×.</summary>
    private const double FullSweepPx = 300.0;

    /// <summary>
    /// Value change per pixel of horizontal travel: whichever is larger of a full-range sweep over
    /// <see cref="FullSweepPx"/> and a tenth of the editing step.
    /// <para>
    /// Deliberately range-relative rather than the one-step-per-pixel some editors use, because our steps
    /// span four orders of magnitude (0.0005 on a title's stroke width, 1.0 on Rotation) — a fixed
    /// step-per-pixel makes Rotation a 360 px haul while Opacity is a twitchy 20 px flick. The step floor then
    /// rescues narrow-range parameters the sweep term would make too slow to move a single step: Opacity
    /// (0–1, step 0.05) scrubs at 0.005/px — a 200 px sweep, 10 px per step — instead of 0.0033/px.
    /// </para>
    /// </summary>
    public static double UnitsPerPixel(double min, double max, double step)
    {
        double range = Math.Abs(max - min);
        double byRange = range / FullSweepPx;
        double byStep = Math.Abs(step) / 10.0;
        double perPixel = Math.Max(byRange, byStep);
        // Degenerate descriptor (zero range and zero step): fall back to a unit-per-pixel scrub rather than
        // freezing the field.
        return perPixel > 0 ? perPixel : 1.0;
    }

    /// <summary>
    /// The value a scrub lands on: <paramref name="start"/> (the value when the drag began) displaced by
    /// <paramref name="dx"/> pixels of horizontal travel, clamped to the parameter's range. Modifiers follow
    /// the Premiere / After Effects convention — Shift coarsens, Ctrl refines — and are ignored if both are
    /// held. An <paramref name="integer"/> parameter snaps to whole units so the slider's tick snapping and
    /// the scrub agree.
    /// </summary>
    public static double Apply(
        double start, double dx, double min, double max, double step,
        bool coarse, bool fine, bool integer)
    {
        double scale = coarse == fine ? 1.0 : coarse ? CoarseScale : FineScale;
        double value = start + dx * UnitsPerPixel(min, max, step) * scale;
        if (integer)
            value = Math.Round(value);
        return Math.Clamp(value, min, max);
    }
}
