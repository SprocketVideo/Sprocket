# 01: Move ramp- and direction-aware span math onto Clip in Core

**What to build:** Prefactor: the source-span / timeline-span math that the timeline's plain trim uses for reversed and ramped clips lives on the clip model in Core, testable and shared.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Span math moved into Core with unit tests; timeline trim uses it with no behaviour change
- [ ] Coordinate with architecture-deepening 06/07 (shared trim and ripple/roll/slide builders) — whichever lands second rebases
