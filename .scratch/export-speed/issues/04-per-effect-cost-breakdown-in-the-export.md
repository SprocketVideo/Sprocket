# 04: Per-effect cost breakdown in the export summary

**What to build:** After an export, the user can see where the time went — decode vs render vs encode, and which effects (e.g. stabilization, grain) dominated render.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Optional timing hooks in the effect pipeline, off when not requested
- [ ] The export completion summary lists the top effect costs
- [ ] Hooks add no per-frame allocation when disabled
- [ ] When 1–4 are done: FEATURES.md / README checked, DONE log appended to `plan/history/steps-58plus.md`, PLAN.md Open-work entry checked
