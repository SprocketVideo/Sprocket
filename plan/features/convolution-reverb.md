# Convolution reverb (Acoustic Space) - build-order step 49

✅ **Shipped 2026-08-26.** The spec below is preserved verbatim (moved from PLAN.md step 49 in the
2026-08-26 restructure); the implementation log lives in
[`plan/history/steps-41-57.md#step-49`](../history/steps-41-57.md#step-49), which the
[PLAN.md](../../PLAN.md) ledger row points at. Deliberate departures from the spec: **no bundled IRs**
(the spec's own licensing-safe option — user WAV import leads), and IR relink is the lightweight
"path stored absolute, missing file → pass-through + Inspector *(missing)* flag + re-Browse" flow
rather than a full step-28-style relink dialog. Relative links below resolve from the repo root.

49. **Acoustic Space (Convolution) Reverb.** A new built-in `IAudioEffect`,
    `src/Sprocket.Audio/Effects/ConvolutionReverbEffect.cs`, split out as its **own dedicated
    effect** rather than a mode of the current `ReverbEffect` — consistent with the step-46
    decision to ship delay as separate purpose-built effects instead of one mode-switching unit,
    and matching how every DAW treats convolution reverb as a distinct plugin from its algorithmic
    reverb (Ableton Convolution Reverb vs. Reverb, Logic Space Designer vs. ChromaVerb/PlatinumVerb,
    Pro Tools IR-1/Revibe vs. D-Verb). **This is the "Convolution Reverb" tier already sketched
    under step 41** — that step bundled Studio/Convolution/Creative reverb as tiers of one
    sequencing note; this step promotes the acoustic-emulation tier to its own numbered item so it
    can ship (and be tested/reviewed) independently of the others. Emulates real captured spaces —
    rooms, halls, chambers, plates — by convolving the dry signal with an impulse response (IR)
    rather than an algorithmic network.
    - **DSP.** **Partitioned convolution** (uniform-partition overlap-save/overlap-add, e.g.
      block sizes tuned to keep per-block cost bounded) so long IRs (multi-second halls) stay
      real-time-safe with bounded per-buffer work — a naive direct convolution is O(IR length) per
      sample and unusable for anything but very short IRs. Managed deterministic implementation
      first (consistent with the no-C++/CLI rule); only reach for a small C-ABI FFT helper if
      profiling shows the managed FFT/partition path can't hold real-time at typical IR lengths.
      IR loading/preprocessing (FFT of each partition) happens off the audio thread; only the
      per-buffer multiply-accumulate runs live.
    - **Parameters.** IR selection (bundled preset IRs + user IR import — WAV, mono/stereo),
      predelay, IR length/decay trim (a "damp" control that shortens the effective tail without a
      new IR), high/low damping (filters shaping the tail independent of the raw IR), width/stereo
      spread, wet/dry mix. Exposes latency/tail metadata the way step-41 called for (a small
      optional metadata surface beside `IAudioEffect` for cost/latency/tail, shared with other
      heavy effects).
    - **IR asset & licensing.** Bundled IRs need clear redistribution rights (the same open risk
      step 41 flagged) — ship only IRs with clear licensing (CC0/public-domain captures or
      Sprocket-recorded ones), or ship with **no bundled IRs at day one** and lead with user IR
      import so licensing isn't a blocker to shipping the engine.
    - **Freeze dependency.** Long IRs are CPU-heavy; **depends on the step-32 audio render
      cache/freeze** the same way step 41 does — convolution should default to steering users
      toward Freeze for anything beyond a short/plate-length IR, not to a "hope realtime holds"
      default.
    - **Core & catalog.** `EffectTypeIds.Audio.ConvolutionReverb` (`builtin.audio.reverb.convolution`)
      registered in `EffectCatalog` under `EffectCategory.Audio` with typed descriptors (IR
      reference is a new descriptor kind — an asset/file reference, not a bare number/enum — so the
      Inspector needs a file-picker row alongside the existing slider/keyframe rows). Persists via
      the existing `EffectInstance` JSON plus an IR-reference field (additive; the step-28
      media-link/relink machinery is the template for keeping IR paths valid after a project moves).
    - **Tests.** Deterministic tests against a synthetic IR (impulse-in produces exactly the IR
      back out at unity mix; partitioned output matches a reference direct-convolution result
      within floating-point tolerance for a short test IR); latency/tail metadata correctness;
      freeze-equivalence (live convolution vs. frozen cache matches within tolerance, mirroring
      step 41's freeze-equivalence tests); IR relink/missing-IR graceful degradation (pass-through,
      not a crash, consistent with unknown/unregistered effect behavior elsewhere).

