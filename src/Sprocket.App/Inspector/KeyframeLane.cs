using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App.Inspector;

/// <summary>
/// A per-parameter keyframe lane for the Inspector (PLAN.md step 16b): a custom-drawn strip spanning the
/// clip's timeline range that shows an animated parameter's keyframes as diamonds, with the playhead marked.
/// Pointer gestures edit the keyframes — drag to move (one coalesced undo entry), double-click empty space to
/// add at that time, right-click a keyframe to delete, double-click a keyframe to toggle Hold↔Linear. The lane
/// only renders the value; the keyframe math lives in the pure <see cref="AnimatableEditing"/> /
/// <see cref="KeyframeLaneMath"/> helpers and every edit is handed back to the owner to run through the command
/// stack, so editing stays undoable by construction (step 10).
/// </summary>
public sealed class KeyframeLane : Control
{
    private const double LaneHeight = 22;
    private const double Pad = 5;          // left/right inset so edge keyframes aren't clipped
    private const double HitTolerancePx = 7;
    private const double Diamond = 4.5;

    private static readonly IBrush LaneBg = Brush("#1A1A22");
    private static readonly IBrush KeyFill = Brush("#9AA4B2");
    private static readonly IBrush KeyHold = Brush("#C9893F");   // held keyframes read differently (square-ish)
    private static readonly IBrush Accent = Brush("#6C5CE7");
    private static readonly Pen PlayheadPen = new(Brush("#6C5CE7"), 1);
    private static readonly Pen Frame = new(Brush("#2A2A33"), 1);

    private AnimatableValue _value = AnimatableValue.Constant(0);
    private long _rangeStart, _rangeEnd, _playhead;

    // Active keyframe drag: the keyframe's current time (re-anchored as it moves).
    private bool _dragging;
    private long _dragFromTicks;

    /// <summary>Raised when a keyframe drag begins / ends, so the owner can open / seal one coalescing scope.</summary>
    public event Action? DragStarted;

    /// <summary>Raised when a keyframe drag ends.</summary>
    public event Action? DragEnded;

    /// <summary>Raised with the new value for any edit (move / add / delete / interpolation). The owner runs it
    /// through the command stack; <c>coalesce</c> is true mid-drag so the whole drag is one undo entry.</summary>
    public event Action<AnimatableValue, bool>? Edited;

    public KeyframeLane()
    {
        Height = LaneHeight;
        Margin = new Thickness(0, 2, 0, 2);
        ClipToBounds = true;
        ToolTip.SetTip(this,
            "Drag a keyframe to move it · double-click to add · double-click a keyframe to toggle Hold/Linear · right-click to delete");
        DoubleTapped += OnDoubleTappedHandler;
    }

    /// <summary>Updates what the lane displays (called by the Inspector's value refresher); triggers a redraw.</summary>
    public void Update(AnimatableValue value, long rangeStartTicks, long rangeEndTicks, long playheadTicks)
    {
        _value = value;
        _rangeStart = rangeStartTicks;
        _rangeEnd = rangeEndTicks;
        _playhead = playheadTicks;
        InvalidateVisual();
    }

    private double LaneWidth => Math.Max(1, Bounds.Width - 2 * Pad);

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.DrawRectangle(LaneBg, Frame, new RoundedRect(rect, 3));

        double midY = Bounds.Height / 2;

        // Playhead marker.
        double px = Pad + KeyframeLaneMath.XAt(_playhead, _rangeStart, _rangeEnd, LaneWidth);
        ctx.DrawLine(PlayheadPen, new Point(px, 2), new Point(px, Bounds.Height - 2));

        // Connecting line through the keyframes, then the keyframe markers.
        var line = new Pen(KeyFill, 1);
        Point? prev = null;
        foreach (Keyframe k in _value.Keyframes)
        {
            double x = Pad + KeyframeLaneMath.XAt(k.Time.Ticks, _rangeStart, _rangeEnd, LaneWidth);
            var here = new Point(x, midY);
            if (prev is { } p)
                ctx.DrawLine(line, p, here);
            prev = here;
        }
        foreach (Keyframe k in _value.Keyframes)
        {
            double x = Pad + KeyframeLaneMath.XAt(k.Time.Ticks, _rangeStart, _rangeEnd, LaneWidth);
            DrawKeyframe(ctx, x, midY, k.Interpolation);
        }
    }

    private static void DrawKeyframe(DrawingContext ctx, double cx, double cy, Interpolation interp)
    {
        IBrush fill = interp == Interpolation.Hold ? KeyHold : KeyFill;
        if (interp == Interpolation.Hold)
        {
            // A small square for a held keyframe.
            ctx.DrawRectangle(fill, null, new Rect(cx - Diamond, cy - Diamond, Diamond * 2, Diamond * 2));
            return;
        }
        // A diamond for a linear keyframe.
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(new Point(cx, cy - Diamond), true);
            g.LineTo(new Point(cx + Diamond, cy));
            g.LineTo(new Point(cx, cy + Diamond));
            g.LineTo(new Point(cx - Diamond, cy));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, null, geo);
    }

    // ── Pointer interaction ─────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_value.IsAnimated)
            return;

        Point p = e.GetPosition(this);
        double laneX = p.X - Pad;
        int index = KeyframeLaneMath.NearestKeyframeIndex(
            laneX, _value.Keyframes, _rangeStart, _rangeEnd, LaneWidth, HitTolerancePx);

        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (index >= 0)
            {
                Edited?.Invoke(AnimatableEditing.RemoveKeyframe(_value, _value.Keyframes[index].Time), false);
                e.Handled = true;
            }
            return;
        }

        if (props.IsLeftButtonPressed && index >= 0)
        {
            _dragging = true;
            _dragFromTicks = _value.Keyframes[index].Time.Ticks;
            DragStarted?.Invoke();
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;

        long to = KeyframeLaneMath.TicksAt(e.GetPosition(this).X - Pad, _rangeStart, _rangeEnd, LaneWidth);
        if (to == _dragFromTicks)
            return;

        AnimatableValue next = AnimatableEditing.MoveKeyframe(_value, new Timecode(_dragFromTicks), new Timecode(to));
        _dragFromTicks = to;
        Edited?.Invoke(next, true); // coalesced: the whole drag is one undo entry
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            DragEnded?.Invoke();
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            DragEnded?.Invoke();
        }
    }

    private void OnDoubleTappedHandler(object? sender, TappedEventArgs e)
    {
        if (!_value.IsAnimated)
            return;

        Point p = e.GetPosition(this);
        double laneX = p.X - Pad;
        int index = KeyframeLaneMath.NearestKeyframeIndex(
            laneX, _value.Keyframes, _rangeStart, _rangeEnd, LaneWidth, HitTolerancePx);

        if (index >= 0)
        {
            // Toggle the keyframe's interpolation mode.
            Keyframe k = _value.Keyframes[index];
            Interpolation mode = k.Interpolation == Interpolation.Hold ? Interpolation.Linear : Interpolation.Hold;
            Edited?.Invoke(AnimatableEditing.SetInterpolation(_value, k.Time, mode), false);
        }
        else
        {
            // Add a keyframe at the clicked time carrying the value the parameter currently has there.
            long ticks = KeyframeLaneMath.TicksAt(laneX, _rangeStart, _rangeEnd, LaneWidth);
            var t = new Timecode(ticks);
            Edited?.Invoke(AnimatableEditing.UpsertKeyframe(_value, t, _value.Evaluate(t)), false);
        }
        e.Handled = true;
    }

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
