# 03: macOS Developer ID signing and notarization

**What to build:** macOS users open the `.app` without the Gatekeeper right-click ceremony, on both osx-arm64 and osx-x64.

**Blocked by:** 1

**Status:** deferred — no paid Microsoft/Apple signing accounts for now; the initial release ships unsigned (decided 2026-09-30). Revisit in a future, unscheduled phase.

- [ ] Ad-hoc re-sign replaced by a Developer ID Application identity with hardened runtime and an audited entitlements file
- [ ] notarytool submit + staple in CI
- [ ] `codesign --verify --deep` and `spctl -a -vv` pass in CI
