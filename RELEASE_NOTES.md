<!--
  This file is the EVERGREEN GitHub-release-body preamble, used verbatim by the CI release workflow
  (.github/workflows/release.yml) as the standing notes for every GitHub release. Keep it
  version-agnostic: do NOT add a hardcoded version number or a per-release "what's new / what works /
  not yet" feature list here — those drift out of date. The per-release "What's changed since
  <prev tag>" section is generated automatically from the git commit log (scripts/changelog.ps1) and
  PREPENDED above this content at release time; the full roadmap/status lives in PLAN.md. Only edit
  this file to change the standing guidance below (bug reporting, installing/running the app, known
  limitations, licensing).

  This file is GITHUB-ONLY. It is NOT what the in-app "Update Available" dialog shows: the Velopack
  feed is packed with the generated change overview alone (scripts/release.ps1's Get-FeedReleaseNotes),
  because install/first-launch guidance is noise to someone already in the app. Nothing written here
  may assume an in-app reader, and nothing here may reference the release page's layout as if the app
  could see it.

  FORMATTING: fenced code blocks (``` lines) MUST start at column 0 — no leading whitespace on the
  opening or closing fence. GitHub renders an indented fence as broken output. To show a code block
  as a step, use a bold label (e.g. "**1. Unzip**") followed by a top-level fence, not a list item
  with the fence indented under it.
-->

# Sprocket — Alpha

Sprocket is a cross-platform (Windows 10 & 11 · Linux · macOS), non-destructive video editor built on
.NET 10, FFmpeg 8, and Skia. This is an **early alpha**: the editing core is real and end-to-end, but
some of the feature set is still in progress and the cross-platform builds have had limited on-device
testing. Expect rough edges.

- **What's new in this release** is listed under **"What's changed"** on this page, generated from the
  commits since the previous release.
- **The full roadmap and current status** live in
  [`PLAN.md`](https://github.com/SprocketVideo/Sprocket/blob/main/PLAN.md).
- **Project website:** <https://sprocketvideo.org>

## 🐞 Found a bug? Tell us — it's quick

**[→ Click here to file an issue](https://github.com/SprocketVideo/Sprocket/issues/new)** (a free GitHub
account is all you need). Or from the repo, go to the **Issues** tab → **New issue**.

To help us reproduce it fast, please include what you can:

- **What you did** — the steps leading up to it.
- **What happened** vs. **what you expected**.
- **Your OS** (Windows 10 / 11 / Linux / macOS) and which download you used (e.g. the Windows installer,
  the AppImage, or a portable zip).
- **The version** — shown in the release title above and under **Help ▸ About** in the app.
- A screenshot, the media file, or the `.sprocket.json` project if it's relevant.

Crashes, confusing UI, and "is this supposed to work?" questions are all welcome — there are no bad
reports during an alpha. If a feature seems missing, check `PLAN.md` first; it may simply be later in
the roadmap.

## Installing it

**Use the Download table at the top of this release to grab the one file for your OS**, then follow
the matching first-launch step below. Every download is self-contained — no .NET install or system
FFmpeg is required. **The alpha builds are not code-signed yet**, so each OS shows a one-time warning
the first time you run them; the steps below get you past it. Installed builds (Windows installer, Linux AppImage, macOS app) check for
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

- On first launch Sprocket offers to **add itself to your applications menu** (a per-user launcher +
  icon, no root needed), so you can start it like any installed app afterwards. You can add or remove
  that entry anytime from the **Help** menu. The AppImage also updates itself in place.
- Prefer to keep it fully portable, or want it to integrate automatically? Installing
  [AppImageLauncher](https://github.com/TheAssassin/AppImageLauncher) makes your system prompt to
  integrate any AppImage on first run — an alternative to Sprocket's built-in offer.
- If it won't start, your distro may need FUSE for AppImages (e.g. Ubuntu ≥ 22.04:
  `sudo apt install libfuse2`), or use the portable zip instead: unzip, `chmod +x Sprocket`, run
  `./Sprocket` (the included `install.sh` adds a launcher icon; portable builds don't self-update).
- `linux-arm64` is portable-zip only for now.

**Supported Linux baseline.** The Linux builds are **glibc**-based and target **modern desktop
distributions with glibc 2.35 or newer** (Ubuntu 22.04 LTS and later, Debian 12+, Fedora, recent
Arch/openSUSE). **musl-based distributions (Alpine, etc.) are not supported.** Treat Linux as
**experimental** for this alpha: software decode/encode is the dependable path, and GPU acceleration
depends on your host drivers (see below).

**Check your system in one command.** The build ships a self-check that reports your glibc version,
loads the bundled FFmpeg/OpenAL, and lists any missing system libraries with the exact package to
install for your distro. The portable zip's `install.sh` runs it automatically; you can also run it
anytime:

```bash
./Sprocket --doctor
```

**Runtime dependencies.** Self-contained builds bundle .NET, FFmpeg 8, and OpenAL, but the bundled
FFmpeg loads a few host libraries at runtime and the GUI needs the usual desktop libraries. Most
desktop installs already have these; on a **minimal** install, `--doctor` tells you which are missing.
Install by family:

| Library | Used for | Debian/Ubuntu (`apt`) | Fedora (`dnf`) | Arch (`pacman`) | openSUSE (`zypper`) |
|---|---|---|---|---|---|
| `libxml2.so.2` | FFmpeg XML/DASH | `libxml2` | `libxml2` | `libxml2` | `libxml2-2` |
| `libdrm.so.2` | DRM / VAAPI paths | `libdrm2` | `libdrm` | `libdrm` | `libdrm2` |
| `libva.so.2`, `libva-drm.so.2` | VAAPI HW accel (optional) | `libva2 libva-drm2` | `libva` | `libva` | `libva2 libva-drm2` |
| `libvdpau.so.1` | VDPAU HW accel (optional) | `libvdpau1` | `libvdpau` | `libvdpau` | `libvdpau1` |
| `libfontconfig.so.1` | GUI text | `libfontconfig1` | `fontconfig` | `fontconfig` | `fontconfig` |
| `libX11.so.6` | GUI (X11) | `libx11-6` | `libX11` | `libx11` | `libX11-6` |

**Hardware acceleration is opt-in and driver-dependent.** VAAPI (Intel/AMD) and NVENC (NVIDIA) require
matching host drivers (Mesa/intel-media-driver, or the NVIDIA driver with `libnvidia-encode`). When they
are absent or unstable, Sprocket falls back to software encode/decode — the reliable path. See the
VAAPI-crash note under *Known limitations* for the `SPROCKET_HWACCEL=off` escape hatch.

### 🍎 macOS

Download the **`Sprocket-osx-arm64-Portable.zip`** (Apple Silicon) or **`Sprocket-osx-x64-Portable.zip`**
(Intel), unzip it, and drag **Sprocket.app** into **Applications**.

Because the alpha isn't notarized yet, macOS blocks the first launch — usually with a dialog
claiming *"Sprocket.app" is damaged and can't be opened. You should move it to the Trash.*
**The download is not damaged** — that's just how recent macOS reports an app it can't verify.
To clear it, run this in Terminal, then launch normally:

```bash
xattr -dr com.apple.quarantine /Applications/Sprocket.app
```

Alternatively, after one blocked launch attempt, **System Settings ▸ Privacy & Security** may
offer an **Open Anyway** button for Sprocket (scroll down) — but if you got the "damaged" dialog
it often doesn't appear, so the Terminal command is the reliable path. The classic right-click →
**Open** trick no longer works on macOS 15 Sequoia and later — Apple removed that bypass for
unsigned apps.

FFmpeg 8 is bundled inside the app — no Homebrew setup is needed. In-app self-update on unsigned
macOS builds is experimental; if an update fails, just download the new zip.

## ⚠️ Known limitations & platform notes

- **Primary testing is on Windows 11.** Windows 10 (64-bit, version 1809 or later) is supported;
  its coverage is a manual smoke checklist rather than CI (GitHub Actions has no Windows 10
  runners). Linux and macOS run the *identical* managed code, but windowed-GPU and on-device
  verification there is still in progress — treat those builds as experimental.
- **Linux support is experimental and scoped to modern glibc desktops (glibc ≥ 2.35);** musl distros
  (Alpine) are unsupported. Release CI runs the full build + smoke on Ubuntu and additionally runs an
  informational headless smoke of the published zips across several distros (Ubuntu 22.04, Debian,
  Fedora, openSUSE, Arch) and an emulated `linux-arm64` — but these are best-effort signals, not a
  release gate. Run `./Sprocket --doctor` to confirm your own host and see any missing dependencies.
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
error if it fails. For a full environment report (glibc baseline, bundled-native load, and any missing
system libraries with per-distro install hints), run `./Sprocket --doctor`.
