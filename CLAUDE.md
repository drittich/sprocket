# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Sprocket — a cross-platform (Windows 11, Linux, macOS) non-destructive video editor in C# / .NET 10.
The work is structured around three documents that must stay authoritative; read them before
making non-trivial changes:

- [BRIEF.md](BRIEF.md) — the feature brief (the *what*).
- [ARCHITECTURE.md](ARCHITECTURE.md) — the detailed technical design the implementation must conform to (the *how/why*). Sections are referenced throughout the code as `§N`.
- [PLAN.md](PLAN.md) — feasibility analysis and the numbered build order, with a ✅ status note recorded inline on each completed step. **When you complete a build-order step, update its PLAN.md entry** the same way existing steps are annotated.
- [UI.md](UI.md) — the target UI mockup and the features it implies.

**When planning a feature, compare it to how Adobe Premiere Pro, DaVinci Resolve, and Final Cut Pro
do the equivalent thing.** Prefer re-using their established behavior — the feature's implementation
approach, default values, naming conventions, and keyboard shortcuts — over inventing our own, so the
editor feels familiar to users coming from professional tools. Note any deliberate departure and why.

The project is mid-build: the **vertical slice (PLAN steps 1–9) is complete** — import, trim, GPU
effects (brightness/fade), audio master clock + mixer, hardware-accel decode, MP4 export, and JSON
save/load, all on one cross-platform managed codebase. The post-slice UI build-out (steps 10+) and
cross-platform packaging (steps 24–25) remain.

Do not commit `.NET` build artifacts: never add any `/bin` or `/obj` directory, or anything under
them, to version control.

## Commands

```bash
# Build everything (uses the .slnx solution)
dotnet build Sprocket.slnx

# Run all tests
dotnet test Sprocket.slnx

# Run one test project
dotnet test tests/Sprocket.Core.Tests/Sprocket.Core.Tests.csproj

# Run a single test by name filter
dotnet test tests/Sprocket.Core.Tests/Sprocket.Core.Tests.csproj --filter "FullyQualifiedName~TimingTests"

# Run the editor app (optional first arg = media file; otherwise a sample clip is generated)
dotnet run --project src/Sprocket.App [path/to/media.mp4]

# Linux headless cross-platform verification (decode→SkSL→offscreen PNG), needs Docker — see header of the script
bash scripts/linux-check.sh
```

**`ffmpeg` CLI must be on PATH** to run `Sprocket.Media.Tests` / `Sprocket.Audio.Tests` /
`Sprocket.Export.Tests` / `Sprocket.Playback.Tests`: they generate a deterministic fixture clip once via
the `ffmpeg` command (see `TestVideo.cs`), cached in the test output dir. They also need **FFmpeg 8**
shared natives at runtime, resolved by `Sprocket.Media`'s `FFmpegLoader` (which searches the output dir
first, then `%SPROCKET_FFMPEG8_DIR%`, preloading in dependency order). Two ways to supply them:
- **Locally (zero setup after a one-time extract):** the decode/encode-backed projects
  (`Media`/`Playback`/`Export.Tests`) set `<UsesFFmpegNatives>true`, and `tests/Directory.Build.targets`
  copies the natives that
  `scripts/release.ps1` extracts into `./.ffmpeg-cache/extract-<rid>/.../{bin,lib}` (gitignored) into the
  test output dir. Run `release.ps1` once to populate the cache; thereafter `dotnet test` and the IDE
  runner resolve the natives with no env var. The copy is best-effort/no-op when the cache is absent.
- **CI / explicit:** set **`SPROCKET_FFMPEG8_DIR`** to the `bin` (Windows) / `lib` (Linux/macOS) of an
  extracted FFmpeg 8 build (BtbN `*-gpl-shared`).

The decode tests keep the `win-x64` RID on Windows for a stable output layout (there is no longer a
transitive native NuGet to land). Pointing the FFmpeg-8 `bin` onto PATH satisfies both the CLI fixture
step and native resolution at once.

Tests are **xUnit**. There is no separate lint step; `Sprocket.Core` and `Sprocket.Media` build with
`TreatWarningsAsErrors=true`, so warnings break the build there.

## The non-negotiable performance rule

**Pixel data must never be allocated on the managed heap per frame** (ARCHITECTURE §1). Decoded
frames stay in native FFmpeg `AVFrame` buffers → wrapped by pointer with `SKImage.FromPixels` →
all compositing/effects run as Skia GPU operations. C# holds handles/pointers only; the few
unavoidable crossings (audio PCM) use pooled / pinned native buffers. Any change to the
decode/upload/render hot path must preserve this — verify with an allocation profiler, target ~0
Gen0 per frame. **No C++/CLI** anywhere: all native interop is P/Invoke against a C ABI so one
managed build serves all three OSes; only the bundled native libs differ per RID.

## Architecture (the parts that span files)

Projects and their **acyclic dependency direction** (ARCHITECTURE §2):

```
Sprocket.App ──► Sprocket.Playback ──► Sprocket.Render ──► Sprocket.Core
     │              │      │              │
     │              │      └──► Sprocket.Audio ──► Sprocket.Core
     │              └──► Sprocket.Media ──────────► Sprocket.Core
     └──► (Persistence, later) ──► Sprocket.Core
```

- **`Sprocket.Core`** is the keystone and depends on **nothing** (no native, no UI — its build
  output is `Sprocket.Core.dll` alone). It holds the pure-data timeline model
  (`Project → Timeline → Track[] → Clip`), the pure render graph (`RenderGraph.PlanVideoFrame` /
  `PlanAudioBuffer` produce a serializable plan; a generic executor drives it), the time model, and
  the **seam interfaces** everyone else implements: `IFrameSource`/`IVideoCompositor` (video),
  `IPcmReader` (audio PCM pull), `IClock`/`IMasterClock` (transport). Keep native/GPU/FFmpeg types
  out of Core.
- **`Sprocket.Media`** — FFmpeg 8 interop via Sprocket's **own hand-rolled `[LibraryImport]` binding**
  (`Native/LibAv.cs` + explicit-layout `Native/AvStructs.cs`, behind a thin RAII layer `Native/Handles.cs`
  / `SwsScaler` / `SwrResampler`; chosen by the Phase-0 spike — no external FFmpeg NuGet): `MediaSource`
  (probe/decode/seek), `AudioSource` (`IPcmReader`, resamples to project rate via libswresample),
  `VideoFramePool`, `VideoDecodeRing` (one worker → bounded `Channel<>`), `HardwareContext` (hw-accel
  decode behind `IHardwareContext`, always with software fallback). `FFmpegLoader` resolves/loads the
  FFmpeg 8 natives and **version-guards** to libavcodec major 62. **No SkiaSharp/UI** — pixels stay native.
- **`Sprocket.Audio`** — `AudioMixer`, `AudioEngine` (the **master clock**), `OpenAlAudioOutput`
  behind `IAudioOutput`. **Depends only on Core, not Media** — the FFmpeg audio decode lives in
  Media and is wired to the mixer by the `Sprocket.App` composition root, keeping the mixer/clock
  FFmpeg-free.
- **`Sprocket.Render`** — SkiaSharp compositing (`FramePresenter`); effects as `SKRuntimeEffect`
  (SkSL) shaders are added in step 7.
- **`Sprocket.Playback`** — `PlaybackEngine` (clock-driven pump that drops/holds frames for A/V
  sync), `SoftwareClock`, `IVideoFrameFeed`.
- **`Sprocket.App`** — Avalonia UI shell + composition root that wires the concrete implementations
  to Core's seams.
- **`Sprocket.Spike`** — the standalone de-risk spike from PLAN step 1. **Not part of the app**;
  leave it as the reference artifact.

Cross-cutting design facts that aren't obvious from any one file:

- **Time is `long` ticks at `Timecode.TicksPerSecond = 240000`** (exact for 48 kHz audio and all
  common + NTSC frame rates). Frame rates are `Rational`, never `double`. Never use `double`
  seconds for positions/durations — it desyncs long timelines (ARCHITECTURE §3).
- **Audio is the master clock.** The audio device's played-frame count is converted to ticks to
  drive video sync; the render pump drops frames when behind and holds when ahead (§6, §8).
- **The same render graph serves preview and export**, and is a pure function of (project, time) —
  this determinism is what makes golden-frame testing possible. Don't add hidden state to it.
- **Non-destructive by construction:** edits change a `Clip`'s `SourceIn/Out`, `TimelineStart`, or
  `Effects` list; source bytes are never written.
- **Undo/redo is a first-class requirement** (PLAN step 10): every model mutation is meant to go
  through a command stack — there should be no direct-mutation path that bypasses it.
- **Avalonia↔Skia GPU seam:** compose on Avalonia's **shared `GRContext`** obtained via
  `ISkiaSharpApiLeaseFeature.Lease()` inside an `ICustomDrawOperation`, on the render thread (§10).
  Never touch `GRContext` off that thread.
- **New features land on existing seams, not rewrites** (§17): hardware decode = new
  `IFrameSource`/`IHardwareContext`; color grading / transform = new effect-chain stage; proxy
  media = alternate `IFrameSource` (export always pulls full-res originals); plugins implement the
  existing `IVideoEffect`.

## Version pinning (do not bump casually)

Versions were verified together by the spike (ARCHITECTURE §14). In particular **SkiaSharp is
pinned to 3.119.4 to match Avalonia 12.0.5's transitive SkiaSharp** — a newer SkiaSharp loads a
second, incompatible Skia assembly and the GPU lease's types stop matching. Avalonia 12.0.5 and
Silk.NET.OpenAL 2.23 round out the locked stack.

**FFmpeg 8 via a hand-rolled binding (migrated off the dormant Sdcb.FFmpeg 2026-06-29).** `Sprocket.Media`
P/Invokes FFmpeg directly (`Native/LibAv.cs`); there is **no FFmpeg binding NuGet and no FFmpeg runtime
NuGet for any RID**. The FFmpeg 8 shared natives (avcodec-62 / avutil-60 / avformat-62 / swscale-9 /
swresample-6) are bundled **per-RID for every platform** by `scripts/release.ps1` (BtbN `*-gpl-shared`),
and in dev/test resolved from `%SPROCKET_FFMPEG8_DIR%`. The explicit struct offsets in `Native/AvStructs.cs`
are pinned to FFmpeg 8.1's x64 layout (`FFmpegLoader` enforces libavcodec major 62); regen the offsets
only on a new FFmpeg **major** (SONAME bump) — the procedure (and the decision record) is in
`Native/SPIKE_RESULTS.md`, and `Native/FUTURE_BINDINGS.md` lists the surface later PLAN steps add. The
three-arm decision spike (`Sprocket.Spike.Bindings`, FFmpeg.AutoGen + Flyleaf) was removed post-migration
to keep dev-only NuGets out of the tree; it lives in git history on the `ffmpeg8-migration` branch.
**Do not reintroduce Sdcb.FFmpeg** (it never shipped FFmpeg 8).
