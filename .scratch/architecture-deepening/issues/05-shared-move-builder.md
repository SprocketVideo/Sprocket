# 05: Shared move builder

**What to build:** Moving a clip (timeline drag, nudge, or MCP `move_clip`) moves the clip and its linked or
selected companions rigidly by one delta, clamped so no member crosses the timeline origin. This matches
Premiere, Resolve, Final Cut and Avid, which stop the whole group at 00:00 rather than break sync. MCP applies
the clamped move and reports the requested and applied start instead of throwing when a linked partner would
cross zero. (Today it silently clamps the addressed clip but throws for partners.)

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] One Core move builder (group move with an optional track change) returns the command plus the delta it actually applied
- [ ] Timeline move-drag, nudge and MCP `move_clip` all use it; MCP's own composition is deleted
- [ ] The MCP result reports when the move was clamped, and the applied start
- [ ] Core tests: rigid group clamp at the origin, cross-track move, linked partners staying in sync
