using Sprocket.App.Inspector;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The pure index math behind the Inspector's effect-stack reordering (PLAN.md step 51): the move up/down
/// affordance and the header drag-to-reorder drop-index resolution. The gesture wiring itself rests on these
/// plus manual verification, mirroring the InspectorTests / TimelineMath split.
/// </summary>
public class EffectReorderTests
{
    // ── StepIndex (move up / move down) ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 3, -1, 0)] // up from the middle
    [InlineData(1, 3, +1, 2)] // down from the middle
    [InlineData(0, 3, -1, 0)] // up at the top clamps — no wrap to the end
    [InlineData(2, 3, +1, 2)] // down at the bottom clamps — no wrap to the start
    [InlineData(0, 1, +1, 0)] // single-item stack can't move
    public void StepIndex_Steps_And_Clamps(int index, int count, int delta, int expected) =>
        Assert.Equal(expected, EffectReorder.StepIndex(index, count, delta));

    // ── DropIndex (drag a section header onto another section) ──────────────────────────────────────

    [Theory]
    // Dragging item 0 downward: top half of a lower target inserts before it, bottom half after it.
    [InlineData(0, 1, false, 3, 0)] // onto item 1's top half → still before item 1 → no-op
    [InlineData(0, 1, true, 3, 1)]  // onto item 1's bottom half → after item 1
    [InlineData(0, 2, true, 3, 2)]  // onto the last item's bottom half → end of stack
    // Dragging item 2 upward: the source's own removal doesn't shift slots above it.
    [InlineData(2, 0, false, 3, 0)] // onto item 0's top half → head of stack
    [InlineData(2, 0, true, 3, 1)]  // onto item 0's bottom half → between 0 and 1
    [InlineData(2, 1, true, 3, 2)]  // onto item 1's bottom half → right where it already is → no-op
    // Dropping onto itself is always a no-op.
    [InlineData(1, 1, false, 3, 1)]
    [InlineData(1, 1, true, 3, 1)]
    public void DropIndex_Resolves_The_Final_Stack_Position(
        int source, int target, bool bottomHalf, int count, int expected) =>
        Assert.Equal(expected, EffectReorder.DropIndex(source, target, bottomHalf, count));

    [Fact]
    public void DropIndex_Clamps_Degenerate_Input()
    {
        Assert.Equal(0, EffectReorder.DropIndex(0, 0, true, 0));  // empty stack
        Assert.Equal(1, EffectReorder.DropIndex(0, 5, true, 2));  // stale target index clamps into range
    }
}
