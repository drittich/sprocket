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
- **Windows and Linux releases bundle FFmpeg 8.** macOS assets are attached only when their FFmpeg 8
  dylibs are bundled correctly; some releases may omit macOS downloads entirely.
- The windowed GPU preview and audio output are display/device-bound and rest on manual verification.
- **FFmpeg licensing (LGPL vs GPL)** for distribution has not been finalized.

## Running it

Windows and Linux archives are self-contained builds — unzip and run the `Sprocket` executable; no
.NET install or system FFmpeg is required.

- **Windows:** unzip and run `Sprocket.exe`. FFmpeg 8 is bundled.
- **Linux:** unzip, then `chmod +x Sprocket` and run `./Sprocket`. FFmpeg 8 is bundled.
- **macOS:** if a macOS asset is attached to the release, read the macOS section below first.

### 🍎 macOS

Some releases may omit macOS downloads entirely. If a release has no `osx-x64` or `osx-arm64` asset
attached, macOS is not published for that release yet.

When a macOS archive does not bundle FFmpeg 8 yet, install it with Homebrew and point Sprocket at the
Homebrew `lib` directory before launch:

1. **Unzip** the download, then in Terminal `cd` into the unzipped folder and run:
   ```bash
  brew install ffmpeg@8
  export SPROCKET_FFMPEG8_DIR="$(brew --prefix ffmpeg@8)/lib"
   chmod +x Sprocket
   xattr -dr com.apple.quarantine .   # clear Gatekeeper's quarantine (the build isn't notarized yet)
   ```

2. **Launch it:**
   ```bash
   ./Sprocket
   ```

Apple Silicon and Intel Macs are both supported (use the `osx-arm64` or `osx-x64` download
respectively). A signed, notarized `.app` so even the `xattr` step isn't needed is planned for a later
release. If video will not open, confirm that `SPROCKET_FFMPEG8_DIR` points to the directory that
contains `libavcodec.62.dylib`, `libavformat.62.dylib`, `libavutil.60.dylib`, `libswscale.9.dylib`,
and `libswresample.6.dylib`.
