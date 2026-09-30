# 33: Pump async shape and synthetic-layer wrappers (measure-gated)

**What to build:** Only if ticket 29's numbers still show churn after 32: the playback pump's timer/task allocations and the synthetic-layer wrappers (placeholder paints, generator/adjustment offscreen surfaces) are removed.

**Blocked by:** 32

**Status:** needs-triage

- [ ] Triage: decide from the post-32 measurement whether this is needed at all
- [ ] If needed: value-task pump methods and a reused timer instead of a per-wait delay task
- [ ] If needed: cached placeholder paints and a pooled offscreen surface sized to the sequence resolution
- [ ] On completion of 29–33: flip the step 60 ledger row, append the DONE log under `## Step 60` in `plan/history/steps-58plus.md`, archive `plan/features/preview-allocation-churn.md`
