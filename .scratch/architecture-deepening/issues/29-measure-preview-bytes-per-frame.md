# 29: Measure preview bytes per frame

**What to build:** A regression gate for the ~0 Gen0/frame rule (ARCHITECTURE §1): we can see and assert how many managed bytes the preview path allocates per frame, headlessly and live in the app. Distinct from 21's throwaway go/no-go measurement: this one stays in the tree as a test. (Step 60 of PLAN.md, folded into this effort.)

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A headless xUnit test drives N preview frames over fixtures with 0, 1 and 3 effects plus a generator and asserts a per-frame byte ceiling (loose at first, tightened as 30–33 land)
- [ ] The Playback Statistics overlay shows allocated bytes per frame beside the GC counts
- [ ] The baseline numbers are appended to `plan/history/performance-log.md`
- [ ] PLAN.md step 60 row / Open-work entry points at this effort (tickets 29–33)
