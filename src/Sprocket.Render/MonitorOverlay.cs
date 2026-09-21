using System.Collections.Generic;
using SkiaSharp;
using Sprocket.Core.Stabilization;

namespace Sprocket.Render;

/// <summary>
/// The monitor's safe-area / framing-grid overlay (PLAN.md step 17, UI.md §3.4): the frame boundary, a
/// rule-of-thirds grid, and action-safe (93%) / title-safe (90%) guide rectangles, drawn over the program/source
/// frame. The boundary stroke matters for non-16:9 (portrait/square) sequences: at Fit the frame's empty canvas
/// is otherwise indistinguishable from the panel background, so thirds lines legitimately spanning the full
/// frame height read as overrunning it — the outline makes where they terminate visible. The geometry
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
    /// rule-of-thirds grid; <paramref name="safeAreas"/> draws the action- and title-safe rectangles. Either
    /// flag also strokes the frame boundary itself, so the guides visibly end at the frame edge even where the
    /// frame's canvas matches the panel background (see the class remarks). No-op for a degenerate frame or
    /// when both flags are off.
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
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0xB3),
            IsAntialias = false,
        };

        // Frame boundary, brightest of the strokes. Inset by half the stroke width so the whole line lands
        // inside the frame — at Fit the frame edge coincides with the surface edge, where a centred stroke
        // would lose its outer half to the bounds clip.
        canvas.DrawRect(new SKRect(frame.Left + 0.5f, frame.Top + 0.5f, frame.Right - 0.5f, frame.Bottom - 0.5f), line);

        line.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x66);
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

    /// <summary>
    /// Draws a stabilization status banner across the top of <paramref name="frame"/> (plan/features/stabilization.md
    /// phase 6, modelled on Premiere's Warp Stabilizer monitor banner): a translucent bar with <paramref name="text"/>
    /// — amber when <paramref name="warn"/> (needs analysis / low confidence), neutral otherwise (analysing progress).
    /// No-op for a degenerate frame or empty text.
    /// </summary>
    public static void DrawBanner(SKCanvas canvas, SKRect frame, string text, bool warn)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (frame.Width <= 0 || frame.Height <= 0 || string.IsNullOrEmpty(text))
            return;

        float textSize = System.Math.Clamp(frame.Height * 0.035f, 11f, 20f);
        using var font = new SKFont(SKTypeface.Default, textSize);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float pad = textSize * 0.6f;
        float textWidth = font.MeasureText(text);
        float barH = textSize + pad * 1.2f;
        var bar = new SKRect(frame.Left, frame.Top, frame.Left + System.Math.Min(frame.Width, textWidth + pad * 2), frame.Top + barH);

        using var barPaint = new SKPaint
        {
            IsAntialias = true,
            Color = warn ? new SKColor(0xD2, 0x99, 0x22, 0xE0) : new SKColor(0x0E, 0x0E, 0x12, 0xC8),
        };
        canvas.DrawRect(bar, barPaint);

        float baseline = frame.Top + pad * 0.6f + textSize;
        canvas.DrawText(text, frame.Left + pad, baseline, SKTextAlign.Left, font, textPaint);
    }

    /// <summary>
    /// Draws the analysis's tracked feature points for one frame (plan/features/stabilization.md phase 6, Warp's
    /// "Show Track Points") as small dots over <paramref name="frame"/>. Positions are fractions of the analysis
    /// width (<see cref="FeaturePoint"/>); since the analysis preserves the source aspect and the frame rect does
    /// too, both axes scale by the frame width. No-op for a degenerate frame or empty set.
    /// </summary>
    public static void DrawTrackPoints(SKCanvas canvas, SKRect frame, IReadOnlyList<FeaturePoint> points)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(points);
        if (frame.Width <= 0 || frame.Height <= 0 || points.Count == 0)
            return;

        using var dot = new SKPaint { Color = new SKColor(0x3F, 0xB9, 0x50, 0xE0), IsAntialias = true };
        float r = System.Math.Clamp(frame.Width * 0.004f, 1.5f, 4f);
        foreach (FeaturePoint p in points)
        {
            float x = frame.Left + p.X * frame.Width;
            float y = frame.Top + p.Y * frame.Width;
            if (x >= frame.Left && x <= frame.Right && y >= frame.Top && y <= frame.Bottom)
                canvas.DrawCircle(x, y, r, dot);
        }
    }
}
