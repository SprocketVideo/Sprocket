# 02: One effect-placement rule for every way of adding an effect

**What to build:** An added effect lands in the same stack position no matter how it was added: the
Inspector's "+ Effect", dropping onto a timeline clip, "apply to selected", a preset stack, or MCP
`add_effect`. The rule: Color Transform goes to the front; Stabilization goes just after a Color Transform
(or to the front if there is none); everything else appends. This fixes MCP appending Stabilization at the
end. Deliberate user-visible change: timeline drop and apply-to-selected now follow this rule instead of
always appending.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] One placement rule in Core, used by every add-effect path; the per-caller copies are deleted
- [ ] Core tests cover the rule (empty stack, existing Color Transform, existing Stabilization, plain append)
- [ ] An MCP test proves `add_effect` of Stabilization onto a clip with a Color Transform lands right after it
- [ ] The FEATURES.md row for effect application is amended in place for the drop behaviour change
