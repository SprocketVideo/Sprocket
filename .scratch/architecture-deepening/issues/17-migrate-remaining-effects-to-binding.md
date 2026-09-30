# 17: Convert the remaining registered effects

**What to build:** Every registry-backed video effect (about 17 besides Day for Night) states its ranges once,
in the catalog. Rendering is unchanged.

**Blocked by:** 16

**Status:** ready-for-agent

- [ ] Each registered effect uses the declarative binding; their duplicated fallbacks and clamps are deleted
- [ ] Split into two batches by category if one context can't hold it; each batch is green on its own
- [ ] All Render tests pass unchanged
