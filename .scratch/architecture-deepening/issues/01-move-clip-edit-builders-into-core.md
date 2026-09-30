# 01: Move the clip-edit builders into Core

**What to build:** ClipEdits and ClipPlacement become Core modules so every caller, including Sprocket.Mcp,
can reach the same edit rules. A pure move with no behaviour change: the app edits exactly as before.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Both builders live in Sprocket.Core (they already depend only on Core types) and App callers use them from there
- [ ] Their existing tests move to Sprocket.Core.Tests and pass unchanged
- [ ] Sprocket.Core still builds with no new references and is clean under TreatWarningsAsErrors
- [ ] `dotnet test Sprocket.slnx` green
