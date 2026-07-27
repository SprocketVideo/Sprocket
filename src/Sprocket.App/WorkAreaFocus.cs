using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace Sprocket.App;

/// <summary>
/// One of the editor's major work areas — the coarse granularity at which keyboard focus moves around the
/// shell (UI.md §3). These are the panes a user thinks of as "where I am", not the individual widgets inside
/// them.
/// </summary>
public enum WorkArea
{
    /// <summary>The Project panel (media bin / effects / transitions / audio + mixer tabs).</summary>
    Project,

    /// <summary>The monitor area (the Program / Source preview surface and its transport).</summary>
    Monitor,

    /// <summary>The timeline.</summary>
    Timeline,

    /// <summary>The Inspector.</summary>
    Inspector,
}

/// <summary>
/// The pure ring arithmetic + key mapping behind the editor's work-area focus model. Plain
/// <c>Tab</c> / <c>Shift+Tab</c> in the shell steps between <see cref="WorkArea"/>s rather than crawling
/// every focusable widget, and <c>Shift+1</c>–<c>Shift+4</c> activate one directly.
/// <para>
/// This is the convention in leading NLEs (Premiere's "activate panel by number" is <c>Shift+1</c>…, and
/// its panels are activated deliberately rather than reached by tabbing through toolbars). The deliberate
/// departure: Sprocket has four work areas rather than a dozen dockable panels, so the digits are
/// <c>Shift+1</c>–<c>4</c> only and the ring is short enough to be predictable.
/// </para>
/// <para>
/// Kept free of any UI state so it is directly testable; <c>MainWindow</c> owns the focus targets, the
/// availability rules (a hidden pane is skipped) and the active-area affordance.
/// </para>
/// </summary>
public static class WorkAreaFocus
{
    /// <summary>
    /// Style class marking a container whose fields keep local <c>Tab</c> traversal — a form-like grouping
    /// such as an Inspector section card. <c>Tab</c> walks the group's own fields first and only escalates to
    /// the work-area ring once it runs off the group's edge.
    /// </summary>
    public const string FieldGroupClass = "fieldGroup";

    /// <summary>Style class applied to the pane <c>Border</c> of the currently active work area.</summary>
    public const string ActiveAreaClass = "activeArea";

    // Left-to-right, then down to the timeline: the reading order of the shell (UI.md §3).
    private static readonly WorkArea[] RingOrder =
        [WorkArea.Project, WorkArea.Monitor, WorkArea.Timeline, WorkArea.Inspector];

    /// <summary>The focus ring, in Tab order.</summary>
    public static IReadOnlyList<WorkArea> Ring => RingOrder;

    /// <summary>
    /// The next work area in <paramref name="forward"/> (Tab) or reverse (Shift+Tab) order, skipping areas
    /// that <paramref name="isAvailable"/> rejects (a hidden Project / Inspector pane). Returns
    /// <paramref name="current"/> when it is the only available area, and <see langword="null"/> when none is
    /// — so the caller can leave focus alone rather than park it somewhere invisible.
    /// </summary>
    public static WorkArea? Advance(WorkArea current, bool forward, Func<WorkArea, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);

        int count = RingOrder.Length;
        int from = Array.IndexOf(RingOrder, current);
        if (from < 0)
            from = 0;
        int step = forward ? 1 : -1;

        // Walk the whole ring: the last candidate examined is `current` itself, which is what makes a
        // single-available-area ring a no-op rather than a null.
        for (int i = 1; i <= count; i++)
        {
            WorkArea candidate = RingOrder[((from + (step * i)) % count + count) % count];
            if (isAvailable(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Maps the digit of a <c>Shift+&lt;digit&gt;</c> chord to the work area it activates —
    /// <c>1</c> Project, <c>2</c> Timeline, <c>3</c> Monitor, <c>4</c> Inspector. The two most-used areas get
    /// the two easiest chords (Premiere likewise puts the Project panel on <c>Shift+1</c>); the numbering
    /// deliberately does not mirror <see cref="Ring"/>'s layout order for that reason. Number-row and numpad
    /// digits both count.
    /// </summary>
    public static bool TryDirectKey(Key key, out WorkArea area)
    {
        switch (key)
        {
            case Key.D1 or Key.NumPad1: area = WorkArea.Project; return true;
            case Key.D2 or Key.NumPad2: area = WorkArea.Timeline; return true;
            case Key.D3 or Key.NumPad3: area = WorkArea.Monitor; return true;
            case Key.D4 or Key.NumPad4: area = WorkArea.Inspector; return true;
            default: area = WorkArea.Timeline; return false;
        }
    }
}
