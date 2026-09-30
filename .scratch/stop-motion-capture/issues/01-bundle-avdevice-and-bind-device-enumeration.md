# 01: Bundle avdevice and bind device enumeration

**What to build:** The app ships FFmpeg's avdevice per RID and can list cameras; a `--capture-check` smoke flag proves it in CI.

**Blocked by:** code-signing 3

**Status:** needs-triage

- [ ] Triage: confirm packaging has stabilized
- [ ] avdevice bundled by the release script and version-guarded in the FFmpeg loader
- [ ] Device enumeration/open imports bound (per `Native/FUTURE_BINDINGS.md`)
- [ ] `--capture-check` lists devices; macOS camera usage description + entitlement added
