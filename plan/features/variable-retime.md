# Variable / ramped speed & reverse retime

✅ **Shipped 2026-08-27** — reverse playback and keyframed speed ramps (the step 21 remainder). Constant-speed
retime shipped earlier and freeze frames shipped separately as step 43's frame hold. Full implementation log in
[plan/history/steps-21-40.md#step-21](../history/steps-21-40.md#step-21); ledger row in [PLAN.md](../../PLAN.md).

## What shipped

- **Reverse playback** — `Clip.Reverse` (a flag beside the always-positive `SpeedRatio`, the "Reverse Speed"
  convention of leading editors). The time map mirrors from the exclusive out-point; providers take the latest
  frame *strictly before* the mapped time, so timeline frame *k* mirrors exactly to source frame *N−1−k*. Video
  decodes backwards GOP-by-GOP (`Sprocket.Media/GopFrameWindow` + `ReverseVideoDecodeRing`, the direction-aware
  feed factory in `MediaBootstrap`, `ExportFrameProvider`'s reverse mode driven by `VideoLayer.Reverse`); audio
  plays the source PCM backwards through the mixer's carried reverse block and the existing streaming resampler.
- **Keyframed speed ramps** — `Clip.SpeedCurve` (speed as a fraction of normal, **clip-local** keyframe ticks) and
  the pure `SpeedRamp` integrator (`Integrate` / `SourceOffsetAt` / `SolveDuration`); the clip's duration derives
  from where the integral covers the source span. UI: the Inspector's Speed row is the shared keyframeable slider
  row (◇ + the step-16b/16d lane and velocity graph). Plain trims and blade splits are ramp-aware.
- Surfaces: Speed / Duration dialog (Reverse speed checkbox, ramp note), clip context menu (Reverse Speed / Play
  Forward), Inspector (Speed lane + Reverse), clip badges (`50%` / `RAMP` / `◀`), MCP `set_clip_speed(reverse)`.

## Deliberate departures / limits (documented in code)

- Speed keyframes are clip-local, not absolute like effect keyframes: the ramp defines the clip's own duration, so
  anchoring it to the clip keeps a moved clip's length/content unchanged without a rebase.
- A *changed* constant speed replaces a ramp (`SetClipSpeedCommand` owns the rule; undo restores the ramp).
- Nested-sequence clips can't be reversed (`Clip.SupportsReverse`): the child audio sub-mix is planned forward
  (nested retime is deferred, step 23).
- Ripple / roll / slide (editor + MCP) refuse reversed or ramped clips — their constant-speed source-edge math
  doesn't model those maps; the Select tool's plain trim (and MCP `trim_clip` for reversed clips) covers them.
  Stop-motion Duplicate / Remove Frame likewise need a constant forward map.

## Still open (later quality tiers, same seams)

- **Pitch-preserving time-stretch** — a DSP tier for retimed audio (the resampler shifts pitch today). Sequenced
  with the audio-effects layer (step 31 seams): `Sprocket.Audio/AudioMixer` `Pull`/`ReadResampled`.
- **Frame-interpolated slow motion** (blend / optical flow) — a *video* quality tier behind the render-graph seam;
  nearest-source-frame remains the behaviour.
- Ripple / roll / slide gestures on reversed / ramped clips (would need the direction/ramp-aware span math that
  `TimelineControl`'s plain trim now has — `SourceSpanOver` / `TimelineSpanFor` — moved onto `Clip` in Core).
- Nested-sequence retime (audio sub-mix at the parent clip's speed / direction).
