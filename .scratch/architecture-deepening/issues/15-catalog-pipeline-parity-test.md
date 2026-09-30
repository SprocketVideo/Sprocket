# 15: Catalog ↔ pipeline parity test

**What to build:** A forgotten render registration fails the test suite instead of shipping as an effect that
silently passes frames through.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A Render test walks every built-in video effect in the catalog and asserts the pipeline can build a shader for it
- [ ] The test fails if a registration is removed (verified once by hand)
- [ ] Any gap it finds today is fixed or explicitly listed as intentional
