# 32: Remove per-draw list and closure churn from the preview executor path

**What to build:** After 23 unifies preview onto the plan executor, the remaining per-frame list allocation and capturing draw callback are gone, as is the per-frame plan-object churn that step 60 had deferred until preview used the planner.

**Blocked by:** 30, 31

**Status:** ready-for-agent

- [ ] The per-draw layer list is an owned, reused buffer; the draw callback takes explicit state instead of capturing
- [ ] Plan construction on the preview path reuses its layer storage (or pools it, per 21's findings)
- [ ] Ticket 29's test reaches ~0 bytes per frame on the plain single-clip fixture; results appended to the performance log
