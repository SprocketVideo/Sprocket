# 09: One "split the companion clips spanning a cut" helper

**What to build:** Every edit that cuts a clip also cuts its linked companions spanning the cut, the same way
each time, with the right halves in a fresh link group. That covers split, add frame hold, insert frame-hold
segment, and switch multicam angle.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] One Core helper; all five current copies use it and are deleted
- [ ] Frame-hold and angle-switch orchestration moves out of TimelineControl into Core so it is testable
- [ ] Core tests: companions split, the right-hand link group is new, and a companion not spanning the cut is untouched
