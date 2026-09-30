# 10: Clip placement shared by the real MCP session and the test fake

**What to build:** Placing media on the timeline via MCP follows the same track-resolution rules as the app,
and the MCP tests exercise those real rules rather than a third copy in the test fake.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] The real MCP session's clip placement and the test fake's both call Core ClipPlacement
- [ ] The fake's own track-resolution checks are deleted
- [ ] MCP placement tests still pass, now against the shared rules
