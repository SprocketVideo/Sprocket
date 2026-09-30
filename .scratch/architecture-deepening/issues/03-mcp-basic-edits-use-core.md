# 03: MCP split, duplicate, delete, enable, link and unlink call the Core builders

**What to build:** The MCP split/duplicate/delete/enable/link/unlink tools produce exactly the edits the
timeline produces, because they call the same Core builders. MCP delete gains the locked-track and dedupe
rules the app already applies to linked companions.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] Each of these MCP tools resolves ids and calls the Core builder; its own command composition is deleted
- [ ] An MCP test covers deleting a clip whose linked partner sits on a locked track (the partner is untouched)
- [ ] Existing MCP tests for these tools pass (tool contracts unchanged)
