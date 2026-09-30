# 22: Render plan executor, used by export

**What to build:** Export composites frames through one Render plan executor fed by a frame-source adapter;
exported output is unchanged. The test-only generic executor in Core is removed and its tests are rewritten
against the real executor on a CPU raster surface.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Export's plan compositing (including nested sequences and side content) moves into a Render executor behind a frame-source seam
- [ ] Export uses it with a seek-decode adapter; Export tests pass unchanged
- [ ] The Core generic executor and its compositor seam are deleted (or replaced by this executor); the render-graph tests now run against the executor with a raster surface and a fake frame source
