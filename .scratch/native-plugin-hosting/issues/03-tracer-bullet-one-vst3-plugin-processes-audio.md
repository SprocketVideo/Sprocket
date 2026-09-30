# 03: Tracer bullet: one VST3 plugin processes audio in a clip chain

**What to build:** A user drops a VST3 plugin on the plugin path, sees it in the effects list and Plugin Manager, adds it to a clip's audio chain, and hears it — on Windows, Linux and macOS.

**Blocked by:** 1, 2

**Status:** ready-for-agent

- [ ] A C-ABI bridge shim (no C++/CLI) is built and bundled per RID by the release script and CI
- [ ] Scan and instantiate run off the audio thread; the loader version-guards like the FFmpeg loader
- [ ] Processing joins the existing audio-effect chain, allocation-free per buffer
- [ ] Parameters appear as keyframeable controls; FEATURES.md row added
