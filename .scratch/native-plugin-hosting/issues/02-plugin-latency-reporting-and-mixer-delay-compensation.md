# 02: Plugin latency reporting and mixer delay compensation

**What to build:** Audio effects can report latency and tail length, and the mixer compensates so a latent effect on one track stays in sync with the others. Prefactor for hosted plugins; also serves the convolution reverb.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] The audio-effect contract gains latency/tail metadata (additive, default zero)
- [ ] The chain executor delays parallel paths to align outputs
- [ ] A test with a synthetic latent effect proves sample-accurate alignment
- [ ] Per-buffer processing stays allocation-free
