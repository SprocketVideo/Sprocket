using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// The in/out mark overlay drawn over the transport scrubber (PLAN.md step 61 phase 2): a faint shaded band over
/// the marked range plus an accent tick at each set mark, the same look as <c>TimelineControl.DrawInOutRange</c>.
/// It shows the active monitor's marks — the Source media's marks on the Source tab, the sequence marks on the
/// Program tab — the way Premiere's monitors do. Purely visual: it sits over the <see cref="Slider"/> in the same
/// grid cell and never takes pointer input.
/// </summary>
internal sealed class ScrubberMarks : Control
{
    private static readonly IBrush RangeFill = new ImmutableSolidColorBrush(Colors.White, 0.10);
    private static readonly Pen TickPen = new(new ImmutableSolidColorBrush(Palette.Accent, 0.95), 2);

    private Slider? _slider;
    private Timecode? _markIn, _markOut;
    private long _duration;

    public ScrubberMarks()
    {
        IsHitTestVisible = false;
    }

    /// <summary>The scrubber this overlays — its thumb width sets how far the track is inset at each end.</summary>
    public void Attach(Slider slider) => _slider = slider;

    /// <summary>Sets the marks to draw over a scrubber spanning <paramref name="duration"/> (null marks are unset).</summary>
    public void SetMarks(Timecode? markIn, Timecode? markOut, Timecode duration)
    {
        if (markIn == _markIn && markOut == _markOut && duration.Ticks == _duration)
            return;
        _markIn = markIn;
        _markOut = markOut;
        _duration = duration.Ticks;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if ((_markIn is null && _markOut is null) || _duration <= 0)
            return;

        // The Fluent slider's thumb centre travels from half a thumb in from each edge, so map ticks onto that span.
        double inset = 10;
        if (_slider?.FindDescendantOfType<Thumb>() is { Bounds.Width: > 0 } thumb)
            inset = thumb.Bounds.Width / 2;
        double span = Math.Max(0, Bounds.Width - 2 * inset);
        double X(long ticks) => inset + span * Math.Clamp((double)ticks / _duration, 0, 1);

        double h = Bounds.Height;
        double top = Math.Max(0, h / 2 - 8), bottom = Math.Min(h, h / 2 + 8);
        double x0 = X(_markIn?.Ticks ?? 0);
        double x1 = X(_markOut?.Ticks ?? _duration);
        if (x1 > x0)
            context.FillRectangle(RangeFill, new Rect(x0, top, x1 - x0, bottom - top));
        if (_markIn is not null)
            context.DrawLine(TickPen, new Point(x0, top), new Point(x0, bottom));
        if (_markOut is not null)
            context.DrawLine(TickPen, new Point(x1, top), new Point(x1, bottom));
    }
}
