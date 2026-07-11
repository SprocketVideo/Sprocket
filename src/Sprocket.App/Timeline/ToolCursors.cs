using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Sprocket.App;

/// <summary>
/// Per-tool bitmap cursors for the timeline (UI.md §3.2), rendered at runtime into a
/// <see cref="RenderTargetBitmap"/> — no shipped image assets, and each glyph is rasterised at the window's
/// render scaling so it stays crisp on HiDPI displays. The glyph set follows the leading NLEs conventions: red
/// side-specific trim brackets, yellow ripple brackets, a double-bracket roll, slip/slide arrow pairs, a razor
/// blade, an open hand, and a magnifier. Every cursor is cached per (kind, scale); if bitmap creation fails on
/// a platform, the nearest <see cref="StandardCursorType"/> is used instead.
/// </summary>
internal static class ToolCursors
{
    // Logical canvas: 24×24 DIPs, hotspots in the same space. Glyphs use a dark outline under a light (or
    // convention-coloured) fill so they read on both the dark timeline and any light surface they cross.
    private const double CanvasSize = 24;

    private static readonly Color Outline = Color.FromArgb(0xE6, 0x10, 0x10, 0x14);
    private static readonly Color GlyphWhite = Colors.White;
    private static readonly Color TrimRed = Color.FromRgb(0xE5, 0x4B, 0x3C);   // red trim brackets — the convention shared by the leading NLEs
    private static readonly Color RippleYellow = Color.FromRgb(0xE8, 0xC5, 0x2A); // yellow ripple brackets — same shared convention

    private static readonly Dictionary<(TimelineCursor Kind, int ScalePct), Cursor> Cache = new();

    /// <summary>The cursor for a <see cref="TimelineCursor"/> kind at a display scale (1.0 = 96 dpi).</summary>
    public static Cursor Get(TimelineCursor kind, double scaling)
    {
        if (kind == TimelineCursor.Default)
            return Cursor.Default;
        int scalePct = (int)Math.Round(Math.Clamp(scaling, 1, 4) * 100);
        if (Cache.TryGetValue((kind, scalePct), out Cursor? cached))
            return cached;

        Cursor cursor;
        try
        {
            cursor = Create(kind, scalePct / 100.0);
        }
        catch
        {
            cursor = Fallback(kind); // e.g. a platform without custom-cursor support
        }
        Cache[(kind, scalePct)] = cursor;
        return cursor;
    }

    /// <summary>The nearest built-in cursor for a kind — the pre-bitmap behaviour, kept as the failure path.</summary>
    private static Cursor Fallback(TimelineCursor kind) => kind switch
    {
        TimelineCursor.TrimStart or TimelineCursor.TrimEnd => new Cursor(StandardCursorType.SizeWestEast),
        TimelineCursor.RippleStart or TimelineCursor.RippleEnd => new Cursor(StandardCursorType.SizeWestEast),
        TimelineCursor.Roll or TimelineCursor.Slip => new Cursor(StandardCursorType.SizeWestEast),
        TimelineCursor.Blade => new Cursor(StandardCursorType.Cross),
        TimelineCursor.Slide or TimelineCursor.Hand => new Cursor(StandardCursorType.SizeAll),
        TimelineCursor.Zoom => new Cursor(StandardCursorType.Hand),
        _ => Cursor.Default,
    };

    private static Cursor Create(TimelineCursor kind, double scale)
    {
        int px = (int)Math.Ceiling(CanvasSize * scale);
        var bitmap = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96 * scale, 96 * scale));
        using (DrawingContext ctx = bitmap.CreateDrawingContext())
            Draw(ctx, kind);
        Point hot = Hotspot(kind);
        return new Cursor(bitmap, new PixelPoint((int)Math.Round(hot.X * scale), (int)Math.Round(hot.Y * scale)));
    }

    // The click point of each glyph: bracket cursors act at the bracket spine (the edge being trimmed),
    // the blade at its cutting edge, the magnifier at the lens centre, the rest at the canvas centre.
    private static Point Hotspot(TimelineCursor kind) => kind switch
    {
        TimelineCursor.TrimStart or TimelineCursor.RippleStart => new Point(9, 12),
        TimelineCursor.TrimEnd or TimelineCursor.RippleEnd => new Point(15, 12),
        TimelineCursor.Blade => new Point(12, 19),
        TimelineCursor.Zoom => new Point(10, 10),
        _ => new Point(12, 12),
    };

    private static void Draw(DrawingContext ctx, TimelineCursor kind)
    {
        switch (kind)
        {
            case TimelineCursor.TrimStart:
                DrawBracketWithArrows(ctx, TrimRed, opensRight: true);
                break;
            case TimelineCursor.TrimEnd:
                DrawBracketWithArrows(ctx, TrimRed, opensRight: false);
                break;
            case TimelineCursor.RippleStart:
                DrawBracketWithArrows(ctx, RippleYellow, opensRight: true);
                break;
            case TimelineCursor.RippleEnd:
                DrawBracketWithArrows(ctx, RippleYellow, opensRight: false);
                break;
            case TimelineCursor.Roll:
                DrawRoll(ctx);
                break;
            case TimelineCursor.Slip:
                DrawSlip(ctx);
                break;
            case TimelineCursor.Slide:
                DrawSlide(ctx);
                break;
            case TimelineCursor.Blade:
                DrawBlade(ctx);
                break;
            case TimelineCursor.Hand:
                DrawHand(ctx);
                break;
            case TimelineCursor.Zoom:
                DrawZoom(ctx);
                break;
        }
    }

    // ── Stroke helpers: every mark is drawn twice — a wider dark outline pass, then the coloured pass — so
    //    the glyph keeps a halo on any background (the standard OS-cursor technique). ─────────────────────

    private static void Stroke(DrawingContext ctx, Geometry geometry, Color color, double thickness)
    {
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Outline), thickness + 2,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color), thickness,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }

    private static void Fill(DrawingContext ctx, Geometry geometry, Color color)
    {
        ctx.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(Outline), 1.4,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }

    private static StreamGeometry Path(Action<StreamGeometryContext> build)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
            build(g);
        return geometry;
    }

    private static StreamGeometry Polyline(bool close, params Point[] points) => Path(g =>
    {
        g.BeginFigure(points[0], isFilled: close);
        for (int i = 1; i < points.Length; i++)
            g.LineTo(points[i]);
        g.EndFigure(close);
    });

    // A small solid arrowhead pointing left (dir = -1) or right (+1) with its tip at (tipX, y).
    private static void ArrowHead(DrawingContext ctx, double tipX, double y, int dir, Color color)
    {
        Fill(ctx, Polyline(true,
            new Point(tipX, y),
            new Point(tipX - dir * 4.5, y - 3.4),
            new Point(tipX - dir * 4.5, y + 3.4)), color);
    }

    // ── Glyphs (24×24 logical canvas) ────────────────────────────────────────────────────────────────

    // Trim / ripple: a side-specific bracket ([ opens right at the clip's start, ] opens left at its end)
    // over a double-headed horizontal arrow — the trim-cursor family shared by professional NLEs.
    private static void DrawBracketWithArrows(DrawingContext ctx, Color color, bool opensRight)
    {
        double spine = opensRight ? 9 : 15;
        double stub = opensRight ? 13.5 : 10.5;
        Stroke(ctx, Polyline(false,
            new Point(stub, 4.5),
            new Point(spine, 4.5),
            new Point(spine, 19.5),
            new Point(stub, 19.5)), color, 2.6);
        // Double-headed arrow through the middle.
        Stroke(ctx, Polyline(false, new Point(4.5, 12), new Point(19.5, 12)), GlyphWhite, 1.8);
        ArrowHead(ctx, 2.5, 12, -1, GlyphWhite);
        ArrowHead(ctx, 21.5, 12, +1, GlyphWhite);
    }

    // Roll: two brackets back to back (][) over the double-headed arrow — the shared cut moves both edges.
    private static void DrawRoll(DrawingContext ctx)
    {
        Stroke(ctx, Polyline(false,
            new Point(7, 4.5), new Point(10, 4.5), new Point(10, 19.5), new Point(7, 19.5)), TrimRed, 2.4);
        Stroke(ctx, Polyline(false,
            new Point(17, 4.5), new Point(14, 4.5), new Point(14, 19.5), new Point(17, 19.5)), TrimRed, 2.4);
        Stroke(ctx, Polyline(false, new Point(4, 12), new Point(20, 12)), GlyphWhite, 1.8);
        ArrowHead(ctx, 2.5, 12, -1, GlyphWhite);
        ArrowHead(ctx, 21.5, 12, +1, GlyphWhite);
    }

    // Slip: two fixed vertical bars (the clip's unmoving edges) with outward arrows — the source shifts
    // beneath them.
    private static void DrawSlip(DrawingContext ctx)
    {
        Stroke(ctx, Polyline(false, new Point(10, 6.5), new Point(10, 17.5)), GlyphWhite, 2.2);
        Stroke(ctx, Polyline(false, new Point(14, 6.5), new Point(14, 17.5)), GlyphWhite, 2.2);
        Stroke(ctx, Polyline(false, new Point(7.5, 12), new Point(5, 12)), GlyphWhite, 1.8);
        Stroke(ctx, Polyline(false, new Point(16.5, 12), new Point(19, 12)), GlyphWhite, 1.8);
        ArrowHead(ctx, 2.5, 12, -1, GlyphWhite);
        ArrowHead(ctx, 21.5, 12, +1, GlyphWhite);
    }

    // Slide: a solid block (the moving clip) with outward arrows — its neighbours absorb the change.
    private static void DrawSlide(DrawingContext ctx)
    {
        Fill(ctx, Polyline(true,
            new Point(9, 8.5), new Point(15, 8.5), new Point(15, 15.5), new Point(9, 15.5)), GlyphWhite);
        Stroke(ctx, Polyline(false, new Point(7, 12), new Point(5, 12)), GlyphWhite, 1.8);
        Stroke(ctx, Polyline(false, new Point(17, 12), new Point(19, 12)), GlyphWhite, 1.8);
        ArrowHead(ctx, 2.5, 12, -1, GlyphWhite);
        ArrowHead(ctx, 21.5, 12, +1, GlyphWhite);
    }

    // Blade: a single-edge razor blade, cutting edge down; the hotspot sits on the edge so the cut lands
    // where the blade touches.
    private static void DrawBlade(DrawingContext ctx)
    {
        // Body: flat-topped blade with slightly tapered sides and a straight cutting edge.
        Fill(ctx, Polyline(true,
            new Point(6, 5), new Point(18, 5), new Point(19.5, 12), new Point(19.5, 17.5),
            new Point(4.5, 17.5), new Point(4.5, 12)), GlyphWhite);
        // The classic centre slot.
        Stroke(ctx, Polyline(false, new Point(10, 10.5), new Point(14, 10.5)), Outline, 1.6);
        // Cutting-edge highlight just above the hotspot.
        Stroke(ctx, Polyline(false, new Point(5.5, 17.5), new Point(18.5, 17.5)), TrimRed, 1.4);
    }

    // Hand: a simplified open palm — four fingers over a rounded palm.
    private static void DrawHand(DrawingContext ctx)
    {
        Fill(ctx, Path(g =>
        {
            g.BeginFigure(new Point(7, 12), isFilled: true);
            g.LineTo(new Point(7, 6.5));
            g.ArcTo(new Point(9.4, 6.5), new Size(1.2, 1.2), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(9.4, 10));
            g.LineTo(new Point(10.2, 10));
            g.LineTo(new Point(10.2, 5));
            g.ArcTo(new Point(12.6, 5), new Size(1.2, 1.2), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(12.6, 10));
            g.LineTo(new Point(13.4, 10));
            g.LineTo(new Point(13.4, 5.8));
            g.ArcTo(new Point(15.8, 5.8), new Size(1.2, 1.2), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(15.8, 10.5));
            g.LineTo(new Point(16.6, 10.5));
            g.LineTo(new Point(16.6, 7.6));
            g.ArcTo(new Point(19, 7.6), new Size(1.2, 1.2), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(19, 14));
            g.ArcTo(new Point(13, 19.5), new Size(6, 5.5), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(11, 19.5));
            g.ArcTo(new Point(7, 15.5), new Size(4.5, 4.5), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }), GlyphWhite);
    }

    // Zoom: a magnifier with a + in the lens (the Zoom tool zooms in by default; Alt-click zooms out).
    private static void DrawZoom(DrawingContext ctx)
    {
        var lens = new EllipseGeometry(new Rect(4, 4, 12, 12));
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Outline), 4.2), lens);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(GlyphWhite), 2.2), lens);
        Stroke(ctx, Polyline(false, new Point(14.6, 14.6), new Point(20, 20)), GlyphWhite, 2.6);
        Stroke(ctx, Polyline(false, new Point(7.5, 10), new Point(12.5, 10)), GlyphWhite, 1.6);
        Stroke(ctx, Polyline(false, new Point(10, 7.5), new Point(10, 12.5)), GlyphWhite, 1.6);
    }
}
