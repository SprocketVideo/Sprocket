using SkiaSharp;

namespace Sprocket.Render;

/// <summary>
/// The monitor's safe-area / framing-grid overlay (PLAN.md step 17, UI.md §3.4): a rule-of-thirds grid plus
/// action-safe (93%) and title-safe (90%) guide rectangles, drawn over the program/source frame. The geometry
/// (<see cref="ComputeSafeAreas"/>) is pure so it is unit-testable without a canvas; <see cref="Draw"/> renders
/// it as thin translucent strokes that read on any image. This is a non-destructive overlay — it never touches
/// the decoded pixels (ARCHITECTURE.md §1), only the surface canvas after the frame is composited.
/// </summary>
public static class MonitorOverlay
{
    /// <summary>Action-safe guide inset as a fraction of each side (3.5% ⇒ a 93%-of-frame rectangle).</summary>
    public const float ActionSafeInset = 0.035f;

    /// <summary>Title-safe guide inset as a fraction of each side (5% ⇒ a 90%-of-frame rectangle).</summary>
    public const float TitleSafeInset = 0.05f;

    /// <summary>
    /// The action-safe and title-safe rectangles for a frame occupying <paramref name="frame"/>, each inset
    /// symmetrically and concentric with the frame. Returns empties for a degenerate frame.
    /// </summary>
    public static (SKRect ActionSafe, SKRect TitleSafe) ComputeSafeAreas(SKRect frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return (SKRect.Empty, SKRect.Empty);
        return (Inset(frame, ActionSafeInset), Inset(frame, TitleSafeInset));
    }

    private static SKRect Inset(SKRect r, float fraction)
    {
        float dx = r.Width * fraction;
        float dy = r.Height * fraction;
        return new SKRect(r.Left + dx, r.Top + dy, r.Right - dx, r.Bottom - dy);
    }

    /// <summary>
    /// Draws the requested overlays inside <paramref name="frame"/>: <paramref name="thirds"/> draws the
    /// rule-of-thirds grid; <paramref name="safeAreas"/> draws the action- and title-safe rectangles. No-op for
    /// a degenerate frame or when both flags are off.
    /// </summary>
    public static void Draw(SKCanvas canvas, SKRect frame, bool thirds, bool safeAreas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (frame.Width <= 0 || frame.Height <= 0 || (!thirds && !safeAreas))
            return;

        using var line = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0x66),
            IsAntialias = false,
        };

        if (thirds)
        {
            for (int i = 1; i <= 2; i++)
            {
                float x = frame.Left + frame.Width * i / 3f;
                float y = frame.Top + frame.Height * i / 3f;
                canvas.DrawLine(x, frame.Top, x, frame.Bottom, line);
                canvas.DrawLine(frame.Left, y, frame.Right, y, line);
            }
        }

        if (safeAreas)
        {
            (SKRect action, SKRect title) = ComputeSafeAreas(frame);
            line.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x99);
            canvas.DrawRect(action, line);
            line.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x66);
            canvas.DrawRect(title, line);
        }
    }
}
