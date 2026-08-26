# Live stop-motion capture

❌ **Not started; deliberately deferred** until packaging stabilizes. Unscheduled feature;
tracked in [PLAN.md](../../PLAN.md) Open work. Relative links resolve from the repo root.

## Original PLAN.md scope note (moved verbatim, 2026-08-26)

**Future step (unscheduled): live stop-motion capture.** A capture mode — live camera feed in the
program monitor, onion-skin ghosting of the last captured frame(s), a capture button appending a
numbered still to an image-sequence media item — is *feasible on the seams this codebase already
has*: FFmpeg's device demuxers (`dshow` on Windows, `v4l2` on Linux, `avfoundation` on macOS) open
through the same `av_find_input_format` + options-dict door step 42 adds for `image2`, a live feed is
just another `IVideoFrameFeed`, onion-skinning is one more Skia layer blend in the existing compositor
(§7/§10), and captured frames append to a step-42 `ImageSequence` MediaRef whose `SequenceFrameCount`
grows through a command. Deferred deliberately: device capture is the first *input*-device surface in
the app (permission prompts, device enumeration/selection UI, per-OS format negotiation, hot-unplug),
`avdevice` is an entirely new native library to bundle and version-guard per RID (steps 35–36 are
themselves unfinished), and a capture UI is a new workspace mode rather than a timeline feature —
each a bigger lift than the editing tiers above, and none of it is needed to *edit* stop-motion shot
in a dedicated capture tool (Dragonframe, Stop Motion Studio) and imported via step 42. Revisit after
packaging stabilizes; the prerequisite work is: bundle `avdevice`, bind device enumeration/open
(~6 imports, listed in `Native/FUTURE_BINDINGS.md`), a `CaptureService` behind `IVideoFrameFeed`, and
the capture workspace.

## Prerequisite checklist (when picked up)

1. Bundle `avdevice` per RID (`scripts/release.ps1`) and version-guard it in `FFmpegLoader`.
2. Bind device enumeration/open (~6 imports — catalogued in
   `src/Sprocket.Media/Native/FUTURE_BINDINGS.md`).
3. `CaptureService` behind the existing `IVideoFrameFeed` seam (per-OS demuxer selection,
   permission prompts, hot-unplug handling).
4. Capture workspace UI: live monitor feed, onion-skin blend layer, capture button appending
   to an `ImageSequence` MediaRef via an undoable command.
