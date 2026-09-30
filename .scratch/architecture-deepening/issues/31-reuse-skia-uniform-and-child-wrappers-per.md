# 31: Reuse Skia uniform and child wrappers per compiled effect

**What to build:** Per-effect Skia wrapper churn (uniform blocks, child collections, uniform float arrays) no longer scales with layers × effects per frame.

**Blocked by:** 29

**Status:** ready-for-agent

- [ ] Uniform and child wrappers are reused per compiled effect and mutated in place; per-draw uniform arrays become reused fields
- [ ] Interface `foreach` loops over layer/effect lists in the draw path become index loops (no boxed enumerators)
- [ ] Per-draw shaders stay per draw unless ticket 29's numbers say otherwise
- [ ] Ticket 29's ceiling is tightened; golden-frame tests unchanged
