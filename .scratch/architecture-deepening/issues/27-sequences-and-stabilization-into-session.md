# 27: Sequences and stabilization status move into the session

**What to build:** Creating a sequence gets the same default tracks everywhere, and a clip's stabilization
status is looked up the same way by the UI banner and by MCP, both through the editor session.

**Blocked by:** 25

**Status:** ready-for-agent

- [ ] New-sequence defaults and the enabled-stabilization lookup move from MainWindow into the session
- [ ] Tests cover the default tracks and the stabilization lookup (none / disabled / enabled)
