# Plugin Manager (user-facing plugin management UI) — build-order step 58

✅ **Shipped 2026-08-26.** The implementation log lives in
[plan/history/steps-58plus.md#step-58](../history/steps-58plus.md#step-58); this file is the
original spec, kept for reference. What shipped matches the sketch below, with one clarification:
bundled (`<exe>/Plugins`) rows can be enabled/disabled (an in-app choice, persisted in settings —
not a filesystem change) but not uninstalled, so "read-only" means the file is not modified, not
that the row is inert.

Sprocket has a working *internal* managed plugin host (step 33:
collectible `AssemblyLoadContext`, discovery, error capture, verified-collectible unload) but
no user-facing way to see, enable, disable, install, or remove plugins. This step adds that
surface. Split out of [plugin-hosting.md](plugin-hosting.md)'s shared-UX list (2026-08-26) so
it can ship on the *existing managed host* without waiting for the native VST3/AU/OFX bridges —
and so those bridges (steps 31/33) and the open standards (step 59) land into a manager that
already exists. Tracked in [PLAN.md](../../PLAN.md) Open work.

## Why / product context

Users can drop a DLL into a folder today, but a broken plugin only surfaces in the crash log,
and there is no way to disable or remove one without filesystem surgery. Comparable behavior
to follow: DAW-style plugin managers (REAPER's plugin browser + rescan, Ableton's Plug-Ins
preferences with per-plugin enable) rather than NLEs (Premiere/Resolve manage plugins purely
by filesystem — weaker UX we deliberately improve on). Keep their conventions: a rescan
button, per-plugin enable toggles, and an "open plugins folder" escape hatch.

## Existing seams

- `src/Sprocket.Plugins` — `PluginHost` (interface scan; per-file/per-type failures recorded
  in `Errors`, never thrown; reserved `builtin.` ids rejected; `Unload` verified collectible
  by test) + `PluginLoadContext` (`AssemblyDependencyResolver` isolation).
- `src/Sprocket.App/PluginService.cs` — startup wiring; discovery from `<exe>/Plugins` and
  `%APPDATA%/Sprocket/Plugins` (broken plugins log + skip).
- `Sprocket.Core/Model/EffectCatalog.cs` — `Register`/`Unregister`/`All`; browsers, Effects
  menu, and Inspector all draw from `All`, so add/remove propagates for free.
- Unknown-effect-id behavior (§15) — a project using a disabled/uninstalled plugin's effects
  loads with those effects passing through; nothing new needed for graceful degradation.
- `UserSettingsStore` / `UserSettings` — persistence pattern for the disabled-plugin list.
- Window patterns: `ExportQueueWindow.cs` / `ProxyStatusWindow.cs`; STYLE_GUIDE.md surfaces.

## Implementation sketch

1. **Model/service (App):** extend `PluginService` to hold per-plugin state — source path,
   assembly name/version, effects contributed (descriptors), status (Loaded / Disabled /
   Error + message from `PluginHost.Errors`). Persist a disabled list in `UserSettings`
   (skip at startup).
2. **Enable/disable live:** disable = `EffectCatalog.Unregister` (+ audio provider
   unregister) + ALC `Unload`; enable = load + register. Open projects keep the effect
   instances; they render/process as pass-through while disabled (§15), and revive on
   re-enable.
3. **Window (Edit ▸ Plugins…):** list with name, version, path, contributed effects, status
   (error rows show the captured message); per-row Enable toggle + Uninstall (user-dir
   plugins only); toolbar: **Install Plugin…** (file picker → copy into
   `%APPDATA%/Sprocket/Plugins` → load), **Rescan**, **Open Plugins Folder**. `<exe>/Plugins`
   entries are read-only rows (bundled location).
4. **Docs/inventory:** FEATURES.md row (starts ❌) in the shipping change; the FEATURES.md
   "Not user-facing — never document" plugin-internals row gains a pointer to the new
   user-facing row; README Features mention when it lands.

## Tests

- Plugins: unload → re-load round-trip keeps catalog consistent; error rows populated from a
  deliberately-broken assembly.
- App: disabled list persists and is honored at startup; disable/enable round-trip
  unregisters/re-registers descriptors; browsers/Effects menu reflect it; install-copy path.
- Existing §15 pass-through tests already cover disabled-plugin project playback.

## On completion

Flip the step 58 ledger row, check the PLAN.md todo, append the DONE log to
`plan/history/` (new `steps-58+.md` chunk), update FEATURES.md, and fold this file's
still-relevant notes into that log.
