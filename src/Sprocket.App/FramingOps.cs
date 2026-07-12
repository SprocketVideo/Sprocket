using System;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// Pure math behind the Inspector's Framing buttons (the Conform/Transform split: the clip's
/// <see cref="ClipConformMode"/> picks the base destination rectangle, the Transform effect reframes on top).
/// Kept out of <see cref="Inspector.InspectorPanel"/> so it is unit-testable headlessly.
/// </summary>
public static class FramingOps
{
    /// <summary>
    /// The Transform <c>scale</c> that makes a <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>
    /// clip's displayed height exactly fill the <paramref name="canvas"/> height, on top of its
    /// <paramref name="mode"/> conform (Transform scale is relative to the conformed base rectangle) — the
    /// Inspector's "Fill Height" button. 1.0 when the conform already renders full-height (portrait media in a
    /// landscape frame under Fit, or any Fill whose crop is horizontal). Returns <see langword="null"/> for
    /// degenerate inputs (unknown/offline dimensions), which the caller treats as a no-op.
    /// </summary>
    public static double? FillHeightScale(Resolution canvas, int sourceWidth, int sourceHeight, ClipConformMode mode)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return null;

        double fitX = (double)canvas.Width / sourceWidth;
        double fitY = (double)canvas.Height / sourceHeight;
        double baseScale = mode == ClipConformMode.Fill ? Math.Max(fitX, fitY) : Math.Min(fitX, fitY);
        return fitY / baseScale;
    }
}
