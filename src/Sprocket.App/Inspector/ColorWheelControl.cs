using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Sprocket.App.Inspector;

/// <summary>
/// One Lift/Gamma/Gain trackball for the Color Wheels effect: a hue disc (dimmed toward the neutral
/// centre) with a draggable puck whose position is the zero-sum R/G/B tint (<see cref="ColorWheelMath"/>).
/// Follows <see cref="KeyframeLane"/>'s contract with the Inspector: the control renders and raises
/// events, the owner runs every edit through the command stack — <see cref="DragStarted"/>/<see
/// cref="DragEnded"/> bracket one coalescing scope per drag, <see cref="PuckChanged"/> reports each move,
/// and <see cref="ResetRequested"/> is the double-click recentre. Shift-drag moves the puck at 0.1× for
/// fine trims (the Resolve trackball convention). <see cref="Update"/> repositions the puck
/// programmatically (called by the owner's value refresher under its suppress guard).
/// </summary>
public sealed class ColorWheelControl : Control
{
    private const double PuckRadius = 5;
    private const double FineScale = 0.1;

    private static readonly Pen RimPen = new(Palette.EdgeBrush, 1);
    private static readonly Pen AnimatedRimPen = new(Palette.AccentBrush, 1.5);
    private static readonly Pen CrosshairPen = new(new ImmutableSolidColorBrush(Colors.White, 0.12), 1);
    private static readonly Pen PuckPen = new(Brushes.White, 1.5);
    private static readonly IBrush PuckFill = new ImmutableSolidColorBrush(Colors.Black, 0.35);

    // The hue sweep, oriented so the channel axes land where ColorWheelMath puts them (R at math angle 0°
    // = screen right, G at 120° = upper left, B at 240° = lower left; screen y is inverted). Conic stops
    // run clockwise from 12 o'clock, so math angle θ sits at fraction (90 − θ)/360 (mod 1).
    private static readonly IBrush HueSweep = new ConicGradientBrush
    {
        Center = RelativePoint.Center,
        GradientStops =
        {
            new GradientStop(Color.Parse("#80FF00"), 0.0),      // chartreuse (wrap point)
            new GradientStop(Color.Parse("#FFFF00"), 1.0 / 12), // yellow
            new GradientStop(Color.Parse("#FF0000"), 3.0 / 12), // red — math 0°, screen right
            new GradientStop(Color.Parse("#FF00FF"), 5.0 / 12), // magenta
            new GradientStop(Color.Parse("#0000FF"), 7.0 / 12), // blue — math 240°
            new GradientStop(Color.Parse("#00FFFF"), 9.0 / 12), // cyan
            new GradientStop(Color.Parse("#00FF00"), 11.0 / 12),// green — math 120°
            new GradientStop(Color.Parse("#80FF00"), 1.0),
        },
    };

    // Neutral-centre dim: opaque panel colour in the middle fading out at the rim, so the disc reads as
    // "no tint" at rest and saturates toward the edge like a grading trackball.
    private static readonly IBrush CenterDim = new RadialGradientBrush
    {
        // Center/origin/radius defaults (50%) already fit the disc exactly.
        GradientStops =
        {
            new GradientStop(Palette.PanelBg, 0.0),
            new GradientStop(Color.FromArgb(0xD8, Palette.PanelBg.R, Palette.PanelBg.G, Palette.PanelBg.B), 0.55),
            new GradientStop(Color.FromArgb(0x60, Palette.PanelBg.R, Palette.PanelBg.G, Palette.PanelBg.B), 1.0),
        },
    };

    private double _puckX, _puckY;   // unit-disc coordinates (x right, y up)
    private bool _animated;
    private bool _dragging;
    private Point _dragStartPointer;
    private (double X, double Y) _dragStartPuck;

    /// <summary>Raised when a puck drag begins, so the owner can open one coalescing scope.</summary>
    public event Action? DragStarted;

    /// <summary>Raised when a puck drag ends.</summary>
    public event Action? DragEnded;

    /// <summary>Raised with the new unit-disc puck position for every move (and the initial press).</summary>
    public event Action<double, double>? PuckChanged;

    /// <summary>Raised on double-click: recentre the tint (one discrete undoable edit, owner-side).</summary>
    public event Action? ResetRequested;

    public ColorWheelControl()
    {
        Width = 84;
        Height = 84;
        ClipToBounds = false;
        ToolTip.SetTip(this,
            "Drag to tint (Shift = fine) · double-click to recentre · master and channel sliders below");
        DoubleTapped += (_, e) =>
        {
            ResetRequested?.Invoke();
            e.Handled = true;
        };
    }

    /// <summary>Repositions the puck (unit-disc coords) and sets the animated rim state; triggers a redraw.
    /// Called by the Inspector's value refresher — never raises <see cref="PuckChanged"/>.</summary>
    public void Update(double x, double y, bool animated)
    {
        _puckX = x;
        _puckY = y;
        _animated = animated;
        InvalidateVisual();
    }

    private double DiscRadius => Math.Min(Bounds.Width, Bounds.Height) / 2 - 1;
    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    public override void Render(DrawingContext ctx)
    {
        Point c = Center;
        double r = DiscRadius;
        var disc = new EllipseGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
        ctx.DrawGeometry(HueSweep, null, disc);
        ctx.DrawGeometry(CenterDim, null, disc);
        ctx.DrawLine(CrosshairPen, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
        ctx.DrawLine(CrosshairPen, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));
        ctx.DrawEllipse(null, _animated ? AnimatedRimPen : RimPen, c, r, r);

        // Puck: unit coords → pixels (screen y down), kept inside the rim.
        double reach = r - PuckRadius - 1;
        var p = new Point(c.X + _puckX * reach, c.Y - _puckY * reach);
        ctx.DrawEllipse(PuckFill, PuckPen, p, PuckRadius, PuckRadius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        _dragStartPointer = e.GetPosition(this);
        _dragStartPuck = (_puckX, _puckY);
        e.Pointer.Capture(this);
        DragStarted?.Invoke();
        // A plain (unshifted) press jumps the puck to the pointer, like dragging a slider thumb from
        // anywhere on the track; a Shift-press anchors fine movement at the current puck instead.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            MovePuckTo(PointerToUnit(_dragStartPointer));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        Point pos = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            double reach = Math.Max(1, DiscRadius - PuckRadius - 1);
            MovePuckTo((
                _dragStartPuck.X + (pos.X - _dragStartPointer.X) * FineScale / reach,
                _dragStartPuck.Y - (pos.Y - _dragStartPointer.Y) * FineScale / reach));
        }
        else
        {
            MovePuckTo(PointerToUnit(pos));
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
        e.Handled = true;
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

    private void EndDrag(IPointer pointer)
    {
        if (!_dragging)
            return;
        _dragging = false;
        pointer.Capture(null);
        DragEnded?.Invoke();
    }

    private (double X, double Y) PointerToUnit(Point pos)
    {
        Point c = Center;
        double reach = Math.Max(1, DiscRadius - PuckRadius - 1);
        return ((pos.X - c.X) / reach, (c.Y - pos.Y) / reach);
    }

    private void MovePuckTo((double X, double Y) unit)
    {
        double radius = Math.Sqrt(unit.X * unit.X + unit.Y * unit.Y);
        if (radius > 1.0)
            unit = (unit.X / radius, unit.Y / radius);
        _puckX = unit.X;
        _puckY = unit.Y;
        InvalidateVisual();
        PuckChanged?.Invoke(_puckX, _puckY);
    }
}
