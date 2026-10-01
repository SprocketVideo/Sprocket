using Sprocket.Core.Model;

namespace Sprocket.Core.Commands;

/// <summary>
/// The one rule for where a newly added effect lands in a clip's stack, shared by every add-effect path (the
/// Inspector's "+ Effect", a timeline drop, apply-to-selected, a preset stack, MCP <c>add_effect</c>). Stack order
/// is processing order (§5d):
/// <list type="bullet">
/// <item>The input <b>Color Transform</b> must run before the creative grade (PLAN.md step 37), so it goes to the
/// front.</item>
/// <item><b>Stabilization</b> reframes the source before any creative grade/transform reads it, so it goes just
/// after a Color Transform if one is present, else to the front (as in leading editors, where the stabilizer sits
/// at the base of the stack).</item>
/// <item>Everything else appends.</item>
/// </list>
/// </summary>
public static class EffectPlacement
{
    /// <summary>Whether <paramref name="effectTypeId"/> is placed at the base of the stack (Color Transform,
    /// Stabilization) rather than appended.</summary>
    public static bool IsBasePlaced(string effectTypeId) =>
        effectTypeId is EffectTypeIds.ColorTransform or EffectTypeIds.Stabilization;

    /// <summary>The index an effect of <paramref name="effectTypeId"/> should be inserted at in <paramref name="stack"/>.</summary>
    public static int IndexFor(string effectTypeId, IReadOnlyList<EffectInstance> stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (effectTypeId == EffectTypeIds.ColorTransform)
            return 0;
        if (effectTypeId == EffectTypeIds.Stabilization)
        {
            for (int i = 0; i < stack.Count; i++)
                if (stack[i].EffectTypeId == EffectTypeIds.ColorTransform)
                    return i + 1;
            return 0;
        }
        return stack.Count;
    }

    /// <summary>The command that adds <paramref name="effect"/> to <paramref name="clip"/> at <see cref="IndexFor"/>'s
    /// position (computed now, against the clip's current stack); undo removes it.</summary>
    public static IEditCommand AddCommand(Clip clip, EffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effect);
        return new InsertEffectAtCommand(clip, effect, IndexFor(effect.EffectTypeId, clip.Effects));
    }
}
