# 28: McpEditorSession becomes a thin adapter over the session

**What to build:** In tests, MCP tools run against the real editor behaviour instead of a hand-written fake,
so drift between the app and MCP is caught by the test suite.

**Blocked by:** 10, 25, 26, 27

**Status:** ready-for-agent

- [ ] McpEditorSession calls the editor session, and the Mcp* methods on MainWindow are deleted
- [ ] Mcp.Tests construct the real session with engine and exporter fakes; FakeEditorSession is deleted or reduced to those fakes
- [ ] The full Mcp.Tests suite passes against the real session
