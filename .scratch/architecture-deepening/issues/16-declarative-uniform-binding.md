# 16: Declarative uniform binding, with Day for Night as the first effect converted

**What to build:** An effect's render binding declares which parameter feeds which uniform, and takes its
defaults and clamps from the catalog descriptor, so an effect's ranges are stated once. Day for Night is the
tracer bullet and renders identically.

**Blocked by:** 15

**Status:** ready-for-agent

- [ ] A declarative binding form in Render that reads defaults and ranges from the descriptor
- [ ] Day for Night uses it; its hand-written fallbacks and clamps are deleted
- [ ] Existing Day for Night render tests pass unchanged; a new test shows an out-of-range value clamps to the catalog range
