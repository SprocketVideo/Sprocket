# 04: Real headless sample export in the release smoke test

**What to build:** Every release artifact proves it can export, not just launch.

**Blocked by:** None (can start immediately)

**Status:** done (2026-09-30)

- [x] Each CI smoke leg runs a headless export of the bundled sample and checks the output opens and has the expected duration/streams
- [x] Failure fails the release build
