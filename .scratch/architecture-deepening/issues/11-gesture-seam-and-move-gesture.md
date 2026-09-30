# 11: Gesture seam, plus a Move gesture

**What to build:** Clip move-drags on the timeline run through a gesture module that a test can drive without
the control: `Begin(model, hit) → Update(pointerTicks) → preview state · Commit() → edit command`. Snap-point
building lives inside the gesture module. The drag feels identical to the user.

**Blocked by:** 05

**Status:** ready-for-agent

- [ ] The gesture interface is defined; Move is its first gesture and commits via the 05 move builder
- [ ] Snap-point building moves out of TimelineControl into the gesture module
- [ ] Tests drive a move gesture (snap, cross-track, origin clamp) with no Avalonia control
- [ ] TimelineControl's move-drag fields are gone; it hit-tests, forwards ticks and paints the preview
