# 07: Shared ripple-trim, roll and slide builders in Core

**What to build:** Ripple trim, roll and slide produce the same edit from the timeline and from MCP, with the
same limits. The UI clamps at the limit; MCP reports the amount actually applied (consistent with 05).

**Blocked by:** 01, 06

**Status:** ready-for-agent

- [ ] Core builders for ripple trim (including downstream capture), roll and slide, reusing 06's span maths
- [ ] TimelineControl's commit paths and the MCP ripple_trim / roll_edit / slide_clip tools call them
- [ ] The MCP copies of the adjacent-clip lookup and the constant-forward-map check are deleted
- [ ] Core tests cover each edit at and beyond its limits
