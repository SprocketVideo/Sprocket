# Build-order history: steps 58+ (open plugin surface & beyond)

> Build-order step details for steps 58 and up. PLAN.md keeps the status ledger + open todos; this
> archive preserves each step's spec and `✅ DONE` implementation log. Anchors are stable: `#step-N`.
> Sibling files: [steps-01-20.md](steps-01-20.md), [steps-21-40.md](steps-21-40.md),
> [steps-41-57.md](steps-41-57.md), [performance-log.md](performance-log.md).
> Relative links inside the moved content are relative to the **repo root** (e.g. `ARCHITECTURE.md`).

## Step 58

58. **Plugin Manager (user-facing plugin management UI).** Adds the user-facing surface over the
    managed plugin host that step 33 built (collectible `AssemblyLoadContext`, interface-scan
    discovery, error capture, verified-collectible unload). Before this step a user could drop a
    DLL into a folder but had no way to see, enable, disable, install, or remove plugins without
    filesystem surgery, and a broken plugin only surfaced in the crash log. The original spec lives
    in [plan/features/plugin-manager.md](../features/plugin-manager.md). Modelled on DAW plugin
    managers (REAPER's browser + rescan, Ableton's per-plugin enable) rather than the
    filesystem-only NLE approach, per the plan's leading-editor comparison.

✅ **DONE (2026-08-26).**

**Host seam (`Sprocket.Plugins`).** `LoadedPlugin` gained a `Version` (the loaded assembly's
`AssemblyName.Version`, surfaced by the manager). `PluginHost` gained a static
`EnumeratePluginFiles(directory)` that yields the plugin-assembly candidates — top-level `*.dll`
plus one level of `<Name>/<Name>.dll` bundles — **without loading them**, so the manager can present
one row per discovered file and skip the user-disabled ones at startup; `LoadDirectory` was
refactored to enumerate through it (no behavior change). No FFmpeg/GPU/UI leaked into the host (§2).

**Manager (`Sprocket.App/PluginManager.cs`).** A new instantiable `PluginManager` holds one
`PluginEntry` per discovered plugin (path, bundled-vs-user, version, `PluginStatus`
Enabled/Disabled/Error, message, contributed `EffectDescriptor`s) and the live operations:

- `Initialize()` scans each configured directory, loads the enabled plugins, **skips the disabled
  ones** (never loads them — their effects pass through in any project that references them, §15),
  and records per-file errors as `Error` rows.
- `SetEnabled(entry, enable)` — disable = unregister the plugin's descriptors from `EffectCatalog`
  + drop its shaders from the render pipeline + `PluginHost.Unload` the collectible context; enable
  = load + register. Persisted immediately.
- `Install(sourcePath)` copies a `.dll` into the user plugins folder and loads it (refuses a
  duplicate file name); `Uninstall(entry)` unregisters + unloads + deletes the file (user plugins
  only; a bounded GC-then-retry tolerates the brief post-unload file lock on Windows); `Rescan()`
  unloads everything and re-scans from disk.

Registration into `EffectCatalog` is done in the manager (pure Core, so it is **headlessly
testable**); the GPU shader compile/registration is injected as a callback wired to
`SkiaEffectPipeline` by the composition root, and the disabled-list persistence is injected too, so
the class has no GPU or settings-store dependency of its own. Audio effect providers are served live
from the host, so unloading a plugin stops it providing with no separate unregister step. All the
step-33 rules are preserved (reserved `builtin.` ids rejected by the host; duplicate ids skipped; a
bad shader unregisters just that effect and leaves the rest).

**Composition root (`PluginService.cs`).** Rewritten as a thin wrapper owning the single
process-wide `PluginManager`, wiring `SkiaEffectPipeline.RegisterEffect`/`UnregisterEffect` as the
shader callbacks and load-modify-save of `UserSettings.DisabledPlugins` as the persistence (the
Preferences dialog never touches that field, so this is its only writer). `AudioEffectFactory` now
routes through the manager's host, then the built-ins. Startup still logs-and-skips any failure (§15).

**Persistence (`UserSettings.DisabledPlugins`).** A new additive `IReadOnlyList<string>` (body
property — a list has no compile-time-constant default) of disabled plugin **full paths**. `Clamp`
normalizes null/empty to the canonical shared empty array so the record's reference-based equality
still treats "no plugins disabled" settings as equal. Honored at startup; a fresh install and an
old settings file both default to none disabled.

**UI (`PluginManagerWindow.cs`, Edit ▸ Plugins…).** A modal window (like Preferences — nothing else
can save settings while its toggles rewrite the disabled list) built with the same dark-`Palette`
primitives as `ProxyStatusWindow`/`ExportQueueWindow`: one card per plugin (name · version, status
+ Bundled tag, path, contributed effects or the captured error message), a per-row **Enabled**
toggle, **Uninstall** for user plugins, and a toolbar with **Install Plugin…** (`.dll` file picker
→ copy into the user folder → load), **Rescan**, and **Open Plugins Folder**. Bundled
(`<exe>/Plugins`) rows can be enabled/disabled (an in-app choice, not a filesystem change) but not
uninstalled, since their files are read-only shipped content. On close, `MainWindow` re-syncs its
in-memory `_userSettings.DisabledPlugins` from the manager so a later settings save can't clobber
the persisted choices; enable/disable/install/uninstall repaint the preview so a re-registered or
removed effect shows at once, and the Effects menu already rebuilds on submenu-open.

**Tests.** `Sprocket.App.Tests/PluginManagerTests.cs` loads the real `Sprocket.TestPlugin` through
the collectible-ALC path (copied to the output, not assembly-referenced, like a third-party plugin)
and asserts: Initialize registers the plugin's effects into `EffectCatalog`; disable → catalog ids
gone + persisted, enable → back; a pre-seeded disabled plugin is skipped at startup and never
registered; Install copies + loads (and refuses a duplicate name); Uninstall unregisters + deletes;
a garbage assembly becomes an Error row, not a crash; Rescan picks up a newly added plugin.
`UserSettingsStoreTests` covers `DisabledPlugins` round-trip / default / null-normalization.
`PluginHostTests` covers the new `Version` capture and `EnumeratePluginFiles`. The window's look
rests on manual verification, per the standing convention for the App's code-built dialogs.

Docs: FEATURES.md gains the Plugin Manager row (❌ undocumented) under §4 Effects, and the
"Not user-facing" plugin-internals row now points at it. The native VST3/AU/OFX bridges (steps
31/33) and the open standards (step 59) land into this manager.

**Follow-up fixes (2026-08-26, post-review).** Five pre-existing findings surfaced (but not fixed)
during step 58's code review were addressed together:
1. *Render-plan Assets aliasing (`RenderGraph.ResolveAudioChain`).* `ResolvedEffect.Assets` aliased
   the model's live `EffectInstance.Assets` dictionary; the plan is consumed on the audio thread
   (`AudioMixer.MixInto`), where `GetAsset` raced a UI-thread `SetEffectAssetCommand` — a `Dictionary`
   data race. Now snapshot-copied when non-empty, matching the numeric `values` snapshot (the plan is a
   true immutable snapshot again).
2. *Convolution Reverb dropped an in-use IR on cache eviction (`ConvolutionReverbEffect.Process`).* It
   re-resolved the IR through `ImpulseResponseCache` every buffer, so a `Trim` eviction of an in-use
   entry (>16 IRs auditioned) returned null → a dry gap mid-playback. It now retains its own `_ir`
   reference (keyed by `_irPath`) and stops polling once loaded — making the cache's documented
   "a live effect keeps its own reference" invariant actually hold (and cheaper per buffer).
3. *MCP `set_effect_asset` / `set_chain_effect_asset` silently accepted an unknown parameter name.* The
   old guard only rejected a known-numeric parameter, so a typo'd asset name wrote a junk `Assets` entry
   no DSP reads. New `SprocketTools.RequireAssetParameter` rejects an unknown or numeric name on a
   *registered* effect (unregistered plugin effects stay lenient, matching `FindParameter`).
4. *MCP asset set didn't retry a failed IR.* The Inspector drops a failed-load cache entry on re-pick;
   the MCP tools didn't, so setting a path that had failed stayed dry-cached. Added the
   `IEditorApi.InvalidateFailedAsset` seam (implemented in `McpEditorSession` mirroring the Inspector's
   failed-only, per-project-rate check) and called from both asset tools.
5. *Inspector probed `File.Exists` on the UI thread (`InspectorPanel` asset row).* An unreachable network
   path could freeze the editor for seconds. The probe now runs on a threadpool task and repaints the row
   from the continuation (optimistic "present" until it lands), still once per distinct path.

Tests: `SprocketToolsTests` now asserts the unknown-param rejection and the invalidate-on-set request;
`ConvolutionReverbEffectTests.An_In_Use_IR_Survives_A_Cache_Eviction` covers the retained-reference fix.

## Step 59

59. **Open plugin standards (frei0r / LADSPA / LV2).** Host the open-source plugin standards — frei0r (video),
    LADSPA and LV2 (audio) — alongside the managed host. All three are plain C ABIs needing no C++ bridge shim
    (P/Invoke directly, ARCHITECTURE.md §1). The original spec + sequencing (LADSPA → LV2 → frei0r) lives in
    [plan/features/frei0r-ladspa-lv2.md](../features/frei0r-ladspa-lv2.md). Highest value on Linux, where these
    are the native plugin ecosystems.

🟡 **LADSPA audio hosting DONE (2026-08-26); LV2 + frei0r open.** Shipped the first (and cleanest) arm of the
step — native LADSPA audio-plugin hosting end to end — per the plan's "ship LADSPA first" sequencing. LV2's
Turtle/RDF discovery and frei0r's CPU-readback video stage remain scheduled.

**Native binding (`Sprocket.Plugins/Ladspa/`).** A hand-rolled `[LibraryImport]`-style binding of the LADSPA 1.1
C ABI in the same no-C++/CLI style as `Sprocket.Media`'s FFmpeg binding (§1):
- `LadspaAbi.cs` — the port/hint bit constants and the `LadspaDescriptor` / `LadspaPortRangeHint` struct
  layouts. **`unsigned long` is bound as `CULong`** (32-bit on Windows LLP64, 64-bit on Unix LP64) so ids, port
  counts/indices, the sample rate and the block sample count are correct on every platform; the plugin's own
  functions are reached through the function pointers carried in its descriptor (only `ladspa_descriptor` is a
  named export). `AllowUnsafeBlocks` was enabled on `Sprocket.Plugins` for the function-pointer calls + pinned
  native buffers.
- `LadspaPluginInfo.cs` — pure managed records for a plugin's identity + ports, and `LadspaDescriptorReader`
  which reads a native descriptor pointer into them (kept apart so it runs against an in-process descriptor in
  tests, no `.so` needed).
- `LadspaParameterMapping.cs` — the pure, fully-unit-tested core: each control-input port → a typed
  `EffectParameterDescriptor` (min/max/default/kind from the LADSPA range hints, incl. TOGGLED→Toggle,
  INTEGER→Integer, logarithmic default interpolation, and SAMPLE_RATE-scaled bounds against a nominal 48 kHz for
  display); the plugin → an `EffectCategory.Audio` `EffectDescriptor` (so `EffectTypeIds.IsAudio` routes it to
  the mixer chain and the Inspector builds its controls with no bespoke UI). Parameter keys are
  `port<index>` (stable per plugin version — LADSPA gives no per-port symbol); the effect id is
  `plugin.ladspa.<uniqueId>` (or `<label>` for an unregistered/dev plugin).
- `LadspaEffect.cs` — the `IAudioEffect` adapter + `LadspaInstanceSet` RAII handle (in the `Native/Handles`
  style, **with a finalizer** because the mixer replaces rather than disposes an effect on a chain rebuild). All
  PCM stays in pinned native memory (§1); it de-interleaves the mixer's interleaved buffer into per-port mono
  buffers around `run()`, then re-interleaves. Topology: a mono (1-in/1-out) plugin — the common case — is
  instantiated **once per channel** (dual-mono); a matched multi-channel plugin runs one instance with a port
  per channel; extra stream channels pass through. Instantiation faults latch into pass-through rather than
  throwing on the audio thread (§15). The owning module is never unmapped within a session, so the descriptor's
  function pointers stay valid for a live instance / the finalizer.
- `LadspaEffectProvider.cs` / `LadspaLibrary.cs` / `LadspaHost.cs` — one provider per hostable plugin on the
  existing `IAudioEffectProvider` seam (so **zero mixer changes**); one library per loaded file (walks
  `ladspa_descriptor(0,1,…)`; skips sources/synths with no audio input); the host scans the `LADSPA_PATH`
  env var + per-OS default dirs (`/usr/lib/ladspa`, `~/.ladspa`, macOS `…/Plug-Ins/LADSPA`, …), records every
  per-file failure in `Errors` rather than throwing, and creates effects by id.

**Lifetime policy.** A loaded LADSPA module stays mapped for the process lifetime; disabling a plugin only
unregisters its catalog descriptors (the mixer drops its instances on the next buffer) — unmapping a shared
library whose function pointers a DSP instance might be mid-`run()` on the audio thread would be a
use-after-free with no safe quiescence barrier. This matches how DAWs keep audio-plugin binaries resident within
a session; documented in `LadspaLibrary`.

**Integration (`Sprocket.App`).** `PluginManager` gained a parallel LADSPA path (a `PluginFormat` tag on
`PluginEntry`, a `LadspaHost`, injected search directories): `Initialize`/`Rescan` scan LADSPA after the managed
scan, one row per library file (deduped by full path), skipping user-disabled files and registering the rest;
`SetEnabled` toggles registration + the shared `DisabledPlugins` persistence; `CreateAudioEffect` routes managed
first, then LADSPA. `PluginService` wires the real `LadspaHost.DefaultSearchDirectories()`. The
`PluginManagerWindow` labels LADSPA rows "· LADSPA" and mentions native plugins in the header. The managed
step-33/58 code path was left untouched (no regression). LADSPA plugins persist by effect id + `port<index>`
values through the existing `EffectInstance`/`EffectDto` shape; a missing plugin passes through offline (§15) with
no special handling.

**Tests.** `Sprocket.Plugins.Tests/Ladspa/` — a `FakeLadspaPlugin` builds a real `LADSPA_Descriptor` in
unmanaged memory whose function pointers target `[UnmanagedCallersOnly]` methods (a stateful gain + one-sample
delay), the native equivalent of the managed tests' real `Sprocket.TestPlugin`. It exercises the binding,
descriptor reader and `LadspaEffect` DSP end to end with no native `.so`: dual-mono per-channel gain,
cross-buffer state continuity, reset, control defaults, and format-change reallocation. `LadspaParameterMapping`
is unit-tested across every hint (bounded/unbounded ranges, all default hints, toggle/integer, log
interpolation, sample-rate scaling, id scheme). `LadspaHostTests` covers discovery (OS-specific extension,
`LADSPA_PATH` parsing + dedup), and per-file error capture for a non-library file. `Sprocket.App.Tests/`
`PluginManagerTests` gained LADSPA rows: format-tagged discovery, disabled-at-startup skip, enable/disable
persistence, and unknown-id → null (pass-through).

Docs: FEATURES.md gains a LADSPA row under §4 Effects; THIRD-PARTY-NOTICES notes that user-installed native
plugins carry their own licenses; README's plugin bullet mentions LADSPA. The remaining LV2 + frei0r arms
(and the CPU-video-effect readback seam frei0r needs) stay in
[plan/features/frei0r-ladspa-lv2.md](../features/frei0r-ladspa-lv2.md).

**Hardening (post-review).** Two defense-in-depth fixes from the security review: (1) `LadspaDescriptorReader`
bounds `PortCount` at 4096 (throws `BadImageFormatException`, recorded as a per-file load error) so a
malformed/hostile descriptor can't drive the reader to walk its port arrays into unmapped memory — an
uncatchable access violation that would crash the editor during startup discovery of every library on
`LADSPA_PATH`; mirrors the existing `MaxDescriptors` runaway guard. (2) `LadspaEffect.Process` now wraps the
steady-state `LadspaInstanceSet.Process` call in try/catch (latches to pass-through), making the "never throw on
the audio thread" guarantee total rather than dependent on the mixer's buffer-length contract.

Four more from the code review: (3) `LadspaEffect.Reset` now resets through `activate()` alone when the
plugin has no `deactivate()` (optional in LADSPA, frequently null) — previously a plugin with `activate` but
no `deactivate` was never reset, carrying stale delay/reverb state across a seek; (4) `Build` now requires
`cleanup` too (mandatory in LADSPA; missing it leaks per teardown); (5) `LadspaHost.Forget` purges the
library's recorded warnings/errors so an enable→disable→enable cycle doesn't accumulate duplicates; (6) a dead
`integer ? 1.0 : 1.0` ternary in the range fallback was removed. Tests added for the null-deactivate reset and
the port-count bound. (Noted but left as conscious v1 choices: LADSPA effects don't implement
`IAudioEffectTail`, so a reverb/delay tail is cut at clip/export boundaries; and a mid-playback block-size
*increase* rebuilds the instance, resetting state once — block size is otherwise constant.)
