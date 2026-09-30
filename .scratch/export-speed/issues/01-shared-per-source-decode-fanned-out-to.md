# 01: Shared per-source decode fanned out to export render workers

**What to build:** Export decodes each source once and fans frames out to the render workers instead of every worker decoding every frame; Final Export output stays byte-identical. This is the half of phase 3b that most likely explains why GPU decode lost.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] One decoder per source feeds the render workers in frame order
- [ ] Final Export output is byte-identical to before (existing determinism tests pass)
- [ ] Before/after throughput against the phase-3 numbers is recorded in `plan/features/export-speed.md`
- [ ] GPU decode is re-measured under shared decode; the recommendation on making it more than an env opt-in is written down
