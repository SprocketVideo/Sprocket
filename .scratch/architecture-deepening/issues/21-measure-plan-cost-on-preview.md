# 21: Measure what planning costs on the preview path

**What to build:** A go/no-go answer for 23: whether calling the render plan every preview tick keeps the
hot path at ~0 Gen0 allocations per frame (ARCHITECTURE §1), and what it costs in time.

**Blocked by:** None (can start immediately)

**Status:** ready-for-human

- [ ] A throwaway measurement outside the solution (per CLAUDE.md, no spike projects in the tree) plans a representative timeline (several tracks, effects, a transition, a nested sequence) at playback rate
- [ ] Records Gen0 collections and allocated bytes per frame, and time per plan
- [ ] Findings and the go/no-go (plus any plan changes needed, e.g. pooling) are appended to `plan/history/performance-log.md`
