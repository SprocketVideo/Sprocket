<!--
  This file is the EVERGREEN release-body preamble, used verbatim by scripts/gh-release.ps1 as the
  explicit notes for every GitHub release. Keep it version-agnostic: do NOT add a hardcoded version
  number or a per-release "what's new / what works / not yet" feature list here — those drift out of
  date. The per-release "What's changed since <prev tag>" section is generated automatically from the
  git commit log and PREPENDED above this content at release time; the full roadmap/status lives in
  PLAN.md. Only edit this file to change the standing guidance below (bug reporting, running the app,
  macOS setup, known limitations, licensing).
-->
# Sprocket — Alpha

Sprocket is a cross-platform (Windows 11 · Linux · macOS), non-destructive video editor built on
.NET 10, FFmpeg 8, and Skia. This is an **early alpha**: the editing core is real and end-to-end, but
some of the feature set is still in progress and the cross-platform builds have had limited on-device
testing. Expect rough edges.

- **What's new in this release** is summarized in the **"What's changed"** section above (generated
  from the commits since the previous release).
- **The full roadmap and current status** live in
  [`PLAN.md`](https://github.com/drittich/sprocket/blob/main/PLAN.md).

## 🐞 Found a bug? Tell us — it's quick

**[→ Click here to file an issue](https://github.com/drittich/sprocket/issues/new)** (a free GitHub
account is all you need). Or from the repo, go to the **Issues** tab → **New issue**.

To help us reproduce it fast, please include what you can:

- **What you did** — the steps leading up to it.
- **What happened** vs. **what you expected**.
- **Your OS** (Windows 11 / Linux / macOS) and which download you used (e.g. `win-x64`).
- **The version** — shown in the release title above and under **Help ▸ About** in the app.
- A screenshot, the media file, or the `.sprocket.json` project if it's relevant.

Crashes, confusing UI, and "is this supposed to work?" questions are all welcome — there are no bad
reports during an alpha. If a feature seems missing, check `PLAN.md` first; it may simply be later in
the roadmap.

## ⚠️ Known limitations & platform notes

- **Primary testing is on Windows 11.** Linux and macOS run the *identical* managed code, but
  windowed-GPU and on-device verification there is still in progress — treat those builds as
  experimental.
- **All platforms bundle FFmpeg 8.** Windows, Linux, and macOS archives each ship their own FFmpeg 8
  native libraries — no system FFmpeg, no Homebrew, no `DYLD_*` needed (see **🍎 macOS — get running**
  below; the macOS dylibs have their install names rewritten to load from the app folder).
- The windowed GPU preview and audio output are display/device-bound and rest on manual verification.
- **FFmpeg licensing (LGPL vs GPL)** for distribution has not been finalized.

## Running it

Each archive is a self-contained build — unzip and run the `Sprocket` executable; no .NET install or
system FFmpeg is required.

- **Windows:** unzip and run `Sprocket.exe`. FFmpeg 8 is bundled.
- **Linux:** unzip, then `chmod +x Sprocket` and run `./Sprocket`. FFmpeg 8 is bundled.
- **macOS:** one extra step (Gatekeeper) — see below. FFmpeg 8 is bundled.

### 🍎 macOS — get running (one Gatekeeper step)

The macOS archive **bundles FFmpeg 8** — no Homebrew, no `DYLD_*`, no system FFmpeg. The only extra
step is clearing Apple's quarantine, because the build isn't notarized yet:

1. **Unzip** the download, then in Terminal `cd` into the unzipped folder and run:
   ```bash
   chmod +x Sprocket
   xattr -dr com.apple.quarantine .   # clear Gatekeeper's quarantine (the build isn't notarized yet)
   ```

2. **Launch it:**
   ```bash
   ./Sprocket
   ```

Apple Silicon and Intel Macs are both supported (use the `osx-arm64` or `osx-x64` download
respectively). A signed, notarized `.app` so even the `xattr` step isn't needed is planned for a later
release. If video won't open, confirm you downloaded a macOS archive that includes the `libav*.dylib`
files next to the executable.
