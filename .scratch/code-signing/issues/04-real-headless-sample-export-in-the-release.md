# 04: Real headless sample export in the release smoke test

**What to build:** Every release artifact proves it can export, not just launch.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Each CI smoke leg runs a headless export of the bundled sample and checks the output opens and has the expected duration/streams
- [ ] Failure fails the release build
