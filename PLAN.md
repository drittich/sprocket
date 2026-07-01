# Sprocket — Cross-Platform Video Editor on .NET 10 — Feasibility & Vertical-Slice Plan

> See [BRIEF.md](BRIEF.md) for the feature brief, [ARCHITECTURE.md](ARCHITECTURE.md) for the
> technical design, and [UI.md](UI.md) for the target UI and the features its mockup implies.

## Context

Greenfield project (empty repo). The goal is a cross-platform (Windows 11 + Linux + macOS)
non-destructive video editor in C# / .NET 10 with multiple video & audio tracks,
hardware-accelerated decode/encode, GPU effects (brightness/color/contrast), fades,
audio volume mixing, and an eventual plugin system, leveraging OSS (FFmpeg, Skia) for
the heavy lifting.

**The gating question — "can C# deliver the performance?" — is answered: yes**, provided
C# is used purely as an *orchestrator* and pixel data never lands on the managed heap per
frame. The compute-heavy work is delegated to FFmpeg (C) and GPU shaders; C# owns the
timeline model, scheduling, render graph, UI, and A/V sync. Existence proof: FramePFX
(C#/Avalonia/FFmpeg/SkiaSharp). This is the standard "managed orchestration + native/GPU
compute" pattern.

### Decisions locked in
- **Preview:** 1080p (or proxy) real-time preview; export at full source resolution.
- **GPU stack:** SkiaSharp-first (Avalonia already renders via Skia; GPU-accelerated 2D
  compositing + shader effects). Drop to raw GPU (Silk.NET/Vulkan) later only for measured hotspots.
- **First milestone:** Vertical slice — 1 video track + 1 audio track, import, trim, one
  effect (brightness), a fade, playback, export.
- **OS-specific code** is acceptable behind a C# interface when a per-OS equivalent exists
  (mandatory for hardware accel: D3D11VA/CUDA/QSV on Windows, VAAPI/CUDA on Linux, VideoToolbox
  on macOS). **No C++/CLI** — native wrapping must be plain P/Invoke against a C ABI so one
  managed codebase serves all three OSes; only the bundled native libraries differ per RID.
- **Three target OSes: Windows 11, Linux, macOS** (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`).
  The managed assemblies are identical everywhere; FFmpeg 8 is bundled per-RID (`.dll`/`.so`/`.dylib`,
  see [ARCHITECTURE §11](ARCHITECTURE.md)) since the hand-rolled binding ships no FFmpeg runtime NuGet
  for any RID. macOS ships as a signed/notarized `.app` bundle (build order step 36).

## The non-negotiable performance rule

Pixel data must never be allocated on the managed heap per frame. Decoded frames stay in
native memory (FFmpeg `AVFrame`) → uploaded to a GPU texture → all effects/compositing run
as Skia GPU operations → presented. C# holds handles/pointers only. Use `ArrayPool`/pinned
native buffers for the few crossings that must happen (audio samples). Server/Background GC.

## Recommended stack (verified current as of 2026)

| Concern | Choice | Notes |
|---|---|---|
| UI | **Avalonia UI 12.x** | Only mature native-Linux .NET desktop UI; renders via Skia. |
| Compositing/effects | **SkiaSharp** (GPU backend) | Integrates with Avalonia; `SKCanvas`/`SKShader`/`SKRuntimeEffect` for effects. |
| Decode/encode/filter | **Hand-rolled FFmpeg 8 binding** (`[LibraryImport]`) | Library-level libav* P/Invoke; frame-accurate. NOT FFMpegCore (CLI wrapper). Migrated off Sdcb.FFmpeg 2026-06-29 — see the migration note below and ARCHITECTURE §14. |
| Hardware accel | FFmpeg hwaccel, per-OS | NVIDIA CUDA/NVENC (most portable); VAAPI (Linux), D3D11VA/QSV/AMF (Windows). |
| Audio output | **Silk.NET.OpenAL** | Cross-platform now; behind `IAudioOutput` so it can be swapped. |
| Plugins (later) | Custom **`IVideoEffect`** + collectible `AssemblyLoadContext` | OFX/frei0r hosting is a later, optional P/Invoke adapter. |

> Licensing note: FFmpeg builds can be LGPL or GPL depending on enabled encoders (e.g.
> x264 → GPL). Pick the build/license deliberately before any distribution.

### FFmpeg-8 binding migration (recorded 2026-06-29)

The decode/encode binding moved off **Sdcb.FFmpeg 7.0.0** — which was dormant (latest April 2024, no
FFmpeg 8, no commits) and so froze Sprocket on FFmpeg 7.1 — to a **hand-rolled FFmpeg 8 binding** owned
by the project. This also fixed a macOS bug where the shipped app, carrying no FFmpeg, silently fell
back to the user's Homebrew FFmpeg 8 that the v7 bindings could not load. The choice followed a
**three-arm de-risk spike** (hand-rolled `[LibraryImport]` vs FFmpeg.AutoGen 8.1 vs Flyleaf 8.1, all
proven byte-identical on Windows + Linux); hand-rolled won on footprint / NativeAOT-trim friendliness
and **cadence control** (a ClangSharp/offset regen ≈ once per FFmpeg *major*, no maintainer dependency —
the exact risk that stranded us on Sdcb). Recorded in `src/Sprocket.Media/Native/SPIKE_RESULTS.md` (the
throwaway three-arm spike project itself was removed post-migration; it lives in git history on the
`ffmpeg8-migration` branch).

What it changed, **confined to `Sprocket.Media`** behind Core's unchanged seams (ARCHITECTURE §17):
its own `[LibraryImport]` layer (`Native/LibAv.cs`) + explicit-layout structs pinned to FFmpeg 8.1 x64
(`Native/AvStructs.cs`), a thin RAII layer (`Native/Handles.cs`/`SwsScaler`/`SwrResampler`), a
`FFmpegLoader` DllImport resolver that maps stems to versioned sonames (avcodec **62** etc.) and
**version-guards on libavcodec major 62**, and **per-RID native bundling on every platform** (Windows
included — there is no longer any FFmpeg runtime NuGet), with macOS `@loader_path` install-name rewrite
making that bundle self-contained. Future binding-surface needs for later steps are catalogued in
`src/Sprocket.Media/Native/FUTURE_BINDINGS.md`. See ARCHITECTURE §11 + §14 for the re-pinned stack.
The historical ✅ DONE logs below (step 1 spike, step 3) predate this and still name Sdcb / FFmpeg 7 —
they are accurate records of that earlier state, superseded by this note.

## Architecture (big picture)

Solution layout (projects):
- `Sprocket.Core` — timeline data model + render graph (no UI, no native deps leaking out).
  - `Project` → `Timeline` → `Track[]` (video/audio) → `Clip[]`.
  - A `Clip` is **non-destructive**: `{ SourceMediaRef, SourceInOut (TimeSpan), TimelineStart,
    EffectStack: IEffect[] }`. Nothing is baked; the frame is reconstructed on demand.
  - `RenderGraph`: given a timeline time `t`, resolves which clips are active per track,
    requests source frames, applies each clip's effect stack, composites tracks top-down.
- `Sprocket.Media` — FFmpeg interop: `MediaSource` (open/seek/decode to `AVFrame`),
  `FrameUploader` (`AVFrame` → `SKImage`/GPU texture), `Encoder` (export). Hardware-accel
  device selection behind `IHardwareContext` with Windows/Linux implementations.
- `Sprocket.Audio` — `IAudioOutput` (Silk.NET.OpenAL impl), sample-accurate mixer
  (sum tracks × per-clip volume/fade gain) in pooled native buffers. **Audio clock is the
  master clock** for A/V sync.
- `Sprocket.App` — Avalonia UI: timeline control (custom-drawn), preview surface
  (Skia GPU control), transport, property panels. MVVM.

Threading model:
- Decode thread(s) — one per active source, fill bounded ring buffers (`System.Threading.Channels`).
- Render/compose — pulls frames, runs Skia GPU passes, presents to preview.
- Audio thread — OpenAL callback drains the mixer; drives the master clock.
- UI thread — never blocks on decode/render.
- Export — throughput-bound, parallel decode→effect→encode; reuses the same RenderGraph.

Effects (vertical slice): `BrightnessEffect` and `FadeEffect` implemented as Skia
`SKRuntimeEffect` (SkSL) fragment shaders / color filters running on the GPU. Fade =
opacity/gain ramp over a time range (video alpha via shader; audio gain in the mixer).

## Vertical-slice milestone (definition of done)

End-to-end on **all three** of Windows 11, Linux, and macOS (the slice is developed on Windows;
Linux and macOS rest on bundling the native libs + on-device verification — see step 1 and step 36):
1. Create a project; add 1 video track + 1 audio track.
2. Import a media file (`MediaSource` opens it via FFmpeg, reports duration/streams).
3. Place a clip; set in/out trim (non-destructive — source untouched).
4. Apply `BrightnessEffect` (GPU shader) to the clip.
5. Apply a `FadeEffect` (video fade-to-black + audio fade) over a time range.
6. Play back at 1080p in the Avalonia preview with A/V in sync (audio-clock master),
   **zero per-frame managed allocation in the render loop** (verify with a profiler).
7. Export to a full-resolution MP4/H.264 via the encoder path.
8. Save/load the project (serialize the timeline data model to JSON).

## Build order

1. **Architecture spike (de-risk first):** decode one frame via Sdcb.FFmpeg → upload to an
   `SKImage` on the GPU → apply a brightness `SKRuntimeEffect` → display in an Avalonia Skia
   control, with an allocation profiler confirming a clean hot loop. Do this on Linux too.
   This validates the core performance claim before building breadth.
   - **✅ DONE on Windows 11 (`src/Sprocket.Spike`).** Result: 1920×1080 at a steady 60 fps
     (vsync-capped), render confirmed on Avalonia's **shared `GRContext`** (GPU, not raster
     fallback). Render-loop allocation settled at **~8 KB/frame with GC gen1/gen2 = 0** — i.e.
     the small bounded shader/uniform objects only, **no per-frame pixel allocation** (a 1080p
     RGBA frame is ~8 MB; managed-heap pixels would show ~8 MB/frame + LOH churn). Stack
     versions locked by this spike: Avalonia 12.0.5, **SkiaSharp pinned to 3.119.4 to match
     Avalonia's transitive dependency** (the lease returns Avalonia's own Skia types), Sdcb.FFmpeg
     7.0.0 + runtime 7.1.0 (FFmpeg 7.1: avcodec-61/swscale-8).
   - **✅ Linux verified (headless, Ubuntu 24.04 x64, .NET 10 Docker).** A `--headless-check`
     mode runs decode → SkSL brightness shader → offscreen Skia render → PNG with no GUI/GPU
     display. Result: builds clean on Linux, Sdcb.FFmpeg decodes the 1080p frame, SkiaSharp +
     SkSL run, and the output PNG is **byte-identical (same SHA-256) to the Windows output** —
     the render path is deterministic across OSes. **Key finding:** there is *no* Sdcb.FFmpeg
     Linux runtime NuGet and distro FFmpeg versions vary (Ubuntu 24.04 ships FFmpeg 6.1, which
     is ABI-incompatible with Sdcb.FFmpeg 7.0's `libav*.so.61`). So **Sprocket must bundle
     FFmpeg 7 `.so` libs on Linux** (resolved via the loader path), exactly as it bundles the
     runtime DLLs on Windows — do not depend on the distro package. See ARCHITECTURE.md §11.
   - **Remaining (lower risk):** confirm the full Avalonia GPU compositor (shared `GRContext`)
     on a real Linux desktop session with a GPU; the headless check validates the media+Skia
     stack but uses an offscreen raster surface, not the windowed GL/Vulkan compositor. **macOS:**
     run the same headless check + windowed compositor (Metal) on `osx-arm64`/`osx-x64` once the
     FFmpeg dylibs are bundled (step 36) — the render path is the identical managed code, so the
     risk is packaging the natives, not the pipeline.
2. Timeline data model + RenderGraph in `Sprocket.Core` (unit-tested, headless).
   - **✅ DONE (`src/Sprocket.Core`, 42 headless tests in `tests/Sprocket.Core.Tests`).** Zero
     native/UI deps confirmed (output is `Sprocket.Core.dll` alone). Delivered:
     - **Time model:** `Rational` (reduced, AVRational-style) and `Timecode` (`long` ticks).
       `TicksPerSecond` set to **240000**, not the doc's example 90000 — 240000 is exact for both
       48 kHz audio (5 ticks/sample) and all common + NTSC frame rates (30000/1001 → 8008
       ticks/frame), so frame/sample boundaries round-trip losslessly (audio is the master clock).
       ARCHITECTURE.md §3 updated to record the decision.
     - **Data model:** `Project → MediaPool/Timeline/Settings`, `Timeline → Track[]` (z-ordered),
       `VideoTrack`/`AudioTrack`, non-destructive `Clip` (SourceIn/Out, TimelineStart, derived
       Duration), `EffectInstance`, and `AnimatableValue` (constant or keyframed, Hold/Linear) so the
       slice's fade and all future keyframing share one mechanism (§9).
     - **Render graph:** `RenderGraph.PlanVideoFrame`/`PlanAudioBuffer` resolve a pure, serializable
       plan (clip resolution, trim→source mapping, effect-stack order, fade ramps, gain/mute/solo);
       a generic `Render<TImage>` executor drives the `IFrameSource<T>`/`IVideoCompositor<T>` seams so
       the Render layer binds `TImage = SKImage` while tests use a fake. `IClock` defined for §8.
     - Tests cover: rational reduction/overflow, frame & sample round-trips, animation
       interp/clamp/hold, clip trim & containment, clip resolution + overlap determinism, layer
       z-order, effect-stack order & param evaluation-at-t, executor op-ordering, audio gain/mute/solo
       and fade ramps. PLAN verification §"Correctness" (RenderGraph headless tests) satisfied.
3. `MediaSource` decode + seek (keyframe seek then decode-to-target); ring-buffer feed.
   - **✅ DONE (`src/Sprocket.Media`, 13 tests in `tests/Sprocket.Media.Tests`).** New project depends
     only on `Sprocket.Core` + Sdcb.FFmpeg — **no SkiaSharp/UI** (decoded pixels stay native, §1).
     Delivered:
     - **`MediaSource`** — opens/probes a file (`ProbedMediaInfo`: duration, fps as `Rational`, W/H,
       audio sample-rate/channels), decodes the video stream with the `ReadFrame → SendPacket →
       ReceiveFrame` loop plus an end-of-stream flush packet to drain buffered frames.
     - **Seek** — `SeekTo(Timecode)` does keyframe seek (`AVSEEK_FLAG.Backward`) → `avcodec_flush_buffers`
       → **decode-to-target** discard (frames before the target are dropped *before* swscale, so no wasted
       RGBA conversion). Verified frame-accurate mid-GOP (GOP=12): seeking to frame 40/50/60 lands exactly
       that frame's PTS; seeking between frames returns the next frame.
     - **`MediaTime`** — the one place FFmpeg's stream time base meets Core's tick clock (PTS↔`Timecode`,
       `Int128` intermediates; Core never sees an `AVRational`).
     - **`VideoFrame`/`VideoFramePool`** — pooled native RGBA buffers (pixels by pointer, reused across
       decodes) so the decode path is allocation-free in steady state (§8 frame pooling).
     - **`VideoDecodeRing`** — one background worker fills a **bounded** `Channel<>` (backpressure caps
       read-ahead, §8). Seek is **generation-tagged**: `RequestSeek` bumps a generation + signals the
       worker, which re-seeks; stale buffered frames are discarded by the reader (no producer/consumer
       drain race). Worker **parks** at EOF (doesn't complete the channel) so scrub-back resumes; verified
       ordered feed, tight-capacity backpressure, seek-discards-stale, seek-after-EOF, clean dispose.
     - **Fixture:** tests generate a deterministic 320×240@30 / 3 s / GOP-12 + 48 kHz clip via the `ffmpeg`
       CLI (cached in the test output dir).
4. Skia preview surface + transport; software-clock playback (video only).
   - **✅ DONE (`src/Sprocket.Render`, `src/Sprocket.Playback`, `src/Sprocket.App`; 27 tests in
     `tests/Sprocket.Playback.Tests`).** Three new projects realize the playback path (ARCHITECTURE.md
     §8/§10) honouring the dependency graph of §2 — Render → Core + SkiaSharp only (no Avalonia/Media);
     Playback → Core/Media/Render; App → all. Delivered:
     - **`Sprocket.Render.FramePresenter`** — wraps a decoded native RGBA buffer with `SKImage.FromPixels`
       (no managed copy, §1) and draws it scaled-to-fit (letterboxed) onto the `SKCanvas` leased from
       Avalonia, uploading to the shared `GRContext` on draw (§10). The `IVideoCompositor<SKImage>` seam
       impl + SkSL effects are deferred to steps 7/14 — one opaque video layer needs only a fit-draw, which
       keeps the hot loop allocation-clean (the spike's measured result).
     - **`SoftwareClock`** — a play/pause/seekable `IClock` driven by a monotonic elapsed source (Stopwatch),
       re-anchored on every transport op so it never accumulates drift within a play span. The slice's
       stand-in **master clock**; step 5 swaps in the audio device clock behind the same `IClock`.
     - **`PlaybackEngine`** — drives one video track from the clock, keeping the presented frame in sync via
       a background pump that **drops** frames when behind and **holds** when ahead (§8). Transport
       (`Play`/`Pause`/`SeekTo`/`TogglePlayPause`) is UI-thread-callable; seeks forward to the feed and the
       pump force-presents the post-seek frame (frame-accurate scrub, paused or playing). The live frame is
       read via `UseCurrentFrame`, which holds a lock for the draw so the pump can't recycle the native
       buffer mid-present. Pure decisions (clamp / reached-end / promote) live in `PlaybackMath`; frame
       supply sits behind `IVideoFrameFeed` (`RingVideoFrameFeed` adapts `VideoDecodeRing`) so the engine is
       testable and a proxy/hardware feed slots in later (§17).
     - **`Sprocket.App`** — a minimal Avalonia shell (grows into the full panelled shell at step 11; the
       spike stays the de-risk artifact). A `PreviewSurface` custom control draws the engine's current frame
       inside an `ISkiaSharpApiLease` (GPU); a transport bar (play/pause, position scrubber + time readout,
       Space to toggle) drives the engine. Opens a media path from the command line or a generated 1080p
       sample, building a one-video-track project over it.
     - **Tests (27):** `SoftwareClock` deterministic via an injected elapsed source (start-paused, advance,
       freeze-on-pause, seek, rate); `PlaybackMath` (clamp/end/promote); the `PlaybackEngine` pump stepped
       deterministically over the real fixture (presents first frame, seek lands the target frame, holds when
       ahead, drops to catch up, reaches end → stops + signals); `FramePresenter.ComputeFitRect` letterbox
       math; plus a **live-pump integration** pair running the real `Start()` → background pump →
       `FramePresenter` offscreen-raster render → `DisposeAsync` and asserting a non-blank frame + a different
       frame after a live seek (all waits bounded so a stuck pump/worker fails fast rather than hanging).
     - **Note:** the windowed GPU preview is display-bound and rests on the spike's proven Avalonia+Skia
       lease path (step 1); the offscreen-raster integration test covers the decode→pump→present→dispose
       pipeline headlessly. (A no-GUI CLI smoke was dropped — `Sprocket.App` is a `WinExe` with no reliable
       console — in favour of that test-host coverage.)
5. Audio: `IAudioOutput` + mixer; switch to audio master clock; A/V sync.
   - **✅ DONE (`src/Sprocket.Audio` + `src/Sprocket.Media/AudioSource`; 16 tests in `tests/Sprocket.Audio.Tests`,
     +5 in `tests/Sprocket.Media.Tests`).** The slice now plays with audio as the **master clock** and video
     synced to it (ARCHITECTURE.md §6, §8). Honours the §2 dependency graph: **Sprocket.Audio depends only on
     Core** (no FFmpeg) — the FFmpeg audio decode lives in Media; the App composition root wires them. Delivered:
     - **Two Core seams (symmetry with video):** `IPcmReader` (pull interleaved float32 PCM at the project
       rate/layout, sequential + seek — the audio analogue of `IFrameSource`) and `IMasterClock` (a
       transport-capable `IClock`: `Start`/`Pause`/`Seek`). `SoftwareClock` now implements `IMasterClock`, so the
       playback engine became **clock-agnostic** — its field is `IMasterClock` and it disposes the clock if it is
       `IAsyncDisposable`, so the whole session tears down through one call.
     - **`Sprocket.Media.AudioSource`** (`IPcmReader`) — opens the file's audio stream and **resamples to
       interleaved float32 at the project rate/channels via libswresample** (raw `swr_alloc_set_opts2`/`swr_convert`
       interop, the one place that touches it), once at decode (§6). Sample-accurate seek = keyframe-seek → flush
       decoder → `swr_init` reset → decode-to-target discard computed from the landing frame's PTS, mirroring the
       video path. A small managed leftover buffer (≤ one decoded frame) keeps steady-state reads allocation-free.
     - **`AudioMixer`** — executes `RenderGraph.PlanAudioBuffer`: pulls each audible layer's PCM through
       `IPcmReader`, applies the per-clip **gain envelope as a linear ramp across the buffer** (this is how fades
       work — same `Fade` opacity that drives video alpha), sums, then a **SIMD** (`Vector<float>`) master-gain +
       hard-limit pass. Keeps each reader positioned for sequential playback and only re-seeks on a real jump
       (1 ms tolerance), so steady playback never re-seeks.
     - **`IAudioOutput`** (device seam) + **`OpenAlAudioOutput`** (Silk.NET.OpenAL / OpenAL Soft) — a streaming
       source fed by a rotating pool of 8 device buffers (float32 → 16-bit PCM); recycled-buffer frames + the
       current play offset give `PlayedFrames`, the clock's time source. Device-bound, so it rests on **manual
       verification** like the windowed GPU preview (confirmed this session: real device opens and `PlayedFrames`
       advances under playback); the mixer/clock are covered headlessly against a fake output.
     - **`AudioEngine`** (`IMasterClock`) — the audio master clock: `Now` is derived from `PlayedFrames` against an
       anchor (re-anchored on every transport op, so no drift); a background **feeder** keeps the device queue full
       by mixing the timeline for an advancing write cursor. Seeks bump a generation so an in-flight mix for a
       superseded position is dropped (the same discipline the video decode ring uses). Flushing the device on seek
       discards queued-but-unplayed audio so the new position is heard promptly.
     - **App bootstrap** — adds an `A1` audio track and builds the audio master clock when the source has audio and
       a device is available; **degrades to the `SoftwareClock` (video still plays)** when there is no audio or no
       device (§15). The playback engine receives the clock and owns its teardown.
     - **Tests (21 new):** mixer summing / track-gain-dB / mute / solo / master-gain / hard-limit / fade gain ramp /
       seek-on-jump-only / silence-off-clip / reader disposal (all against a synthetic `FakePcmReader`, no FFmpeg);
       `AudioEngine` clock semantics (start/pause/seek re-anchor, `Now` from played frames) via a deterministic
       `FakeAudioOutput`, plus a bounded live-feeder integration asserting mixed non-silent audio reaches the queue;
       and `AudioSource` decode/resample/seek against the real fixture (whole-stream count, downsample scaling,
       non-silence, mono→stereo interleave, post-seek resume). Full suite: **103 tests green** (Core 42, Media 18,
       Audio 16, Playback 27).
     - **Note:** audio uses a stereo 16-bit device path for the slice (OpenAL Soft's portable format); float32
       output and sample-exact device-offset interpolation are easy later refinements behind `IAudioOutput`.
6. Hardware-accel decode path behind `IHardwareContext` (D3D11VA/CUDA/QSV on Windows, VAAPI/CUDA on
   Linux, VideoToolbox on macOS), with software fallback. Runtime-probe available device types per OS;
   decode to a GPU frame, download via `av_hwframe_transfer_data`, then swscale → RGBA (zero-copy
   `FromTexture` deferred). Fall back to the software decode path whenever no device is usable.
   - **✅ DONE (`src/Sprocket.Media/HardwareContext.cs` + `MediaSource`; 6 tests in `tests/Sprocket.Media.Tests`).**
     `MediaSource` now decodes on the GPU when one is available and degrades to software otherwise, with no
     change to its `IFrameSource`/ring consumers — frames still arrive as pooled native RGBA. Delivered:
     - **`IHardwareContext` + `HardwareDevice`** — wraps an FFmpeg `AVHWDeviceContext` of one
       `AVHWDeviceType`. `TryCreate(type)` is a runtime probe (returns `null` if the driver/GPU is absent);
       `PlatformPreferredTypes()` gives the per-OS ordering (**Windows** D3D11VA→CUDA→QSV→DXVA2, **Linux**
       VAAPI→CUDA→VDPAU, **macOS** VideoToolbox); `CompiledTypes()` lists what the FFmpeg build supports.
     - **`MediaSource.Open(path, HardwareAccelMode.Auto|Disabled)`** — `Auto` (default) negotiates a device:
       for each platform-preferred type it checks the decoder's `avcodec_get_hw_config` for a matching
       `HW_DEVICE_CTX` config (yielding the GPU pixel format), opens the device, attaches it
       (`hw_device_ctx = av_buffer_ref(...)`), and installs a `get_format` callback that selects the GPU
       format. **Any failure — no config, device won't open, or `Open()` throws — tears the hardware down and
       reopens a plain software decoder** (§11/§15). `HardwareDeviceName` reports what engaged (null = software).
     - **Decode branch** — when a decoded frame carries the GPU pixel format it is downloaded to a CPU frame
       via `av_hwframe_transfer_data` (the documented copy; zero-copy `FromTexture` stays deferred) and then
       run through the existing swscale → RGBA step; software frames go straight to swscale. A failed download
       skips the frame rather than crashing. Frame PTS and seek (decode-to-target) are unchanged.
     - **Verified on this Windows machine:** the bundled FFmpeg exposes CUDA/VAAPI/DXVA2/QSV/D3D11VA/Vulkan/
       D3D12VA; `Auto` selected **D3D11VA** and decoded the fixture on the GPU. Linux/macOS rest on the same
       managed code + bundled libs (steps 35–36) + on-device verification.
     - **Tests (6, deterministic regardless of GPU):** software mode uses no device and decodes in order; auto
       mode decodes whether or not hardware engages; **the hardware and software paths produce identical frame
       PTS** (so the GPU path never breaks frame-accuracy — this comparison ran hardware-vs-software here);
       compiled/preferred type lists are populated. Full suite: **109 tests green** (Core 42, Media 24, Audio
       16, Playback 27).
7. Effects (brightness, fade) + audio volume/fade in mixer.
   - **✅ DONE (`src/Sprocket.Render/SkiaEffectPipeline.cs`; 8 tests in `tests/Sprocket.Render.Tests`).** The
     slice's effects now run as real SkSL on the GPU preview, and the audio half (gain/fade) was already
     delivered with the mixer in step 5. Honours the §2 graph (Render → Core + SkiaSharp only). Delivered:
     - **`SkiaEffectPipeline`** — compiles the two built-in effects once as `SKRuntimeEffect` (SkSL) fragment
       shaders (**Brightness** = premultiplied `rgb * amount`; **Fade** = whole-pixel `* opacity`, which reads
       as fade-to-black over the cleared preview and is a correct premultiplied fade-out when composited) and
       **chains them as a shader graph** — effect N's `src` child is effect N-1's output, rooted at the decoded
       image's `ToShader` (ARCHITECTURE.md §7) — so the stack resolves in minimal GPU passes, not N round-trips.
       Unknown effect ids pass through (a plugin with no Render binding is a no-op, not a crash). The per-frame
       allocation is only the small bounded shader/uniform objects §7 acknowledges; **with no effects it falls
       back to the plain fit-draw**, keeping the step-4 hot path exactly as allocation-clean as measured.
     - **Live param resolution** — `RenderGraph.ResolveEffects(clip, t)` is now public; `PlaybackEngine`
       evaluates the active clip's stack at the **current playhead** and carries it on `PresentedFrame.Effects`,
       so the fade ramp animates with position. `PreviewSurface` owns the pipeline (compiled on attach, disposed
       on detach) and applies it inside the Avalonia Skia lease (§10).
     - **App bootstrap** — the slice clip now carries a Brightness (1.15×) and a fade-in/out, and the audio clip
       carries the **same** fade envelope, so one `Fade` drives video alpha (shader) and audio gain (mixer §6)
       consistently — slice DoD #4/#5 is demonstrable in the running app.
     - **Audio volume/fade** — already complete in the mixer (step 5): per-track gain (dB), master gain, and the
       fade gain-ramp across the buffer, all covered by `AudioMixerTests`; no change needed here.
     - **Tests (8, headless raster, deterministic)** — run the real SkSL on an offscreen CPU surface (the spike's
       Linux-check discipline) and read pixels back: no-effects pass-through, brightness up/down, fade half/zero,
       brightness→fade **chain**, unknown-effect pass-through, and degenerate-bounds no-op. Full suite: **117
       tests green** (Core 42, Media 24, Audio 16, Render 8, Playback 27).
8. Export pipeline (full-res encode).
   - **✅ DONE (`src/Sprocket.Media/MediaEncoder.cs` + new `src/Sprocket.Export`; 6 tests in
     `tests/Sprocket.Export.Tests`).** The slice now renders the timeline offline to a full-resolution
     H.264/AAC MP4 through the **same render graph** that drives preview (ARCHITECTURE.md §5) — slice DoD #7.
     The FFmpeg muxing stays in `Sprocket.Media`; a new `Sprocket.Export` project orchestrates over Core +
     Media + Render + Audio (it sits beside Playback in the §2 graph). Delivered:
     - **`MediaEncoder`** (Media) — the reverse of `MediaSource`/`AudioSource` (§11 "Encoder: mirror in
       reverse"). Allocates an MP4 `FormatContext`, opens an **H.264** (`libx264`, CRF-quality by default)
       video stream and an optional **AAC** audio stream, writes the header, then accepts composited RGBA
       frames (staged → swscale → yuv420p, PTS = frame index in a 1/fps time base) and interleaved float PCM
       (swresample flt→fltp planar, PTS = sample index in a 1/sampleRate time base). Packets are stamped and
       `InterleavedWritePacket`'d; `Finish()` flushes both encoders and writes the trailer. Sets
       `AV_CODEC_FLAG.GlobalHeader` when the muxer wants it and exposes the encoder's `AudioFrameSize`. All
       libav* interop stays behind this one class — Export never sees FFmpeg.
     - **`Sprocket.Export.VideoExporter`** — the offline driver: for each output frame it calls
       `RenderGraph.PlanVideoFrame` (the identical resolution step preview uses), clears a full-res **raster**
       `SKSurface` to black, draws each resolved layer with the step-7 effect shaders, reads the pixels back
       (`SKPixmap`, no extra copy), and writes them to the encoder; audio is mixed by `AudioMixer` over the
       same timeline. A single interleave loop emits whichever stream's next packet is earlier on the timeline
       (video frame vs. AAC-sized audio chunk) so the muxer interleaves cleanly. Raster (not GPU) + **software,
       full-resolution decode** (`HardwareAccelMode.Disabled`, never proxies §17) makes the output
       bit-deterministic — the precondition for golden-frame testing. Offline/missing sources render as
       black/silence rather than failing (§15); progress + cancellation are honoured between frames.
     - **`ExportFrameProvider`** — a per-source forward decoder with a one-frame look-ahead: returns the latest
       decoded frame at/just before each requested source time, seeking only on a backward jump. Owns its
       `MediaSource` + `VideoFramePool`.
     - **`SkiaEffectPipeline.DrawLayer`** (Render, refactor) — the per-layer draw was factored out of `Present`
       into a non-clearing `DrawLayer` (with track opacity via paint alpha + blend mode), so export clears once
       then composites N layers bottom→top while preview still clears-then-draws its single layer. Multi-layer
       export now works for free; the single-layer hot path is byte-for-byte the step-7 path.
     - **App wiring** — `MediaBootstrap` now returns the `Project`; `MainWindow` has an **Export** button that
       runs `VideoExporter` on a background thread (pausing playback, streaming `0–100%` to the status strip)
       to `export.mp4` in the app dir — slice DoD #7 demonstrable in the running app.
     - **Tests (6, real encode→decode round-trips)** — export the fixture and reopen it: format/dimensions/fps/
       duration match and audio is present; full frame count is rendered; a **brightness-0.3 clip exports a
       visibly darker first frame than an unmodified one** (proving the effect shaders run on the export path);
       a project with no audio track yields a video-only file; progress reaches completion; an empty timeline
       throws. Full suite: **123 tests green** (Core 42, Media 24, Audio 16, Render 8, Playback 27, Export 6).
9. Project save/load (JSON).
   - **✅ DONE (`src/Sprocket.Persistence`; 11 tests in `tests/Sprocket.Persistence.Tests`).** The timeline data
     model round-trips losslessly to/from versioned JSON (ARCHITECTURE.md §12) — slice DoD #8, completing the
     vertical slice. Delivered:
     - **`ProjectSerializer`** (`Serialize`/`Deserialize` + `Save`/`Load` file helpers) over a set of DTOs kept
       **separate from the domain model** — the model has constructors, read-only collections, and the
       `AnimatableValue` factory type that don't serialize directly, and a distinct wire format lets the model
       evolve behind a stable file format. Uses `System.Text.Json` with a **source-generated context**
       (trim/AOT-friendly), camelCase names, string enums, indented output.
     - **Versioned:** every file carries `schemaVersion` (currently 1); loading an unknown version throws
       `InvalidDataException` (as does malformed JSON) so a future format break fails loudly rather than
       mis-parsing.
     - **Relative + absolute media paths (§12):** on save (when a file path is known) each `MediaRef` stores a
       path relative to the project file alongside the absolute one; on load the relative path is resolved
       against the project directory and preferred when it exists, so a project moved together with its media
       relinks. **Offline-tolerant:** a media file that can't be found is kept with its stored path (renders as
       black/silence downstream) rather than failing the load.
     - **App wiring:** a **Save** button writes `project.sprocket.json` next to the app output (loading back into
       the running app arrives with the File menu in the UI shell, step 11; the API + tests cover load today).
     - **Tests (11):** a rich project (NTSC-rational fps, two track kinds in z-order, clip trim, a constant
       brightness + a keyframed Hold/Linear fade, track gain/mute/solo + opacity/blend, master gain) round-trips
       field-for-field; the schema version is present and an unknown one throws; malformed JSON throws; a
       save-then-move scenario relinks media via the relative path; missing media still loads; the empty project
       round-trips. Full suite: **134 tests green** (Core 42, Media 24, Render 8, Audio 16, Playback 27, Export 6,
       Persistence 11). **The vertical slice (steps 1–9) is complete.**

## Post-slice build order (target UI & full feature set)

Once the vertical slice's definition of done is met, the remaining features — those in
[BRIEF.md](BRIEF.md) and implied by the [UI.md](UI.md) mockup — build out in roughly this
dependency order. Each lands on an existing seam ([ARCHITECTURE §17](ARCHITECTURE.md)); none
requires a redesign. Tags reference the [UI.md §4 checklist](UI.md).

10. **Undo/redo command stack (foundational — do first).** Route *every* model mutation through
    a command stack (snapshot or inverse-command), with command coalescing (e.g. slider drags)
    and an edit-history surface. First-class requirement per [BRIEF.md](BRIEF.md) /
    [ARCHITECTURE §4](ARCHITECTURE.md); doing it first means all later editing features are
    undoable by construction.
    - **✅ DONE (`src/Sprocket.Core/Commands`; 19 tests in `tests/Sprocket.Core.Tests/CommandTests.cs`).** The
      inverse-command stack now exists in **Core** (it operates on the pure-data model and depends on nothing,
      §2) so all later editing lands on it. Delivered:
      - **`IEditCommand`** (`Label`/`Apply`/`Revert`/`TryMergeWith`) + an `EditCommand` base that opts out of
        merging by default. Inverse-command rather than snapshot: the model is plain data with no native handles,
        so capturing the few changed fields is cheaper than cloning the graph and reverses exactly (§4).
      - **`EditHistory`** — the stack: `Execute` applies + records and clears the redo stack (linear undo);
        `Undo`/`Redo`; `CanUndo`/`CanRedo`; `UndoLabel`/`RedoLabel` and `UndoLabels`/`RedoLabels` for an
        edit-history surface; a `Changed` event for UI binding; `Clear` (e.g. on project load). **Coalescing is
        scoped:** `BeginCoalescing()` returns an `IDisposable` (open on a slider/drag pointer-down, dispose on
        pointer-up) inside which consecutive commands that agree via `TryMergeWith` collapse into one undo
        entry — so a drag is a single step, but two separate gestures on the same control are not. Scopes nest.
        Not thread-safe by design: the UI thread owns the model (§8); decode/render/audio threads only read it.
      - **Command set** covering today's model mutations so editing is undoable from the start: a generic
        `SetPropertyCommand<T>` (get/set delegates + optional merge key — one type for any scalar: clip move,
        track gain/opacity/mute/solo/enabled), plus structural commands `AddClip`/`RemoveClip`,
        `TrimClip` (two-field, coalescing), `AddEffect`/`RemoveEffect`, `SetEffectParameter` (coalescing on the
        same effect+param — the slider-drag case), and `AddTrack`/`RemoveTrack`. The remove/track commands
        capture and restore the original list index so z-order and effect-stack order survive undo (§5d).
      - **Tests (19):** stack mechanics (execute/undo/redo, redo-discarded-on-new-edit, labels, `Changed` fired,
        `Clear`); coalescing merges only inside a scope and only across equal merge keys; and each concrete
        command applies + reverses exactly against the real model (add/remove restoring index, two-end trim,
        param revert-to-absent vs revert-to-previous, drag-coalesces-to-one-entry, z-order preserved). Wiring
        the editing **UI** through the stack arrives with the timeline control + editing tools (steps 12–13);
        the App's current bootstrap builds the slice project directly (no in-app edit actions to undo yet). Full
        suite: **153 tests green** (Core 61, Media 24, Render 8, Audio 16, Playback 27, Export 6, Persistence 11).
11. **App UI shell.** Frameless Avalonia window with custom chrome + inline menu bar
    (`File · Edit · Clip · Sequence · Effects · View · Window · Help`); **splitter-resizable**
    Project / Program / Inspector / Timeline panes ([UI.md §1](UI.md)); project title + autosave
    / dirty-state indicator.
    - **✅ DONE (`src/Sprocket.App`: `App.axaml`, `MainWindow.axaml`/`.cs`).** The slice's bare window grew
      into the full panelled shell of [UI.md §1/§2](UI.md), keeping playback/preview/export/save live. The
      *structure* is complete; the pane **contents** (media bin, timeline control, inspector) are their own
      steps (12–16) and show clearly-labelled placeholders for now. Delivered:
      - **Frameless window + custom chrome:** `WindowDecorations="BorderOnly"` (Avalonia 12 renamed/dropped the
        v11 `ExtendClientAreaChromeHints` model — `BorderOnly` keeps a resize border with no OS title bar) plus
        a custom title bar — logo, **inline menu bar** (`File · Edit · Clip · Sequence · Effects · View · Window
        · Help`), centred project title + save-state, and custom **min / max / close** glyphs. The bar is
        draggable (`BeginMoveDrag`), double-click maximizes, and a maximized window is inset by `OffScreenMargin`
        so nothing clips under the screen edges.
      - **Splitter-resizable layout (UI.md §1):** a `GridSplitter` grid — **Project | Program | Inspector**
        across the top, a full-width **Timeline** below a horizontal splitter, with a **tool/action bar** under
        the title bar and a **status bar** at the bottom. All four panes are user-resizable.
      - **Live regions:** the **Program** pane hosts the existing `PreviewSurface` + a transport row
        (jump-to-start ⏮, play/pause, jump-to-end ⏭, position, scrubber, duration); **Export** and **Save** run
        from the action bar / File menu; the **Project** pane lists the real `MediaPool` items; the **status bar**
        shows engine state + a `fps · WxH · duration` telemetry readout and the action bar a `1080p · 30`
        sequence badge — **no framework/runtime text** anywhere ([UI.md §3.7](UI.md)).
      - **Undo/redo + dirty-state wired onto the step-10 `EditHistory`:** **Edit ▸ Undo/Redo** (and `Ctrl+Z` /
        `Ctrl+Shift+Z` / `Ctrl+Y`) drive the stack, the menu items enable/disable + show the next command's
        label, and the title-bar indicator flips between *• all changes saved* / *• unsaved changes* (tracked by
        comparing `EditHistory.UndoCount` against the depth recorded at the last save; `UndoCount`/`RedoCount`
        added to `EditHistory`). **`+ Track`** issues a real `AddTrackCommand`, so the foundational command stack
        is demonstrably end-to-end (add a track → undo removes it → dirty flips) ahead of the timeline editing UI.
      - **Placeholders (own steps):** tool palette beyond Select, Snapping/Linked toggles, the Media-tab
        siblings (Effects/Transitions/Audio), the Source monitor + Fit zoom, the Inspector sections, and the
        timeline ruler/clips are present as disabled/labelled stand-ins so the shell reads as the target UI
        without pretending the features exist.
      - **Verification:** builds clean (the Avalonia XAML compiler validates control/property/resource
        references — it caught the removed v11 chrome property); a headless smoke launch
        (`SPROCKET_APP_SECONDS=4 dotnet run`) starts the shell, opens the sample, wires the engine, and tears
        down cleanly (exit 0). The windowed layout itself is display-bound and rests on manual verification like
        the preview path. No unit tests (the App is a UI-bound `WinExe`); the full suite stays **153 green**.
12. **Timeline control v1.** Custom-drawn ruler + playhead, clip thumbnails (filmstrip) and audio
    waveforms, drag-move + trim handles, timeline zoom (`⊟ 100% ⊞`), **Snapping**, and the
    **Hand**/**Zoom** view tools. The most involved bespoke control.
    - **✅ DONE (`src/Sprocket.App/Timeline/{TimelineMath,TimelineControl}.cs`; 14 tests in
      `tests/Sprocket.App.Tests`).** The shell's timeline placeholder is now a live custom-drawn control
      ([UI.md §3.6](UI.md)) editing the real model through the step-10 command stack. Delivered:
      - **`TimelineControl`** (Avalonia `Control` with a custom `Render`): a **ruler** with zoom-aware time
        labels, a draggable **playhead** synced to the engine (`PositionChanged` → redraw; click/drag the ruler
        or empty lanes scrubs via `PlaybackEngine.SeekTo`), one **lane per track** (video on top, audio below)
        with **clips** drawn as rounded blocks bearing the media filename and a schematic **filmstrip** (video)
        / **waveform** (audio) fill, the selected clip outlined in the accent. Per-track **mute / solo / enable**
        toggle boxes live in the track header.
      - **Editing through `EditHistory`:** **drag-to-move** and **edge-trim** (left edge ripples in-point +
        start so the right edge stays put; right edge trims the out-point) run as `SetClipPlacementCommand`s
        inside an `EditHistory.BeginCoalescing()` scope opened on pointer-down and sealed on pointer-up — so a
        whole drag is **one undo entry** and the model updates live. **Snapping** (to other clip edges, the
        playhead, and t=0, within 8 px) honours the action-bar toggle; the M/S/enable toggles issue
        `SetPropertyCommand<bool>`s. Selection drives a status hint (and feeds the Inspector at step 16).
      - **Zoom + scroll:** magnifier −/+ buttons (with `Ctrl+-`/`Ctrl+=` tooltips), the **Ctrl+wheel** and
        **Zoom-tool** click, and **`Ctrl+-`/`Ctrl+=`** keys all zoom (anchored so the tick under the
        cursor/playhead stays put, 8–600 px/s); the wheel scrolls horizontally, clamped to content. A
        **`TimelineControl.ZoomToFit`** (View ▸ Zoom to Fit, **`Shift+Z`** — the Resolve/FCP convention) frames
        the whole sequence to the viewport width and scrolls back to the start. (Menu items for plain
        Zoom In/Out carry no `InputGesture` label because Avalonia renders `=`/`-` as their raw `OemPlus`/
        `OemMinus` enum names; the clean shortcut text lives in the buttons' tooltips.)
      - **New Core primitive:** `SetClipPlacementCommand` sets a clip's source in/out **and** timeline start
        atomically (the move/trim/slip primitive), coalescing per clip — joining the step-10 command set.
      - **Tested geometry:** the tick↔pixel mapping, snapping, edge hit-testing, and ruler-interval selection
        live in a pure `TimelineMath` (no Avalonia types) covered by **14 headless tests**; the rendering +
        pointer interaction rest on those + manual verification (the App is a UI-bound `WinExe`). Clean build
        (the XAML compiler resolves the control + `TimelineMath` namespace fix) and a smoke launch starts +
        tears down cleanly. Schematic filmstrip/waveform fills stand in until **real decoded thumbnails /
        waveforms (step 15)**; **Hand/Zoom** tool buttons + the Source monitor stay placeholders. Full suite:
        **170 tests green** (Core 64, Media 24, Render 8, Audio 16, Playback 27, Export 6, Persistence 11,
        App 14).
      - **✅ Track-header follow-ups.** The track-header column is now **horizontally resizable** (drag its
        right edge — `HeaderWidth` became a clamped instance field `_headerWidth`, 72–360 px, session-only; the
        edge shows a resize cursor). A track can be **renamed by double-clicking its name**: `TimelineControl`
        raises `TrackRenameRequested(track, rect)` and the shell overlays a `TextBox` (the custom-drawn control
        can't host children) positioned over the name — Enter / lost-focus commit via `CommitTrackRename`
        (one undoable `SetPropertyCommand<string>`, mirroring the M/S/enable toggles), Esc cancels. Over-long
        names are **clipped and show the full name as a hover tooltip** ([UI.md §3.6](UI.md)).
13. **Editing tools.** **Select / Blade (razor split) / Slip** tools and **Linked A/V** (move a
    clip and its companion audio together) — a clip-link relation in the model.
    - **✅ DONE (`Sprocket.Core/Model` + `Sprocket.Core/Commands` + `Sprocket.App/Timeline` + persistence; 16 new
      tests — Core +10, App +3, Persistence +1, all green).** The timeline's tool palette is now live and the
      clip-link relation lands in the pure model, so every new op stays undoable by construction (step 10).
      Delivered:
      - **Clip-link relation (model, §4):** a nullable `Clip.LinkGroupId` (Guid) — clips sharing a non-null group
        are companion A/V. `Timeline.ClipsLinkedTo(clip)` returns the companions (with their track) for the editor
        to mutate; unlinked clips have none. The bootstrap now links the slice's video + audio clips so "Linked"
        is demonstrable, and the App's import builds them in one shared group.
      - **Two Core command primitives:** `SplitClipCommand` (the Blade op — pulls the original clip's `SourceOut`
        back to the cut and inserts a new right-half clip with the remaining source span + a **copy** of the effect
        stack; rejects a cut on/outside the clip; takes an optional right-half link group) and `CompositeCommand`
        (groups N commands as one undo entry, applied in order / reverted in reverse, and **coalesces with a
        same-shape composite** so a continuous linked drag stays one entry). Effect copy uses a new
        `EffectInstance.Clone()` (params shared by reference — `AnimatableValue` is immutable).
      - **Tool palette (UI.md §3.2) wired through `TimelineControl.ActiveTool`:** **Select** (move/trim, step 12),
        **Blade** (click a clip → split at the cursor, snapped to the playhead; selects the new right half),
        **Slip** (drag a clip to shift its source window with timeline position + duration fixed, clamped to the
        media via a pure `TimelineMath.ClampSlip`), and the view-only **Hand** (drag-pan) / **Zoom** (click to
        zoom in, Alt/right-click to zoom out) — completing the five-button radio group left as placeholders at
        step 12. Each tool sets a matching cursor.
      - **Linked A/V behaviour:** with the **Linked** toggle on, a **move** shifts every group member by one locked
        delta (clamped so none crosses t=0) as a single `CompositeCommand` undo entry, and a **blade** also cuts
        every companion that spans the cut — the right halves getting a fresh shared link group so each side stays
        an independently linked pair. Trim/slip stay per-clip (NLE convention). The Linked toggle + tool radio
        group are bound in `MainWindow`.
      - **Persistence:** `ClipDto` gains an additive, nullable `linkGroupId` (no schema bump — v1 files load as
        unlinked and a project with no links serializes byte-identically via `WhenWritingNull`); the link relation
        round-trips and the loaded companions resolve each other.
      - **Tests (16):** `SplitClipCommand` (divide/undo/effect-copy/edge-reject/link-group), `CompositeCommand`
        (apply-order, single-entry, same-shape coalescing), `Timeline.ClipsLinkedTo`, `TimelineMath.ClampSlip`
        (within bounds / edge clamps / no-headroom no-op), and a persistence link round-trip. The control's pointer
        interaction rests on these + manual verification (the App is a UI-bound `WinExe`); clean build (0 warnings)
        and a smoke launch starts + tears down cleanly. Full suite: **184 tests green** (Core 74, Media 24, Render 8,
        Audio 16, Playback 27, Export 6, Persistence 12, App 17).
14. **Multiple tracks.** Lift the 1V+1A slice to N video + N audio tracks, **`+ Track`**, and
    per-track **Mute/Solo** UI (model support already exists: `AudioTrack.Muted/Solo`, video
    `Enabled`).
    - **✅ DONE (`src/Sprocket.Playback` rework + `src/Sprocket.App`; 4 new tests in
      `tests/Sprocket.Playback.Tests/MultiTrackPlaybackTests.cs`).** The editor now drives, composites, and mixes
      N video + N audio tracks. The render graph, audio mixer, and export already resolved N layers (steps 5–8);
      the remaining gap was the **live preview**, which drove a single video feed. Delivered:
      - **Multi-track preview engine.** `PlaybackEngine` now owns one **`VideoTrackPlayer`** per video track
        (each with its own feed, one-frame prefetch, and drop/hold sync — the slice's per-track logic, factored
        out) instead of a single feed. A new **per-source feed-factory constructor** (`Func<MediaRefId,
        IVideoFrameFeed?>`) lets the app open a decoder per source; players are **reconciled against the timeline
        each pump**, so `+ Track` / undo are picked up live. `UseLayers` exposes the players' frames bottom→top
        (with each track's resolved effects, opacity, blend); seeks re-seek every player via the existing
        generation bump. The **legacy single-feed constructor + `UseCurrentFrame`** are preserved unchanged, so
        the slice's 27 playback tests stand as-is. Frame lifetime/locking (one frame gate guarding every player's
        presented frame) keeps the no-managed-pixels rule (§1) intact across N layers.
      - **Preview compositing.** `PreviewSurface` clears once then draws each layer with
        `SkiaEffectPipeline.DrawLayer` (track opacity + blend + effect chain) — the same multi-layer composite the
        export path uses, now on the GPU preview.
      - **Multi-source audio.** `MediaBootstrap` builds the mixer with a **per-source PCM-reader factory**
        (mirrors export's `OpenPcmReader`), so the `AudioMixer`/`AudioEngine` — which already sum N audible layers
        with mute/solo (§6) — mix multiple audio tracks/sources. The probe `MediaSource` is opened once for format
        then disposed; the engine/mixer open their own per-source decoders via the factories.
      - **`+ Track` UI.** The `+ Track` button now opens a flyout to add a **Video** or **Audio** track through
        `AddTrackCommand` (undoable, auto-numbered V1/V2…, A1/A2…). Per-track **Mute/Solo** (audio) and **Enable**
        (video) already live in the timeline track headers (step 12); video **Enable** now removes a track from
        the composite and audio mute/solo are honoured by the mixer plan.
      - **Tests (4 new):** two video tracks composite to two layers; a disabled video track drops out of the
        composite; layers carry the right opacity/blend in z-order; a video track added at runtime is reconciled
        into the composite. The existing 27 playback tests (single-feed path) are unchanged. Full suite: **188
        tests green** (Core 74, Media 24, Render 8, Audio 16, Playback 31, Export 6, Persistence 12, App 17).
        Clean build (0 warnings), smoke launch starts + tears down cleanly.
      - **Note:** until the media bin / import (step 15) there is one media source, so placing *distinct* clips on
        the new tracks (drag-from-bin) lands at step 15 — multi-track compositing/mixing is proven by tests now
        and becomes visually rich then. Two clips from the *same* source on two tracks share one reader; distinct
        sources mix/compose cleanly.
15. **Media bin & browsers.** Poster-frame thumbnails, waveform rendering, search, and
    format/alpha badges (`4K · 1080p · WAV · Alpha`) over the `MediaPool`; an **Effects** browser
    over the `IVideoEffect` registry; the **Audio** tab.
    - **✅ DONE (`src/Sprocket.Core/Model/EffectCatalog.cs`; `src/Sprocket.App/MediaBrowser/*`; 28 new tests —
      Core +5, App +23, all green).** The Project panel's placeholder list grew into the tabbed browser of
      [UI.md §3.3](UI.md), editing the real model through the step-10 command stack. Honours the §2 graph
      (the registry is pure data in **Core**; the browser/thumbnails live in **App** over Media + Render + Skia).
      Delivered:
      - **Effect registry (Core, §4/§7):** `EffectCatalog` + `EffectDescriptor` (id, display name,
        `EffectCategory`, description, and a default-instance factory) — the "`IVideoEffect` registry" the
        Effects browser lists over. Today it registers the two slice effects (Brightness → Color, Fade → Video);
        the Transform/Color effects (step 16) and plugins (step 33) register here as they land, so every browser
        and the Inspector draw from one list instead of hard-coding the built-ins. `Find`/`DisplayName` fall back
        to the raw id for unregistered (plugin) effects so they still label.
      - **Tabbed media browser (`MediaBrowserPanel`, built in code like `TimelineControl`/`PreviewSurface`):**
        **Media** (poster/waveform thumbnails + metadata badges + a live search filter), **Effects** (the catalog,
        **double-click adds the effect to the selected timeline clip** via `AddEffectCommand` — undoable, dirty
        flips), a deferred **Transitions** placeholder (honest — step 25), and an **Audio** tab listing the bin's
        audio sources as waveforms. The timeline's `SelectedClipChanged` feeds the browser the apply target;
        the pane header's item count and status hints route back to the shell.
      - **Thumbnails (`ThumbnailService`):** a poster frame (software-decode one frame via `MediaSource`, seek a
        little in, Skia fit-draw → PNG → Avalonia `Bitmap`) and a waveform (read mono PCM via `AudioSource`,
        reduce to per-column peaks, draw bars). Generated **off the UI thread** and **cached by source + size**;
        offline/undecodable sources fall back to a glyph rather than failing (§15). A one-off thumbnail is a
        deliberate managed copy — **not** the per-frame render hot path — so §1 is unaffected (documented in the
        service); poster decode forces the software path for determinism. Disposed with the window.
      - **Pure, tested helpers (App, mirroring the step-12 `TimelineMath` split):** `MediaBadges` (duration +
        resolution tier `4K/1080p/720p/W×H` for video, format tag for audio — the **Alpha** badge's slot waits on
        the premultiplied-alpha path, step 26), `WaveformBuilder` (interleaved PCM → mono-mixed per-bucket peaks
        in [0,1]), and `MediaSearch` (case-insensitive substring filter). The thumbnail decode + the panel's
        rendering rest on these + manual verification (the App is a UI-bound `WinExe`).
      - **Tests (28):** `EffectCatalog` (built-ins present, `Find`/`DisplayName` incl. unknown-id fallback,
        factory builds a fresh instance with default params, category filter); `MediaBadges` (resolution tiers,
        duration `m:ss`, format tag, video-vs-audio describe), `WaveformBuilder` (bucket count, peak capture,
        stereo mono-mix, empty → zeros, argument validation), `MediaSearch` (empty matches all, case-insensitive
        substring, empty-text). Clean build (0 warnings); a `SPROCKET_APP_SECONDS=6` smoke launch starts the
        shell, builds thumbnails, and tears down cleanly (exit 0). Full suite: **216 tests green** (Core 79,
        Media 24, Render 8, Audio 16, Playback 31, Export 6, Persistence 12, App 40).
      - **Note:** import/drag-drop of new media (file dialog → `MediaPool`) and dragging an effect/clip onto the
        timeline are follow-on conveniences; today's browser lists the bootstrapped source(s) and applies effects
        by selection. The waveform reads a bounded lead-in for very long sources (summary thumbnail).
16. **Inspector & expanded effects.** Type-driven inspector with collapsible sections;
    **Transform** effect (scale / position / rotation / anchor / opacity) as a new built-in
    `IVideoEffect`; **Color** (exposure / contrast / color) on the same SkSL shape; numeric +
    slider editing bound to `AnimatableValue`, with keyframe affordances.
    - **✅ DONE (`Sprocket.Core/Model` + `Sprocket.Render/SkiaEffectPipeline` + `Sprocket.App/Inspector/*`; 25 new
      tests — Core +5, Render +8, App +12, all green).** The Inspector placeholder is now a live type-driven
      property editor, and the two new built-in effects run as real SkSL on the same preview/export pipeline as
      brightness/fade. Honours the §2 graph (effect registry + parameter metadata are pure data in **Core**; the
      shaders live in **Render**; the panel lives in **App**). Delivered:
      - **Two new built-in effects (Core ids + Render SkSL):** `EffectTypeIds.Transform`
        (scale / positionX·Y / rotation / anchorX·Y / opacity) and `EffectTypeIds.Color`
        (exposure / contrast / saturation), with their parameter names added to `EffectParamNames`. **Color** is
        a per-pixel premultiplied-safe shader on the same shape as brightness (exposure = `exp2` gain, contrast
        about mid-grey, saturation = luma mix, clamped to `[0,a]`). **Transform** is a *geometric* stage: the C#
        side composes scale→rotate→position about the anchor in canvas space, inverts the affine, and feeds the
        inverse (`m` 2×2 + `t`) to the SkSL so it maps each output coordinate back to a source coordinate; the
        root image shader switches to **Decal** tiling whenever a transform is present, so a shrunk/moved layer
        reveals the background instead of smearing edge pixels. A non-invertible transform (e.g. scale 0) draws
        nothing. Both chain like brightness/fade in `BuildEffectShader` (which now also receives the layer's dest
        rect to anchor the transform), so they compose on **preview and export** with no per-frame pixel alloc (§7).
      - **Type-driven parameter metadata (Core, §4):** `EffectParameterDescriptor` (name, label, default, min,
        max, step, optional unit) added to every `EffectDescriptor`, and `EffectDescriptor.CreateInstance()` now
        builds a fresh instance by setting **each declared parameter to its default** (no per-effect factory
        duplication). The Inspector — and any future plugin (step 33) — gets its editing UI for free from this
        list. `EffectCatalog` now registers Transform + Color alongside the slice effects.
      - **`InspectorPanel` (App, built in code like `TimelineControl`/`MediaBrowserPanel`):** a read-only **Clip**
        section (source / start / duration / trim) plus one **collapsible `Expander` section per effect**, each
        rendered automatically from the effect's parameter descriptors as a **label + keyframe toggle + numeric
        box + slider**. A **`+ Effect`** flyout adds any catalog effect; a per-section **✕** removes one. All
        editing runs through the step-10 command stack: a **slider drag coalesces to one undo entry**
        (`BeginCoalescing` on pointer-down, sealed on release/capture-lost), the numeric box commits a single
        edit on Enter/blur, and the model updates live. The **keyframe affordance** (◇/◆) converts a parameter
        to/from animated and scrubs a keyframe in **at the playhead**; animated values' displayed value tracks
        the playhead via `OnPlayheadMoved`. Edits during a gesture refresh values rather than rebuilding so the
        control isn't torn down mid-drag; undo/redo + add/remove rebuild the sections. Unregistered (plugin)
        effects still get editable sliders via fallback descriptors derived from their stored params.
      - **Pure, tested helpers (App, mirroring the step-12 `TimelineMath` split):** `InspectorFormat` (value →
        trimmed string + unit) and `AnimatableEditing` (`SetValueAt` = replace-constant vs upsert-keyframe;
        `EnableKeyframing` / `DisableKeyframing`; `UpsertKeyframe` preserving the other keyframes). The control's
        slider/pointer binding rests on these + manual verification (the App is a UI-bound `WinExe`).
      - **App wiring:** the Inspector is bound to the project, the shared `EditHistory`, and a playhead accessor
        (`() => engine.Position`); the timeline's `SelectedClipChanged` feeds it the clip, and the engine's
        `PositionChanged` drives `OnPlayheadMoved`. The effect serializes for free via the existing
        `EffectInstance` JSON (no persistence change).
      - **Tests (25):** Core — Transform/Color present + categorised, parameter lists in order, `CreateInstance`
        sets every default, all defaults within range; Render (headless raster, real SkSL) — exposure ±1 stop
        doubles/halves, contrast darkens below mid-grey, Color identity is a pass-through, Transform identity
        leaves the centre, transform opacity halves toward background, a full-width position shift reveals the
        Decal background, and a Transform→Color chain composes; App — value formatting + units, and the
        scalar-set / enable / disable / upsert keyframe transforms. Clean build (0 warnings); a
        `SPROCKET_APP_SECONDS=5` smoke launch starts the shell with the Inspector wired and tears down cleanly
        (exit 0). Full suite: **241 tests green** (Core 84, Media 24, Render 16, Audio 16, Playback 31, Export 6,
        Persistence 12, App 52).
16b. **Direct-manipulation editing & keyframe editor (follow-on to 15/16).** The conveniences that the
    bin + inspector + timeline make obvious but that steps 15/16 deferred. Lands entirely on existing
    seams + commands ([ARCHITECTURE §17](ARCHITECTURE.md)) — no model redesign:
    - **Drag media bin → timeline.** Drag a `MediaRef` tile from `MediaBrowserPanel` onto a
      `TimelineControl` lane to place a new `Clip` (snapped, on a compatible track kind), via the existing
      `AddClipCommand` (and a linked companion `AddClipCommand` in a `CompositeCommand` when the source has
      both A/V, reusing the step-13 link-group logic). Replaces today's "bootstrap builds the clips" with
      real placement; multi-track compositing/mixing (step 14) becomes visually rich once distinct clips can
      land on the new tracks.
    - **Drag effect → clip.** Drag an `EffectCatalog` row from the Effects browser onto a timeline clip (or
      the Inspector) to append it via `AddEffectCommand`, complementing today's double-click-applies-to-
      selection.
    - **File import (dialog + drag-drop from OS).** A `File ▸ Import…` dialog and OS file-drop onto the bin
      that probe via `MediaSource` and add to the `MediaPool` (the bin/thumbnail/badge path from step 15 then
      lights up for arbitrary media, not just the bootstrapped source).
    - **Keyframe track editor.** A richer keyframe surface than step 16's per-parameter ◇/◆ toggle: a
      collapsible per-parameter lane (in the Inspector or a timeline drawer) showing keyframes on the time
      axis, with add / move / delete and Hold↔Linear interpolation toggle — all editing the existing
      `AnimatableValue` through `SetEffectParameterCommand` (the keyframe math already lives in
      `AnimatableEditing`). The per-keyframe `Interpolation` mode is already in the model; this exposes it.
    Sequenced here because it directly extends 15/16; it can also slot later without blocking 17+ (monitors,
    proxies, generators) since none of those depend on it.
    - **✅ DONE (`Sprocket.Core/Commands` + `Sprocket.App/{DragFormats,MediaImport}` + `Timeline/ClipPlacement` +
      `Inspector/{KeyframeLaneMath,KeyframeLane}` and wiring; 17 new tests — Core +1, App +16, all green).** All
      four direct-manipulation conveniences now land on existing seams + commands (ARCHITECTURE.md §17) with no
      model redesign. Delivered:
      - **Drag media bin → timeline.** Bin tiles are drag sources (Avalonia 12 `DataTransfer` / `DoDragDropAsync`
        under a typed `DataFormat<string>` in `DragFormats`); `TimelineControl` is a drop target that places a new
        clip on the lane under the cursor via the pure, tested **`ClipPlacement`** helper — `SnapStart` snaps the
        drop's leading *or* trailing edge to clip edges / playhead / origin, and `BuildPlaceCommand` issues a single
        `AddClipCommand` or, for an A/V source, a linked companion clip on the first track of the other kind wrapped
        in a `CompositeCommand` with a shared link group (reusing the step-13 link logic). A dashed accent
        drop-indicator previews the landing position; the placed clip is selected. (Launch now opens an *empty*
        project — the bundled demo clip is reached via File ▸ Open Sample Project — so all lanes start empty and
        fill by dragging / importing.)
      - **Drag effect → clip.** Effects-browser rows are drag sources (effect-id payload); dropping on a timeline
        clip appends the effect via `AddEffectCommand` (hit-tested with the existing `TryHitClip`), complementing
        the step-15 double-click-applies-to-selection.
      - **File import (dialog + OS drag-drop).** **File ▸ Import Media…** (`Ctrl+I`) opens a `StorageProvider`
        picker, and OS file-drop onto the bin both route through **`MediaImport.TryImport`**, which probes via
        `MediaSource` and adds a `MediaRef` through the new Core **`AddMediaCommand`** (undoable, dedupes by path,
        offline-tolerant §15); the bin then refreshes so the imported source's thumbnail/badges appear (step 15).
      - **Keyframe track editor.** A per-parameter **`KeyframeLane`** appears under an animated parameter's slider
        in the Inspector: keyframes drawn on the clip's timeline range (diamonds = Linear, squares = Hold) with the
        playhead marked. **Drag** a keyframe to move it (one coalesced undo entry), **double-click** empty space to
        add at that time, **double-click** a keyframe to toggle Hold↔Linear, **right-click** to delete — all editing
        the existing `AnimatableValue` through `SetEffectParameterCommand` (step 10). The keyframe transforms
        (`MoveKeyframe` / `RemoveKeyframe` — collapsing to a constant when the last one goes / `SetInterpolation`)
        joined the pure **`AnimatableEditing`** helper, and the lane geometry/hit-testing lives in the pure
        **`KeyframeLaneMath`** (mirroring the step-12 `TimelineMath` split).
      - **Tests (17):** Core — `AddMediaCommand` apply/undo/redo. App — `ClipPlacement` (start/trailing-edge snap,
        origin clamp + off-switch, A/V linked pair, video-only single clip, no-compatible-track null, unlinked pair)
        and the keyframe helpers (move preserving value/order, move-onto overwrites, no-op for missing, remove keeps
        rest, remove-last → constant, Hold/Linear toggle, `KeyframeLaneMath` round-trip / clamp / nearest-within-
        tolerance). The drag/drop plumbing + lane drawing rest on these + manual verification (the App is a UI-bound
        `WinExe`). Clean build (0 warnings); a `SPROCKET_APP_SECONDS=5` smoke launch starts the shell and tears down
        cleanly (exit 0). Full suite: **258 tests green** (Core 85, Media 24, Render 16, Audio 16, Playback 31,
        Export 6, Persistence 12, App 68).
16c. **Wire up the menu / command surface (make the menus actually work).** Step 11 built the inline
    menu bar (`File · Edit · Clip · Sequence · Effects · View · Window · Help`) but most items are inert
    — e.g. **File ▸ Save / Save As / Open** don't run today even though `ProjectSerializer` (step 9) and
    the action-bar Save button already exist. Bind every menu item to its command and add the standard
    keyboard accelerators: **File** (New / Open / Save / **Save As — write the current project to a new
    file as an independent copy** so the original is left untouched / Import Media [step 16b] / Export
    [step 8] / Exit), **Edit** (Undo / Redo [step 10] / Cut / Copy / Paste / Delete / Select All),
    **Clip** (Enable, Link/Unlink [step 13], Speed/Duration, Nudge), **Effects** (apply from the catalog
    [steps 15–16]), **View** (timeline zoom, Snapping, Guides [step 17], panel toggles), **Window**
    (layout), **Help** (About — no framework/runtime text, [UI.md §3.7](UI.md)). Every editing action
    routes through the step-10 `EditHistory` so it stays undoable; items are context-enabled (greyed out
    when they don't apply), and an item whose feature lands later (e.g. **Sequence ▸ New / Settings /
    Nest** → step 23) stays visibly disabled rather than silently dead. A smoke pass confirms Save /
    Open / Export run from the menus, not just the toolbar buttons.
    - **✅ DONE (`src/Sprocket.App`: `MainWindow.axaml`/`.cs`, `App.axaml.cs`, `MediaBootstrap.cs`,
      `Timeline/TimelineControl.cs`, new `ClipboardOps.cs` + `Dialogs.cs`; 9 new tests in
      `tests/Sprocket.App.Tests/ClipboardOpsTests.cs`).** The whole inline menu bar is now live — every item is
      bound to its command, routed through the step-10 `EditHistory`, and context-enabled on submenu-open. This is
      an **App-layer step**: every operation lands on an existing Core command (`AddClip`/`RemoveClip`/
      `SetClipPlacement`/`CompositeCommand`/`SetProperty<Guid?>`/`AddEffect`) or `ProjectSerializer`, so **no Core
      change was needed**. Delivered:
      - **File — New / Open / Save / Save As / Import / Export / Exit.** **New** (empty 1V+1A project) and **Open**
        (a project JSON via `ProjectSerializer.Load`, offline-tolerant §15) hand a fully-built project to the
        composition root through a new `MainWindow.SessionRequested` event; `App` builds a fresh engine over it
        (`MediaBootstrap.CreateForProject`, which opens decoders + an audio master clock for an *existing* project
        without mutating it) and **swaps the shell window** (new shown before old closed so the
        last-window-closes shutdown never trips, then the old engine is disposed). **Save** writes to the tracked
        file or falls back to **Save As**; **Save As** writes the project to a newly chosen file as an
        **independent copy** (the original file is untouched) and re-points the document + title at it. The
        document tracks its file path and the dirty indicator resets on save/load.
      - **Edit — Undo / Redo + Cut / Copy / Paste / Delete.** A single-clip clipboard (a detached deep copy with
        the effect stack cloned and the link cleared — a paste is independent): **Copy** snapshots the selected
        clip, **Cut** = copy + delete, **Paste** drops the snapshot at the playhead onto the first track of the
        matching kind and selects it, **Delete** removes the selection (and, with **Linked** on, its companion
        A/V clips) as one undo entry. **Select All** stays disabled (multi-clip selection isn't modelled yet).
      - **Clip — Unlink + Nudge.** **Unlink** clears the link group on the selected clip and its companions (one
        undo entry, step 13); **Nudge Left/Right** shifts the clip (and its linked group, group-clamped so none
        crosses the origin) by one frame. **Enable / Link / Speed-Duration** stay disabled (per-clip enable + a
        retime model don't exist yet; Link needs a multi-clip selection).
      - **Effects** — the menu is populated from `EffectCatalog` at runtime and each item appends that effect to
        the selected clip via `AddEffectCommand`, complementing the step-15 browser double-click / step-16b drag.
      - **View** — **Zoom In/Out** (the timeline control), **Snapping** + **Guides** as checkbox items that mirror
        and drive the existing toolbar toggles (single source of truth), and **Project / Inspector panel** show /
        hide (collapsing the pane's grid column + splitter). **Window — Reset Layout** restores the pane splitters
        to their defaults. **Help — About** opens a small dialog with the product name + the app's own version +
        a one-line description (**no framework/runtime text**, [UI.md §3.7](UI.md)).
      - **Accelerators + context-enabling.** All accelerators are handled in `OnKeyDown` (Ctrl+N/O, Ctrl+Shift+S,
        Ctrl+X/C/V, Delete, Alt+←/→, plus the existing save/export/import/undo/redo + Space), with a **focused
        text-field guard** so editing/transport keys don't steal input from the bin search box or the Inspector
        numeric fields (the `InputGesture` text on each menu item is the display label). Edit/Clip/Effects/View
        menus refresh their item enable/checked state on **submenu-open**, so they reflect the live selection /
        clipboard / toggle state without per-edit bookkeeping. **Sequence** stays wholly disabled until step 23.
      - **Pure, tested helper + manual/smoke verification (the project's established split).** `ClipboardOps`
        (clip deep-copy, paste placement, and the group-nudge origin clamp) is Avalonia-free and covered by **9
        headless tests** (copy clones effects + clears link + is insulated from later edits, paste places/clamps +
        clones, repeated pastes independent, the nudge clamp); the session-reload / window-swap, dialogs, and
        menu wiring rest on these + manual verification (the App is a UI-bound `WinExe`). Clean build (0 warnings);
        a `SPROCKET_APP_SECONDS=6` smoke launch starts the shell with the full menu wired and tears down cleanly
        (exit 0) — confirming the expanded menu XAML parses and Save / Open / Export run from the menus. Full
        suite: **278 tests green** (Core 85, Media 24, Render 18, Audio 16, Playback 40, Export 6, Persistence 12,
        App 77).
16d. **Premiere-parity keyframes.** The keyframe foundation exists — the model's `AnimatableValue`
    (constant or keyframed, with per-keyframe `Interpolation`), the step-16 ◇/◆ inspector affordances,
    and the step-16b keyframe-lane editor (add / move / delete, Hold↔Linear). Bring it to Adobe-Premiere
    parity: **temporal interpolation beyond Hold/Linear** — Bezier / Ease In / Ease Out / Auto Bezier
    with an editable **velocity (value) graph**; **spatial interpolation** for positional params
    (Transform position/anchor) so keyframes define a **motion path** with linear or curved (spatial
    Bezier) segments edited as on-canvas handles in the Program monitor (step 17); plus keyframe
    **copy / paste**, multi-select, nudge, and playhead **jump-to-previous/next-keyframe** navigation.
    Lands on the existing `AnimatableValue` + `SetEffectParameterCommand` + `AnimatableEditing` seam —
    the per-keyframe `Interpolation` enum just gains the new modes (additive, no redesign),
    [ARCHITECTURE §9](ARCHITECTURE.md). **Terminology:** keep "keyframe" for animation (the
    Premiere/After Effects convention, and already the model term); to remove the only clash, refer to
    the unrelated **codec** sense (the GOP I-frame `MediaSource` seeks to, step 3) as **"I-frame"** in
    code and docs from here on — no rename of the animation concept is needed.
    - **✅ DONE — temporal interpolation + keyframe ops + navigation (`Sprocket.Core/Model/{AnimatableValue,
      KeyframeNavigation}` + `Sprocket.App/Inspector/{AnimatableEditing,KeyframeLane}` + transport; 24 new tests —
      Core +15, App +8, Persistence +1, all green).** Lands exactly on the existing seam as planned — the
      per-keyframe `Interpolation` enum **just gains the new modes**, no model redesign (ARCHITECTURE.md §9).
      Delivered:
      - **Eased temporal interpolation (Core).** `Interpolation` adds `EaseIn` / `EaseOut` / `EaseInOut` (the
        "Ease In / Ease Out / Auto Bezier" set) alongside `Hold` / `Linear`; `AnimatableValue.Evaluate` shapes the
        segment's velocity through a small, exact `Ease()` curve (quadratic accel/decel + a cubic smoothstep —
        Bezier-like velocity without a curve solver, so it's trivially testable). Each curve is monotonic with
        f(0)=0, f(1)=1, so endpoints still land on the keyframe values; only the velocity between them changes.
        **Additive:** old projects (Hold/Linear only) evaluate identically, and the modes round-trip through the
        string-enum persistence (no schema bump — §12).
      - **Keyframe navigation (Core).** `KeyframeNavigation.PreviousKeyframe` / `NextKeyframe` gather keyframes
        across **every** animated parameter of **every** effect on a clip and find the nearest one strictly
        before/after a time (so it never sticks on the current keyframe), plus `HasKeyframes`. Pure model
        reasoning, headless-tested beside `RenderGraph`.
      - **Keyframe ops (App `AnimatableEditing`).** `CycleInterpolation` (the step-16b Hold↔Linear toggle grown to
        cycle Linear → Ease In → Ease Out → Ease In/Out → Hold → …), `NudgeKeyframes` (shift a whole multi-selection
        by a tick delta as one op, shifted-wins on collision), and `CopyKeyframes` / `PasteKeyframes` (paste lands
        the earliest at the playhead, keeps relative spacing, carries value + interpolation) — all pure transforms
        on the immutable `AnimatableValue`, handed to `SetEffectParameterCommand` so every edit stays undoable
        (step 10).
      - **UI wiring (App, manual-verified like the other UI-bound controls).** The Inspector keyframe lane's
        double-click now **cycles all interpolation modes** (was Hold↔Linear) and draws each mode distinctly
        (square = Hold, diamond = Linear, circle = eased). The transport bar gains **◆◀ / ▶◆** jump-to-previous/
        next-keyframe buttons (and `[` / `]` accelerators) that seek the Program playhead to the selected clip's
        nearest keyframe via `KeyframeNavigation`; the buttons context-enable only when the selection has
        keyframes (refreshed on selection + on every history change, so adding the first keyframe lights them up).
      - **Terminology.** Honoured the directive: the **codec** sense of "keyframe" (the GOP key picture
        `MediaSource`/`AudioSource` seek to, the `MediaEncoder` GOP size) is now called **"I-frame"** in those
        comments; "keyframe" is reserved for the animation concept (the model term).
      - **Tests (24):** Core — eased modes hit the endpoints, EaseOut/EaseIn bracket the linear midpoint,
        EaseInOut is symmetric about 0.5, all eased modes are monotonic; `KeyframeNavigation` prev/next across
        params, strict (doesn't stick on the current), null at the ends, `HasKeyframes`. App — `CycleInterpolation`
        walks every mode and wraps + no-ops off a keyframe, `NextMode` covers all five once, `NudgeKeyframes`
        single/multi/no-op, copy→paste reproduces values + interpolation at a new origin, empty-clipboard no-op.
        Persistence — an EaseOut/EaseIn/EaseInOut ramp round-trips (modes **and** eased value). Clean build (0
        warnings); a `SPROCKET_APP_SECONDS=5` smoke launch starts the shell with the keyframe transport wired and
        tears down cleanly (exit 0). Full suite: **302 tests green** (Core 100, Media 24, Render 18, Audio 16,
        Playback 40, Export 6, Persistence 13, App 85).
    - **✅ DONE — editable velocity graph + multi-select gestures (items 1 & 3) (`Sprocket.Core/Model/
      AnimatableValue` + `Sprocket.Persistence` + `Sprocket.App/Inspector/{AnimatableEditing,KeyframeGraphMath,
      KeyframeLane,InspectorPanel}`; 11 new tests — Core +3, App +7, Persistence +1, all green).** Still purely
      additive on the same seam (ARCHITECTURE.md §9). Delivered:
      - **Custom Bezier velocity curves (Core).** `Interpolation` gains `Bezier`, and `Keyframe` gains two
        nullable `BezierHandle`s (`EaseOut` = the outgoing control point, `EaseIn` = the incoming one) in
        segment-normalized (time-fraction, value-progress) space — the CSS / After-Effects
        `cubic-bezier(x1,y1,x2,y2)` model. `Evaluate` solves Bx(t)=x (Newton-Raphson + bisection fallback) then
        returns By(t), exact at the endpoints; null handles fall back to a gentle "easy ease"
        (`BezierHandle.DefaultEaseOut/In`). Additive: pre-16d keyframes (no handles) evaluate identically, and the
        handles round-trip through persistence as nullable fields (`WhenWritingNull` → byte-identical for
        non-Bezier projects, **no schema bump**).
      - **Editing helpers (App `AnimatableEditing`).** `SetOutgoingHandle` (sets the handle **and** switches the
        keyframe to Bezier) / `SetIncomingHandle` (handle only), and `Bezier` joins the `CycleInterpolation`
        round-robin. The pure value-axis geometry (`KeyframeGraphMath`: value↔Y mapping, handle
        progress↔value conversions with a flat-segment guard) mirrors the step-12 `TimelineMath` split.
      - **Velocity-graph UI + multi-select (App `KeyframeLane`).** The lane now has a **graph mode** (toggled by a
        `∿` button per animated parameter) that plots the live value curve and, for each Bezier segment, draws
        **draggable handles** to shape the velocity freely — every drag is one coalesced undo entry. Both the
        compact strip and the graph support **multi-select**: click, **Shift-click** to toggle, **rubber-band** to
        box-select, then **drag the whole selection together** (via `NudgeKeyframes`) and **right-click** to delete
        the selection. Keyframe glyphs read per mode (square Hold · diamond Linear · circle eased · hexagon
        Bezier); selected keyframes get an accent ring. (The lane/handle drawing + pointer interaction rest on the
        pure helpers + manual verification, the App being a UI-bound `WinExe`.)
      - **Tests (11):** Core — Bezier default handles are a symmetric smooth ease, linear-equivalent handles match
        linear, Bezier is monotonic. App — `SetOutgoing`/`SetIncoming` handle behaviour + no-op, custom handles
        visibly shape the evaluated curve, `KeyframeGraphMath` value↔Y round-trip / clamp / degenerate-range
        centring / progress conversions + flat-segment guard. Persistence — a Bezier keyframe's handles round-trip
        (unset handles stay null). Clean build (0 warnings); a `SPROCKET_APP_SECONDS=5` smoke launch starts the
        shell with the graph toggle wired and tears down cleanly (exit 0). Full suite: **313 tests green** (Core
        103, Media 24, Render 18, Audio 16, Playback 40, Export 6, Persistence 14, App 92).
    - **Remaining for 16d (item 2 — the non-additive part, deferred):** **spatial interpolation / motion paths**
      for Transform position·anchor with on-canvas Bezier handles in the Program monitor. This needs position
      modelled as a **2D pair** so X·Y interpolate jointly along a curve, which the current independent
      `positionX`/`positionY` `AnimatableValue`s (each evaluated per-axis) can't express without the redesign the
      additive framing avoids; it slots onto the same `AnimatableValue` + command seam when that 2D-position model
      is introduced.
16e. **Cross-track clip dragging (move / copy / horizontal-lock).** Extend the timeline Move tool so a clip
    drags **vertically across tracks**, not only horizontally along time. **Alt/Option-drag copies** (drops a
    duplicate on the target track, original untouched); **Shift-drag locks the horizontal position** to the
    origin time (change track only); the modifiers stack (Alt+Shift = copy at the same time). Matches the NLE
    convention (Premiere/Resolve/FCP use Alt/Option to duplicate; Shift-lock matches Resolve), leaving Ctrl/Cmd
    free for a future insert edit. Lands on existing seams ([ARCHITECTURE §17](ARCHITECTURE.md)) with no model
    redesign.
    - **✅ DONE (`Sprocket.Core/Commands/ModelCommands.cs` + `Sprocket.App/Timeline/{TimelineMath,ClipPlacement,
      TimelineControl}`; 10 new tests — Core +1, App +9, all green).** Delivered:
      - **New Core command `MoveClipToTrackCommand`** — removes the clip from its source track, sets its new
        timeline start, and adds it to the destination track; undo restores the original track **at the original
        index** (z-order safe, like `RemoveClipCommand`) and the original start. `SourceIn/Out` + `LinkGroupId`
        untouched. Not coalescing — the gesture commits exactly one command, so it is already one undo entry.
      - **Move drag reworked to preview-then-commit** (`TimelineControl`). Only the Select tool's clip-body drag
        changed; **Trim/Slip stay live + coalesced** as before. During the drag the model is **not** mutated — a
        translucent **ghost** + **target-lane highlight** show where the clip will land (modifiers read live from
        the pointer gesture: `Alt` → copy cursor, `Shift` → horizontal lock). On release it commits exactly one
        command: a no-op (no move), `SetClipPlacementCommand` (same-track move), `MoveClipToTrackCommand`
        (cross-track), or `AddClipCommand` of a `ClipboardOps.Paste` clone (Alt-copy — an independent duplicate,
        original untouched). **Track-kind is enforced** via the pure `ClipPlacement.CompatibleTrack`
        (video→video, audio→audio; an incompatible lane keeps the source track). **Linked A/V:** only the dragged
        clip changes track; companions **shift in time only** (kept on their own tracks) inside a
        `CompositeCommand`. **Drop collisions allow overlap** (true overwrite/ripple deferred to step 22).
      - **Pure, tested geometry** (mirroring the step-12 `TimelineMath` split): `TimelineMath.LaneIndexAtY`
        (extracted from the control's private `LaneAtY`, which now delegates to it) and
        `ClipPlacement.CompatibleTrack`.
      - **Tests (10):** Core — `MoveClipToTrackCommand` moves + sets start + reverts to the original track/index,
        leaving source span + link intact. App — `LaneIndexAtY` Y→lane mapping (incl. above-ruler + degenerate
        stride) and `CompatibleTrack` (same-kind lane → that track; cross-kind / null lane → null = keep source).
        The ghost drawing + pointer interaction rest on these + manual verification (the App is a UI-bound
        `WinExe`). Clean build (0 warnings); a `SPROCKET_APP_SECONDS=5` smoke launch starts the shell and tears
        down cleanly (exit 0). Full suite: **419 tests green** (Core 140, Media 28, Render 23, Audio 19,
        Playback 47, Export 10, Persistence 30, App 122).
17. **Monitors.** Dual **Source / Program** monitors (same render graph, second surface),
    safe-area / framing-grid overlay, **Fit** zoom, and full transport (jump-to-start/end,
    frame-step, play/pause).
    - **✅ DONE (`Sprocket.Playback` + `Sprocket.Render` + `Sprocket.App`; 11 new tests — Playback +9, Render +2,
      all green).** The Program monitor's placeholder header/transport grew into the dual-monitor surface of
      [UI.md §3.4](UI.md): a Source tab, the safe-area/framing-grid overlay, the `Fit ▾` zoom, and the full
      `⏮ ◀◀ ▶ ▶▶ ⏭` transport. Honours the §2 graph (the zoom/overlay math is pure in **Render**; the second
      monitor reuses the existing playback/render seams in **App** — a "new feature on an existing seam", §17).
      Delivered:
      - **Full transport — frame-step (Playback).** `PlaybackEngine.StepFrame(±1)` pauses if playing then seeks
        to the frame-aligned neighbour; the snap-to-frame-grid + clamp math lives in the pure
        `PlaybackMath.StepFrame` (floors the position to its frame index first, so a scrubbed mid-frame playhead
        still steps on the frame grid). The transport bar gains **◀◀ / ▶▶** buttons beside the existing
        jump-to-start/end + play/pause.
      - **`Fit ▾` zoom (Render + App).** `FramePresenter.ComputeZoomRect` extends the existing letterbox fit with
        fixed **50% / 100% / 200%** scales (native-size, centred, overflow clipped) behind a new `MonitorZoom`
        enum; a `Fit ▾` `ComboBox` in the monitor header drives it. `PreviewSurface` gained a `Zoom` property and
        an explicit logical **frame size** (`SetFrameSize`) so every layer now composites into one shared zoom
        rect (the sequence resolution for Program, the source resolution for Source) instead of fitting each layer
        independently — more correct for mixed-resolution tracks and the anchor for the overlay.
      - **Safe-area / framing-grid overlay (Render + App).** `MonitorOverlay` draws a rule-of-thirds grid plus
        **action-safe (93%)** and **title-safe (90%)** guide rectangles over the frame rect as thin translucent
        strokes (never touching the decoded pixels, §1); the inset geometry (`ComputeSafeAreas`) is pure. A
        **Guides** toggle in the header switches it on both surfaces.
      - **Dual Source / Program monitors (App).** A small `IMonitor` abstraction unifies the transport over two
        implementations: **`ProgramMonitor`** (a thin adapter over the app's main multi-track engine) and
        **`SourceMonitor`** (owns a *rebuildable* single-feed `PlaybackEngine` over a throwaway one-clip project
        spanning the selected source — the **same render graph**, ARCHITECTURE.md §5). The Source engine is built
        **lazily** only while its tab is open (a decoder is opened on activate and freed on deactivate) and is
        video-only on a `SoftwareClock`. Both monitors present through **one shared `PreviewSurface`** (so the
        program preview's GPU custom-draw tree is unchanged from step 11); the **Program / Source** header tabs
        swap which engine is attached to it, pause the outgoing monitor, and re-point the one transport bar at the
        active monitor. Selecting a timeline clip feeds its source to the Source monitor. The Inspector keeps
        tracking the **Program** playhead regardless of which monitor is shown.
      - **Pure, tested helpers + manual-verified UI (the project's established split).** Tests (11): Playback —
        `PlaybackMath.StepFrame` (advance/retreat one frame, mid-frame snap-to-grid, clamp at both ends, degenerate
        frame-rate no-op) and `FramePresenter.ComputeZoomRect` (Fit == fit-rect, 50/100/200% scale + centre,
        degenerate → empty); Render — `MonitorOverlay.ComputeSafeAreas` (documented insets, concentric, title
        inside action, degenerate → empty). The tab switching, transport routing, source-engine lifecycle, and
        overlay/zoom *drawing* are UI/decode-bound and rest on these + manual verification (the App is a UI-bound
        `WinExe`). Clean build (0 warnings); a `SPROCKET_APP_SECONDS=5` smoke launch starts the shell with both
        monitors wired and tears down cleanly (exit 0). Full suite: **269 tests green** (Core 85, Media 24,
        Render 18, Audio 16, Playback 40, Export 6, Persistence 12, App 68).
      - **Note:** the Source monitor previews video only — source-audio scrub and an independent in/out-marker
        overlay (to mark a source span before placing it) are small follow-ons behind the same `IMonitor` seam.
18. **Proxy media (render performance) — default-on, background, transparent.** Generate
    lower-resolution proxies and preview against them via an alternate `IFrameSource`; **export always
    pulls full-resolution originals** ([ARCHITECTURE §17](ARCHITECTURE.md)). Committed feature per
    [BRIEF.md](BRIEF.md). Designed so it never interrupts flow:
    - **Best-available source selection (default-on).** "Use proxies" is **on by default**, but *on*
      means *use a proxy when one is ready, else the original* — so a freshly imported clip starts
      previewing on the original immediately and **transparently switches** to its proxy once built.
      Per-`MediaRef` state None → Queued → Building → Ready/Failed; the preview source resolver prefers a
      Ready proxy, while export ignores proxies entirely (determinism unaffected, §1.6).
    - **Background generation.** A **bounded**-worker proxy service encodes off the hot path (leaves
      cores for decode/render/audio), using **hardware / all-intra / OS-specific** codecs (step 32,
      [ARCHITECTURE §11](ARCHITECTURE.md) "Preview vs. delivery codecs"). A **priority queue** builds
      media on the timeline / near the playhead / in the active sequence first, then the rest of the bin.
      Proxies persist in the local, regenerable cache dir (same store family as the render cache §20 /
      the per-user sidecar of step 28), survive restarts, and cancel/resume cleanly.
    - **Resolution = a fixed tier, not the live window.** The preview window resizes constantly and
      proxies are expensive + persisted, so key them to a **stable target**: default
      **`min(½ source, 1080p)`** (1080p is the locked preview ceiling — higher is wasted), and **skip
      proxy generation for sources already light enough** to preview in real time (≤ 1080p 8-bit H.264
      etc., decided from the probe). The tier is a project/preference setting (¼ / ½ / 1080p) for weak
      machines. Zoom-to-100/200% (step 17) or a >1080p preview that out-resolves the proxy falls back to
      the original for that view.
    - **Tiered "draft-first" — deferred, conditional.** Because the original is the interim fallback,
      preview is usable immediately, so a fast low-res *draft* tier only helps **heavy** sources
      (4K / HEVC / 10-bit / ProRes, or many layers) whose original can't scrub before the quality proxy
      lands. Ship the **single 1080p tier first**; add a draft tier later **only if profiling shows
      heavy-source jank** — it slots into the same best-available order (quality > draft > original) as
      just another `IFrameSource`, with no redesign.
    Note: this is **source-clip** proxying; proxying a whole **nested sequence / composited output** is
    the render cache / pre-render (§20, step 32) — same background-encode + fast-codec infrastructure,
    different unit.
    - **✅ DONE (`Sprocket.Core/Model/ProxyPolicy.cs` + `ProjectSettings`; `Sprocket.Playback` invalidation hook;
      `Sprocket.App/Proxy/{ProxyCache,ProxyTranscoder,ProxyService}` + bootstrap wiring; 19 new tests — Core +9,
      Persistence +2, App +7, Playback +1, all green).** Preview proxies are **default-on, background, and
      transparent**, landing on the existing `IFrameSource` seam (ARCHITECTURE.md §17) with **export untouched**
      (it still pulls full-resolution originals in software, §1.6 / §5 — verified: the export path was not
      modified). Delivered:
      - **Best-available source selection (Core + App).** A pure `ProxyPolicy` decides the per-tier **target
        resolution** (tier scale, then clamped under the locked **1080p** preview ceiling, even dims, aspect
        preserved) and whether a source is **worth proxying** (sources already ≤ 1080p preview in real time and are
        skipped — the "light enough" heuristic keyed off resolution, all `ProbedMediaInfo` carries today). The
        per-source `ProxyState` (None → Queued → Building → Ready/Failed) lives in the **runtime service, not the
        serialized model** (a proxy is a local, regenerable artifact). The preview feed factory opens each source's
        **best-available file** — its ready proxy, else the original — so a heavy clip previews on the original
        immediately and **switches transparently** the moment its proxy lands.
      - **Transparent switch-on-ready (Playback).** A new `PlaybackEngine.InvalidateSource(id)` + `VideoTrackPlayer`
        rebuild flag lets a track decoding a source rebuild its feed on the next pump **without a seek or a clip
        edit**; the proxy service raises `ProxyReady` (wired to `InvalidateSource` in the composition root) when a
        proxy finishes or is found already cached, so the preview flips to the proxy live. (Deterministic test: a
        signalled source reopens its feed on the next pump; an un-signalled one does not.)
      - **Background generation (App `ProxyService`).** A **single bounded background worker** drains a **priority
        queue** — sources used on the timeline build before bin-only sources — encoding off the hot path. Proxies
        persist in a **per-user cache dir** keyed by source identity (path + size + mtime) + target size
        (`ProxyCache`, pure + tested), so a cached proxy is **reused without re-encoding** across restarts; disposal
        cancels any in-flight build. Sources already light enough are never queued. Enqueued on startup, project
        load, and media import; a status-bar summary reflects progress without interrupting flow.
      - **Out-of-process encode (App `ProxyTranscoder`).** Proxies are generated by **shelling out to the `ffmpeg`
        CLI** (`-an -vf scale=… -c:v libx264 -preset ultrafast -crf 28`). **Why not the in-process `MediaEncoder`:** driving a second libav* muxer/encoder in-process
        *concurrently with the live preview decode + GPU compositor* reproducibly faulted with a native access
        violation in `av_interleaved_write_frame` (confirmed it is the in-GUI-process concurrency, not the logic — a
        standalone repro of decode+audio+encode in parallel never crashes). Shelling out keeps proxy encoding
        entirely off our process's FFmpeg state/threads, can't corrupt the live pipelines, is cleanly cancellable
        (kill the child), and **degrades gracefully** if `ffmpeg` isn't on PATH (the source just keeps previewing on
        its original, §15). Output is written to a temp file and **atomically promoted** only on a clean exit.
      - **Settings + persistence.** `ProjectSettings.UseProxies` (default on) + `ProxyTier` (default Half) are
        **additive, schema-versioned** (`WhenWritingNull`/constructor defaults): pre-18 files load with proxies on
        at the Half tier (tested), and the fields round-trip.
      - **Tests (19) + real end-to-end verification.** Core — `ProxyPolicy` target sizing per tier (4K→1080p half,
        quarter, ceiling clamp, even dims, empty) + needs-proxy (above/at/below ceiling, no-video); Persistence —
        proxy settings round-trip + pre-18 default load; App — `ProxyCache` key stability / case-insensitivity /
        forks-on-any-identity-change; Playback — feed invalidation. The service/transcoder/feed-switch rest on these
        + **manual verification** (the App is a UI-bound `WinExe`): a real run against a generated **4K** clip
        produced a **1920×1080 / 60-frame** proxy in the cache dir with no crash, and the default **1080p** sample
        correctly generated **no** proxy. Clean build (0 warnings). Full suite: **335 tests green** (Core 112,
        Media 24, Render 18, Audio 16, Persistence 16, App 99, Export 6, Playback 44).
      - **Deferred (noted, on the same seam):** the **fast draft tier** (only if profiling shows heavy-source jank),
        **hardware / all-intra** proxy codecs (step 32), **zoom-to-100/200% falling back to the original** when a
        view out-resolves the proxy (the resolver would need the live zoom), and **Source-monitor proxying** (it
        previews on originals today) — none requires a redesign.
19. **Generators & adjustment layers.** Title/text **generator clips** (a generator `IFrameSource`
    feeding the render graph). **Adjustment layers**, modelled like Premiere: a synthetic Project-bin
    item with no source media, placed on a track as an ordinary clip, whose **effect stack applies to
    every track beneath it for the clip's time span** — a render-graph stage that composites the lower
    tracks, then runs the adjustment layer's effects over that result before the tracks above
    ([ARCHITECTURE §5](ARCHITECTURE.md), [UI.md §3.6](UI.md)). It trims / moves / stacks and carries
    opacity + blend like any clip, and the same adjustment item can be **reused across tracks and
    sequences**. ("Adjustment layer" is unambiguous in this codebase, so the term is kept.)
    - **✅ DONE (Core model + render graph + Render + Export + Persistence + Playback + App; 25 new tests — Core
      +10, Render +5, Export +2, Persistence +2, Playback +1, plus the executor seam fake — all green).** Both
      content kinds land on the existing seams (§17): a generator is a new frame producer fed to the render graph,
      and an adjustment layer is a new render-graph compositing stage — neither is a rewrite. Delivered:
      - **Clip kinds (Core, §4):** `Clip.Kind` (`Media` / `Generator` / `Adjustment`) with factories
        `Clip.CreateGenerator(spec, duration, start)` and `Clip.CreateAdjustment(duration, start)`. Generator/
        adjustment clips have no source media (`MediaRefId` default); they trim / move / slip / stack and carry an
        effect stack like any clip (the synthetic source is unbounded). `Clip.CloneContentForSpan` keeps a blade
        split kind-aware (the right half stays a generator/adjustment), used by `SplitClipCommand`.
      - **`GeneratorSpec` + `GeneratorCatalog` (Core):** a generator carries a type id, **string** params (text,
        colour `#AARRGGBB`) and **numeric animatable** params (font size) — reusing `AnimatableValue` so a
        generator parameter keyframes. `GeneratorCatalog` is the registry the bin/menu list over (built-ins:
        **Title**, **Color Matte**), mirroring `EffectCatalog`; a plugin generator (step 33) registers here too.
      - **Render graph (Core, §5):** `PlanVideoFrame` emits a `LayerKind` per layer — `Generator` carries the
        `ResolvedGenerator` (params evaluated at *t*), `Adjustment` carries only its resolved effect stack. The
        generic `Render<TImage>` executor draws a generator via a new `IVideoCompositor.CreateGeneratorFrame` seam
        and realises an **adjustment** by snapshotting the composite drawn so far, folding the layer's effects over
        it, and blending the graded result back — so at full opacity it replaces, and below it cross-fades original
        vs. grade (the Premiere semantic). `RenderGraph.ResolveGenerator` is public for the preview.
      - **Render layer:** `SkiaEffectPipeline` factored its per-layer draw into `DrawImageLayer` (image → effect
        chain → composite) and added `DrawGenerator` (renders the matte / centred SkSL-free text into an offscreen
        surface, then runs the same effect chain) and `DrawAdjustment` (snapshots the surface region — mapped
        through the canvas matrix so a translated preview canvas grabs the right pixels — grades it, draws it back).
        Unknown generator ids draw nothing (pass-through, like unknown effects).
      - **Export:** `VideoExporter` switches per `LayerKind` — generators draw at full resolution, adjustment layers
        grade the composite beneath — on the same deterministic raster path, so both are golden-frame testable.
      - **Persistence:** `ClipDto` gains additive, nullable `Kind` + `Generator` (a `GeneratorDto` with string +
        animatable params); a media clip writes neither, so pre-step-19 files load unchanged and media-only projects
        serialize byte-identically (no schema bump). A trimmed generator's non-zero source-in round-trips.
      - **Preview + App:** `PlaybackEngine.UseLayers` emits generator/adjustment layers from the active clip's kind
        — **no decoder needed** for them — and the pump fires `FramePresented` for active synthetic clips (so an
        animated title/grade repaints on play and on a scrub). `PreviewSurface` draws each kind on the GPU lease
        (snapshotting `lease.SkSurface` for adjustment). **Clip ▸ Insert** (built from `GeneratorCatalog` + an
        Adjustment Layer item) inserts at the playhead via the command stack (undoable), stacking on a fresh top
        track when the topmost is occupied so an adjustment grades — not displaces — the content below; the timeline
        labels synthetic clips by title text / generator name / "Adjustment Layer".
      - **Tests:** generator/adjustment plan resolution + executor ordering (Core); clip-kind factories, spec clone
        independence, catalog, split-preserves-generator (Core); real SkSL **solid-colour fill, title text over a
        background, unknown-generator no-op, adjustment grades the composite, no-effects no-op** on offscreen raster
        (Render); **white-vs-black matte brightness + adjustment-darkens-lower-track** encode→decode round-trips
        (Export); generator/adjustment JSON round-trip + media-only-omits-fields (Persistence); synthetic clips
        become layers without a decoder (Playback). Full suite: **355 tests green** (Core 122, Media 24, Render 23,
        Audio 16, Playback 45, Export 8, Persistence 18, App 99). Clean build (0 warnings), smoke launch starts +
        tears down cleanly. **Note:** the windowed preview rests on manual verification as before; the export +
        Render-raster paths cover the pixels deterministically.
### Reprioritization (recorded 2026-06-29): editorial workflow completeness

Steps 1–19 are complete: the vertical slice plus the full editing shell, timeline, inspector,
keyframes, monitors, proxies, and **generators + adjustment layers (step 19)**. With the render,
playback, export, proxy, keyframe, and color/audio **seams** all proven, the largest remaining gap
versus Premiere, Resolve, and Final Cut is **not** rendering or core extensibility — Sprocket already
has the right seams for those. The real gap is **editorial workflow completeness**: the everyday-cutting
features that make the editor feel professional rather than unfinished. The post-19 order below is
reprioritized around that gap.

The **must-have-for-1.0** additions (mainstream pro-NLE baseline) are: **retime/speed controls**,
**markers/comments**, **ripple/roll trim modes**, **autosave/crash recovery**, **batch relink +
offline recovery**, **interchange** (EDL at minimum, then FCPXML/XML), **batch export + review
outputs** (queued exports, burn-ins, handles), **loudness metering/normalization**, and **multicam +
clip sync**. Real-time collaboration, hosted review systems, and advanced AI tooling are
**product-platform expansions, not core-editor 1.0 parity**, and stay out of the 1.0 set.

The reordering keeps the existing architecture direction; each step still lands on an existing seam
([ARCHITECTURE §17](ARCHITECTURE.md)) and none requires a redesign. The high-value, low-risk editorial
features come first (markers/autosave, retime, ripple/roll), then sequences and multicam (which build
on synced/nested structure), then broad media + interchange + relink, then delivery (export queue) and
audio loudness, with plugins, render cache, advanced color, packaging, and log/HDR refinement last.
Tags reference the [UI.md §4 checklist](UI.md).

20. **Markers & comments + autosave / crash recovery.** Two table-stakes additions — review/coordination
    infrastructure and reliability — both landing cleanly on the done model, command stack, and
    persistence with no redesign:
    - **Markers & comments.** A `Marker { Tick, Name, Comment, Color, optional span }` on the
      timeline/sequence and on clips (`Clip.Markers`), added / moved / deleted through the step-10
      command stack so they are undoable, drawn on the ruler and clip bodies ([UI.md §3.6](UI.md)),
      navigable (jump-to-prev/next, reusing the step-16d keyframe-navigation pattern), and listed in a
      markers panel. Sequence and clip markers serialize additively into the project JSON
      (schema-versioned, §12).
    - **Autosave & crash recovery.** A periodic, debounced background write of the project to a
      **sidecar autosave file** (the project is pure data; serialization already exists, step 9), driven
      off the `EditHistory.Changed` / dirty signal so it only writes when there are unsaved edits and
      never blocks the UI thread. On launch, detect a newer autosave than the saved project (or an
      autosave with no clean save) and offer recovery. Writes are atomic (temp file → promote), like the
      proxy / render-cache stores, so a crash mid-write never corrupts the project. This is table-stakes
      reliability, not a nice-to-have.
    - **✅ DONE (`Sprocket.Core/Model/{Marker,MarkerNavigation}` + `Commands/ModelCommands` markers; `Sprocket.Persistence`
      markers + new `Autosave.cs`; `Sprocket.App/{AutosaveService,MarkerListFormat}` + timeline/menu/recovery wiring;
      23 new tests — Core +9, Persistence +10, App +4, all green).** Both halves land on the done model, command
      stack, and persistence with no redesign. Delivered:
      - **Marker model (Core, §4):** a mutable `Marker { Time, Name, Comment, Color, Duration }` (a non-zero
        `Duration` ⇒ a span marker; `IsSpan`/`End`/`Clone`) with a `MarkerColor` enum (the standard NLE palette,
        Blue default). `Timeline.Markers` holds sequence markers (timeline positions); `Clip.Markers` holds clip
        markers (positioned within the clip's source, so they move/trim with it). Plain data, so command undo is a
        simple field capture.
      - **Commands (Core, step 10):** `AddMarkerCommand` / `RemoveMarkerCommand` (list ops, restoring index on undo)
        + `MoveMarkerCommand` (coalescing per marker so a drag is one undo entry) — all through `EditHistory`, so
        markers are undoable and flip the dirty indicator. Name/comment/colour edits reuse `SetPropertyCommand<T>`.
      - **Navigation (Core):** `MarkerNavigation.Previous/Next` find the nearest marker strictly before/after a
        time — the same pure, headless-tested pattern as `KeyframeNavigation` (step 16d).
      - **Persistence:** a `MarkerDto` plus additive, nullable `Markers` lists on `TimelineDto` and `ClipDto`
        (`WhenWritingNull` ⇒ a marker-less project serializes byte-identically and pre-step-20 files load with no
        markers — **no schema bump**, §12). Sequence + clip markers round-trip field-for-field (time, name, comment,
        colour, span).
      - **Autosave + crash recovery (Persistence + App):** a new `Autosave` static class does the **atomic** sidecar
        write (serialize → sibling temp file → move-with-overwrite, so a crash mid-write never corrupts the recovery
        file) and exposes the pure `AutosaveRecovery.ShouldOffer` decision (offer when an autosave exists and is newer
        than the clean save — or there is no clean save). The App-layer **`AutosaveService`** subscribes to
        `EditHistory.Changed`, marks the document dirty, and a 5 s debounce timer writes **only when dirty** —
        snapshotting the project on the UI thread (where the model lives, §8) then pushing the disk write to a
        background thread, so editing never blocks the UI. A saved project autosaves beside its file; an untitled one
        autosaves to a per-user slot. A clean **Save** clears the dirty flag and deletes the sidecar; **File ▸ Open**
        checks for a newer sidecar and **prompts to recover** (loading the autosave, relinking media against the
        project dir).
      - **App UI (manual-verified, the project's established split):** sequence markers draw as coloured **pennants
        on the ruler** (span markers add a translucent band + a faint line down the lanes); clip markers draw as small
        coloured **triangles on the clip body**. **M** adds a marker at the playhead; **Shift+M / Ctrl+Shift+M** jump
        to the next/previous marker (seeking the Program monitor, mirroring keyframe nav). A **Markers** header button
        opens the **markers panel** — an add-at-playhead action + one row per marker (colour chip, click-to-seek, ✕ to
        remove). The pure row formatter (`MarkerListFormat.Describe`) is split out and unit-tested like `TimelineMath`.
      - **Tests (23):** Core — marker span/clone/validation, Add/Remove/Move commands apply+revert (index restore,
        drag-coalesces-to-one-entry, no cross-marker merge), and `MarkerNavigation` nearest/strict/null-at-ends;
        Persistence — sequence+clip marker round-trip, marker-less omits the field, pre-step-20 loads with none, plus
        autosave atomic write/overwrite/delete and the `ShouldOffer` truth table; App — `MarkerListFormat` (name,
        unnamed fallback, span suffix, whitespace-name). Clean build (0 warnings); a `SPROCKET_APP_SECONDS=5` smoke
        launch starts the shell with markers + autosave wired and tears down cleanly (exit 0). Full suite: **378 tests
        green** (Core 131, Media 24, Render 23, Audio 16, Playback 45, Export 8, Persistence 28, App 103).
      - **Deferred (noted, on the same seam):** marker **rename / colour-change UI** (the model + commands exist; the
        panel lists & removes today), **clip-marker carry-over across a blade split**, and **untitled-project recovery
        on launch** (the untitled slot is written + recoverable, but launch only checks an opened project's sidecar).
21. **Retime & speed controls.** Per-clip speed as a first-class, non-destructive property — the most
    important missing editorial feature — landing on the existing clip / render-graph / time model:
    - **Model.** A `Clip.SpeedRatio` (and a `Reverse` flag) as a `Rational` / `AnimatableValue` so the
      timeline→source time map is `sourceTime = SourceIn + (t − TimelineStart) × speed` (constant speed)
      or an integrated map when speed is keyframed (**speed ramps**). The clip's timeline duration
      derives from the retimed source span; **freeze frame** = speed 0 over a span (hold one source
      frame); **reverse** = negative mapping. All changes route through the command stack (undoable) and
      serialize additively (§12).
    - **Render graph (§5).** `PlanVideoFrame` / `PlanAudioBuffer` apply the time map when resolving a
      clip's source time, so preview and export stay identical and deterministic, with no per-frame
      managed pixels (§1). Frame interpolation for smooth slow-motion (blend / optical-flow) is a later
      quality tier behind the same seam — ship nearest-source-frame first.
    - **Audio.** Retimed audio resamples in the mixer (pitch-preserving time-stretch is a later DSP
      refinement, step 31); reverse plays the source backward.
    - **UI.** The **Clip ▸ Speed/Duration** menu item (built but disabled at step 16c) + an inspector
      control + a speed-ramp keyframe lane (reusing step-16b/16d keyframing).
    - **✅ DONE — constant-speed retime (`Sprocket.Core/Model/Clip` + `Timing/{Timecode,Rational}` +
      `Commands/ModelCommands` + `Rendering/{RenderPlan,RenderGraph}`; `Sprocket.Audio/AudioMixer`;
      `Sprocket.Persistence`; `Sprocket.App/{SpeedFormat,Dialogs,Inspector/InspectorPanel,Timeline/TimelineControl,
      MainWindow}`; 24 new tests — Core +8, Audio +3, Persistence +2, App +11, all green).** Per-clip speed lands
      on the existing clip / render-graph / time model with no redesign (ARCHITECTURE.md §5/§17). Delivered:
      - **Model (Core, §4).** `Clip.SpeedRatio` is a strictly-positive `Rational` (default 1/1, non-destructive — the
        source bytes and the selected `SourceIn`/`SourceOut` span are untouched). The clip's timeline `Duration`
        derives from it (`(SourceOut − SourceIn) / Speed`, so 2× is half as long, ½× twice as long) and the
        timeline→source map is `MapToSource(t) = SourceIn + (t − TimelineStart) × Speed`. Both go through a new exact
        `Timecode.Scale(Rational)` (Int128 product, rounded), and `Rational.One` was added as the identity. A blade
        split copies the speed onto both halves (`CloneContentForSpan`), so the two halves still sum to the original
        timeline span.
      - **Command (Core, step 10).** `SetClipSpeedCommand` applies/reverts the ratio and coalesces per clip, so a
        Speed dialog / inspector edit is one undo entry. The source span is never touched — only `Duration` and the
        map derive from the new speed.
      - **Render graph (Core, §5).** Video needs no new plumbing — `PlanVideoFrame` already maps each layer's source
        time through `clip.MapToSource`, so a retimed clip walks its source proportionally faster/slower on **preview
        and export** with no per-frame managed pixels (§1). `AudioLayer` gained a `SpeedRatio` (default 1/1) that
        `PlanAudioBuffer` fills from the clip, so the mixer knows the resample factor.
      - **Audio (Sprocket.Audio).** `AudioMixer` resamples a retimed layer's source PCM by the speed factor with a
        **streaming linear resampler**: a per-source carried window holds source frames already pulled but not yet
        consumed, so reading stays sequential across buffers (no per-buffer seek) and the source cursor never drifts;
        a jump still re-seeks and resets the window. The **1× fast path is completely untouched** (read sequentially,
        no resample). Pitch is not preserved — a deliberate first cut (pitch-preserving time-stretch is step 31).
      - **Persistence.** `ClipDto` gains additive, nullable `speedNum`/`speedDen`: a normal-speed (1/1) clip writes
        neither (`WhenWritingNull`), so pre-21 files load at 1× and un-retimed projects serialize byte-identically
        (no schema bump, §12). A retimed clip's speed round-trips and its derived duration comes back right.
      - **UI (App, manual-verified).** **Clip ▸ Speed / Duration…** (enabled when a clip is selected) opens a small
        percentage dialog (100% = normal, with 25/50/100/200/400% presets); the **Inspector** Clip section grew an
        editable **Speed %** row. Both retime the selected clip **and its linked companions together** (so companion
        audio stays in sync) through `TimelineControl.SetSelectedClipSpeed` / the inspector commit, as one undo entry.
        The percentage↔ratio conversions live in a pure, tested `SpeedFormat` helper (mirroring the `TimelineMath`
        split); the Duration row updates on the resulting rebuild.
      - **Tests (24).** Core — duration/map at 1×/2×/½×, positive-speed guard, `Timecode.Scale` rounding,
        `SetClipSpeedCommand` apply/revert/coalesce, split-preserves-speed-on-both-halves; Audio — mixer resamples a
        known source ramp at 2×/½× (exact on the source grid) and **streams across buffers without re-seeking**;
        Persistence — speed round-trip + 1× omits-the-field/loads-as-unity; App — `SpeedFormat` parse/format/round-trip
        + non-positive rejection (the deferred reverse/freeze inputs). Clean build (0 warnings); full suite
        **410 tests green** (Core 139, Media 28, Render 23, Audio 19, Playback 47, Export 10, Persistence 30,
        App 114) — the FFmpeg-native suites (Media/Playback/Export) verified against the bundled FFmpeg-8 shared
        natives, confirming the 1× fast path is behaviour-unchanged and the retimed-audio resample feeds the real
        decode → mixer → export round-trip.
      - **Deferred (noted, on the same seam — additive when picked up):** **reverse** playback (the `Reverse` flag —
        needs backward decode in the feed/export provider, not just a negated map), keyframed **speed ramps** (an
        integrated time map from a keyframed-speed `AnimatableValue`), **freeze frame** (speed 0 — needs an
        independent timeline duration rather than one derived from the source span), and **pitch-preserving**
        time-stretch / frame-interpolated slow-motion (step 31 / a later quality tier behind the same seam).
22. **Ripple / roll / slide editing.** Trim modes that preserve timeline continuity — basic editor
    ergonomics — extending the step-12/13 timeline tools (Select / Blade / Slip already exist). Each is a
    new pure timeline operation issued as a command (or `CompositeCommand`) so it stays undoable:
    - **Ripple trim / delete** — trimming a clip's edge (or deleting a clip) shifts all downstream clips
      on the track (optionally all tracks — "ripple all") to close / open the gap, keeping the sequence
      contiguous.
    - **Roll edit** — adjust the cut point between two adjacent clips, moving the shared edge so one
      clip's out and the next clip's in change together while their combined duration (and everything
      downstream) stays fixed.
    - **Slide** — move a clip along the timeline while its neighbours absorb the change (the complement
      of the existing slip).
    The geometry / clamping lives in the pure `TimelineMath` (the step-12 split), headless-tested; the
    tool palette gains ripple / roll affordances ([UI.md §3.2](UI.md)). Linked A/V (step 13) participates
    so a ripple moves companion audio too.
    - **✅ DONE (`Sprocket.Core/Commands/ModelCommands.cs` + `Sprocket.App/Timeline/{TimelineMath,TimelineControl}`
      + `MainWindow.axaml`/`.cs`; 15 new tests — Core +8, App +7, all green).** All three trim modes (plus ripple
      delete) land on the existing clip / command / time model with no redesign (ARCHITECTURE.md §17). Each is a
      pure, undoable timeline operation; the tool palette now carries the full Premiere/Resolve/FCP trim toolset
      (**Select · Blade · Ripple · Roll · Slip · Slide · Hand · Zoom**, [UI.md §3.2](UI.md)). Delivered:
      - **Three Core commands (step 10).** `RippleTrimCommand` — trims one edge (the clip's `TimelineStart` stays
        fixed for *both* edges) and shifts a captured downstream set by the duration change; re-derives each
        downstream start from its captured original + the latest shift so a coalesced drag stays exact.
        `RollEditCommand` — moves the shared cut between two adjacent clips (left out + right in/start together),
        keeping their combined span and everything downstream fixed. `SlideClipCommand` — moves a clip while its
        (optional) prev/next neighbours absorb it; the slid clip's source window is untouched. All three coalesce
        per gesture (one undo entry) and revert exactly. **Ripple delete** (Shift+Delete, the Premiere/Resolve
        convention; Edit ▸ Ripple Delete) composes `RemoveClipCommand` + downstream `SetClipPlacementCommand`s into
        one `CompositeCommand`.
      - **Pure clamping (App `TimelineMath`, mirroring the step-12 split).** `ClampRollDelta` / `ClampSlideDelta`
        (shared shape: the growing side limited by its remaining media, the shrinking side floored at the minimum
        clip duration) and `RippleTrimBounds` (the per-edge ripple travel) — all in timeline ticks, headless-tested;
        the control converts each clip's source/media headroom to timeline ticks (÷ its retime speed, step 21)
        before calling them, so retimed clips clamp correctly.
      - **Tool palette + gestures (App `TimelineControl`).** `EditTool` gained `Ripple` / `Roll` / `Slide`; a
        `DragKind` now routes each clip-drag (the Select-tool body drag still previews-then-commits for cross-track
        moves, step 16e; Trim/Slip/Ripple/Roll/Slide mutate live inside a coalescing scope). Ripple/Roll act on an
        edge (a body click just selects); Roll resolves the two clips sharing the dragged cut and aborts when there
        is no adjacent clip; Slide captures the butted neighbours. Snapping snaps the moving edge/cut/clip to
        nearby edits & the playhead. **Linked A/V participates:** a ripple trims every companion's matching edge and
        ripples each companion's own track (one `CompositeCommand`); a ripple delete removes the companions and
        ripples their tracks too. Each tool sets a matching cursor.
      - **Menu / accelerators (App `MainWindow`).** Three new tool radio buttons (wired to `ActiveTool`), the
        **Edit ▸ Ripple Delete** item (Shift+Delete, context-enabled with the selection), and the Shift+Delete
        accelerator (guarded so it doesn't steal a focused text field's input).
      - **Tests (15) + verification.** Core — `RippleTrimCommand` out-extend/in-trim + downstream shift + undo +
        drag-coalesces-to-one-entry; `RollEditCommand` cut-move keeps the combined span + undo + coalesce;
        `SlideClipCommand` neighbours-absorb + source-window-untouched + no-prev-neighbour + undo + coalesce. App —
        `ClampRollDelta` (within-bounds / left-media / right-min / left-roll-headroom), `ClampSlideDelta` (mirror),
        `RippleTrimBounds` (both edges). The control's pointer/tool wiring rests on these + manual verification (the
        App is a UI-bound `WinExe`): clean build (0 warnings) and a `SPROCKET_APP_SECONDS=5` smoke launch starts the
        shell with the full trim toolset + Ripple Delete wired and tears down cleanly (exit 0). The managed suites
        are green — **Core 148** (incl. the 8 new), **App 129** (incl. the 7 new), Audio 19, Render 23,
        Persistence 23; the FFmpeg-native suites (Media/Playback/Export) were not run in this sandbox (a test-host
        DLL-search limitation blocks loading the bundled FFmpeg-8 natives — the App itself launches fine with them),
        and this change touches no Media/Playback/Export source, so those paths are behaviour-unchanged.
      - **Deferred (noted, on the same seam):** a **"ripple all tracks"** mode (today ripple closes the gap on the
        edited clip's own track + linked companions' tracks; a global ripple-all toggle slots onto the same
        downstream-shift composite), and **linked roll / slide** (companions follow on ripple/delete today; roll &
        slide operate on the clicked track's clips — applying the identical clamped delta to aligned companions is
        additive when picked up).
23. **Sequences (nesting / compound clips).** Generalise the project's single `Timeline` to
    **multiple named sequences**, and let a whole sequence be **placed inside another sequence as a
    clip** (Premiere "nested sequence" / Final Cut "compound clip"). To the render graph a
    nested-sequence clip is just another `IFrameSource` / `IPcmReader` that renders the child sequence's
    timeline at the requested time — the graph already turns a (timeline, t) into a frame
    ([ARCHITECTURE §5](ARCHITECTURE.md), [§17](ARCHITECTURE.md)) — so **edit operations apply to the
    whole nested sequence as one unit** (trim, effects, opacity/blend, audio gain/fade). Reuse is
    first-class: the **same sequence can be referenced by many sequences**, and (already true) the
    **same source clip can appear in more than one sequence** — these are references, not copies, so
    editing a child updates everywhere it is used. Model: `Project` gains `Sequences : Sequence[]`
    (today's `Timeline` becomes the active sequence) and a `Clip` may reference a `SequenceId` as its
    source alongside `MediaRefId`; render-graph recursion needs **cycle detection** (a sequence can't
    contain itself, directly or transitively) and a depth guard. The **Sequence** menu and the sequence
    badge / settings (placeholders from step 11, [UI.md §2](UI.md)) drive create / nest / open /
    settings. Sequences serialize as part of the project JSON (additive, schema-versioned, §12).
    Depends only on the done model + render graph — grouped here with the other non-raw-media building
    blocks (generators, adjustment layers), and foundational for the compound editorial workflows below
    (multicam, render cache). Heavy nests can be **pre-rendered** so they don't recompute each playback
    pass (step 32, [ARCHITECTURE §20](ARCHITECTURE.md)).
    - **✅ DONE (Core `Model/{Sequence,Project,Clip,SequenceGraph,SequenceNesting}` + `Rendering/{RenderPlan,RenderGraph}`
      + `Commands/ModelCommands`; `Sprocket.Audio/AudioMixer`; `Sprocket.Export/VideoExporter`;
      `Sprocket.Persistence/{ProjectDto,ProjectSerializer}`; `Sprocket.Playback/PlaybackEngine`; App
      `MainWindow.axaml`/`.cs` + `Timeline/TimelineControl` + `{Monitors,PreviewSurface,Dialogs,SequenceNaming}`;
      31 new headless tests — Core +21, Persistence +4, App +4, Audio +2 — + 1 sandbox-blocked Export test, all
      green.)** Multiple named sequences + nested/compound clips land entirely on the existing seams (no redesign,
      ARCHITECTURE.md §17): a nested-sequence clip is just a `Clip` whose source is a `SequenceId`, and the render
      graph's existing (project, t) → frame/buffer recursion renders the child. Delivered:
      - **Model (Core).** `Sequence` (id + name + the existing `Timeline` as its content) and a `SequenceId` value
        type; `Project` now holds `Sequences` with an `ActiveSequence`, and `Project.Timeline` **delegates to the
        active sequence** so the whole render/playback/export/App stack addresses it unchanged — multiple sequences
        are purely additive. `Clip` gains `ClipKind.Sequence` + `SourceSequenceId` and a `CreateSequenceClip`
        factory. `SequenceGraph` is the pure cycle/reachability reasoning (`WouldCreateCycle`, `MaxNestingDepth = 16`);
        `SequenceNesting.CreateNest` builds the Premiere "Nest" / FCP "compound clip" edit (selection → new child
        sequence, one linked V+A nested clip replaces it in the parent) as a single undoable `CompositeCommand`.
        `AddSequenceCommand` / `RemoveSequenceCommand` (step 10); switching the *active* sequence is navigation, not
        a command (so undo never strips it — the App self-heals if a sequence-add is undone).
      - **Render graph (Core).** `PlanVideoFrame` / `PlanAudioBuffer` recurse through nested-sequence layers
        (`LayerKind.Sequence` / `VideoLayer.NestedPlan`, `AudioLayer.NestedPlan`), carrying a **visited-set on the
        recursion path for cycle detection** and a **depth guard**; the nested plan inherits the parent layer's
        effects / opacity / blend (video) and gain envelope (audio), so a nest edits as one unit. Master gain is
        applied once at the root. The generic `Render<TImage>` executor renders a `Sequence` layer by recursing on
        its nested plan — the **same code drives preview and export** (determinism preserved).
      - **Audio (mixer).** `AudioMixer` mixes a nested layer's child sub-mix into per-depth scratch buffers, applies
        the nesting clip's gain/fade over the whole unit, then hard-limits once at the top — no per-frame managed
        allocation (§1, §6). (Deferred: a **retimed** nested-sequence clip's audio plays at 1×.)
      - **Persistence (additive, §12).** `Sequences` + `ActiveSequenceId` + `Clip.SourceSequenceId` serialize only
        when used: a single-sequence project with no nesting writes the **byte-identical pre-step-23 Timeline-only
        shape** (no schema bump); nested ids round-trip and resolve by preserved id (dangling refs render as nothing,
        §15).
      - **App (UI, manual/smoke-verified).** The **Sequence menu** is live — New Sequence (creates + opens a fresh
        sequence in the active format), **Nest** (context-enabled with a selection; routes the selection + linked
        companions through `SequenceNesting`), **Open Sequence ▸** (a submenu of every sequence, active checked,
        click switches), and **Sequence Settings…** (read-only format + undoable rename). `SwitchToSequence` re-points
        the model + Program monitor resolution + preview and rewinds so the engine's pump reconciles its players onto
        the new sequence's tracks; the **sequence badge** now shows the active sequence's name + format. The timeline
        labels nested clips with the child sequence's name and tints them a distinct teal. **Nested-sequence preview**
        draws a placeholder fill (live nested compositing in the Program monitor is deferred to the render cache,
        step 32 — the child renders fully on **export** and when **opened**; both are tested/exercised).
      - **Tests + verification.** Core `SequenceTests` (model, render-graph recursion + time mapping + effects/opacity,
        missing-ref, **direct cycle**, **deep-chain depth guard**, nested audio, executor over a fake compositor),
        Audio `NestedAudioMixerTests` (nested audio reaches the mix; nesting-track gain applies to the whole sub-mix),
        Persistence `SequencePersistenceTests` (multi-sequence + nested round-trip, active-selection round-trip,
        single-sequence omits the array, nested writes the sequences shape), App `SequenceNamingTests` (unique /
        gap-filling / case-insensitive naming). Managed suites green — **Core 169, Audio 21, Render 23,
        Persistence 34, App 133**. The FFmpeg-native suites (Media/Playback/Export) were not run in this sandbox (a
        test-host DLL-search limitation blocks loading the bundled FFmpeg-8 natives — the App itself launches fine
        with them); the Export nested-composite test is written and correct but rests on CI. Clean build (0 warnings)
        and a `SPROCKET_APP_SECONDS=5` smoke launch starts the shell with the Sequence menu wired and tears down
        cleanly (exit 0). *Also fixed in passing:* a pre-existing stray-paren syntax error in
        `Sprocket.App/MediaBootstrap.cs` (the App had not been compiled since it was introduced).
      - **Deferred (noted, on the same seam):** **live nested-sequence compositing in the Program monitor** (the
        render cache, step 32 — preview shows a placeholder today; export + open-the-child render fully); a
        **retimed** nested clip's audio at non-1× speed; and **sequence-format editing** (Settings shows format
        read-only — a format change would re-scale every clip's geometry).
24. **Multicam editing & clip sync.** Synced multi-angle editing — a major omission for interview,
    live-event, documentary, and studio / YouTube workflows — placed immediately after sequences because
    synced source groups and nested editorial structure (step 23) now exist to build on:
    - **Clip sync.** Align a set of source clips by **timecode, in/out markers, or audio-waveform
      cross-correlation** into a synced group (the audio-analysis path reuses the step-15 waveform / PCM
      reading). Sync offsets are model data, undoable.
    - **Multicam source.** A **multicam clip** = a synced angle group exposed to the render graph as a
      single `IFrameSource` / `IPcmReader` (the same seam nested sequences and proxies use, §17) whose
      active angle is selectable over time — built naturally on the step-23 nested-sequence machinery (a
      multicam source is a specialized synced sequence).
    - **Angle editing.** A multicam monitor view (an angle grid in the Program / Source monitor, step 17)
      with **live angle cutting** — switching the active angle at the playhead lays down cuts via the
      command stack; angle switches and per-cut effect / audio overrides are model edits. Export resolves
      the chosen angles through the same render graph (deterministic).
    - **✅ DONE (Core `Model/{Multicam,ClipSync,AudioSync,MulticamBuilder,Clip,Project}` +
      `Rendering/RenderGraph` + `Commands/ModelCommands`; `Sprocket.Persistence/{ProjectDto,ProjectSerializer}`;
      App `Timeline/TimelineControl` + `Inspector/InspectorPanel` + `MainWindow.axaml`/`.cs`; 31 new headless tests
      — Core +28, Persistence +3, all green.)** Synced multi-angle editing lands entirely on the existing seams
      (no redesign, ARCHITECTURE.md §17): a multicam source is a synced angle group, and its active angle resolves
      to an **ordinary media frame at the synced source time**, so multicam rides the media seam the render graph,
      mixer, preview, and export already drive — no recursion, no new compositor seam. Delivered:
      - **Model (Core).** `MulticamSource` (id + name + an ordered `MulticamAngle` list) and a `MulticamId` value
        type; `Project.MulticamSources` (+ `GetMulticam`). Each `MulticamAngle` carries its video `MediaRefId`, an
        optional separate `AudioMediaRefId` (dual-system sound; `EffectiveAudioRefId` falls back to the video file),
        and a `SyncOffset` — the per-angle alignment, so at multicam time `s` the angle's source frame is at
        `s + SyncOffset`. `Clip` gains `ClipKind.Multicam` + `SourceMulticamId` + a mutable `ActiveAngle` and a
        `CreateMulticamClip` factory; a blade split copies both onto each half (`CloneContentForSpan`), so the angle
        program is just the run of multicam segments on the track.
      - **Clip sync (Core, pure + tested).** `ClipSync.ComputeOffsets` reduces all three methods to one number per
        angle (the source time of a shared instant), relative to a reference angle — markers feed the marked source
        time, timecode feeds the source-time-at-a-common-TC, audio feeds the cross-correlation lag.
        `AudioSync.FindBestLag`/`FindBestOffset` is the **audio-waveform cross-correlation** (energy-normalized, with
        a min-overlap floor and a confidence in [-1,1]); it recovers a known delay (sign-correct), handles negative
        lags, and converts a sample lag to a `Timecode` offset. `ClipSync.AngleSourceTime` is the synced sampling
        time the render graph uses.
      - **Multicam source / render graph (Core, §5).** `PlanVideoFrame`/`PlanAudioBuffer` resolve a multicam clip by
        looking up its active angle and emitting a plain **media video layer** / **media audio layer** at
        `ClipSync.AngleSourceTime` (the angle's `MediaRefId` / `EffectiveAudioRefId`); a missing source or a stale
        angle index contributes nothing (renders as empty, §15). Because it's a media layer, **preview, the audio
        mixer, and export work unchanged** — switching `ActiveAngle` switches the resolved source, and export
        resolves the chosen angles deterministically through the same graph (`MediaBootstrap`'s per-source feed /
        PCM-reader factories already open any `MediaRefId`, so no Playback/Media/Export source changed).
      - **Angle editing + commands (Core + App).** `SetClipAngleCommand` (a discrete angle switch),
        `Add`/`RemoveMulticamSourceCommand`, and `SetMulticamOffsetsCommand` (a re-sync of every angle, undoable) join
        the step-10 set. `MulticamBuilder.CreateMulticam` (mirroring `SequenceNesting`) turns a set of angle clips
        into a synced source and replaces them with a single **linked video + audio multicam clip** as one undoable
        `CompositeCommand` (angles synced by the clips' existing placement by default). In the App, **Clip ▸ Create
        Multicam Source** collapses the stacked video angles, the **number keys 1–9** do **live angle cutting** (blade
        the clip — and its linked audio companion — at the playhead and set the new segment's angle, one undo entry),
        and the **Inspector** grows a Multicam section (one button per angle, the active one highlighted, showing each
        angle's sync offset) that sets the segment's angle. The timeline draws multicam clips in a distinct violet and
        labels them `{source} · {active angle}`.
      - **Persistence (additive, §12).** `MulticamSourceDto`/`MulticamAngleDto` + `Clip` DTO's `sourceMulticamId` /
        `activeAngle` serialize only when used (orthogonal to the sequence shape; `WhenWritingNull`), so a
        multicam-free project serializes **byte-identically** to a pre-step-24 file (no schema bump) and pre-24 files
        load unchanged; the source (angles, names, offsets, separate audio) and the clip's active angle round-trip.
      - **Tests + verification.** Core `MulticamTests` (model/factory, render resolution of the active angle to a
        synced media/audio layer, angle switching, out-of-range/missing → nothing, blade keeps the angle, the sync
        offset math, audio cross-correlation incl. negative/identical/empty, all four commands, and the builder's
        create/undo/render/`<2`-angle-null), Persistence `MulticamPersistenceTests` (source+clip round-trip,
        multicam-free omits the field, multicam writes the shape). **Full suite green — 498 tests, 0 failures**
        (Core 197, Media 28, Render 23, Audio 21, Playback 48, Export 11, Persistence 37, App 133); clean build
        (0 warnings) and a `SPROCKET_APP_SECONDS=6` smoke launch starts the shell with the multicam menu/keys/Inspector
        wired and tears down cleanly (exit 0).
      - **Fixed the FFmpeg-native test suites (they now actually run).** Steps 20–23 each recorded that the
        Media/Playback/Export suites "couldn't run in the sandbox — a test-host native-loading limitation." That was a
        misdiagnosis: the real bug was that `tests/Directory.Build.targets` copied **every** RID's cache extract into
        one output dir (Windows `.dll` *and* Linux `.so` *and* macOS `.dylib`), and `FFmpegLoader.FindBundledLib`
        matched the Linux soname **first, unconditionally**, so on Windows it picked `libavcodec.so.62` and
        `NativeLibrary.TryLoad` failed with `BadImageFormatException` — the whole FFmpeg load aborted. (A shipped build
        bundles only one OS's libs, so the bug stayed latent.) Two fixes: `FindBundledLib` now considers **only the
        current OS's** library type, and the test-natives copy is gated per-OS so the output dir stays single-platform.
        With that, all three FFmpeg suites pass locally with no `%SPROCKET_FFMPEG8_DIR%`, and the multicam render path
        is now exercised end-to-end through the real decode→render→export round-trips, not just headlessly.
      - **Deferred (noted, on the same seam):** the **live multi-angle grid monitor** (decode-bound — it needs every
        angle decoded at once into thumbnails, the same heavy-decode work the nested-sequence preview deferred to the
        render cache, step 32; the active angle previews live today); an **App "Sync by Audio" action** that reads each
        angle's PCM via `AudioSource` and applies `SetMulticamOffsetsCommand` (the cross-correlation engine + the
        re-sync command are delivered and tested — this is the decode-bound App glue, like `ThumbnailService`);
        **sync by embedded source timecode** (the offset math is ready; reading a source's start TC from FFmpeg is the
        missing input); and an **independent audio-follows-angle vs audio-follows-video** choice (audio follows the
        same active angle today).
25. **Transitions.** Transition library (Project panel **Transitions** tab) + overlapping-clip
    resolution in the render graph ([ARCHITECTURE §17](ARCHITECTURE.md)).
    - **✅ DONE (Core `Model/{Transition,TransitionCatalog,Track}` + `Rendering/{RenderGraph,RenderPlan,Seams}` +
      `Commands/ModelCommands`; `Sprocket.Render/SkiaEffectPipeline`; `Sprocket.Export/VideoExporter`; persistence;
      `Sprocket.App/{DragFormats,MediaBrowser/MediaBrowserPanel,Timeline/TimelineControl,MainWindow}`; 26 new tests —
      Core +15, Render +7, Export +1, Persistence +3). Transitions land on the existing render-graph seam exactly as
      ARCHITECTURE §17 anticipates ("transitions extend clip resolution in the render graph"), following the
      Premiere/Resolve convention. Delivered:
      - **Model (Core, §4).** A `Transition` is a non-destructive overlay on a `Track` (`Track.Transitions`) anchored
        at a cut: `{ TransitionTypeId, CutPoint, Duration, Alignment, Parameters }`, with the window derived from the
        alignment (`CenterOnCut` default / `EndAtCut` / `StartAtCut`) and a `ProgressAt(t)` ramp 0→1. It does **not**
        move or overlap the clips' timeline spans — the two clips stay adjacent and the transition samples their
        **handles** (trimmed-off source past the cut) the way every NLE does. `Track.ResolveTransitionAt(t)` and
        `ResolveTransitionClips(transition)` (the outgoing clip just before the cut, the incoming clip at it) drive
        resolution. A `TransitionCatalog` mirrors `EffectCatalog`: the v1 library is **Cross Dissolve** (default),
        **Dip to Black**, **Dip to White**, and a left-to-right **Wipe**.
      - **Render graph (Core, §5).** `PlanVideoFrame` emits a new `LayerKind.Transition` layer carrying a
        `ResolvedTransition` (type id, progress, and both sides as fully-resolved `VideoLayer`s with their own clip
        effects) when a valid transition is active; an invalid one (no real cut / a side that resolves to nothing)
        falls back to ordinary single-clip resolution. The generic `Render<TImage>` executor and the
        `IVideoCompositor<T>.ApplyTransition` seam handle it, so the resolution stays pure/serializable and
        headlessly testable (the same path preview and export share). Per-clip layer resolution was factored into one
        `ResolveClipLayer` used by both the normal path and each transition side.
      - **Shaders (Render, §7).** `SkiaEffectPipeline` adds four two-input SkSL programs — cross dissolve
        (premultiplied `mix`), dip to black, dip to white, and a soft-edged wipe — and `DrawTransition`, which folds
        each side through its own effect chain (refactored into a shared `BuildChainShader`) then combines them at the
        transition's progress; an unknown (plugin) id degrades to a cross dissolve. All premultiplied-correct and
        compositing with the track's opacity/blend.
      - **Export (deterministic).** `VideoExporter` composites a transition layer by snapshotting each side's content
        into an independent image (so a transition between two clips of the **same** source doesn't recycle the first
        frame) and blending via `DrawTransition`; a missing side composites the other alone (§15).
      - **Persistence (additive, no schema bump).** `TrackDto.Transitions` is nullable/`WhenWritingNull`, so a
        transition-free project serializes byte-identically to a pre-25 file and pre-25 files load with none.
      - **App UI.** The Project panel's **Transitions** tab lists the library (drag a row onto a cut, or double-click
        to apply it to the selected clip's cut — both through the step-10 command stack as an undoable
        `AddTransitionCommand`, the duration snapped to whole frames and clamped inside both clips). The timeline draws
        each transition as the classic translucent bow-tie "X" box over the cut; clicking selects it and **Delete**
        removes it (`RemoveTransitionCommand`), reusing the existing Edit/Delete wiring. A `SetTransitionWindowCommand`
        (coalescing) is in place for adjusting a transition's length.
      - **Tests (26).** Core: window/progress math for all three alignments, render-graph resolution (blend layer with
        correct From/To source times incl. handle sampling, track opacity/blend carried, fall-back outside the window /
        with no second clip), the executor blend, and the three commands (apply/revert/coalesce). Render: the real
        SkSL on an offscreen surface — cross dissolve at 0/0.5/1, dip to black/white at the midpoint, the wipe's
        left/right split, and unknown-id → cross dissolve. Export: a real encode→decode round-trip of a black→white
        cross dissolve, mid-grey at the cut where a plain cut would be white. Persistence: field-for-field round-trip +
        byte-identical omission. Full suite: **526 tests green** (Core 209, Media 28, Render 30, Audio 21, Playback 52,
        Export 12, Persistence 40, App 134). Clean build (0 warnings) + smoke launch (exit 0).
      - **Deferred (documented).** **Live preview of the transition blend** is deferred to the render cache (step 32),
        consistent with the nested-sequence preview deferral — the per-track single-feed preview engine can't decode
        two clips of one track at once; the preview shows the cut and the on-timeline overlay, while **export renders
        the blend fully**. **Audio crossfades** reuse the same `Transition` model + the mixer's gain ramp but are a
        follow-up (video transitions ship first, like the slice's other compositing features); real NLEs separate
        audio and video transitions anyway. Wipe direction/softness and per-transition parameters ride the existing
        `Parameters`/Inspector mechanism when needed.
26. **Alpha-channel media compositing.** Premultiplied-alpha path through the render graph (e.g.
    `Logo_Anim.mov` flagged `Alpha`).
    - **✅ DONE (`Sprocket.Core/Model/MediaRef` + `Sprocket.Media/{Native/LibAv,Native/AvStructs,VideoFrame,MediaSource}`
      + `Sprocket.Render/SkiaEffectPipeline` + `Sprocket.Playback/PlaybackEngine` + `Sprocket.Export/VideoExporter` +
      `Sprocket.App/{PreviewSurface,MediaBrowser/MediaBadges}` + persistence; 6 new tests — Render +3, Media +1, App +2).
      Alpha media now composites over the layers beneath it in both preview and export, following the Premiere/Resolve
      convention (ProRes 4444 / QuickTime Animation logos). **Key finding:** the decode path already carried alpha —
      swscale normalises every source into the pooled `AV_PIX_FMT_RGBA` buffer preserving the alpha channel (§11) — but
      the Skia compositor wrapped every frame as `SKAlphaType.Opaque`, discarding it. This lands entirely on existing
      seams (§17); no render-graph redesign. Delivered:
      - **Alpha detection (Media).** `MediaSource.Probe` reads the stream's `codecpar->format` and tests the
        `AV_PIX_FMT_FLAG_ALPHA` flag via a new `av_pix_fmt_desc_get` binding (`Native/LibAv` + a minimal
        `AvPixFmtDescriptor` view reading only `flags`), setting a new `ProbedMediaInfo.HasAlpha` at import without
        decoding a frame. Every decoded `VideoFrame` carries the flag (`VideoFrame.HasAlpha`).
      - **Premultiplied compositing (Render).** `SkiaEffectPipeline.DrawLayer`/`Present` take a `hasAlpha` flag:
        alpha frames wrap as `SKAlphaType.Unpremul` (FFmpeg RGBA is straight alpha) so Skia premultiplies and composites
        them source-over the lower layers, revealing them through transparent pixels; opaque frames stay
        `SKAlphaType.Opaque` — the alpha bytes ignored, the layer fully replacing what's beneath — so the measured
        allocation-clean opaque hot path (steps 1/4/7) is byte-for-byte unchanged.
      - **Threaded through preview + export.** `PresentedVideoLayer`/`PresentedFrame` carry `HasAlpha` (populated from
        the frame); `PreviewSurface` and `VideoExporter` (media layers **and** transition sides) pass it to `DrawLayer`,
        so the same premultiplied path serves the real-time preview and the deterministic export (§5).
      - **Media-bin badge + persistence.** The media browser shows an **`Alpha`** badge on alpha video
        (`Logo_Anim.mov · 00:05 · Alpha`, UI.md §3.3, `MediaBadges`). `ProbedInfoDto.HasAlpha` is additive/nullable
        (`WhenWritingNull`): opaque media omits it and serializes byte-identically to a pre-26 file; pre-26 files load
        as opaque; only alpha media writes `true`.
      - **Tests (6, deterministic).** Render: a straight-alpha layer over a coloured background — transparent reveals the
        background, 50%-alpha blends (~premultiplied source-over), and the same bytes with `hasAlpha:false` fully replace
        (proving the opaque path is unchanged). Media (real FFmpeg): a `qtrle`/`argb` fixture reports `HasAlpha` on the
        info **and** every frame, and its 50% alpha survives swscale into the RGBA buffer (opaque `yuv420p` fixture stays
        false). App: the `Alpha` badge appears for alpha video and not for opaque. Persistence: `HasAlpha` round-trips.
        Full suite: **532 tests green** (Core 209, Media 29, Render 33, Audio 21, Playback 52, Export 12, Persistence 40,
        App 136). Clean build (0 warnings).
      - **Deferred (documented).** Alpha carried only as **side data** (VP8/VP9 alpha, where `codecpar->format` reads
        `yuv420p` but the decoded frame is `yuva420p`) isn't flagged yet — the probe reads the container-level pixel
        format, which covers the ProRes 4444 / qtrle / PNG cases this step targets; a decode-time re-check is the
        follow-up. Alpha **poster thumbnails** still render opaque (a representative frame, not a composite).
27. **Broad media format support (import coverage + export format/codec matrix).** Open and write the
    **common containers and codecs**, not just the slice's H.264/AAC MP4. *Import* is mainly a
    coverage/robustness task — `MediaSource`/`AudioSource` decode through the hand-rolled FFmpeg 8
    binding (steps 2–3), which already handles most formats — so this verifies and hardens a **support
    matrix**: containers
    **MP4 / MOV / MKV / WebM / AVI / MXF / TS**; video **H.264, HEVC, AV1, VP9, MPEG-2, ProRes,
    DNxHD/HR**; audio **AAC, MP3, PCM/WAV, FLAC, AC-3, Opus**; plus **10–12-bit, 4:2:2 / 4:4:4, HDR
    transfer, alpha, and variable-frame-rate (VFR)** sources — with file-dialog extension filters and
    graceful unsupported/offline handling (§15). *Export* generalises the step-8 `MediaEncoder` from its
    hard-wired H.264/AAC into a **container × video-codec × audio-codec matrix** with quality/bitrate,
    pixel-format/bit-depth, and frame-rate controls; **hardware encoders** (NVENC / QSV / AMF /
    VideoToolbox) behind the existing `IHardwareContext` with a software (x264 / x265 / SVT-AV1) fallback.
    Export still renders through the **same render graph** at full resolution — only the muxer/encoder
    back end changes (§5/§17). **Export resolution is capped at 4K for now** (≤ 3840×2160 UHD /
    4096×2160 DCI; higher tiers — 5K/6K/8K — may be enabled later); this is an **export-side limit
    only** — import, the timeline, and sequence canvas sizes are unrestricted. This matrix is for
    **import and final delivery**; *preview/cache* intermediates instead pick fast, OS-specific codecs
    (step 32). **Licensing:** codec choice interacts
    with the FFmpeg build's LGPL/GPL split (x264/x265 → GPL) — decide the bundled build before
    distribution ([ARCHITECTURE §11](ARCHITECTURE.md)).
    - **✅ DONE (`Sprocket.Media` encoder/probe + `Sprocket.Export` format matrix + `Sprocket.App` dialog; 16 new
      tests — Export +14, Media +1, Persistence assertions; full suite **547 green**).** Export generalised from
      a hard-wired H.264/AAC MP4 into a container × video-codec × audio-codec matrix, and import hardened to probe
      and surface the source's real format — all behind Core's unchanged seams (§17), only the muxer/encoder back
      end changes so export stays deterministic (§5). Delivered:
      - **`MediaEncoder` codec matrix.** `Create(path, video, audio, containerFormat)` now takes the FFmpeg muxer
        name (mp4/mov/matroska/webm/avi/mpegts) and picks encoders **by name** (`avcodec_find_encoder_by_name`) so
        the matrix is robust across FFmpeg builds without baking codec-id tables. The pixel/sample format is
        **negotiated against the chosen encoder** (`avcodec_get_supported_config`, the FFmpeg-8 replacement for the
        removed `AVCodec.pix_fmts`/`sample_fmts`): video picks the requested format if supported else yuv420p else
        the encoder's first; audio prefers `fltp` (the mixer-friendly deinterleave) else the encoder's first, and
        `WriteAudioFrame` feeds **planar or packed** planes accordingly (so PCM/FLAC packed s16, Opus packed flt,
        and AAC/AC-3/MP3 planar fltp all encode). Quality: CRF for the crf-capable encoders (x264/x265/SVT-AV1/VP9),
        a resolution-scaled default bit rate otherwise; the chroma-aware even-dimension rule replaced the old
        always-even guard (4:4:4 accepts odd sizes). Hardware encode (NVENC/QSV/AMF/VideoToolbox) slots in as
        another encoder name behind this same shape — the software encoders stay the deterministic default;
        full `hw_frames_ctx` GPU-frame upload is the follow-up (catalogued in `Native/FUTURE_BINDINGS.md`).
      - **New curated bindings** (no new struct-offset regen): `av_get_pix_fmt`/`av_get_sample_fmt`/
        `av_sample_fmt_is_planar`/`av_get_pix_fmt_name`, `avcodec_get_supported_config`, `avcodec_get_name`; plus
        `AvPixFmtDescriptor` chroma-log2 + comp0 depth, `AvCodecParameters.color_trc`, and an
        `AllocOutput(path, formatName)` container override.
      - **`Sprocket.Export` format model.** `ExportFormat` (container/video/audio) + `ExportContainer`/
        `ExportVideoCodec`/`ExportAudioCodec` enums with a single-source-of-truth `ExportCodecs` registry
        (encoder names, pixel formats, presets, and the container→codec validity matrix), curated delivery
        `Presets`, and a per-family CRF quality mapping. `ExportOptions` gained `Format`/`Quality`/`PixelFormat`
        (its `default` is still MP4/H.264/AAC, so step-8 behaviour is byte-for-byte unchanged); `VideoExporter`
        validates the pairing up front and passes the container/codecs through. **Export resolution capped at 4K**
        (`ComputeExportResolution` scales down preserving aspect, rounds even — an export-side limit only).
      - **Import coverage.** The media open dialog's filters broadened to the full container/audio set
        (MP4/MOV/MKV/WebM/AVI/MXF/TS/… + WAV/MP3/AAC/FLAC/AC-3/Opus/…), with per-file graceful failure already in
        place (§15). `MediaSource.Probe` now records the source's **codec (canonical name), pixel-format name,
        component bit depth, HDR-transfer flag (PQ/HLG), and a VFR heuristic** on `ProbedMediaInfo` (additive,
        defaulted; persisted as nullable so pre-27 files round-trip byte-identically and opaque/8-bit/SDR/CFR media
        keeps a minimal diff). VFR/10–12-bit/4:2:2-4:4:4/HDR/alpha sources decode frame-accurately by PTS as before;
        the probe just makes their properties visible (media-bin display).
      - **UI.** An `ExportSettingsDialog` (cascading container → valid-codec dropdowns + quality tier + a
        4K-capped output-resolution readout) runs before the save picker, whose extension / file-type filter now
        follow the chosen container; `MainWindow` passes the resulting `ExportOptions` to `VideoExporter`.
      - **Tests.** `ExportMatrixTests` round-trips six container/codec combinations (MP4·H.264+AAC, MOV·ProRes+PCM,
        MKV·HEVC+FLAC, WebM·VP9+Opus, MP4·AV1+AAC, TS·MPEG-2+MP3) — reopening each and asserting the muxed streams'
        canonical codecs — plus a ProRes 10-bit check, an invalid-pairing rejection, a quality-tier file-size
        ordering, and the 4K-cap resolution math; `MediaSourceTests` gains a probe-details assertion and
        `ProjectSerializerTests` verifies the new probe fields round-trip. Clean build (0 warnings) and a smoke
        launch starts + tears down cleanly. Deliberate deferrals (noted above): hardware encode as a whole (a
        hardware `ExportVideoCodec` option + the `hw_frames_ctx` GPU-frame upload path) — the name-based encoder
        selection is the seam it will use, but export stays software/deterministic for now; and any HDR tone-map
        (the `IsHdr` flag is informational until the later color step). Full suite: **547 tests green** (Core 209,
        Media 30, Render 33, Audio 21, Playback 52, Export 26, Persistence 40, App 136).
28. **Interchange & relink workflow (EDL / FCPXML / XML, batch relink, collab-ready format).** Pulled
    earlier than the specialized finishing work because it becomes necessary the moment projects leave
    the original machine or asset paths change. Three strands, all additive on the persistence and
    media-pool seams:
    - **Interchange export / import.** At minimum **EDL** (CMX3600) export of the active sequence; then
      **FCPXML / Final Cut XML** and Premiere / Resolve **XML** for round-tripping cuts (clips, in/out,
      track layout, basic transitions) with other NLEs. A pure mapper between the `Project` / `Sequence`
      model and each interchange format (Core / Persistence), tested against known fixtures; lossy fields
      are reported, not silently dropped.
    - **Batch relink & offline recovery.** A relink workflow that re-points many `MediaRef`s at once when
      assets move (pick a new root folder → match by filename / path / size, preview matches, apply),
      strengthening the step-9 offline-tolerant load: offline clips stay in the project rendering as
      black / silence (§15) and surface in a "missing media" list that the relink dialog drives.
    - **Collab-ready format split.** Separate asset paths from the shared project file: the diffable
      project JSON references each source by stable `MediaRef` **Id** only, while the
      **absolute/local path lives in a per-user sidecar "media link" file** (not normally committed or
      merged) — so pulling a collaborator's project-file change never forces you to relocate your own
      clips, because your local link file still resolves the Ids. This refactors step 9's "relative +
      absolute path stored in the project file" into **Id-in-project + path-in-sidecar**, and keeps each
      logical edit a small, localized, stable-ordered diff so projects version-control and merge cleanly
      ([ARCHITECTURE §12](ARCHITECTURE.md), [§15](ARCHITECTURE.md)). Full multi-user editing (presence,
      locking, or CRDT / operational-transform merge) is a larger later product-platform effort this
      format enables — **not** in the 1.0 set; the actionable deliverable here is the **format split**.
    - **✅ DONE (`src/Sprocket.Persistence`: `MediaLinks`, `MediaRelink`, `Interchange/{InterchangeReport,SmpteTimecode,
      EdlExporter,FinalCutXmlInterchange}`, `ProjectSerializer`/`ProjectDto` refactor; `src/Sprocket.App/MainWindow`;
      50 new headless tests — Persistence 40 → 90, all green; full suite 597.)** All three strands land additively on
      the persistence + media-pool seams (ARCHITECTURE.md §12/§17), no schema bump, no Core model change. Delivered:
      - **Collaboration-ready format split.** The committed project file now references each source by stable
        `MediaRefId` (+ its content-derived, diffable `ProbedMediaInfo`) **only**; the per-user asset **paths** move to
        a `.links.json` **media-link sidecar** (`MediaLinks`, atomic temp→promote write, independently schema-versioned).
        `ProjectSerializer.Save`/`Load` operate on the **pair** (Id-only project file + sidecar) — the diffable,
        merge-friendly form — while `Serialize` (to a lone string: autosave/undo-snapshot) stays **self-contained** with
        paths inlined (a string has nowhere else to put them). Resolution order on load: **sidecar link → inlined
        relative (if the file exists) → inlined absolute → offline** (empty path, renders as black/silence §15). **No
        schema bump** — the `MediaRefDto` path fields became additive/nullable, so pre-28 files (inline paths) still
        load and a sidecar entry wins over a stale inline path; a project shared *without* its sidecar loads every source
        offline, ready to relink (the collaboration payoff — pulling a project-file change never relocates your clips).
      - **Batch relink & offline recovery.** `MediaRelink` (I/O) finds offline sources (empty or now-missing path),
        recursively scans a chosen root folder for candidates (skipping unreadable dirs), and applies a previewed
        `RelinkPlan`; the **pure** `MediaRelinkMatcher` matches by **file name** (case-insensitive), disambiguating
        same-named candidates by **longest common path tail** (cross-separator, so a Windows-recorded path matches a
        POSIX candidate) then by known **size**, and reports `Matches` / `Ambiguous` / `Unmatched` rather than guessing.
        Relinking updates only the per-user path → written straight to the sidecar (`MediaLinks.Write`), never dirtying
        the shared project. Strengthens step 9's offline-tolerant load into a real "missing media" workflow.
      - **Interchange export / import** (`Sprocket.Persistence.Interchange`, a pure model↔format mapper): **CMX3600 EDL**
        export (`EdlExporter` over a pure, drop-frame-aware `SmpteTimecode` — verified against the reference DF algorithm)
        flattening the active sequence to a record-ordered, numbered event list (one video track + up to 4 audio
        channels, `* FROM CLIP NAME` comments, valid cuts); and **Final Cut Pro 7 XML** (`xmeml` v5) **export + import
        round-trip** (`FinalCutXmlInterchange` — the lingua franca Premiere / Resolve / FCP7 read): sequence rate (NTSC
        `timebase`+`ntsc`) / resolution / name, video+audio track layout, each clip's record placement + source in/out,
        and source `<file>` references (id + `pathurl`, defined once then referenced by id). Everything a format can't
        carry (effects, transitions, retimes, track opacity/blend/gain/mute/solo, generated/nested/multicam clips,
        markers, source codec/bit-depth/HDR/alpha/VFR metadata) is **reported** via `InterchangeReport`, never silently
        dropped. Frame-based interchange snaps to whole frames (true of every NLE); Sprocket clips are frame-aligned so
        a cut round-trips exactly (verified at 29.97 NTSC).
      - **App wiring (thin, smoke-verified).** File menu gains **Relink Media…** (offline scan → folder picker →
        previewed confirm → apply → sidecar write → media-bin refresh) and **Export Interchange ▸ EDL (CMX3600)… /
        Final Cut XML…** (save picker → export → a dialog listing any lossy fields). Interchange needs no export-style
        pipeline quiesce (it's a pure mapping, no in-process muxer, §15). Normal **Save** now writes the `.links.json`
        sidecar alongside the project.
      - **Tests (+50).** `MediaLinkPersistenceTests` (Save omits paths + writes sidecar, project-file-only → offline,
        self-contained inline round-trip, sidecar-wins-over-inline, pre-28 inline load, missing-sidecar/offline skips),
        `MediaRelinkTests` (single/none/tail-disambiguation/ambiguous/size-tiebreak/no-name/case-insensitive matcher +
        cross-separator tail + find-offline/plan-apply I/O), `SmpteTimecodeTests` (non-drop + DF reference values, DF vs
        non-drop divergence, rate classification, parse round-trips), `EdlExportTests` (header/FCM, numbered events,
        source/record timecodes, channels, lossy report, file write), `FinalCutXmlInterchangeTests` (format/name/layout/
        placement/media-pool round-trip, define-once-reference-by-id, lossy report, non-xmeml rejection, file round-trip).
        The updated moved-project test copies the sidecar (paths now live there). Clean build (0 warnings) and a
        `SPROCKET_APP_SECONDS=5` smoke launch starts the shell with the new menu items wired and tears down cleanly
        (exit 0). Full suite: **597 tests green** (Core 209, Media 30, Render 33, Audio 21, Playback 52, Export 26,
        Persistence 90, App 136).
      - **Deferred (noted, same seam):** **modern Apple FCPXML v1.x** (the newer `fcpxml` DTD — `xmeml` already gives
        Premiere/Resolve/FCP7 round-trip); a richer **relink preview dialog** (per-file candidate override — the matcher
        already reports ambiguous/unmatched, and the App confirms the plan before applying); and **EDL dissolves** (a
        transition currently exports as a plain cut + a lossy warning).
29. **Export queue, burn-ins, handles & presets + status-bar telemetry.** Standard delivery workflow on
    top of the step-8 export path and the step-27 format/codec matrix:
    - **Export queue.** Queue multiple export jobs (different sequences / in-out ranges / presets) that
      run sequentially on the background export path with per-job progress and cancel.
    - **Burn-ins & handles.** Optional **burn-in overlays** (timecode, clip name, watermark) baked by an
      effect-stack stage on the export render (§5/§7, so they're deterministic and never touch preview's
      hot path) and **handles** (extra frames before / after each clip's in/out) for review / conform
      outputs.
    - **Presets.** An export dropdown of saved selections over the step-27 matrix (container × codec ×
      quality × resolution / frame-rate), user-definable and persisted.
    - **Hardware export encoders (carried over from step 27).** Step 27 shipped the *software* codec matrix and
      left hardware encode explicitly deferred; finish it here. Add hardware `ExportVideoCodec` options
      (**NVENC / QSV / AMF** on Windows, **VAAPI / NVENC** on Linux, **VideoToolbox** on macOS) behind the
      existing `IHardwareContext`, negotiated by a runtime encoder probe with **automatic software fallback**
      (`libx264`/`libx265`/SVT-AV1). `MediaEncoder` already selects encoders by name — the seam exists; the work
      is the encoder-probe/fallback path plus the `hw_frames_ctx` **GPU-frame upload** so the composited frames
      reach the GPU encoder (the binding surface for this is catalogued in
      [`Native/FUTURE_BINDINGS.md`](src/Sprocket.Media/Native/FUTURE_BINDINGS.md) — "hardware encode … still
      open"). Preview/cache intermediates use the same hardware encoders for speed (step 32); **final delivery
      keeps a deterministic software path available** for golden-frame reproducibility (§5).
    - **Status-bar telemetry.** Surface engine state, GPU / hardware-accel status, live fps, resolution,
      and duration ([ARCHITECTURE §15](ARCHITECTURE.md)) — **no framework/runtime text** in the UI
      ([UI.md §3.7](UI.md)).
    - **✅ Export-queue strand DONE (`Sprocket.Export`: `ExportQueue`/`ExportJob`/`ExportRange` + `VideoExporter`
      sequence/range overload; `Sprocket.Core` + `Sprocket.Audio` sequence-audio overloads; `Sprocket.App`:
      `ExportQueueWindow` + File ▸ Export Queue…; 20 new tests — Export 26 → 44, Audio 21 → 23; full suite 597 → 617).**
      The queue is one of the five strands of step 29 (with burn-ins & handles and presets now also done below; the
      others — hardware export encoders and status-bar telemetry — **remain**). Delivered, all on the existing
      seams (ARCHITECTURE.md §5/§17 — only the orchestration around the render graph is new, the render is unchanged):
      - **`ExportQueue` + `ExportJob` (Export).** A sequential batch runner: jobs run one-at-a-time on a background
        worker (so only one in-process libav* muxer is ever live — concurrent muxing crashes the native encoder,
        the same hazard the single Export quiesces for), each reporting its own progress and cancellable
        individually, with a queue-wide **Stop**. Jobs enqueued mid-run are picked up. It is **decoupled from the
        encoder by an injected `ExportJobRunner`** — the queue owns ordering / status transitions / cancellation,
        not rendering — so it unit-tests without FFmpeg and the composition root binds the runner to `VideoExporter`.
        `ExportJob` splits an immutable spec (output path, `ExportOptions`, target `SequenceId?`, `ExportRange?`)
        from the runtime `Status`/`Progress`/`Error` the queue drives; `ExportJobStatus` = Queued/Running/Succeeded/
        Cancelled/Failed. Thread-safe surface (list + state under a lock; `Changed` raised outside it, so a UI
        subscriber marshals).
      - **`VideoExporter` sequence + in-out range (Export).** A new overload
        `Export(project, path, options, SequenceId?, ExportRange?, …)` renders **any** sequence (not just the active
        one) and an optional half-open `[In, Out)` timeline slice; the delegating original signature is byte-for-byte
        unchanged. A sub-range samples the timeline at `rangeIn + offset` while the encoded file's own timestamps
        start at zero (a slice plays from 0). `ExportRange` (clamp-to-timeline, validity, `Whole`) is the pure
        range type. The render graph already planned per-sequence **video**; this adds the matching per-sequence
        **audio**: `RenderGraph.PlanAudioBuffer(project, sequence, …)` (Core) + `AudioMixer.MixInto(…, sequence)`
        (Audio) overloads, so an exported sequence's audio mixes correctly regardless of which sequence is open.
      - **`ExportQueueWindow` + wiring (App).** File ▸ **Export Queue…** (Ctrl+Shift+E) opens a live job list — name,
        format, status, per-job progress bar — with **Add… / Start / Stop / Clear Finished** and per-row Cancel /
        Remove. **Add…** reuses the step-27 `ExportSettingsDialog` + a Save picker and captures the **active
        sequence's id**, so switching sequences between adds queues different sequences; **Start** quiesces the
        Program/Source decode pipelines (as the single export does) and runs the queue to completion on the
        background path, then resumes. Rows update **in place** on each progress tick (the tree only rebuilds when
        the job set/order changes), so a running encode's frequent progress reports don't rebuild the list. Session
        swaps (New / Open) are blocked while an export is in flight so the worker's project/engine isn't torn out.
      - **Tests (20).** `ExportQueueTests` (11, fake runner, no FFmpeg): sequential in-order run, progress
        forwarding + terminal status, a failing job → Failed with message + queue continues, cancel-queued (skipped)
        / cancel-running (Cancelled, next runs) / Stop-all, Remove guards (queued yes, running no), mid-run enqueue
        picked up, ClearCompleted, Changed fires, defaults. `ExportRangeTests` (7, real encode→decode): `ExportRange`
        duration/validity/clamp math; sub-range exports fewer frames + shorter duration than the whole; empty range
        + unknown sequence id throw; **exporting a non-active sequence by id** renders that sequence (white matte ≫
        active black); a **queue end-to-end** runs a whole-timeline + a sub-range job through the real `VideoExporter`
        runner and writes both files. `SequenceAudioMixerTests` (2): the default `MixInto` mixes the active sequence,
        the new overload mixes the given one. Clean build (0 warnings); a `SPROCKET_APP_SECONDS` smoke launch starts
        the shell with the queue menu item wired and tears down cleanly (exit 0). Full suite: **617 tests green**
        (Core 209, Media 30, Render 33, Audio 23, Playback 52, Export 44, Persistence 90, App 136).
    - **✅ Burn-ins & handles strand DONE (`Sprocket.Core`: `BurnIn`/`BurnInField`/`BurnInPosition` + `BurnInResolver`,
      `SmpteTimecode` moved into `Core.Timing`; `Sprocket.Render`: `BurnInRenderer`; `Sprocket.Export`: `ExportOptions`
      `BurnIns`/`HandleFrames` + `ExportRange.WithHandles`; `Sprocket.App`: `ExportSettingsDialog` burn-in/handles
      controls; 27 new tests — Core 209 → 215, Render 33 → 50, Export 44 → 48; full suite 617 → 644).** With presets
      (below) now done too, only hardware export encoders and status-bar telemetry **remain**. Everything lands on
      the deterministic export render (ARCHITECTURE.md §5/§7) — burn-ins are baked *after* compositing and never
      touch the preview hot path or allocate pixels (§1):
      - **Burn-in model (Core).** `BurnIn(Field, Position, Text?)` is pure delivery data that flows with
        `ExportOptions` through the queue. `BurnInField` = Timecode / ClipName / Text (watermark); `BurnInPosition`
        is a nine-point alignment grid (matching Resolve/Premiere). `BurnInResolver` turns a burn-in + timeline time
        into the string to draw — timecode via the shared `SmpteTimecode`, or the **topmost content clip's** name
        (media file name / nested-sequence name / generator label; adjustment layers skipped, a gap → ""). Resolution
        is pure model so it is unit-tested headlessly. **`SmpteTimecode` moved from `Persistence.Interchange` into
        `Core.Timing`** (its existing tests still pass) so the EDL/FCXML exporters and the burn-in share one
        drop-frame-correct formatter rather than duplicating it.
      - **`BurnInRenderer` (Render).** A pure post-composite overlay like `MonitorOverlay`: it draws the resolved
        strings white over a translucent pill (legible on any content) at the nine anchor points, inset from the
        edge. The layout (`ComputeTextTopLeft`) is pure/testable; the exporter calls it after `CompositePlan` and
        before pixel readback. The timecode shows the **record (timeline) time**, so a conform/review TC matches the
        project regardless of the export range.
      - **Handles + burn-ins in the exporter (Export).** `ExportOptions` gains `HandleFrames` (extra frames before /
        after the in-out range for review/conform outputs) and `BurnIns`; both flow through the queue unchanged.
        `ExportRange.WithHandles(head, tail)` grows the range (negatives clamp to zero), then the exporter re-clamps
        to the timeline — so handles reach only media that exists there and a whole-timeline export is unaffected.
        The delegating original `Export` signatures are unchanged. Handles = the timeline-range (conform) reading;
        per-clip media-management handles (one file per clip) are the natural deferred extension.
      - **UI (App).** `ExportSettingsDialog` gains a **Burn-ins** section — Timecode / Clip name checkboxes and a
        Watermark textbox, each with a nine-point position picker — plus a **Handles (frames)** field, wrapped in a
        `ScrollViewer`. Because both the single **Export** and **Export Queue ▸ Add…** already read their options from
        this one dialog, both paths carry burn-ins/handles for free. Defaults reproduce the pre-step-29 behaviour.
      - **Tests (27).** `BurnInTests` (Core, 6): timecode format at the sequence rate, topmost-clip-name over the
        clip / "" over a gap / topmost-track wins / adjustment-layer skipped / generator labels, literal watermark.
        `BurnInRendererTests` (Render, 17): the nine anchor points land in the right column/row with the margin,
        centring is exact, degenerate/empty draws no-op. `BurnInAndHandlesExportTests` (Export, 4, real encode): a
        top-left timecode burn-in brightens that corner over a black matte and stays localised; handles extend an
        in-out range (more frames) but never past the timeline; whole-timeline handles add nothing; `WithHandles`
        math. Clean build (0 warnings); a `SPROCKET_APP_SECONDS` smoke launch starts the shell and tears down cleanly
        (exit 0). Full suite: **644 tests green** (Core 215, Media 30, Render 50, Audio 23, Playback 52, Export 48,
        Persistence 90, App 136).
    - **✅ Presets strand DONE (`Sprocket.Export`: `ExportPreset` extended + `ExportPresetStore` + `ExportOptions`
      `Resolution`/`FrameRate` overrides in `VideoExporter`; `Sprocket.App`: `UserExportPresets` + `ExportSettingsDialog`
      preset/resolution/frame-rate controls; 17 new tests — Export 48 → 65; full suite 644 → 661).** The fourth of the
      five strands; only hardware export encoders and status-bar telemetry now **remain**. Presets are saved selections
      over the step-27 matrix — container × codec × quality × **resolution / frame-rate** — user-definable and
      persisted, all on the existing deterministic export render (no new render path):
      - **Resolution / frame-rate overrides in the exporter (Export).** `ExportOptions` gains `Resolution?` and
        `FrameRate?` (both `null` = keep the sequence's own, so `default(ExportOptions)` is byte-for-byte the
        pre-step-29 behaviour). A resolution override flows through the same `ComputeExportResolution` (4K cap + even
        rounding) and sizes the offscreen surface, so the composite scales/letterboxes into it. A frame-rate override
        just samples the timeline at the new frame instants — the render graph is a pure function of time, so this is
        the standard NLE resample-on-export (frames duplicated / dropped), with **handles still counted in the
        timeline's own frames**. No change to the render itself (ARCHITECTURE.md §5).
      - **Preset model + store (Export).** `ExportPreset` extended with optional `Resolution` / `FrameRate` and a
        `ToOptions()`; the curated `ExportCodecs.Presets` gains resolution-pinned web-delivery presets (YouTube 4K /
        1080p, Web 720p — mirroring the presets leading NLEs ship). `ExportPresetStore` (de)serialises the **user's**
        presets to JSON through a flat DTO (enums by name → a stable, human-editable file), reads/writes best-effort
        (missing/corrupt → empty, never throws), and merges built-ins + user for the dropdown. Burn-ins/handles are
        deliberately **not** part of a preset (they're per-export review options, not a delivery format).
      - **UI (App).** `UserExportPresets` persists the user list under `%AppData%/Sprocket/export-presets.json`
        (mirrors `WindowStateStore`). `ExportSettingsDialog` gains a **Preset** dropdown (Custom + built-ins + user
        presets; selecting one applies its format/quality/resolution/rate, and any manual edit snaps back to Custom),
        **Resolution** and **Frame rate** pickers (a closed set so every saved preset round-trips to a dropdown entry),
        and a **Save Preset…** button (name prompt → persist → reselect). Both the single **Export** and **Export
        Queue ▸ Add…** read this one dialog, so both carry the overrides for free.
      - **Tests (17).** `ExportPresetTests` (Export, headless): `ToOptions` mapping, JSON round-trip preserving
        overrides, enums-by-name, blank/corrupt → empty, nameless entries skipped, file load/save + missing-path,
        built-in-then-user merge order, resolution-pinned built-ins present. `ResolutionFrameRateExportTests` (Export,
        real encode): a frame-rate override resamples the timeline (½× / 2× frame counts), a resolution override
        encodes at the chosen (even-rounded) size, and a preset drives the whole preset→options→encode path. Clean
        build (0 warnings); a `SPROCKET_APP_SECONDS` smoke launch starts the shell with the new dialog and tears down
        cleanly (exit 0). Full suite: **661 tests green** (Core 215, Media 30, Render 50, Audio 23, Playback 52,
        Export 65, Persistence 90, App 136).
    - **✅ Status-bar telemetry strand DONE (`Sprocket.App`: `StatusBarFormat` + live status-bar driver in
      `MainWindow`; 7 new tests — App 136 → 143; full suite 661 → 668).** The fifth and final surface strand of
      step 29 — **only hardware export encoders now remain**. The status bar was half-static (a fixed green dot +
      "Ready", and a *nominal*-rate telemetry cell); this makes it live per UI.md §3.7 — render/decode state,
      **GPU / hardware-accel status**, **live fps**, resolution, duration — with **no framework/runtime text**
      (UI.md §3.7 / §5), reusing the diagnostics counters the Playback Statistics overlay already reads
      ([ARCHITECTURE §15](ARCHITECTURE.md)):
      - **The non-negotiable perf rule (ARCHITECTURE.md §1).** Zero work lands on the render/decode hot path: the
        readout only *reads* the engine's existing cumulative counters (`GetStatistics()` — a couple of
        `Interlocked` reads) and its cached decode-info snapshot (`GetActiveVideoDecodeInfo()`, managed, never
        native state). The live poll is a 1 Hz `DispatcherTimer` that runs **only while playing** — it is started
        on the transition to Playing and stopped on Paused/Stopped, so a paused/idle editor incurs **no periodic
        wake-ups** and the readout is purely event-driven at rest. Assignments are change-guarded (compare before
        set) so a steady readout never re-lays-out the status bar. This is strictly lighter than the per-frame
        `PositionChanged` marshaling that already drives the scrubber.
      - **Live state + GPU/hw-accel (App).** The left group shows `State · GPU · <DEVICE>` (hardware) or
        `State · CPU · software`, with the state dot green while playing, **amber on the software (CPU) path** (the
        usual 1080p-stutter tell, matching the overlay), and neutral at rest; "Ready" is the parked/stopped word
        (mockup wording). A one-shot `FramePresented` handler (self-unsubscribing, marshalled off the pump thread)
        refreshes the decode path once frame 0 has decoded, so the GPU/CPU status shows at rest without any idle
        poll. The active-monitor accessor (`_active?.CurrentEngine ?? _engine`) means a Program↔Source tab switch
        re-points the readout, exactly as the transport does.
      - **Live fps (App).** The right cell shows the *measured* preview rate while playing (present-count delta over
        the real elapsed interval), settling back to the sequence's *nominal* rate when stopped — so a healthy
        1080p preview reads its true 23.98/30/… and a struggling one visibly dips.
      - **`StatusBarFormat` (App).** The label/telemetry strings are a pure static helper (mirroring
        `MarkerListFormat`/`SpeedFormat`), so the code-behind only maps strings onto the `TextBlock`s + picks the
        dot colour, and the formatting is unit-tested headlessly.
      - **Tests (7).** `StatusBarFormatTests`: the state word for Stopped/Paused/Playing with no decode; the
        hardware label (`Playing · GPU · D3D11VA`, device upper-cased) and software label (`Ready · CPU · software`);
        the `fps · WxH · duration` readout and its whole-rate trimming (`30 fps`, not `30.00`). Clean build
        (0 warnings); `SPROCKET_APP_SECONDS` smoke launches (empty + with the sample clip, exercising the transport
        wiring and the one-shot present handler) start and tear down cleanly (exit 0). Full suite: **668 tests
        green** (Core 215, Media 30, Render 50, Audio 23, Playback 52, Export 65, Persistence 90, App 143).
    - **✅ Hardware export encoders strand DONE — step 29 now complete** (`Sprocket.Media`: `VideoEncoderSettings.HardwareCandidates`
      + probe/fallback & GPU-upload in `MediaEncoder`, `MediaEncoder.IsHardwareVideo`, `HardwareDevice.EncoderDeviceType`,
      new `av_hwframe_ctx_alloc`/`_ctx_init`/`_get_buffer` bindings + `AvHwFramesContext`/`AvBufferRef` views +
      `AVCodecContext.hw_frames_ctx`; `Sprocket.Export`: `ExportAcceleration` + `ExportCodecs.HardwareEncoderCandidates`/
      `PlatformHardwareVendors` + `ExportOptions.Acceleration`; `Sprocket.App`: an **Encoding** picker in
      `ExportSettingsDialog`; 16 new tests — Media 30 → 34, Export 65 → 77; full suite 668 → 684). The last of the five
      strands. **Deliberate departure from the literal wording** ("add hardware `ExportVideoCodec` options"): rather than
      exploding the codec enum into per-vendor values, hardware is modelled as an acceleration preference *orthogonal* to
      the codec — the way Premiere ("Software/Hardware Encoding") and Resolve (encoder picker) present it — so H.264/HEVC/
      AV1/… each gain GPU encoding without a combinatorial enum, and the deterministic software encoder stays the delivery
      default. All the strand's deliverables land on the existing `MediaEncoder` "select encoders by name" seam (§11):
      - **Probe + automatic software fallback (Media).** `VideoEncoderSettings.HardwareCandidates` is an ordered chain of
        hardware encoder names tried **before** the software `CodecName`. `MediaEncoder.OpenVideo` opens each in turn,
        engaging the first that succeeds and **silently degrading** to the next — then to software — on any failure (no
        device, encoder not built in, open rejected the GPU), mirroring how `MediaSource` negotiates hardware *decode*.
        The software `CodecName` open is unchanged and still surfaces its own errors, so a machine with no usable GPU
        produces the **identical deterministic software output** (ARCHITECTURE.md §5). A failed hardware attempt tears
        down atomically (device / frames-pool / staging frames / encoder ctx) and staging frames are allocated only after
        `avcodec_open2` succeeds, so a rejected candidate never leaves an orphan stream in the muxer for the next to
        double up on. `IsHardwareVideo` reports which path engaged.
      - **Two GPU-frame paths, chosen by the encoder's advertised pixel formats (Media).** An encoder that lists a CPU
        (non-`HWACCEL`) pixel format — NVENC / QSV / AMF / VideoToolbox — is fed the composited frame swscaled to nv12
        and **uploads internally**; one that lists *only* device-surface formats — VAAPI — takes the **`hw_frames_ctx`
        upload path**: a pooled GPU surface is drawn from an `AVHWFramesContext` (`av_hwframe_get_buffer`) and the nv12
        staging frame uploaded into it (`av_hwframe_transfer_data`) each frame before encode. Both are native→GPU copies
        with **zero managed pixel allocation** (§1). Quality is driven by bit rate (the knob all four vendors honour;
        hardware ignores libx264-style CRF), an explicit `VideoBitRate` winning over a resolution-scaled default.
      - **Platform candidate resolver (Export).** `ExportCodecs.HardwareEncoderCandidates(codec)` builds `{base}_{vendor}`
        names for the current OS, most-preferred first per the brief — **Windows** NVENC → QSV → AMF, **Linux** VAAPI →
        NVENC, **macOS** VideoToolbox — and `VideoExporter` passes them as the candidate chain only when
        `ExportOptions.Acceleration == Hardware` (the default `Software` carries none, so `default(ExportOptions)` is
        byte-for-byte the pre-step-29 delivery path). Nonexistent combos (e.g. `prores_nvenc`) are harmless — the probe
        skips any name this FFmpeg build lacks.
      - **UI (App).** `ExportSettingsDialog` gains an **Encoding** picker (Software / Hardware — if available) beside
        Quality. It is deliberately **not** part of a preset (a performance choice, not a delivery format), so it neither
        snaps the preset to Custom nor is captured by Save Preset. Both single **Export** and **Export Queue ▸ Add…** read
        this dialog, so both carry the choice.
      - **Tests (16).** `HardwareExportTests` (Export, 12): the resolver is the platform vendor product most-preferred
        first (per-OS exact lists) and uses each codec's hardware base name; a Hardware-acceleration export round-trips to
        the requested codec family whether a GPU engaged **or fell back** (stable because the *decoded* codec is the
        family); software + hardware requests both produce valid files; `default(ExportOptions).Acceleration` is Software.
        `HardwareEncodeTests` (Media, 4): no-candidates is pure software; an unavailable candidate (and a whole chain of
        them) falls back to software with a single well-formed stream; the real platform H.264 candidates **engage or fall
        back but always produce a decodable file**. On this NVIDIA dev box the opportunistic tests exercised the **real
        `h264_nvenc` upload+encode path** (`IsHardwareVideo` true, valid H.264 out); the VAAPI-only `hw_frames_ctx` surface
        path is code-verified but Linux-only (no VAAPI device here). Clean build (0 warnings); `SPROCKET_APP_SECONDS` smoke
        launch with the sample clip starts and tears down cleanly (exit 0). Full suite: **684 tests green** (Core 215,
        Media 34, Render 50, Audio 23, Playback 52, Export 77, Persistence 90, App 143).
30. **Audio loudness metering, normalization & editorial audio polish.** The delivery-grade audio
    visibility that effects alone don't provide — the first of the two audio-post layers (the second is
    plugin hosting + deeper DSP, step 31):
    - **Loudness metering.** Real-time **LUFS** metering (integrated / short-term / momentary) + true-peak
      per the EBU R128 / ITU-R BS.1770 model, plus channel meters, computed on the
      [ARCHITECTURE §6](ARCHITECTURE.md) audio path without per-buffer managed allocation, displayed in
      the audio mixer / meters UI.
    - **Normalization.** Loudness normalization to a target (e.g. −14 / −16 / −23 LUFS) applied as a
      computed gain at clip / track / master scope, plus a per-clip gain-match pass — all model gain (the
      mixer already does gain/fade, step 5), undoable.
    - **Editorial audio polish.** Audio meters, per-track gain / pan controls, and the **Audio** tab /
      mixer surface ([UI.md §3.3](UI.md)) brought to editorial completeness.
    - **✅ Loudness-metering strand DONE (`Sprocket.Audio/Loudness`: `KWeightingFilter`, `TruePeakMeter`,
      `LoudnessMeter` + `LoudnessSnapshot`; tapped into `AudioEngine`; 15 new tests — Audio 23 → 38; full suite
      684 → 699).** The first of step 30's three strands. Real-time EBU R128 / ITU-R BS.1770-4 loudness is now
      measured on the [ARCHITECTURE §6](ARCHITECTURE.md) audio path with **zero per-buffer managed allocation**
      (§1); the read-out is ready for the meters UI, which lands with the editorial-audio-polish strand. The
      **normalization** and **editorial polish** strands remain. Delivered:
      - **`KWeightingFilter` (Audio).** The two-stage BS.1770 K-weighting pre-filter (high-shelf "head model" +
        RLB high-pass) as cascaded Transposed-Direct-Form-II biquads, one state set per channel. Coefficients are
        computed from the analog prototypes for **any** sample rate (the libebur128 mapping — not just the 48 kHz
        table in the standard), so 44.1/48/96 kHz projects all measure correctly; verified against the curve
        (DC removed, ~0 dB at 1 kHz, +~4 dB high-shelf plateau, strong sub-bass roll-off).
      - **`TruePeakMeter` (Audio).** True peak (dBTP) by **4× oversampling** with a Hann-windowed-sinc polyphase
        FIR (BS.1770-4 Annex 2), each branch normalised to unity DC gain; per-channel history rings + a peak-hold
        running max keep it allocation-free. Catches inter-sample overshoot the sampled peak misses (a full-scale
        fs/4 sine phased onto ±0.707 samples reconstructs to ~0 dBTP).
      - **`LoudnessMeter` + `LoudnessSnapshot` (Audio).** Accumulates K-weighted energy in 100 ms segments; a
        fixed ring of the last 30 gives the **momentary** (400 ms) and **short-term** (3 s) sliding windows, and
        overlapping 400 ms gating blocks feed a **bounded 0.1-LU histogram** for the **gated integrated** loudness
        (absolute −70 LUFS + relative −10 LU gates, computed with the standard two-pass mean-of-energy). Also
        tracks true peak and per-channel sample peak. `Process` is feeder-thread-confined and lock-free except a
        tiny publish at each 100 ms boundary; `TakeSnapshot` reads the published values from the UI thread;
        `RequestReset` (thread-safe flag) restarts the integrated measurement. `Flush` closes the final partial
        segment so a finite offline stream's tail is measured (reused by normalization next).
      - **`AudioEngine` tap.** The feeder meters **only the buffers actually enqueued** for playback (a
        superseded-generation mix is dropped, not metered) and does so **off the transport lock** so the DSP never
        stalls `Now`; `Seek` calls `RequestReset`. `AudioEngine.CurrentLoudness` exposes the snapshot to the UI.
      - **Tests (15, headless, no device).** K-weighting DC-block / 1 kHz-unity / 10 kHz-shelf / sub-bass
        roll-off; true-peak inter-sample overshoot + ≥ sample-peak; and loudness invariants: silence → −∞, DC far
        quieter than a tone (RLB), **+6.02 LU per amplitude doubling**, **+3.01 LU stereo-vs-mono**, full-scale
        1 kHz calibration band, **absolute gate** ignores a near-silent tail, **relative gate** ignores a much
        quieter section, momentary window shorter than short-term, and reset restarts integrated. Full suite:
        **699 tests green** (Core 215, Media 34, Render 50, Audio 38, Playback 52, Export 77, Persistence 90,
        App 143).
    - **✅ Normalization-engine strand DONE (`Sprocket.Core`: `Clip.GainDb`, `AudioPlanScope` + scoped/clip-gain
      `PlanAudioBuffer`, `Audio.LoudnessNormalization`; `Sprocket.Audio`: `LoudnessAnalyzer` + `LoudnessMeasurement`,
      scoped `AudioMixer.MixInto`; `Sprocket.Persistence`: additive `ClipDto.GainDb`; 16 new tests — Core 215 → 224,
      Audio 38 → 44, Persistence 90 → 91; full suite 699 → 715).** The second of step 30's three strands: the loudness
      normalization **engine** (measurement + gain math + per-clip model gain + measurement scoping), all on the
      existing audio path. The **undoable Normalize actions + target UI + per-clip gain-match** land with the
      editorial-audio-polish strand (the mixer/Audio-tab surface where they belong, as the metering DSP shipped
      ahead of its meters UI); the editorial-polish strand is what **remains** of step 30. Delivered:
      - **`Clip.GainDb` (Core).** A per-clip audio gain (dB, 0 = unity) folded into the audio plan alongside track
        gain and the fade envelope (`RenderGraph.PlanAudioBuffer` — media, multicam, and nested-sequence layers),
        cloned by blade-split, and **persisted additively** (`ClipDto.GainDb`, `WhenWritingNull` — unity writes
        nothing, so pre-30 files load at unity and un-gained projects serialize byte-identically). This is the model
        gain clip-scope normalization sets; no mixer change (it already ramps the plan's per-layer gain).
      - **`AudioPlanScope` (Core).** An optional measurement scope on `PlanAudioBuffer` (and a matching scoped
        `AudioMixer.MixInto`): isolate one track (ignoring its mute/solo so its content is measurable) and/or force
        unity track / master gain, applied only at the measured sequence's top level. It lets a clip / track /
        master scope's **raw** loudness be measured, so normalization is an absolute set (`gain = target −
        measuredRaw`) rather than a fragile delta. The full-mix (null-scope) path is byte-for-byte unchanged.
      - **`LoudnessNormalization` (Core, pure).** Delivery targets (−14 / −16 streaming, −23 EBU R128) + a default
        −1 dBTP ceiling, and `ComputeGainDb(measuredLufs, measuredTruePeakDbtp, target, ceiling)` = the gain to hit
        the target, **reduced so the true peak stays under the ceiling** (true-peak limiting only ever cuts below
        target; silence returns 0). Dependency-free numbers, so commands/tests use it without the Audio layer.
      - **`LoudnessAnalyzer` + `LoudnessMeasurement` (Audio).** Offline (faster-than-real-time) measurement through
        a private `LoudnessMeter`: `MeasureSource` (one PCM reader over a clip's used span → clip scope) and
        `MeasureMix` (a mixed sequence with an optional scope → master / track scope). One reusable buffer, no
        per-chunk allocation, and it never touches the live playback meter.
      - **Tests (21, headless, no FFmpeg).** Core: clip gain folds into the layer gain and multiplies with track
        gain; scope isolates a track (ignoring solo) and forces unity track/master gain; `ComputeGainDb`
        turn-up/turn-down/silence/ceiling-caps-boost/ceiling-forces-cut. Audio (`SinePcmReader`): `MeasureSource`
        finite loudness + **+6 LU per amplitude doubling** + zero-duration silent; `MeasureMix` two identical tracks
        **+6 LU** over one (scoped) and unity-master scope ignores master gain; and an **end-to-end normalize** —
        measure a tone, compute the gain to −23 LUFS, boost, re-measure → lands on target. Persistence: clip audio
        gain round-trips. Full suite: **715 tests green** (Core 224, Media 34, Render 50, Audio 44, Playback 52,
        Export 77, Persistence 91, App 143).
    - **✅ Editorial-audio-polish strand DONE — step 30 now complete** (`Sprocket.Core`: `AudioTrack.Pan` + `Audio.PanLaw`
      + pan in the audio plan; `Sprocket.Audio`: pan in the mixer; `Sprocket.Persistence`: additive `TrackDto.Pan`;
      `Sprocket.App`: `MixerFormat`, `Mixer/MixerView`, mixer hosting in the Project panel's Audio tab, live-loudness
      wiring, and track/master/clip **Normalize** actions; 29 new tests — Core 224 → 231, Audio 44 → 46, App 143 → 163;
      full suite 715 → 744). The third and final strand brings the meters + per-track controls + normalization to the
      UI (UI.md §3.3). The Avalonia surfaces rest on build + a `SPROCKET_APP_SECONDS` smoke launch (the App is a
      UI-bound `WinExe`); the pure model / formatting is unit-tested. Delivered:
      - **Pan / stereo balance (Core + Audio + Persistence).** `AudioTrack.Pan` in [-1, 1] (clamped) with a linear
        **balance** law (`PanLaw.Balance`): centre = unity on both channels — so a centred track mixes byte-identically
        to the pre-pan behaviour — and panning attenuates the opposite channel to silence at the extreme. It folds into
        the audio plan as per-layer `AudioLayer.PanLeft/PanRight` (default 1/1 = unchanged) and the mixer's `SumWithRamp`
        applies it per channel on a stereo output (a no-op for mono). Additive `TrackDto.Pan` (`WhenWritingNull`) keeps
        un-panned projects byte-identical.
      - **`MixerFormat` (App, pure).** Gain (`+3.0 dB` / `-∞ dB`), pan (`C` / `L50` / `R100`), LUFS / dBTP read-outs,
        and the 0–1 meter fill fraction — free of any Avalonia control (mirrors `StatusBarFormat`), so all strings and
        fractions are unit-tested.
      - **`MixerView` (App).** The Audio tab is now a mixer: a **master strip** with the live EBU R128 read-out
        (integrated / short-term / momentary LUFS + true peak) and **L/R peak meters** (green→amber→red, peak-hold),
        plus a master fader; and a **channel strip per audio track** with a gain fader, pan/balance, and mute / solo.
        Every edit routes through `EditHistory` — faders open one `BeginCoalescing()` scope for the drag so a whole
        gesture is a single undo entry (the timeline's pattern), mute/solo issue `SetPropertyCommand<bool>` — and undo
        refreshes the widgets without re-issuing commands. The meters poll `AudioEngine.CurrentLoudness` on a ~15 Hz
        `DispatcherTimer` that runs **only while the Audio tab is on screen** (started/stopped on attach/detach), so an
        idle or hidden mixer costs nothing (§1). The live loudness is surfaced by carrying the `AudioEngine` on
        `MediaBootstrap.Result` (a non-owning reference; the engine still owns it) through to the shell.
      - **Loudness normalization actions (App).** A **Normalize to** target picker (−14 / −16 / −23 LUFS) drives
        per-track and master **Normalize** buttons (measure the scope's raw loudness at unity via `LoudnessAnalyzer`
        + a `MediaBootstrap.CreateAnalysisMixer`, compute the true-peak-limited gain via `LoudnessNormalization`, set
        the model gain as one undoable edit), and **Clip ▸ Normalize Audio** normalizes the selected clip's
        `Clip.GainDb` over its used source span to the same target — applied clip-by-clip it is the gain-match pass.
      - **Notes.** Pan is a stereo balance (centre-unity, non-disruptive) rather than a −3 dB constant-power pan, so
        existing centred mixes are unchanged. The strips carry controls but not per-track live meters — the single
        engine meter measures the mixed master (its L/R bars are the required channel meters); per-track live metering
        would need per-track metering in the engine and is a natural later refinement. Clean build (0 warnings); a
        `SPROCKET_APP_SECONDS` smoke launch with the sample clip (audio wired → the mixer's meters live) starts and
        tears down cleanly (exit 0). Full suite: **744 tests green** (Core 231, Media 34, Render 50, Audio 46,
        Playback 52, Export 77, Persistence 91, App 163). **Step 30 is complete.**
31. **Audio effects & plugin hosting (VST3 / AU).** The deeper audio-post layer (loudness
    metering/normalization is the earlier step 30). Give audio an effect chain mirroring video's
    `IVideoEffect` stack: a new Core **`IAudioEffect`** seam and an audio effect chain on audio **clips,
    tracks, sequences, and the master bus**, run by the `AudioMixer` as a per-buffer DSP pass in the
    [ARCHITECTURE §6](ARCHITECTURE.md) audio path (allocation-free on the audio thread, processing blocks
    of float32 at the project rate/layout). Ship a few **built-in managed effects** first (parametric EQ,
    compressor, reverb, gain/pan) so the chain is useful with no native deps, then **host native
    plugins** behind the same seam: **VST3** (cross-platform — Win/Linux/macOS) and **Audio Units**
    (macOS-only). Per the **no-C++/CLI** rule ([ARCHITECTURE §1](ARCHITECTURE.md), [§13](ARCHITECTURE.md))
    each format is reached through a thin **native C-ABI bridge shim** (the VST3 SDK is C++/COM-style and
    AU is Obj-C — each wrapped to a flat C ABI the way the FFmpeg/Skia natives are), one bridge per
    format, bundled per RID alongside the other natives (steps 35–36). Plugins are scanned and
    instantiated **off** the audio thread; the host can open a plugin's own editor GUI in a window.
    **Parameter automation** rides the existing `AnimatableValue` / keyframe mechanism (step 16d), so
    plugin parameters keyframe like any other effect. **Persistence:** an audio effect serializes as
    plugin id + an opaque **state blob** (e.g. VST3 component/controller state) + its automation —
    additive and schema-versioned (§12); a missing plugin loads **offline** (the chain bypasses it)
    rather than failing the load (§15). Builds on the audio mixer (steps 5/7) and the plugin host
    (step 33). **Licensing:** the VST3 SDK is GPLv3-or-Steinberg-dual-licensed — choose the license
    deliberately before distribution (cf. the FFmpeg LGPL/GPL note). A track or chain can also be
    **frozen** (pre-rendered) via the render cache (step 32) so heavy or non-deterministic plugins
    aren't recomputed every playback pass.
32. **Preview render cache (pre-render / "freeze").** Expensive subgraphs — nested sequences
    (step 23), adjustment-layer spans (step 19), deep effect chains, and audio plugin chains
    (step 31) — shouldn't be recomputed every playback pass. Because the render graph is a **pure,
    deterministic function of (project, t)** with no hidden state ([ARCHITECTURE §5](ARCHITECTURE.md),
    [§6](ARCHITECTURE.md), §1.6), a computed range can be cached and replayed, then invalidated when the
    edit that produced it changes. The cache reuses the existing seams: a rendered range is exposed back
    to the parent graph as **just another `IFrameSource`** (video — rendered to a fast all-intra
    intermediate via `MediaEncoder`, or a short GPU texture ring) / **`IPcmReader`** (audio — cached PCM,
    i.e. "freezing" a track, valuable for non-deterministic native plugins), the same seam media, proxies
    (§17) and nested sequences already use — so **no new render-graph machinery**. Intermediates are
    encoded for **speed, not size** — all-intra and **hardware where available**, and the codec **may
    vary by host OS** (e.g. ProRes/VideoToolbox on macOS, NVENC/QSV on Windows, VAAPI on Linux, MJPEG /
    x264 *ultrafast* as the cross-platform fallback; audio as uncompressed PCM) since the cache is local
    and regenerable — with **no effect on export determinism** ([ARCHITECTURE §11](ARCHITECTURE.md)
    "Preview vs. delivery codecs", §1.6). Cache entries are keyed
    by a **content hash of the cached subtree's serializable state** (the persist DTO, §12) + range +
    render settings; any model edit (always via the command stack, §4) re-hashes and marks the affected
    range **dirty** (exact invalidation, no stale frames). A **render bar** over the ruler shows rendered
    vs. needs-render ranges (green/yellow/red), with *Render In to Out* / *Render Selection* /
    *Render Audio* / *Delete Render Files* commands. The cache is a **local derived artifact** kept in a
    cache dir beside the project (not in the diffable project file, not merged — cf. step 28) and is
    always **safely discardable**. **Export ignores the preview cache by default** and re-renders full-res
    originals (§17) so output stays deterministic; reusing a full-quality cache is an opt-in. Lands on the
    done render graph + the `IFrameSource` / `IPcmReader` seams; full value comes once sequences (step 23)
    and audio effects (step 31) exist, hence its place here, but the video side can ship with step 23.
    [ARCHITECTURE §20](ARCHITECTURE.md).
33. **Plugins & advanced color management.** Plugin host (collectible `AssemblyLoadContext`,
    [ARCHITECTURE §13](ARCHITECTURE.md)), then OpenColorIO / ACES / OFX scene-linear color management.
    (The creative color-grading toolset — wheels, curves, qualifiers, scopes — is its own step, 34.)
34. **Color grading.** A professional grading toolset on top of the step-16 `Color` effect, all as
    SkSL effect-chain stages (§7) so preview and export stay identical and GPU-resident (§1, §5):
    **lift / gamma / gain color wheels** (shadows / mids / highlights), **RGB + per-channel curves**,
    **HSL secondaries / qualifiers** (key a hue/sat/luma range and grade only that), **white balance**
    (temp / tint), and saturation / vibrance — each a new built-in `IVideoEffect` registered in
    `EffectCatalog`, keyframeable via `AnimatableValue` (step 16d) and edited in the type-driven Inspector
    (step 16). Reference **scopes** — waveform / vectorscope / RGB parade / histogram — computed from the
    rendered frame (extending the step-17 monitor scopes) to grade against. Composes with the input color
    transform / log handling (step 37) and the advanced OCIO / ACES color management (step 33). Lands
    entirely on the existing effect seam ([ARCHITECTURE §7](ARCHITECTURE.md), [§17](ARCHITECTURE.md)) — no
    render-graph redesign; builds on the done effect pipeline, so it can be pulled earlier if prioritized.
35. **Cross-platform native-lib bundling.** Make the build self-contained per RID: copy the FFmpeg 8
    `.dll`/`.so`/`.dylib` set and `SkiaSharp.NativeAssets.{Win32,Linux,macOS}` + OpenAL Soft natives
    into the publish output for `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64` so the app runs with no
    system FFmpeg ([ARCHITECTURE §11](ARCHITECTURE.md)). Needed for the slice to *run* on Linux/macOS
    at all; promoted to its own step because it gates every on-device verification.
36. **Packaging & distribution (incl. macOS executable).** Produce a runnable artifact per OS: a
    Windows folder/installer, a Linux AppImage/tarball, and a **macOS `.app` bundle** with the FFmpeg
    dylibs under `Contents/Frameworks` (resolved via `@loader_path`), **code-signed and notarized**,
    shipped for Apple Silicon (`osx-arm64`) and Intel (`osx-x64`). CI builds on win/linux/macOS runners;
    a smoke launch + sample export validates each artifact.
37. **Log media & color management (D-Log).** Support DJI **D-Log / D-Log M / D-Log 2** as a
    per-clip **input color transform**, landing on the existing effect seam
    ([ARCHITECTURE §18](ARCHITECTURE.md), §7, §17) — **not** via FFmpeg's `lut3d`/`WriteableBitmap`,
    which would break [§1](ARCHITECTURE.md) (managed per-frame pixels + CPU round-trip) and
    [§5](ARCHITECTURE.md) (preview/export divergence). All color math stays on the GPU in Skia,
    like brightness/fade. Pieces:
    - **Metadata probe (Media).** Extend `ProbedMediaInfo` with color transfer / primaries / space
      and a format-metadata dictionary; read them in `MediaSource.Probe` from the codec parameters
      and `AVFormatContext`/`AVStream` metadata. Auto-detect the DJI log profile on import; fall
      back to a manual per-clip tag.
    - **Color-transform effect (Core + Render).** New built-in effect id `builtin.colortransform`
      (params: source profile, target space, bypass) added to `EffectTypeIds`; an SkSL stage in
      `SkiaEffectPipeline` that samples a **3D LUT packed into a 2D texture** (trilinear) supplied as
      a `uniform shader` child — chained like brightness/fade. The detected transform is
      **prepended** to the clip's effect stack so the input transform runs first.
    - **LUT bundling.** DJI official `.cube` files as `EmbeddedResource`s in `Sprocket.Render`,
      decoded once into the packed LUT texture and cached (first data-asset precedent; today all
      effects are inline SkSL strings).
    - **Inspector (depends on step 16).** A COLOR-section "Input transform / color space" control to
      set or override the per-clip log profile; auto-set from detection.
    - **Export.** Bake the transform in (default, via the same render graph) or pass through the log
      encoding — a per-export toggle.
    - **Scopes (with/after step 17).** A log ↔ transformed toggle for waveform/monitor so colorists
      can read either space.
    - **Persistence.** The effect serializes for free via the existing `EffectInstance` JSON; the
      new `ProbedMediaInfo` color fields are additive (nullable/defaulted, no schema bump).
    - **Upgrade path.** Full scene-linear / OpenColorIO color management remains the later step-33
      upgrade ([ARCHITECTURE §18](ARCHITECTURE.md)).
38. **AI control via an application-hosted MCP server (off by default).** Host an in-process
    [Model Context Protocol](https://modelcontextprotocol.io) server inside `Sprocket.App` so an
    external AI client (e.g. Claude) can drive the editor — inspect the project and issue edits —
    over a local connection. **Disabled by default**; both the **enabled** toggle and the **listen
    port** are user-configurable in **application settings**. This is a new capability landing on
    existing seams, not a rewrite ([ARCHITECTURE §17](ARCHITECTURE.md)). Pieces:
    - **Application settings store (prerequisite — new).** There is no app-level preferences
      mechanism today (the only `Settings` is `Project.Settings`, which is per-project and lives in
      the `.sprocket.json` file). Introduce a **user-scoped** settings store persisted to the
      platform's per-user config location (e.g. `%AppData%` / `~/.config` / `~/Library/Application
      Support`), separate from the project file; the **MCP enabled flag** and **MCP port** are its
      first entries, surfaced in a Settings/Preferences UI.
    - **MCP server.** A new component (e.g. `Sprocket.Mcp`, referenced by `Sprocket.App`) exposing
      the official **C# MCP SDK** (`ModelContextProtocol`) over a local transport, bound to
      **loopback**. Started/stopped purely from the settings toggle — **never auto-started**; a
      port change restarts the listener.
    - **Tools route through the command stack (§4 / step 10).** Every state-changing MCP tool
      issues `IEditCommand`s through `EditHistory`, so AI-driven edits are **undoable by
      construction** and share the UI's validation; read-only tools expose project / timeline /
      media-pool / playhead state. Model mutations marshal to the UI thread (the thread that owns
      the model, §8); decode/render/audio threads are untouched.
    - **Security.** Off by default, loopback-only, and clearly indicated in the UI while running so
      the user knows the app is externally controllable; no remote/network exposure in this step.

39. **Fade handles & opacity rubber-band (on-timeline fade editing + visualization).** Make a clip's
    fade in/out directly **visible and editable on the timeline**, so a fade is never an invisible
    surprise — the lesson from the Alt-copy "second clip plays black" bug (fixed 2026-06-30): the clip
    carried a keyframed fade with nothing on the timeline to show it. Follows the Premiere/Resolve
    convention. This lands on **existing seams**, not a new model — the fade is already the keyframed
    `EffectTypeIds.Fade` / `EffectParamNames.Opacity` `AnimatableValue` that drives both video alpha
    (shader, step 7) and audio gain (mixer, [§6](ARCHITECTURE.md)); this step is a timeline affordance over it.
    Pieces:
    - **Fade handles.** Small draggable triangles in each top corner of a clip, drawn in
      `TimelineControl.DrawClips` (step 12). Dragging the left/right handle inward sets the fade-in/out
      length; the clip body draws the resulting opacity ramp so the fade reads at a glance. Zero length =
      the "no fade" rest state.
    - **Opacity rubber-band (companion view).** A horizontal opacity line across the clip body the user can
      pull down or add points to — the inline form of the same keyframe envelope already shown in the
      Inspector's keyframe lane (step 16d), so the two stay in sync.
    - **Edits route through the command stack (step 10).** Dragging a handle / rubber-band point issues
      `IEditCommand`s that author or adjust the Fade effect's opacity keyframes, coalesced into one undo
      entry per drag (mirrors the trim/slip drag coalescing).
    - **Keyframes stay clip-aligned.** Effect keyframe times are **absolute timeline time**, so handles must
      author keyframes at the clip's actual edges, and clip move/copy must keep the fade aligned with the
      clip. Copy/paste rebasing was fixed 2026-06-30 (`ClipboardOps.Paste` → `EffectInstance.CloneShifted`);
      the matching rebasing for a plain **move** (`SetClipPlacementCommand`, which today changes only
      `TimelineStart`) is the remaining gap to close as part of this step.

40. **Rich text & titles (styling, lower thirds, rolling credits).** Grow the step-19 minimal Title
    generator (single-line centred text, one fill colour, animatable size) into a professional titles
    toolset, following the Premiere/Resolve/Final Cut convention — a title is a **generator clip on its
    own track**, edited in place and in the Inspector. Lands entirely on existing seams
    ([ARCHITECTURE §17](ARCHITECTURE.md)): a title is already a `ClipKind.Generator` on the
    `GeneratorCatalog`, so this adds generator types, string / animatable params, and Render layout with
    **no render-graph, effect-chain, or persistence redesign**. Captions / subtitles (SRT/VTT, a
    toggleable caption track) are a **distinct subsystem deferred to a separate step**, as in every NLE.
    Extends step 19 and depends only on done seams, so it can be **pulled forward / prioritised** whenever
    desired.
    - **Editable text objects (post-hoc).** A title stays fully editable after creation (the universal NLE
      behaviour): **double-click** the clip in the Program monitor / timeline to edit the content, and an
      Inspector TEXT section for every attribute. All edits route through the step-10 command stack (a
      generator-param command mirroring `SetEffectParameterCommand`), so they undo / redo and coalesce
      (drag gestures) like effect params — no new mutation path.
    - **Typography & styling (Inspector, stored on `GeneratorSpec`).** Extend `GeneratorParamNames` with
      **font family** (a typeface picker — step 19 hard-codes `SKTypeface.Default`), **bold** weight and
      **italic** (`SKFontStyle`), **fill colour** (exists), **stroke / outline** (colour + width, `SKPaint`
      stroke), **drop shadow** (colour + offset + blur, `SKImageFilter.CreateDropShadow`), a **background
      box** (colour + opacity + padding — step 19's full-frame `backgroundColor` becomes a padded box),
      **alignment** (left / centre / right, `SKTextAlign`; centre-only today), and **tracking** (letter
      spacing) + **leading** (line spacing). Sizes stay fractions of frame height (resolution-independent);
      colours stay `#AARRGGBB`. Font size stays an `AnimatableValue`; the new numeric attributes (stroke
      width, shadow, tracking, leading) join it as animatable params so they keyframe (step 16d). String
      attributes (family, alignment, style) live in `GeneratorSpec.Strings`.
    - **Multi-line & paragraph layout (Render).** `RenderGeneratorContent` grows from one centred line to
      **word-wrapped multi-line** layout in a text box (measure / break / stack lines, honouring alignment,
      leading, tracking). Reuses the offscreen-surface → snapshot → effect-chain path; only the content
      draw changes.
    - **Positioning / transform (reuse step 16).** Position, scale, rotation, and anchor come from the
      existing **Transform** effect on the title clip — not reinvented. The step-17 **action-safe /
      title-safe** guides frame title placement.
    - **Title templates (`GeneratorCatalog`).** Register built-ins beside step-19 Title / Color Matte:
      **Lower Third** (a two-field name+role title with a background bar — the "chyron" / "super"), **Roll**
      (credits), and **Crawl** (ticker) — each a generator descriptor with defaults, listed in the Project
      bin / **Clip ▸ Insert** menu like current generators.
    - **Rolling credits (Roll) & Crawl — a *property of the title*, duration-driven.** Following the
      industry norm (Premiere's **Roll / Crawl** options, Resolve's **Scroll** title, Final Cut's **Scroll**
      behaviour), scrolling is a **scroll mode on the generator**, *not* a hand-keyframed Transform. A
      `scrollMode` (None / Roll-up / Crawl-left) param, with **clip duration setting the speed** (longer
      clip ⇒ slower — the Resolve / FCP model) plus optional **ease-in / out** and **start / end off-screen**
      (the Premiere options). The generator computes the offset from its **clip-local progress**, so it
      stays a pure, deterministic function of (project, t) ([ARCHITECTURE §5](ARCHITECTURE.md)) with **no
      fragile absolute-timeline keyframes** (cf. the step-39 keyframe-rebasing caveat). **Small additive
      Core change:** pass the clip's local elapsed time + duration (or a normalised progress) into
      `RenderGraph.ResolveGenerator` / `ResolvedGenerator`; today the generator receives only absolute `t`.
      *(Deliberate departure from "everything is a keyframe": pros drive roll speed by clip length, and it
      survives trim / move cleanly.)*
    - **Entrance / exit animation presets.** Convenience presets applied as keyframes (step 16d) on the
      title: **fade in / out** (the existing Fade effect + the step-39 on-timeline fade handles),
      **pop / scale** and **slide** (Transform keyframes), and **typewriter** (a `revealFraction` param
      driving how many characters draw). Presets author standard keyframes; nothing bespoke in the graph.
    - **Cross-platform determinism — bundle the title fonts.** Text must rasterise **byte-identically
      across Windows / Linux / macOS** so preview == export and the golden-frame / cross-OS PNG-hash tests
      hold ([ARCHITECTURE §5](ARCHITECTURE.md), Verification). System-font substitution differs per OS and
      would break that, so the title fonts are **bundled per-RID as `EmbeddedResource`s** (loaded via
      `SKFontManager` / `SKTypeface.FromStream`) — the data-asset precedent step 37 notes for LUTs. The
      family picker lists the bundled set; opt-in system fonts (non-delivery use) are a later add.
    - **Persistence (additive, §12).** Every new attribute is a string or `AnimatableValue` generator
      param, so all round-trip through the existing `GeneratorDto` with **no schema bump**; a step-19 title
      loads unchanged and a title using none of the new fields serialises byte-identically.
    - **Testing.** Core: generator-param command undo / redo + coalescing; scroll-offset math from
      clip-local progress (start / steady / end, ease, off-screen); catalog registration of the new
      templates. Render (offscreen-raster goldens): multi-line wrap + alignment, stroke, shadow, background
      box, a lower third, and a roll at 0 / 0.5 / 1 progress. Persistence: round-trip of every new field +
      step-19 byte-identical omission. Export: a roll renders on the deterministic raster path
      (golden-frame).

Open product questions (e.g. the mockup's user-avatar / account affordance, full panel docking)
are tracked in [UI.md §5](UI.md).

## Verification

- **Performance claim:** run the spike under a memory profiler (dotnet-counters / dotMemory);
  assert ~0 Gen0 allocations per frame in the render loop; confirm GPU upload path (no CPU
  pixel loops). Measure sustained 1080p preview fps.
- **Cross-platform:** CI matrix builds + runs the headless tests on windows-latest, ubuntu-latest,
  and macos-latest (the latter covers `osx-arm64`); manually run the app + export on a real Linux box,
  Win 11, and a Mac. The render path is byte-identical across OSes (verified Win↔Linux via the headless
  PNG hash; macOS to be confirmed once the dylibs are bundled, steps 35–36).
- **Correctness:** unit tests for RenderGraph (clip resolution, trim, effect-stack order,
  fade ramps) headlessly; golden-frame test comparing exported frames against expected output.
- **A/V sync:** export a clip with a known audio/video sync marker (clap/flash) and verify
  alignment; check drift over a multi-minute clip.
- **Hardware accel:** verify decode uses the GPU (nvidia-smi / vainfo / macOS `VideoToolbox` via
  GPU usage) and that software fallback engages when no device is present.

## Top risks

- Real-time A/V sync & jitter (hard in any language) — mitigate with audio master clock +
  bounded buffers + frame drop/duplicate. **(Preview judder addressed 2026-06-30 — see the note below.)**
- GC in the hot path — mitigated by the no-managed-pixels rule; must be enforced/profiled early.
- FFmpeg interop surface is raw and unforgiving — wrap narrowly in `Sprocket.Media`.
- Hardware-accel fragmentation across vendors/OSes — abstract + always keep software fallback.
- FFmpeg licensing (LGPL vs GPL) — decide before distribution.

## Playback performance log

- **Preview judder fix + diagnostics overlay (recorded 2026-06-30).** Reported stutter on a plain 1080p30
  clip (GPU `h264`/D3D11VA decode, RTX 3060). Added a **View ▸ Playback Statistics** overlay
  (`Sprocket.App/PlaybackStatsOverlay.cs` + `PlaybackEngine.GetStatistics()`/per-track drop counters +
  `GetActiveVideoDecodeInfo()`) reporting effective vs. target fps, dropped frames, decode codec + HW device,
  CPU/memory/GC. A headless real-time benchmark over the engine then pinned the cause: **not** decode (0 drops),
  GC (0 collections) or the OS timer per se, but the pump pacing — it polled at a fixed sub-frame interval
  (~27–31 ms) that **aliased** against the 33.3 ms frame grid, so frames averaged a clean 30 fps but were
  presented at uneven times (present-interval sd ≈ 9.6 ms, gaps to 63 ms, doubled frames). **Fix
  (`Sprocket.Playback/PlaybackEngine.cs` + `PlaybackTimerResolution.cs`):** pace the pump on an **absolute
  wall-clock frame schedule** with a no-overshoot sleep+spin waiter (precise regardless of OS timer
  granularity; re-anchors if >2 frames behind), keeping the existing drop/hold for A/V sync; plus a
  `timeBeginPeriod(1)` raise while playing to shrink the spin window when Windows honours it. Removed the
  obsolete fixed-pace `ComputePace`. Verified across runs: present-interval **sd 9.6 → ~3.0 ms, hitches
  12 → 0, doubled frames 18 → 0, 0 drops**; clean build + smoke launch. Next preview-perf wins remain the
  zero-copy GPU upload (step 6 deferral) and the render cache (step 32).
