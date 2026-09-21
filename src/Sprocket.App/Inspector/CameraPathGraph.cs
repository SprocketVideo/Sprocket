using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Sprocket.Core.Stabilization;

namespace Sprocket.App.Inspector;

/// <summary>
/// The Inspector's camera-path diagnostic for Stabilization (plan/features/stabilization.md phase 6; Resolve's
/// Color-page camera-path graph): four stacked lanes — Pan, Tilt, Zoom, Rotation — each plotting the recovered
/// <b>raw</b> camera path (dim) against the <b>smoothed</b> target path (accent), with a vertical playhead marker.
/// The zoom lane is the focus-breathing tell: accidental-autofocus FOV pumping shows as a wobble the smoothed line
/// flattens. Read-only (the owner feeds it a solved <see cref="StabilizationSolution"/> via <see cref="Update"/>);
/// the plotting geometry lives in the testable <see cref="CameraPathGraphMath"/>.
/// </summary>
public sealed class CameraPathGraph : Control
{
    private static readonly (string Label, int Channel)[] Lanes =
    [
        ("Pan", 0), ("Tilt", 1), ("Zoom", 2), ("Rotation", 3),
    ];

    private const double LaneHeight = 30;
    private const double LaneGap = 6;
    private const double LabelWidth = 52;

    private static readonly IBrush LaneBg = new ImmutableSolidColorBrush(Palette.SectionBg);
    private static readonly Pen LaneBorder = new(Palette.EdgeBrush, 1);
    private static readonly Pen RawPen = new(new ImmutableSolidColorBrush(Palette.MutedText, 0.55), 1);
    private static readonly Pen SmoothPen = new(Palette.AccentBrush, 1.5);
    private static readonly Pen PlayheadPen = new(new ImmutableSolidColorBrush(Colors.White, 0.35), 1);
    private static readonly IBrush LabelBrush = Palette.FaintTextBrush;
    private static readonly IBrush MidBrush = Palette.MutedTextBrush;

    private IReadOnlyList<CameraPathSample>? _raw;
    private IReadOnlyList<CameraPathSample>? _smoothed;
    private double _playhead; // 0..1 across the analysed range, or <0 to hide

    public CameraPathGraph()
    {
        Height = Lanes.Length * LaneHeight + (Lanes.Length - 1) * LaneGap + 2;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        ClipToBounds = true;
        ToolTip.SetTip(this,
            "Recovered camera path: raw (dim) vs smoothed (accent). The Zoom lane reveals focus-breathing pumping.");
    }

    /// <summary>Feeds a freshly-solved path and the playhead position (fraction 0..1 of the analysed range, or a
    /// negative value to hide the marker); triggers a redraw. Passing <see langword="null"/> clears the graph.</summary>
    public void Update(StabilizationSolution? solution, double playhead01)
    {
        _raw = solution?.RawPath;
        _smoothed = solution?.SmoothedPath;
        _playhead = playhead01;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double plotX = LabelWidth;
        double plotW = Math.Max(1, Bounds.Width - LabelWidth - 2);
        bool haveData = _raw is { Count: > 0 } && _smoothed is { Count: > 0 };

        for (int lane = 0; lane < Lanes.Length; lane++)
        {
            double y = lane * (LaneHeight + LaneGap) + 1;
            var laneRect = new Rect(plotX, y, plotW, LaneHeight);
            ctx.DrawRectangle(LaneBg, LaneBorder, laneRect);

            var label = new FormattedText(
                Lanes[lane].Label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, Typography.Caption, LabelBrush);
            ctx.DrawText(label, new Point(0, y + (LaneHeight - label.Height) / 2));

            if (!haveData)
                continue;

            double[] raw = Extract(_raw!, Lanes[lane].Channel);
            double[] smooth = Extract(_smoothed!, Lanes[lane].Channel);
            (double min, double max) = CameraPathGraphMath.Range(raw, smooth);

            DrawSeries(ctx, CameraPathGraphMath.Polyline(raw, plotX, plotW, y, LaneHeight, min, max), RawPen);
            DrawSeries(ctx, CameraPathGraphMath.Polyline(smooth, plotX, plotW, y, LaneHeight, min, max), SmoothPen);

            if (_playhead is >= 0 and <= 1)
            {
                double px = plotX + _playhead * plotW;
                ctx.DrawLine(PlayheadPen, new Point(px, y), new Point(px, y + LaneHeight));
            }
        }

        if (!haveData)
        {
            var hint = new FormattedText(
                "Analyze to see the camera path.", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, Typography.Caption, MidBrush);
            ctx.DrawText(hint, new Point(plotX + 6, Bounds.Height / 2 - hint.Height / 2));
        }
    }

    private static void DrawSeries(DrawingContext ctx, CameraPathGraphMath.Pt[] pts, Pen pen)
    {
        for (int i = 1; i < pts.Length; i++)
            ctx.DrawLine(pen, new Point(pts[i - 1].X, pts[i - 1].Y), new Point(pts[i].X, pts[i].Y));
    }

    private static double[] Extract(IReadOnlyList<CameraPathSample> path, int channel)
    {
        var values = new double[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            CameraPathSample s = path[i];
            values[i] = channel switch
            {
                0 => s.Tx,
                1 => s.Ty,
                2 => s.LogScale,
                _ => s.Angle,
            };
        }
        return values;
    }
}
