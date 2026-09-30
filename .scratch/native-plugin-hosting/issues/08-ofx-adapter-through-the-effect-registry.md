# 08: OFX adapter through the effect registry

**What to build:** OFX video plugins appear as ordinary video effects; plugins never touch SkiaSharp directly, and CPU-only ones run through the frei0r readback seam.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] OFX C-ABI adapter registers hosted effects in the effect registry
- [ ] CPU-only OFX plugins reuse the CPU-effect readback seam
- [ ] Plugin Manager lists OFX plugins; persistence by plugin id + parameter values
- [ ] FEATURES.md row added
