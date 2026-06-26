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
  The managed assemblies are identical everywhere; FFmpeg is bundled per-RID (`.dll`/`.so`/`.dylib`,
  see [ARCHITECTURE §11](ARCHITECTURE.md)) since there is no Sdcb runtime NuGet for Linux/macOS.
  macOS ships as a signed/notarized `.app` bundle (build order step 25).

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
| Decode/encode/filter | **Sdcb.FFmpeg** (or FFmpeg.AutoGen) | Library-level libav* P/Invoke; frame-accurate. NOT FFMpegCore (CLI wrapper). |
| Hardware accel | FFmpeg hwaccel, per-OS | NVIDIA CUDA/NVENC (most portable); VAAPI (Linux), D3D11VA/QSV/AMF (Windows). |
| Audio output | **Silk.NET.OpenAL** | Cross-platform now; behind `IAudioOutput` so it can be swapped. |
| Plugins (later) | Custom **`IVideoEffect`** + collectible `AssemblyLoadContext` | OFX/frei0r hosting is a later, optional P/Invoke adapter. |

> Licensing note: FFmpeg builds can be LGPL or GPL depending on enabled encoders (e.g.
> x264 → GPL). Pick the build/license deliberately before any distribution.

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
Linux and macOS rest on bundling the native libs + on-device verification — see step 1 and step 25):
1. Create a project; add 1 video track + 1 audio track.
2. Import a media file (`MediaSource` opens via Sdcb.FFmpeg, reports duration/streams).
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
     FFmpeg dylibs are bundled (step 25) — the render path is the identical managed code, so the
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
       managed code + bundled libs (steps 24–25) + on-device verification.
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
      - **Zoom + scroll:** ⊟ / ⊞ buttons and **Ctrl+wheel** zoom (anchored so the tick under the cursor/playhead
        stays put, 8–600 px/s); the wheel scrolls horizontally, clamped to content.
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
16. **Inspector & expanded effects.** Type-driven inspector with collapsible sections;
    **Transform** effect (scale / position / rotation / anchor / opacity) as a new built-in
    `IVideoEffect`; **Color** (exposure / contrast / color) on the same SkSL shape; numeric +
    slider editing bound to `AnimatableValue`, with keyframe affordances.
17. **Monitors.** Dual **Source / Program** monitors (same render graph, second surface),
    safe-area / framing-grid overlay, **Fit** zoom, and full transport (jump-to-start/end,
    frame-step, play/pause).
18. **Proxy media (render performance).** Generate lower-resolution proxies and edit/preview
    against them via an alternate `IFrameSource`, with a "use proxies" toggle; **export still
    pulls full-resolution originals** ([ARCHITECTURE §17](ARCHITECTURE.md)). Committed feature
    per [BRIEF.md](BRIEF.md).
19. **Generators & adjustment layers.** Title/text **generator clips** (a generator
    `IFrameSource` feeding the render graph) and **adjustment layers** (a clip/track kind whose
    effect stack applies to all tracks beneath it — a render-graph stage, [ARCHITECTURE §5](ARCHITECTURE.md)).
20. **Alpha-channel media compositing.** Premultiplied-alpha path through the render graph (e.g.
    `Logo_Anim.mov` flagged `Alpha`).
21. **Transitions.** Transition library (Project panel **Transitions** tab) + overlapping-clip
    resolution in the render graph ([ARCHITECTURE §17](ARCHITECTURE.md)).
22. **Export presets & status-bar telemetry.** Export dropdown with presets; status bar
    surfacing engine state, GPU / hardware-accel status, live fps, resolution, and duration
    ([ARCHITECTURE §15](ARCHITECTURE.md)) — **no framework/runtime text** in the UI ([UI.md §3.7](UI.md)).
23. **Plugins & advanced color.** Plugin host (collectible `AssemblyLoadContext`,
    [ARCHITECTURE §13](ARCHITECTURE.md)), then OpenColorIO/OFX and color grading beyond the
    basics.
24. **Cross-platform native-lib bundling.** Make the build self-contained per RID: copy the FFmpeg 7
    `.dll`/`.so`/`.dylib` set and `SkiaSharp.NativeAssets.{Win32,Linux,macOS}` + OpenAL Soft natives
    into the publish output for `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64` so the app runs with no
    system FFmpeg ([ARCHITECTURE §11](ARCHITECTURE.md)). Needed for the slice to *run* on Linux/macOS
    at all; promoted to its own step because it gates every on-device verification.
25. **Packaging & distribution (incl. macOS executable).** Produce a runnable artifact per OS: a
    Windows folder/installer, a Linux AppImage/tarball, and a **macOS `.app` bundle** with the FFmpeg
    dylibs under `Contents/Frameworks` (resolved via `@loader_path`), **code-signed and notarized**,
    shipped for Apple Silicon (`osx-arm64`) and Intel (`osx-x64`). CI builds on win/linux/macOS runners;
    a smoke launch + sample export validates each artifact.

Open product questions (e.g. the mockup's user-avatar / account affordance, full panel docking)
are tracked in [UI.md §5](UI.md).

## Verification

- **Performance claim:** run the spike under a memory profiler (dotnet-counters / dotMemory);
  assert ~0 Gen0 allocations per frame in the render loop; confirm GPU upload path (no CPU
  pixel loops). Measure sustained 1080p preview fps.
- **Cross-platform:** CI matrix builds + runs the headless tests on windows-latest, ubuntu-latest,
  and macos-latest (the latter covers `osx-arm64`); manually run the app + export on a real Linux box,
  Win 11, and a Mac. The render path is byte-identical across OSes (verified Win↔Linux via the headless
  PNG hash; macOS to be confirmed once the dylibs are bundled, steps 24–25).
- **Correctness:** unit tests for RenderGraph (clip resolution, trim, effect-stack order,
  fade ramps) headlessly; golden-frame test comparing exported frames against expected output.
- **A/V sync:** export a clip with a known audio/video sync marker (clap/flash) and verify
  alignment; check drift over a multi-minute clip.
- **Hardware accel:** verify decode uses the GPU (nvidia-smi / vainfo / macOS `VideoToolbox` via
  GPU usage) and that software fallback engages when no device is present.

## Top risks

- Real-time A/V sync & jitter (hard in any language) — mitigate with audio master clock +
  bounded buffers + frame drop/duplicate.
- GC in the hot path — mitigated by the no-managed-pixels rule; must be enforced/profiled early.
- FFmpeg interop surface is raw and unforgiving — wrap narrowly in `Sprocket.Media`.
- Hardware-accel fragmentation across vendors/OSes — abstract + always keep software fallback.
- FFmpeg licensing (LGPL vs GPL) — decide before distribution.
