# 02: One effect-placement rule for every way of adding an effect

**What to build:** An added effect lands in the same stack position no matter how it was added: the
Inspector's "+ Effect", dropping onto a timeline clip, "apply to selected", a preset stack, or MCP
`add_effect`. The rule: Color Transform goes to the front; Stabilization goes just after a Color Transform
(or to the front if there is none); everything else appends. This fixes MCP appending Stabilization at the
end. Deliberate user-visible change: timeline drop and apply-to-selected now follow this rule instead of
always appending.

**Blocked by:** None (can start immediately)

**Status:** resolved

- [x] One placement rule in Core, used by every add-effect path; the per-caller copies are deleted
- [x] Core tests cover the rule (empty stack, existing Color Transform, existing Stabilization, plain append)
- [x] An MCP test proves `add_effect` of Stabilization onto a clip with a Color Transform lands right after it
- [x] The FEATURES.md row for effect application is amended in place for the drop behaviour change

## Answer

Done. `EffectPlacement` (`Sprocket.Core.Commands`) owns the rule: `IndexFor(typeId, stack)` and
`AddCommand(clip, effect)` (an `InsertEffectAtCommand` at that index). Callers: Inspector "+ Effect" (its private
`PlacementCommand` / `StabilizationInsertIndex` copies deleted), timeline drop and apply-to-selected
(`TimelineControl`), the Effects browser's double-click apply (`MediaBrowserPanel.ApplyEffect`), and MCP
`add_effect` (description updated). Preset stacks route `Append` entries through `IndexFor`; their `Front`
anchor now skips any leading base-placed effect (`EffectPlacement.IsBasePlaced`: Color Transform or
Stabilization), so a front entry such as Posterize Time lands after a Stabilization as well as a Color Transform.
Left as plain appends, outside the rule's scope: the Inspector's Transform shortcuts (Fill Height, transform
presets; Transform always appends anyway), `Look` application (Color-only), and MCP copy-effects (copies keep the
source's order).
