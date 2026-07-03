<!--
  This file is the EVERGREEN release-body preamble, used verbatim by the CI release workflow
  (.github/workflows/release.yml) as the standing notes for every GitHub release. Keep it
  version-agnostic: do NOT add a hardcoded version number or a per-release "what's new / what works /
  not yet" feature list here — those drift out of date. The per-release "What's changed since
  <prev tag>" section is generated automatically from the git commit log (scripts/changelog.ps1) and
  PREPENDED above this content at release time; the full roadmap/status lives in PLAN.md. Only edit
  this file to change the standing guidance below (bug reporting, installing/running the app, known
  limitations, licensing).

  FORMATTING: fenced code blocks (``` lines) MUST start at column 0 — no leading whitespace on the
  opening or closing fence. GitHub renders an indented fence as broken output. To show a code block
  as a step, use a bold label (e.g. "**1. Unzip**") followed by a top-level fence, not a list item
  with the fence indented under it.
-->

# Sprocket — Alpha

Sprocket is a cross-platform (Windows 11 · Linux · macOS), non-destructive video editor built on
.NET 10, FFmpeg 8, and Skia. This is an **early alpha**: the editing core is real and end-to-end, but
some of the feature set is still in progress and the cross-platform builds have had limited on-device
testing. Expect rough edges.

- **What's new in this release** is summarized in the **"What's changed"** section above (generated
  from the commits since the previous release).
- **The full roadmap and current status** live in
  [`PLAN.md`](https://github.com/SprocketVideo/Sprocket/blob/main/PLAN.md).
- **Project website:** <https://sprocketvideo.org>

## 🐞 Found a bug? Tell us — it's quick

**[→ Click here to file an issue](https://github.com/SprocketVideo/Sprocket/issues/new)** (a free GitHub
account is all you need). Or from the repo, go to the **Issues** tab → **New issue**.

To help us reproduce it fast, please include what you can:

- **What you did** — the steps leading up to it.
- **What happened** vs. **what you expected**.
- **Your OS** (Windows 11 / Linux / macOS) and which download you used (e.g. the Windows installer,
  the AppImage, or a portable zip).
- **The version** — shown in the release title above and under **Help ▸ About** in the app.
- A screenshot, the media file, or the `.sprocket.json` project if it's relevant.

Crashes, confusing UI, and "is this supposed to work?" questions are all welcome — there are no bad
reports during an alpha. If a feature seems missing, check `PLAN.md` first; it may simply be later in
the roadmap.

## Installing it

Every download is self-contained — no .NET install or system FFmpeg is required. **The alpha builds
are not code-signed yet**, so each OS shows a one-time warning the first time you run them; the steps
below get you past it. Installed builds (Windows installer, Linux AppImage, macOS app) check for
updates on launch and can update themselves in place — you install once.

### 🪟 Windows

Download **`Sprocket-win-x64-Setup.exe`** (or `win-arm64` for Windows on ARM) and run it.

- SmartScreen will warn because the alpha isn't code-signed: click **More info → Run anyway**.
- Sprocket installs per-user (no admin rights), appears in the Start menu, and updates itself.
- Prefer no installer? The portable `Sprocket-<version>-win-x64.zip` is also attached — unzip and
  run `Sprocket.exe` (portable builds don't self-update).

### 🐧 Linux

Download **`Sprocket-linux-x64.AppImage`**, then:

```bash
chmod +x Sprocket-linux-x64.AppImage
./Sprocket-linux-x64.AppImage
```

- The AppImage integrates a launcher icon and updates itself.
- If it won't start, your distro may need FUSE for AppImages (e.g. Ubuntu ≥ 22.04:
  `sudo apt install libfuse2`), or use the portable zip instead: unzip, `chmod +x Sprocket`, run
  `./Sprocket` (the included `install.sh` adds a launcher icon; portable builds don't self-update).
- `linux-arm64` is portable-zip only for now.

### 🍎 macOS

Download the **`Sprocket-osx-arm64-Portable.zip`** (Apple Silicon) or **`Sprocket-osx-x64-Portable.zip`**
(Intel), unzip it, and drag **Sprocket.app** into **Applications**.

Because the alpha isn't notarized yet, macOS blocks the first launch. Any ONE of these clears it:

- **Right-click** (Control-click) Sprocket.app → **Open** → **Open** in the dialog (may need doing twice), or
- **System Settings ▸ Privacy & Security** → scroll down → **Open Anyway** (macOS 15 Sequoia shows
  the blocked app there after your first launch attempt), or
- in Terminal:

```bash
xattr -dr com.apple.quarantine /Applications/Sprocket.app
```

FFmpeg 8 is bundled inside the app — no Homebrew setup is needed. In-app self-update on unsigned
macOS builds is experimental; if an update fails, just download the new zip.

## ⚠️ Known limitations & platform notes

- **Primary testing is on Windows 11.** Linux and macOS run the *identical* managed code, but
  windowed-GPU and on-device verification there is still in progress — treat those builds as
  experimental.
- The windowed GPU preview and audio output are display/device-bound and rest on manual verification.
- The bundled FFmpeg is a **GPL build** (it provides the H.264/H.265 export encoders); its
  corresponding source is linked in
  [`THIRD-PARTY-NOTICES.md`](https://github.com/SprocketVideo/Sprocket/blob/main/THIRD-PARTY-NOTICES.md),
  which also ships inside the app (Help ▸ Third-Party Notices).

### 🐧 Linux: if the app closes when you open a video

Some Linux systems have an unstable GPU video-decode driver (VAAPI) that can crash the app the first
time it decodes a clip — for example when you use **File ▸ Open Sample Project** or import media. If
Sprocket closes at that moment, force software decoding by setting `SPROCKET_HWACCEL=off` before launch:

```bash
SPROCKET_HWACCEL=off ./Sprocket-linux-x64.AppImage
```

If that fixes it, your system's hardware decoder was the culprit — playback simply uses the CPU instead.

Two things that help us pin it down (please include them in a bug report):

- **Logs** are written to `~/.local/share/Sprocket/logs/`. The exact folder is also shown under
  **Help ▸ About** (with an *Open Logs Folder* button). Attach the newest log file.
- You can check decoding from a terminal without the UI (from the portable zip's folder):

```bash
./Sprocket --probe Samples/sample.mp4
```

This prints the media's details (resolution, codec, whether hardware decode was used) — or the full
error if it fails.
