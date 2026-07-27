using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Sprocket.App.Controls;

/// <summary>
/// How a drag-scrubbed field reaches its parameter: the current model value, the range/step that set the
/// scrub sensitivity, the coalescing scope brackets, and the commit. Mirrors the contract
/// <see cref="Inspector.ColorWheelControl"/> and <see cref="Inspector.KeyframeLane"/> already use with the
/// Inspector — the control gestures, the owner runs every edit through the command stack.
/// </summary>
/// <param name="Get">Reads the parameter's current value in model units.</param>
/// <param name="Set">Commits a value in model units; <c>coalescing</c> is true for every move inside a drag,
/// so one scrub collapses to a single undo entry.</param>
/// <param name="Min">Lower bound, model units.</param>
/// <param name="Max">Upper bound, model units.</param>
/// <param name="Step">Editing step, model units — feeds the sensitivity floor.</param>
/// <param name="BeginDrag">Opens the owner's coalescing scope.</param>
/// <param name="EndDrag">Closes it.</param>
/// <param name="Integer">Whole-number parameter: the scrub snaps to integers.</param>
public sealed record DragNumberOptions(
    Func<double> Get,
    Action<double, bool> Set,
    double Min,
    double Max,
    double Step,
    Action BeginDrag,
    Action EndDrag,
    bool Integer = false);

/// <summary>
/// Makes a numeric <see cref="TextBox"/> a "scrubby slider": drag horizontally over it to change the value,
/// click without dragging to focus it and type. This is the numeric-field gesture in Premiere Pro, After
/// Effects and Resolve, so it's what an editor coming from those tools reaches for first.
/// <para>
/// The gesture only arms while the box is unfocused, so once you've clicked in to type, ordinary text
/// selection by dragging works exactly as it always did. Shift coarsens the scrub, Ctrl refines it
/// (<see cref="DragNumberMath"/>), and Escape mid-drag restores the value the drag started from.
/// </para>
/// </summary>
public static class DragNumber
{
    public static void Attach(TextBox box, DragNumberOptions options)
    {
        bool dragging = false;
        bool scrubbed = false;   // travel exceeded the threshold — this is a scrub, not a click
        double originX = 0;
        double startValue = 0;

        void Finish(IPointer? pointer)
        {
            if (!dragging)
                return;
            dragging = false;
            pointer?.Capture(null);
            box.Cursor = null;
            options.EndDrag();
        }

        // Tunnel, because TextBox's own handler marks the press handled to place the caret — by the bubble
        // phase we'd never see it.
        box.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (box.IsFocused || !e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
                return;
            dragging = true;
            scrubbed = false;
            originX = e.GetPosition(box).X;
            startValue = options.Get();
            e.Pointer.Capture(box);
            // Swallow the press so the TextBox doesn't focus itself and start a text selection; a click that
            // never becomes a scrub focuses it on release instead.
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        box.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (!dragging)
                return;
            double dx = e.GetPosition(box).X - originX;
            if (!scrubbed)
            {
                if (Math.Abs(dx) < DragNumberMath.DragThresholdPx)
                    return;
                scrubbed = true;
                box.Cursor = new Cursor(StandardCursorType.SizeWestEast);
                options.BeginDrag();
            }
            double next = DragNumberMath.Apply(
                startValue, dx, options.Min, options.Max, options.Step,
                coarse: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                fine: e.KeyModifiers.HasFlag(KeyModifiers.Control),
                options.Integer);
            options.Set(next, true); // coalescing: the whole scrub is one undo entry
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        box.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!dragging)
                return;
            bool wasScrub = scrubbed;
            if (!wasScrub)
            {
                // Never crossed the threshold: treat it as the plain click it looked like. Selecting all
                // means typing a replacement value takes no extra keystroke, matching the same fields in
                // Premiere / After Effects.
                dragging = false;
                e.Pointer.Capture(null);
                box.Focus();
                box.SelectAll();
            }
            else
            {
                Finish(e.Pointer);
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        box.PointerCaptureLost += (_, _) => Finish(null);

        // Hover affordance: the ↔ cursor advertises the gesture, but only while the field is in scrub mode —
        // once focused it's a text field and wants the I-beam.
        box.PointerEntered += (_, _) =>
        {
            if (!box.IsFocused)
                box.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        };
        box.PointerExited += (_, _) =>
        {
            if (!dragging)
                box.Cursor = null;
        };
        box.GotFocus += (_, _) => box.Cursor = null;

        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!dragging || e.Key != Key.Escape)
                return;
            options.Set(startValue, true); // still inside the drag's scope, so the cancel doesn't add an entry
            Finish(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
