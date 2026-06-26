# Sprocket — Cross-Platform Video Editor on .NET 10 — Feasibility & Vertical-Slice Plan

## Context

Greenfield project (empty repo). The goal is a cross-platform (Windows 11 + Linux)
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
- **OS-specific code** is acceptable behind a C# interface when a Linux equivalent exists
  (mandatory for hardware accel). **No C++/CLI** — native wrapping must be plain P/Invoke
  against a C ABI so one managed codebase serves both OSes.

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

End-to-end on **both** Windows 11 and Linux:
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
   - **TODO:** repeat on Linux to confirm the OpenGL/Vulkan backend + system-FFmpeg path.
2. Timeline data model + RenderGraph in `Sprocket.Core` (unit-tested, headless).
3. `MediaSource` decode + seek (keyframe seek then decode-to-target); ring-buffer feed.
4. Skia preview surface + transport; software-clock playback (video only).
5. Audio: `IAudioOutput` + mixer; switch to audio master clock; A/V sync.
6. Hardware-accel decode path behind `IHardwareContext` (CUDA/VAAPI/D3D11VA), with software fallback.
7. Effects (brightness, fade) + audio volume/fade in mixer.
8. Export pipeline (full-res encode).
9. Project save/load (JSON).

Defer to post-slice: multiple-of-everything beyond 1+1 tracks, plugin system
(`AssemblyLoadContext`), OpenColorIO/OFX, color grading beyond basics, proxy-media workflow.

## Verification

- **Performance claim:** run the spike under a memory profiler (dotnet-counters / dotMemory);
  assert ~0 Gen0 allocations per frame in the render loop; confirm GPU upload path (no CPU
  pixel loops). Measure sustained 1080p preview fps.
- **Cross-platform:** CI matrix builds + runs the headless `Sprocket.Core` tests on
  windows-latest and ubuntu-latest; manually run the app + export on a real Linux box and Win 11.
- **Correctness:** unit tests for RenderGraph (clip resolution, trim, effect-stack order,
  fade ramps) headlessly; golden-frame test comparing exported frames against expected output.
- **A/V sync:** export a clip with a known audio/video sync marker (clap/flash) and verify
  alignment; check drift over a multi-minute clip.
- **Hardware accel:** verify decode uses the GPU (nvidia-smi / vainfo / GPU usage) and that
  software fallback engages when no device is present.

## Top risks

- Real-time A/V sync & jitter (hard in any language) — mitigate with audio master clock +
  bounded buffers + frame drop/duplicate.
- GC in the hot path — mitigated by the no-managed-pixels rule; must be enforced/profiled early.
- FFmpeg interop surface is raw and unforgiving — wrap narrowly in `Sprocket.Media`.
- Hardware-accel fragmentation across vendors/OSes — abstract + always keep software fallback.
- FFmpeg licensing (LGPL vs GPL) — decide before distribution.
