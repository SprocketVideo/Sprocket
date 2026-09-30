# 05: linux-arm64 AppImage

**What to build:** linux-arm64 users get an AppImage like linux-x64 users, not just a zip.

**Blocked by:** None (can start immediately)

**Status:** done (2026-09-30)

- [x] The release script and CI produce a linux-arm64 AppImage
- [x] It is smoke-launched on an arm runner or the QEMU distro-smoke leg (native `ubuntu-24.04-arm`
  leg of the release workflow's `linux` job)
