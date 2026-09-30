# 25: Editor session module, with export orchestration

**What to build:** Getting the editor ready for an export (begin the export, deactivate the Source monitor,
suspend playback, pause stabilization analysis) and restoring it afterwards happens in one place. Every
export entry point (export dialog, export queue, render cache, MCP export) uses it, and export range
validation moves with it.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] An editor-session module outside the window (testable from App.Tests) owns export orchestration
- [ ] The playback engine and exporter sit behind seams with two adapters: the real ones and test fakes
- [ ] All four copies of the quiesce sequence in MainWindow are deleted
- [ ] Tests cover quiesce/restore ordering, including the cancel and failure paths
