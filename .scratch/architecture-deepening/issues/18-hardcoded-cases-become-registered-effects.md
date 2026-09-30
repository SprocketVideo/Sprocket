# 18: Hard-coded pipeline cases become registered effects

**What to build:** Brightness, Fade, Color, PosterizeTime, Transform, Stabilization, Color Transform and
Creative LUT dispatch through the same effect registry as every other effect (and plugins), so the pipeline
has one dispatch path.

**Blocked by:** 15

**Status:** ready-for-agent

- [ ] Each hard-coded case becomes a registered effect; the effect-id switch in shader building is deleted
- [ ] Their inline SkSL moves with them
- [ ] Render tests for each effect pass unchanged, and the parity test from 15 covers them
