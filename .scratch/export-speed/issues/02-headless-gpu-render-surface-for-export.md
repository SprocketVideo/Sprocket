# 02: Headless GPU render surface for export

**What to build:** Export can composite on a GPU Skia context of its own (not Avalonia's render thread), if ticket 1's measurements show compositing is the bottleneck.

**Blocked by:** 1

**Status:** needs-triage

- [ ] Triage: go/no-go from ticket 1's numbers
- [ ] If go: per-OS headless context bootstrap (GL / Vulkan / D3D / Metal) with software raster fallback
- [ ] Fast Export uses it when available; Final Export stays on the reference path
- [ ] Cross-platform CI still passes; hash-based render tests unchanged
