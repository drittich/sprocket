# Sprocket — Alpha

Sprocket is a cross-platform (Windows 11 · Linux · macOS), non-destructive video editor built on
.NET 10, FFmpeg 8, and Skia. This is an **early alpha**: the editing core is real and end-to-end,
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
- **All platforms now bundle FFmpeg 8.** Windows, Linux, and macOS archives each ship their own
  FFmpeg 8 native libraries — no system FFmpeg, no Homebrew, no `DYLD_*` needed (see **🍎 macOS — get
  running** below; the macOS dylibs have their install names rewritten to load from the app folder).
- The windowed GPU preview and audio output are display/device-bound and rest on manual verification.
- **FFmpeg licensing (LGPL vs GPL)** for distribution has not been finalized.

## Running it

Each archive is a self-contained build — unzip and run the `Sprocket` executable; no .NET install or
system FFmpeg is required.

- **Windows:** unzip and run `Sprocket.exe`. FFmpeg 8 is bundled.
- **Linux:** unzip, then `chmod +x Sprocket` and run `./Sprocket`. FFmpeg 8 is bundled.
- **macOS:** one extra step (Gatekeeper) — see below. FFmpeg 8 is bundled.

### 🍎 macOS — get running (one Gatekeeper step)

The macOS archive now **bundles FFmpeg 8** — no Homebrew, no `DYLD_*`, no system FFmpeg. The only extra
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
