# 04: VST3 state in the project and offline bypass

**What to build:** A project with a VST3 plugin reopens with the plugin's settings intact; if the plugin is missing, the project still opens and the effect is bypassed and flagged.

**Blocked by:** 3

**Status:** ready-for-agent

- [ ] Plugin id + opaque component/controller state persist (additive, schema-versioned)
- [ ] Missing plugins load as bypassed placeholders that round-trip their state unchanged
- [ ] Persistence tests for both cases
