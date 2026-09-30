# 03: Cache-aware Fast Export

**What to build:** Fast Export reuses the preview render cache for ranges it covers exactly, so a pre-rendered timeline drafts much faster. Final Export never consults the cache.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Only exact-coverage cached ranges are spliced into the output; no partial or stale reuse
- [ ] Final Export is cache-blind (test proves it)
- [ ] The export dialog / completion summary makes clear the draft was cache-assisted
- [ ] FEATURES.md Export Mode row amended
