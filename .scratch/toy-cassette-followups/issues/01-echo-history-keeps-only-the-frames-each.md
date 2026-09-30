# 01: Echo history keeps only the frames each echo needs

**What to build:** The Low Light toy cassette look plays 4K60 footage without dropping its oldest echoes, and export no longer re-seeks per frame.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Echo's frame history holds only the frames the configured echoes sample
- [ ] 4K60 Low Light keeps all echoes in preview; export stops re-seeking per frame
- [ ] Output unchanged (golden-frame test)
- [ ] ARCHITECTURE.md gains a section describing the temporal footprint contract
