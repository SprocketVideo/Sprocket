# 26: Marks and range edits move into the session

**What to build:** Setting In/Out marks, clearing a conflicting mark, and lift/extract with their mark
fallbacks behave identically from the UI and MCP, because both call the editor session.

**Blocked by:** 25

**Status:** ready-for-agent

- [ ] Mark rules and lift/extract preconditions move from MainWindow into the session; the MCP copy is deleted
- [ ] Tests cover the conflicting-mark rule and each lift/extract fallback
