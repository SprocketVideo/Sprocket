using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// The Source monitor's picture for audio-only media (PLAN.md step 61 phase 5): the source's waveform across the
/// whole frame, the marked In/Out range shaded, and the playhead — Premiere's audio-waveform view of a Source clip.
/// Drawn over the (blank) preview surface and hit-test invisible, so dragging the picture still drags the source.
/// The shell supplies the peaks (decoded off the UI thread) and pushes position / mark changes.
/// </summary>
internal sealed class SourceWaveformView : Control
{
    private static readonly IBrush WaveFill = new ImmutableSolidColorBrush(Color.Parse("#5E9C76"));
    private static readonly Pen CentrePen = new(new ImmutableSolidColorBrush(Color.Parse("#2C4A39")), 1);
    private static readonly IBrush MarkedRange = new ImmutableSolidColorBrush(Palette.Accent, 0.14);
    private static readonly Pen MarkPen = new(new ImmutableSolidColorBrush(Palette.Accent, 0.8), 1);
    private static readonly Pen PlayheadPen = new(Palette.AccentBrush, 1.5);

    private const double VerticalInset = 0.12; // fraction of the height left clear above and below the waveform

    private float[]? _peaks;
    private Timecode _duration;
    private Timecode _position;
    private Timecode? _markIn;
    private Timecode? _markOut;

    public SourceWaveformView() => IsHitTestVisible = false;

    /// <summary>Per-column peaks in [0, 1] across the source, or <see langword="null"/> while decoding.</summary>
    public void SetPeaks(float[]? peaks)
    {
        _peaks = peaks;
        InvalidateVisual();
    }

    /// <summary>The source's length and its marked range (either end may be unset).</summary>
    public void SetSpan(Timecode duration, Timecode? markIn, Timecode? markOut)
    {
        if (_duration == duration && _markIn == markIn && _markOut == markOut)
            return;
        (_duration, _markIn, _markOut) = (duration, markIn, markOut);
        InvalidateVisual();
    }

    public void SetPosition(Timecode position)
    {
        if (_position == position)
            return;
        _position = position;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        Rect bounds = Bounds;
        double w = bounds.Width, h = bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        double mid = h / 2;
        double half = h * (0.5 - VerticalInset);

        if (_duration.Ticks > 0 && (_markIn is not null || _markOut is not null))
        {
            double x0 = X(_markIn ?? Timecode.Zero, w);
            double x1 = X(_markOut ?? _duration, w);
            if (x1 > x0)
                ctx.FillRectangle(MarkedRange, new Rect(x0, 0, x1 - x0, h));
            if (_markIn is { } i)
                ctx.DrawLine(MarkPen, new Point(X(i, w), 0), new Point(X(i, w), h));
            if (_markOut is { } o)
                ctx.DrawLine(MarkPen, new Point(X(o, w), 0), new Point(X(o, w), h));
        }

        ctx.DrawLine(CentrePen, new Point(0, mid), new Point(w, mid));
        if (_peaks is { Length: > 0 } peaks)
        {
            // One bar per pixel column, each the loudest peak its column covers (so zoomed-out stays honest).
            int columns = (int)Math.Ceiling(w);
            for (int x = 0; x < columns; x++)
            {
                int from = (int)((long)x * peaks.Length / columns);
                int to = Math.Max(from + 1, (int)((long)(x + 1) * peaks.Length / columns));
                float peak = 0;
                for (int p = from; p < to && p < peaks.Length; p++)
                    peak = Math.Max(peak, peaks[p]);
                double bar = Math.Max(0.5, Math.Min(1, peak) * half);
                ctx.FillRectangle(WaveFill, new Rect(x, mid - bar, 1, bar * 2));
            }
        }

        if (_duration.Ticks > 0)
        {
            double px = X(_position, w);
            ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
        }
    }

    private double X(Timecode t, double width) =>
        Math.Clamp((double)t.Ticks / _duration.Ticks, 0, 1) * width;
}
