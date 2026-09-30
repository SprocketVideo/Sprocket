# 14: Slip gesture, plus contract

**What to build:** Slip drags run as a gesture, and TimelineControl no longer holds any per-drag state: it
maps pointer events to a gesture and paints.

**Blocked by:** 11

**Status:** ready-for-agent

- [ ] A slip gesture with tests (clamps to available media)
- [ ] The DragKind switch and the remaining per-drag fields are gone from TimelineControl
- [ ] Every interactive timeline drag works as before (manual check list in the PR)
