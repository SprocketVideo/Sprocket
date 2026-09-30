# 04: MCP ripple delete uses the app's version

**What to build:** MCP `ripple_delete` closes gaps correctly when several removed clips share a track: each
surviving clip shifts once, by the total duration removed before it, not once per removed clip.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] MCP ripple delete calls the Core ripple-delete builder; its own shift loop is deleted
- [ ] Regression test: a ripple delete that removes several clips on one track leaves downstream clips at the correct, gap-free positions
- [ ] Undo restores the original layout as one entry
