using System;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure index math for reordering a clip's effect stack in the Inspector (PLAN.md step 51) — the
/// headlessly-testable half of the drag-to-reorder and move up/down gestures, split from the UI the same way
/// <see cref="Timeline.TimelineMath"/> backs <see cref="Timeline.TimelineControl"/> (step 12). Both return the
/// final stack index to hand to <c>MoveChainEffectCommand</c>; callers skip the edit when it equals the
/// effect's current index so no-op gestures don't pollute the undo history.
/// </summary>
public static class EffectReorder
{
    /// <summary>The target index for a one-step move up (<paramref name="delta"/> = −1) or down (+1) from
    /// <paramref name="index"/> in a stack of <paramref name="count"/>; clamps at the ends, never wraps.</summary>
    public static int StepIndex(int index, int count, int delta) =>
        count <= 0 ? 0 : Math.Clamp(index + delta, 0, count - 1);

    /// <summary>
    /// The final index for dropping the effect at <paramref name="sourceIndex"/> onto the section at
    /// <paramref name="targetIndex"/>: dropping in the target's top half inserts before it,
    /// <paramref name="bottomHalf"/> inserts after it. Accounts for the source's own removal shifting the
    /// insertion slot, and clamps to the stack of <paramref name="count"/>.
    /// </summary>
    public static int DropIndex(int sourceIndex, int targetIndex, bool bottomHalf, int count)
    {
        if (count <= 0)
            return 0;
        int slot = targetIndex + (bottomHalf ? 1 : 0); // insertion slot between items of the unmoved list
        int final = slot > sourceIndex ? slot - 1 : slot;
        return Math.Clamp(final, 0, count - 1);
    }
}
