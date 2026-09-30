# 08: Shared set-speed builder

**What to build:** Changing clip speed from the timeline or via MCP `set_clip_speed` applies the same edit
(duration and source mapping) through one Core builder.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] One Core set-speed builder; the timeline speed edit and the MCP tool call it; both loops are deleted
- [ ] Core tests cover speed-up, slow-down and linked companions
