# 23: Preview renders through the executor

**What to build:** Preview draws the same frame export would, because it renders the same plan through the
same executor. Transitions now appear in preview.

**Blocked by:** 21, 22

**Status:** ready-for-agent

- [ ] A decode-ring frame-source adapter feeds the executor during playback and scrubbing
- [ ] The playback engine's own layer derivation, its copy of the plan's gating, and the preview surface's layer-kind switch are deleted
- [ ] An allocation profile confirms ~0 Gen0 per frame during playback (numbers in `plan/history/performance-log.md`)
- [ ] A parity test renders one timeline through both adapters and compares the output; the FEATURES.md row for transitions is amended
