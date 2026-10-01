# 01: Move the clip-edit builders into Core

**What to build:** ClipEdits and ClipPlacement become Core modules so every caller, including Sprocket.Mcp,
can reach the same edit rules. A pure move with no behaviour change: the app edits exactly as before.

**Blocked by:** None (can start immediately)

**Status:** resolved

- [x] Both builders live in Sprocket.Core (they already depend only on Core types) and App callers use them from there
- [x] Their existing tests move to Sprocket.Core.Tests and pass unchanged
- [x] Sprocket.Core still builds with no new references and is clean under TreatWarningsAsErrors
- [x] `dotnet test Sprocket.slnx` green

## Answer

Done. `ClipEdits` and `ClipPlacement` now live in `Sprocket.Core.Commands`. The "already depend only on Core
types" premise was off by three small App helpers, each moved to Core with an App wrapper delegating so no App
call site changed: `TickSnapping` (Core.Timing: `Snap` + `ClampNonNegative`, behind `TimelineMath`),
`SourceStreams` (Core.Model), and `ClipEdits.ClampGroupNudge` (behind `ClipboardOps`). The tests moved to
Core.Tests unchanged, except the `SourceRangePayload` round-trip test, which exercises an App type and so moved
to `tests/Sprocket.App.Tests/SourceRangePayloadTests.cs` verbatim.
