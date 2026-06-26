# Sprocket — Cross-Platform Video Editor on .NET 10 — Feasibility & Vertical-Slice Plan

> See [BRIEF.md](BRIEF.md) for the feature brief, [ARCHITECTURE.md](ARCHITECTURE.md) for the
> technical design, and [UI.md](UI.md) for the target UI and the features its mockup implies.

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
     stack but uses an offscreen raster surface, not the windowed GL/Vulkan compositor.
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
5. Audio: `IAudioOutput` + mixer; switch to audio master clock; A/V sync.
6. Hardware-accel decode path behind `IHardwareContext` (CUDA/VAAPI/D3D11VA), with software fallback.
7. Effects (brightness, fade) + audio volume/fade in mixer.
8. Export pipeline (full-res encode).
9. Project save/load (JSON).

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
11. **App UI shell.** Frameless Avalonia window with custom chrome + inline menu bar
    (`File · Edit · Clip · Sequence · Effects · View · Window · Help`); **splitter-resizable**
    Project / Program / Inspector / Timeline panes ([UI.md §1](UI.md)); project title + autosave
    / dirty-state indicator.
12. **Timeline control v1.** Custom-drawn ruler + playhead, clip thumbnails (filmstrip) and audio
    waveforms, drag-move + trim handles, timeline zoom (`⊟ 100% ⊞`), **Snapping**, and the
    **Hand**/**Zoom** view tools. The most involved bespoke control.
13. **Editing tools.** **Select / Blade (razor split) / Slip** tools and **Linked A/V** (move a
    clip and its companion audio together) — a clip-link relation in the model.
14. **Multiple tracks.** Lift the 1V+1A slice to N video + N audio tracks, **`+ Track`**, and
    per-track **Mute/Solo** UI (model support already exists: `AudioTrack.Muted/Solo`, video
    `Enabled`).
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

Open product questions (e.g. the mockup's user-avatar / account affordance, full panel docking)
are tracked in [UI.md §5](UI.md).

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
