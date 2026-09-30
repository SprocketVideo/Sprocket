# Architecture deepening (review of 2026-09-30)

Source: an architecture review of the most-edited areas since mid-August 2026 (MainWindow,
TimelineControl, InspectorPanel, the effect pipeline, the MCP tools). Five candidates, all turned into
tickets under `issues/`:

- **Clip edits in Core** (01–10): the clip-edit builders live in Sprocket.App, which Sprocket.Mcp cannot
  reference, so the MCP tools carry ~12 parallel copies of the same edits, and those copies have drifted
  (Stabilization placement, ripple-delete shifts, clamp vs throw, ramped-clip trims). Move the builders to
  Core as one deep module; the timeline view, Inspector and MCP tools resolve targets and call it.
- **Timeline gestures** (11–14): ~1,340 lines of trim/ripple/roll/slide/slip drag state inside
  TimelineControl, untestable. Each drag kind becomes a gesture behind `Begin → Update(ticks) → Commit`.
- **Effects own their facts** (15–20): an effect's defaults and ranges are stated in the catalog and again as
  fallbacks/clamps in its Render binding; a missing registration silently passes frames through; effect-id
  special cases stop plugins being geometric, temporal or time-modifying. The descriptor stays in Core (Core
  depends on nothing); the binding in Render derives from it, and a parity test ties them.
- **One plan executor** (21–24): preview does not render the plan. PlaybackEngine re-derives the layers and
  PreviewSurface runs its own switch, so nested sequences and transitions are missing in preview; export has
  its own executor; `RenderGraph.Render<T>` is used only by tests. One Render executor with frame-source
  adapters (decode ring for preview, seek-decode for export). Must keep the ~0 Gen0/frame rule (ARCHITECTURE §1).
- **Editor session** (25–28): the MCP bridge's real behaviour and several domain rules (mark clearing,
  lift/extract fallback, export quiescing ×4) live in MainWindow, unreachable by tests. An editor-session
  module owns them; MainWindow and McpEditorSession both call it.

Decisions made while ticketing:
- Group moves clamp rigidly at the timeline origin (what every leading editor does); MCP reports the
  applied move instead of throwing. (ticket 05)
- Adding an effect follows one placement rule everywhere, including timeline drop. This is a deliberate
  user-visible change. (ticket 02)
- Tickets stay fine-grained; no merges.
- PLAN.md step 60 (preview allocation churn) is folded in as tickets 29–33: the in-tree bytes-per-frame gate
  (29) is independent; the remediations wait on 23, because the unified executor replaces the `UseLayers`
  path that `plan/features/preview-allocation-churn.md` targeted. (2026-09-30)
