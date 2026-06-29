# Sprocket — Alpha

Sprocket is a cross-platform (Windows 11 · Linux · macOS), non-destructive video editor built on
.NET 10, FFmpeg 7, and Skia. This is an **early alpha**: the editing core is real and end-to-end,
but large parts of the feature set are still to come and the cross-platform builds have had limited
on-device testing. Expect rough edges.

## 🐞 Found a bug? Tell us — it's quick

**[→ Click here to file an issue](https://github.com/drittich/sprocket/issues/new)** (a free GitHub
account is all you need). Or from the repo, go to the **Issues** tab → **New issue**.

To help us reproduce it fast, please include what you can:

- **What you did** — the steps leading up to it.
- **What happened** vs. **what you expected**.
- **Your OS** (Windows 11 / Linux / macOS) and which download you used (e.g. `win-x64`).
- **This version:** `v0.1.20-alpha.1` (also under **Help ▸ About** in the app).
- A screenshot, the media file, or the `.sprocket.json` project if it's relevant.

Crashes, confusing UI, and "is this supposed to work?" questions are all welcome — there are no bad
reports during an alpha. Please skip the items in "Not in this build yet" below; those are known.

## ✅ What works in this build

**Editing**
- Import media (MP4/MOV/MKV/WebM/etc. via FFmpeg); probe duration, streams, frame rate.
- Non-destructive trim, drag-to-move, blade (razor) split, and slip; linked A/V clips.
- Multiple video and audio tracks; add tracks; per-track mute / solo / enable.
- Undo/redo for every edit; working menu bar (File · Edit · Clip · Effects · View · …) with
  keyboard accelerators; cut/copy/paste/delete/nudge.

**Effects & keyframing (GPU)**
- Built-in effects as GPU (SkSL) shaders: **Brightness**, **Fade**, **Transform**
  (scale / position / rotation / anchor / opacity), and **Color** (exposure / contrast / saturation).
- Every effect parameter is keyframeable with Hold / Linear / Ease / custom **Bezier** interpolation,
  including an editable velocity-graph editor and multi-select keyframe editing.

**Audio**
- Mixer with per-track gain (dB), mute, solo, master gain, and fades; audio is the master clock.

**Playback & monitoring**
- Hardware-accelerated decode (D3D11VA / CUDA / QSV on Windows, VAAPI/CUDA on Linux, VideoToolbox on
  macOS) with automatic software fallback.
- A/V-synced 1080p preview; dual **Source / Program** monitors with safe-area / framing-grid overlay,
  Fit / 50 / 100 / 200 % zoom, and full transport (jump-to-start/end, frame-step, play/pause).

**Project & I/O**
- Media bin with poster-frame thumbnails, audio waveforms, format/resolution badges, and search;
  Effects browser; type-driven Inspector.
- Export the timeline to a full-resolution **H.264 / AAC MP4**.
- Save / load projects as JSON (relinks media by relative path when moved alongside the project).

## 🚧 Not in this build yet

- **Export formats:** only H.264/AAC MP4 — no broader container/codec matrix, hardware encoders, or
  export presets yet.
- **Proxy media** and the **preview render cache / pre-render ("freeze")**.
- **Transitions** (cross-dissolve, etc.) and overlapping-clip resolution.
- **Generators / titles**, **adjustment layers**, and **nested sequences / compound clips**.
- **Alpha-channel** media compositing.
- **Audio effects** and **VST3 / AU plugin hosting**; the video **plugin host**.
- **Color-grading** toolset (wheels / curves / qualifiers / scopes) and **log / D-Log** input
  transforms.
- **Spatial motion paths** (2-D position curves) and multi-clip selection (Select All is disabled).

## ⚠️ Known limitations & platform notes

- **Primary testing is on Windows 11.** Linux and macOS run the *identical* managed code, but
  windowed-GPU and on-device verification there is still in progress — treat those builds as
  experimental.
- **macOS builds ship without the FFmpeg libraries.** There is no automated source for FFmpeg 7.1
  macOS `.dylib`s yet, so you supply FFmpeg 7 once — a one-time, two-minute setup covered step by step
  in **🍎 macOS — get running** below. Windows and Linux archives bundle FFmpeg automatically.
- The windowed GPU preview and audio output are display/device-bound and rest on manual verification.
- **FFmpeg licensing (LGPL vs GPL)** for distribution has not been finalized.

## Running it

Each archive is a self-contained build — unzip and run the `Sprocket` executable; no .NET install or
system FFmpeg is required.

- **Windows:** unzip and run `Sprocket.exe`. FFmpeg is bundled.
- **Linux:** unzip, then `chmod +x Sprocket` and run `./Sprocket`. FFmpeg is bundled.
- **macOS:** one extra step — see below.

### 🍎 macOS — get running (one extra step for FFmpeg)

The macOS archive ships **without FFmpeg**, so you need to supply FFmpeg **7** once. The easiest way is
[Homebrew](https://brew.sh):

1. **Install FFmpeg 7** (skip if you already have it):
   ```bash
   brew install ffmpeg
   ```
   Sprocket needs the **FFmpeg 7.x** libraries (`libavcodec.61`, `libswscale.8`, …). Confirm with
   `ffmpeg -version` — the first line should start with `7.`. If Homebrew gives you a newer major
   (8.x), Sprocket won't load it; install the 7 line instead (e.g. `brew install ffmpeg@7`).

2. **Unzip** the download, then in Terminal `cd` into the unzipped folder and make the app runnable:
   ```bash
   chmod +x Sprocket
   xattr -dr com.apple.quarantine .   # clear Gatekeeper's quarantine (the build isn't notarized yet)
   ```

3. **Launch it**, pointing Sprocket at Homebrew's FFmpeg 7 libraries:
   ```bash
   DYLD_FALLBACK_LIBRARY_PATH="$(brew --prefix ffmpeg)/lib" ./Sprocket
   ```
   To avoid typing that each time, copy the FFmpeg libraries next to the executable once, then just
   run `./Sprocket`:
   ```bash
   cp "$(brew --prefix ffmpeg)"/lib/lib{avcodec,avformat,avutil,avfilter,avdevice,swscale,swresample,postproc}*.dylib .
   ```

If video won't open or playback/export does nothing on macOS, it's almost always a missing or
wrong-version FFmpeg — re-check step 1 (`ffmpeg -version` must report 7.x). Apple Silicon and Intel
Macs are both supported (use the `osx-arm64` or `osx-x64` download respectively). A signed, notarized
`.app` that bundles FFmpeg so none of this is needed is planned for a later release.
