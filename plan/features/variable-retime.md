# Variable / ramped speed & reverse retime

❌ **Not started** (the step 21 remainder). Constant-speed retime shipped — full record in
[plan/history/steps-21-40.md#step-21](../history/steps-21-40.md#step-21); freeze frames shipped
separately as step 43's frame hold. Tracked in [PLAN.md](../../PLAN.md) Open work.

## What remains (deferred from step 21, on the same seams — additive when picked up)

- **Reverse playback** — the `Reverse` flag from the original spec. Not just a negated
  `MapToSource`: needs **backward decode** in the video feed / export provider (GOP-aware:
  seek to the previous keyframe, decode forward, serve frames in reverse order from a small
  ring) and reversed audio (play the source PCM backward through the existing streaming
  resampler seam in `Sprocket.Audio/AudioMixer`).
- **Keyframed speed ramps** — an integrated time map from a keyframed-speed `AnimatableValue`
  (`sourceTime = SourceIn + ∫ speed dt`), so the clip's duration derives from the integral.
  UI: a speed keyframe lane reusing the step-16b/16d keyframe editor; compare Premiere's
  Time Remapping rubber-band and Resolve's retime curve for gesture conventions.
- **Pitch-preserving time-stretch** — a DSP quality tier for retimed audio (the current
  resampler shifts pitch; deliberate first cut). Sequenced with the audio-effects layer
  (step 31 seams).
- **Frame-interpolated slow motion** (blend / optical flow) — a later *video* quality tier
  behind the same render-graph seam; ship nearest-source-frame first (already the behavior).

## Where it lands

- `src/Sprocket.Core/Model/Clip.cs` — `SpeedRatio` (`Rational`, strictly positive today) grows
  the `Reverse` flag and/or an animatable speed; `MapToSource` becomes the integrated map.
- `src/Sprocket.Core/Rendering/RenderGraph.cs` — already maps through `clip.MapToSource`;
  ramps only change how the map is computed (preview/export stay identical, §5).
- `src/Sprocket.Media` / `src/Sprocket.Playback` — backward decode support in the frame feed.
- `src/Sprocket.Audio/AudioMixer.cs` — reverse read + (later) pitch-preserving stretch.
- `src/Sprocket.App` — Speed/Duration dialog (`Dialogs.cs SpeedDialog`) gains Reverse + ramp
  entry points; the dialog's non-positive-input rejection notes this deferral today.

## Constraints

- Non-destructive: only the time map changes; source bytes and `SourceIn/Out` untouched.
- All edits through the command stack (`SetClipSpeedCommand` pattern, coalescing preserved).
- Persistence additive + nullable (pre-existing files load unchanged; 1×/no-reverse writes
  nothing — the step 21 pattern).
- §1 hot-path rule: backward decode must reuse the pooled `AVFrame` path — no managed pixels.

## Tests

Extend the step-21 suite: integrated-map correctness at ramped speeds (analytic cases),
reverse frame order end-to-end (decode → pump → export golden frames), duration derivation
from a keyframed speed, persistence round-trip, linked-companion retime staying in sync.
