# 30: Stop re-resolving effect and generator parameters every frame

**What to build:** Effected and generator clips play back without rebuilding their parameter dictionaries and resolved-effect objects on every repaint. The render graph stays a pure function of (project, time); any cache lives outside Core, on the executor/preview side that 23 establishes.

**Blocked by:** 29, 23

**Status:** ready-for-agent

- [ ] Resolved effect/generator state is cached per clip, invalidated by clip change and (for keyframed parameters) evaluated time
- [ ] Generators with no parameters take a fast path that allocates nothing
- [ ] Ticket 29's test shows the drop for the 1- and 3-effect and generator fixtures; the ceiling is tightened
- [ ] Golden-frame and export tests are unchanged (no pixel differences)
