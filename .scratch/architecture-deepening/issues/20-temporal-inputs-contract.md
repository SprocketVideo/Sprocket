# 20: Temporal-inputs contract

**What to build:** Any effect, including a plugin, can ask for prior frames through a documented contract,
not by matching Echo's uniform names. Echo renders identically.

**Blocked by:** 15

**Status:** ready-for-agent

- [ ] The pipeline's references to Echo's uniform names and bounds are replaced by a contract on the effect
- [ ] Echo tests (including preview/export parity) pass unchanged
- [ ] A test plugin receives prior frames through the contract
