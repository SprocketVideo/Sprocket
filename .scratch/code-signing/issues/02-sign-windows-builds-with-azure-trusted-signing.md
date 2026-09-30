# 02: Sign Windows builds with Azure Trusted Signing

**What to build:** Windows users install Setup.exe without a SmartScreen override.

**Blocked by:** 1

**Status:** deferred — no paid Microsoft/Apple signing accounts for now; the initial release ships unsigned (decided 2026-09-30). Revisit in a future, unscheduled phase.

- [ ] The exe, Setup.exe and delta packages are signed in the release script and the CI release workflow
- [ ] Auto-update from an unsigned prior version to a signed one still works
- [ ] Verified on a clean Windows VM
