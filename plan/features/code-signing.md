# Code-signing, notarization & remaining distribution polish

🟡 **Deliberately deferred** (the step 36 remainder — packaging itself shipped: Velopack
Setup.exe / AppImage / macOS `.app`, auto-update, CI matrix with per-artifact smoke tests;
full record in [plan/history/steps-21-40.md#step-36](../history/steps-21-40.md#step-36)).
The alpha ships unsigned with documented SmartScreen/Gatekeeper steps in RELEASE_NOTES.md.
Tracked in [PLAN.md](../../PLAN.md) Open work.

## What remains

- **Windows code-signing** — obtain a signing certificate (an OV cert still triggers
  SmartScreen until reputation accrues; an **EV cert** or **Azure Trusted Signing** clears it
  fastest — Trusted Signing is the cheap/modern route for an open-source org account). Sign
  the exe + Velopack Setup.exe/delta packages in `scripts/release.ps1` (Velopack's `vpk` has
  first-class `--signParams`/Trusted Signing support) and in the CI release workflow
  (`.github/workflows/release.yml`) with the secret held as a GitHub Actions secret /
  federated credential.
- **macOS Developer ID signing + notarization** — Apple Developer Program account for the
  org (`org.sprocketvideo.sprocket`); replace the current ad-hoc re-sign in
  `scripts/macos-bundle-ffmpeg.sh` with a Developer ID Application identity, hardened
  runtime + entitlements audit (JIT not needed; audio input not needed today), then
  `notarytool` submit/staple in CI. Both `osx-arm64` and `osx-x64` artifacts.
- **`linux-arm64` AppImage** — currently zip-only until an arm runner (or the QEMU distro-smoke
  leg) smoke-launches the AppImage.
- **Sample-export CI validation** — release smoke today is launch + native checks
  (`--version` / `--ffmpeg-check` / `--audio-check` / `--mcp-check`); add a real headless
  sample export per artifact.
- **Copy updates on completion** — remove the unsigned-alpha caveats from RELEASE_NOTES.md /
  README.md; flip the FEATURES.md platform notes if they mention unsigned builds; update the
  "Planned" bullet in README.md.

## Prerequisites / blockers

Both signing routes need org-level accounts (cert authority or Apple Developer) and are
gated on the repo's move to a GitHub org (see the branding/distribution memory note) so
certificates and secrets bind to the org, not a personal account.

## Verification

A signed Windows Setup.exe installs without SmartScreen override on a clean VM (step 56's
Win10 VM doubles for this); a notarized `.app` opens without Gatekeeper right-click
ceremony on a clean macOS account; `spctl -a -vv` / `codesign --verify --deep` pass;
auto-update from an unsigned prior version to the signed one still works.
