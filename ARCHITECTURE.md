# Architecture — Sprocket (.NET 10 cross-platform video editor)

> Companion to [PLAN.md](PLAN.md). PLAN.md is the *what* and the *build order*; this
> document is the *how* and the *why* — the detailed technical design the implementation
> must conform to. See [UI.md](UI.md) for the target UI and its implied features. Library
> facts here were verified against current (mid-2026) releases;
> see [§14 Verified facts](#14-verified-library-facts-mid-2026).

---

## 1. Architectural principles

1. **C# orchestrates; native code and the GPU compute.** Managed code owns the timeline
   model, scheduling, the render graph, A/V sync, UI, and plugin lifetime. It never runs a
   per-pixel loop in the hot path. All pixel math happens in FFmpeg (C) or in Skia/GPU shaders.
2. **Pixels never touch the managed heap per frame.** Decoded frame data lives in FFmpeg's
   native `AVFrame` buffers (reference-counted by libav) and is handed to Skia by *pointer*,
   not by copy. The only per-frame managed objects are tiny structs/handles, ideally pooled.
3. **Non-destructive by construction.** Source media is read-only. An edit changes the
   *description* of how to reconstruct a frame, never the source. A frame at timeline time
   `t` is computed on demand from the project graph.
4. **One managed codebase, three OSes (Windows 11, Linux, macOS).** Platform differences (audio
   backend, hardware-accel device, file dialogs, native-lib packaging) sit behind C# interfaces
   selected at runtime. **No C++/CLI** — all native interop is P/Invoke against a C ABI, so the
   same managed assemblies run on all three; only the bundled native libraries differ per
   RID (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`). Inherently-divergent paths (hardware
   accel) always have a software fallback.
5. **The same render graph serves preview and export.** Preview = the graph driven in real
   time at 1080p/proxy with frame-dropping allowed. Export = the same graph driven offline,
   deterministically, at full resolution with no drops. Effects are written once.
6. **Determinism for export and tests.** Given a project + a time, the rendered frame is a
   pure function of the inputs. This is what makes golden-frame testing possible.

---

## 2. Module / project layout

Projects marked *(planned)* are designed-for but not yet created; the rest exist in
`Sprocket.slnx` today.

```
Sprocket.slnx
├── src/
│   ├── Sprocket.Core         // timeline model, render graph, time model. No native deps. No UI.
│   ├── Sprocket.Media        // FFmpeg (Sdcb.FFmpeg) interop: decode, seek, encode, hw-accel.
│   ├── Sprocket.Render       // SkiaSharp compositing + effects (SKRuntimeEffect shaders).
│   ├── Sprocket.Audio        // mixer + IAudioOutput (Silk.NET.OpenAL). Master clock.
│   ├── Sprocket.Playback     // playback engine: scheduling, ring buffers, A/V sync, transport.
│   ├── Sprocket.Export       // offline export: drives the render graph + mixer → MediaEncoder (MP4).
│   ├── Sprocket.Persistence  // versioned JSON project (de)serialization (System.Text.Json source-gen).
│   ├── Sprocket.Plugins      // (planned, PLAN step 23) IVideoEffect contract + AssemblyLoadContext host.
│   └── Sprocket.App          // Avalonia UI (MVVM): timeline control, preview surface, panels.
│       (Sprocket.Spike — standalone PLAN step 1 de-risk artifact, not part of the app)
└── tests/
    ├── Sprocket.Core.Tests        // headless: render-graph resolution, trim, fades, time math.
    ├── Sprocket.Media.Tests       // decode/seek correctness against known fixtures.
    ├── Sprocket.Audio.Tests       // mixer/clock against fakes; AudioSource decode/resample/seek.
    ├── Sprocket.Render.Tests      // effects produce expected pixels (offscreen raster, SkSL).
    ├── Sprocket.Playback.Tests    // clock, pump drop/hold, present pipeline.
    ├── Sprocket.Export.Tests      // export round-trip: encode → reopen → assert format/effects.
    └── Sprocket.Persistence.Tests // project JSON round-trip, schema version, relink, offline tolerance.
```

**Dependency direction (acyclic):**

```
Sprocket.App ──► Sprocket.Playback ──► Sprocket.Render ──► Sprocket.Core
     │   │          │      │              │
     │   │          │      └──► Sprocket.Audio ──► Sprocket.Core
     │   │          └──► Sprocket.Media ──────────► Sprocket.Core
     │   └──► Sprocket.Export ──► {Core, Media, Render, Audio}
     └──► Sprocket.Persistence ──► Sprocket.Core
                Sprocket.Plugins ──► Sprocket.Core   (planned)
```

`Sprocket.Export` composes the same four lower layers Playback uses plus Audio — it reuses the render
graph (Core), full-res decode (Media), the effect shaders (Render), and the mixer (Audio) to write an
MP4 offline, so it sits beside Playback rather than under it. The FFmpeg muxer (`MediaEncoder`) lives in
Media with the rest of the libav* interop (§11); Export only orchestrates.

**`Sprocket.Core` is the keystone and depends on nothing.** It defines the data model and the
*abstractions* the render graph and engine call into: `IFrameSource`/`IVideoCompositor` (video),
`IPcmReader` (audio PCM pull — the audio analogue of `IFrameSource`), and `IClock`/`IMasterClock`
(the read-only and transport-capable clock). The Media/Render/Audio layers provide concrete
implementations. This keeps the model testable headlessly and keeps native/GPU concerns out of the
domain types. Note **Sprocket.Audio depends only on Core, not Media** — the FFmpeg audio decode
(`AudioSource : IPcmReader`) lives in Media and is wired to the mixer by the composition root, so the
audio mixer/clock stay FFmpeg-free.

---

## 3. Time model

Sloppy time is the #1 source of A/V drift. Rules:

- **Canonical timeline unit: `long` ticks at a fixed high resolution.** Implemented as
  `Timecode.TicksPerSecond = 240000` (`Sprocket.Core.Timing.Timecode`). 240000 is an exact
  multiple of 48 kHz audio (5 ticks/sample) *and* of every common frame rate including the NTSC
  rationals (30000/1001 → 8008 ticks/frame, 24000/1001, 60000/1001) and 24/25/30/50/60, so both
  frame and sample boundaries land on whole ticks and round-trip losslessly. The MPEG 1/90000 base
  was considered but rejected: it is not divisible by 48000 (1.875 ticks/sample) and audio is the
  master clock (§8), where sample-exactness matters most. Avoid `double` seconds for
  positions/durations — accumulating float error desyncs long timelines.
- **Wrap it in a `readonly struct Timecode { long Ticks; }`** with explicit conversions to
  frames (given a frame rate) and audio samples (given a sample rate). All arithmetic in ticks.
- **Frame rates are rational** (`30000/1001`, not `29.97`). Mirror FFmpeg's `AVRational`.
  A `readonly struct Rational(int Num, int Den)`.
- **Frames vs. samples both derive from the same tick clock.** Video frame index =
  `floor(ticks * fps)`; audio sample index = `floor(ticks * sampleRate)`. The audio device
  position (in samples) is converted back to ticks to drive sync (§8).

---

## 4. Data model (Sprocket.Core)

Immutable-friendly, serializable, UI-bindable. The model is **pure data + pure functions**;
it holds no native handles.

```
Project
 ├─ MediaPool : MediaRef[]            // imported sources, addressed by stable Id
 ├─ Timeline
 │   ├─ FrameRate : Rational          // project render rate
 │   ├─ Resolution : (int W, int H)   // project canvas size
 │   ├─ SampleRate : int              // project audio rate (e.g. 48000)
 │   └─ Tracks : Track[]              // z-order: index 0 = bottom, last = top
 └─ Settings

MediaRef         { Id, AbsolutePath, ProbedInfo (duration, streams, fps, sampleRate, codec) }

Track (abstract)
 ├─ VideoTrack   { Clips, Enabled, Opacity, BlendMode }
 └─ AudioTrack   { Clips, Enabled, Gain (dB), Muted, Solo }

Clip
 ├─ MediaRefId                        // which source
 ├─ SourceIn  : Timecode              // in-point within the SOURCE (non-destructive trim)
 ├─ SourceOut : Timecode              // out-point within the SOURCE
 ├─ TimelineStart : Timecode          // where it sits on the timeline
 ├─ Effects : EffectInstance[]        // ordered stack, applied bottom→top
 └─ (Duration = SourceOut - SourceIn)

EffectInstance
 ├─ EffectTypeId : string             // "builtin.brightness", "plugin.acme.glow"
 └─ Parameters : Map<string, AnimatableValue>   // see §9
```

**Non-destructive editing falls out of this for free:** trimming edits `SourceIn/Out`;
moving edits `TimelineStart`; effects are an additive list. The source bytes are never
written.

**Undo/redo is a first-class requirement** (see [BRIEF.md](BRIEF.md)), not an afterthought.
Every model mutation goes through a **command stack** (snapshot or inverse-command) — there is
no "direct" mutation path that bypasses it. Because the model is pure data with no native
handles, commands are cheap to record and reverse, and the design must keep it that way:
editing operations are expressed as commands from the start so undo/redo, command merging
(e.g. coalescing a slider drag), and an edit-history surface come for free as the editor grows.

---

## 5. Render graph — how one frame is produced

The render graph is the heart of the editor. Given a timeline time `t` and a target
resolution, it produces one composited `SKImage`.

```
RenderFrame(Project p, Timecode t, RenderTarget target) -> SKImage
  1. Create/clear a GPU-backed SKSurface of target size (transparent).
  2. For each VideoTrack, bottom → top (skip if !Enabled):
       a. Resolve the active Clip at t:  clip where
             TimelineStart <= t < TimelineStart + Duration
          (slice model: at most one clip per track per instant for the slice;
           generalizes to overlaps/transitions later).
       b. Map timeline t → source time:
             sourceT = clip.SourceIn + (t - clip.TimelineStart)
       c. Ask the clip's IFrameSource for the frame nearest sourceT  → SKImage (GPU).
       d. Build the effect chain: fold clip.Effects into an SKShader/SKPaint pipeline,
          each effect an SKRuntimeEffect taking the previous stage as a child shader
          (see §7). Evaluate effect params at t (animation).
       e. Draw the result onto the surface with the track's Opacity + BlendMode.
  3. surface.Snapshot() -> SKImage  (the composited frame; stays on GPU).
```

Key properties:

- **Pull-based and stateless per call.** `RenderFrame` reads the model and the frame sources;
  it mutates nothing. This is what makes it reusable for both preview and export and trivial
  to unit-test.
- **`IFrameSource` is the seam between Core and Media.** Core asks "give me source `X` at
  time `sourceT`"; the Media layer owns decoding/seeking/caching behind it. Core never sees
  FFmpeg. A test can supply a fake `IFrameSource` that returns synthetic frames.
- **Effects compose as a shader graph, not as N round-trips.** Folding effects into a single
  chained `SKShader` lets Skia run the whole stack in as few GPU passes as possible.
- **Audio is rendered by the parallel audio graph (§6), not here.** This function is video-only.

---

## 6. Audio path (Sprocket.Audio)

Audio is cheap enough to mix in C# with SIMD, and it owns the master clock.

```
For each output buffer request (from the audio device callback):
  1. Determine the timeline range [t, t + bufferDuration) in samples.
  2. For each AudioTrack (respect Mute/Solo):
       a. Resolve active clip; map to source sample range.
       b. Pull decoded PCM (float32, project sample rate) from the audio frame source
          (resampled via swresample at decode time to the project rate/layout).
       c. Apply per-clip gain + fade envelope (a gain ramp across the buffer).
       d. Sum into the mix accumulator (System.Numerics.Vector<float> / SIMD).
  3. Apply master gain, clamp/limit, write to the device buffer (pooled native buffer).
  4. Advance the master sample clock by the buffer's frame count.
```

- **PCM buffers are pooled** (`ArrayPool<float>` or pinned native buffers); no per-callback
  managed allocation.
- **Resampling** (rate/channel-layout normalization to the project format) happens once, at
  decode, via libswresample, so the mixer only ever sums uniform float32 buffers.
- **The audio device callback is the heartbeat.** Its consumed-sample count *is* the master
  clock (§8).
- **Output device behind `IAudioOutput`.** The slice uses Silk.NET.OpenAL (OpenAL Soft), whose
  native package ships `win-x64`, `linux-*`, **`osx-x64` and `osx-arm64`** binaries — so the same
  output works on all three OSes. On macOS this rides OpenAL Soft (Apple's system OpenAL is
  deprecated); a native **CoreAudio** `IAudioOutput` is a later, optional swap if lower latency is
  wanted. *(Implemented PLAN step 5: `OpenAlAudioOutput`, the `AudioMixer`, and the `AudioEngine`
  master clock whose `Now` derives from the device's played-frame count.)*

---

## 7. GPU rendering & effects (Sprocket.Render)

Backed by SkiaSharp on a **GPU-backed surface that shares Avalonia's `GRContext`** (§10), so
composited frames never leave the GPU before being presented.

**Effects are `SKRuntimeEffect` (SkSL) fragment shaders.** Each built-in effect:

```csharp
// Brightness — illustrative SkSL
uniform shader src;     // previous stage (input image or prior effect)
uniform float amount;   // parameter, evaluated at time t
half4 main(float2 p) {
    half4 c = src.eval(p);
    return half4(c.rgb * amount, c.a);
}
```

- The C# `IVideoEffect` implementation compiles its SkSL once (cache the `SKRuntimeEffect`),
  and per frame builds a shader with `effect.ToShader(uniforms, children: [previousStage])`.
- **Chaining:** effect N's `src` child = effect N-1's output shader. The clip's decoded
  `SKImage.ToShader()` is the root. The whole stack resolves in minimal GPU passes.
- **GPU-accelerated** whenever the surface is GPU-backed (it is). Verified: SkSL compiles to
  native GPU code on the active backend.

**Built-ins for the slice:** `Brightness`, plus `Fade` (a time-driven alpha multiply for
video; the audio side is a gain ramp in the mixer §6). Contrast/color follow the same shape.

**Known per-frame allocation:** `effect.ToShader(...)` allocates a managed shader object each
frame because SkiaSharp snapshots uniforms at that call. This is small, bounded, and *not*
pixel data — acceptable under the rule in §1. Pool/reuse uniform buffers to minimize it.

**Color management (forward-looking):** tag `SKImage`s with an `SKColorSpace` (sRGB for the
slice). **Log-encoded input transforms (DJI D-Log et al.) are GPU SkSL effect-chain stages**
backed by embedded 3D LUTs — they land on this exact seam, not on a separate FFmpeg/CPU pipeline
(see [§18](#18-log-media--color-management-d-log)). When OpenColorIO is added later it sits as a
stage in the effect chain via a C-ABI P/Invoke wrapper — no C++/CLI.

---

## 8. The frame lifecycle & A/V sync (Sprocket.Playback)

This is where the layers meet and where correctness is hardest. The pipeline:

```
 ┌────────────┐   AVPacket   ┌────────────┐  AVFrame(native)  ┌──────────────┐
 │  Demux/Read│─────────────►│  Decode     │──────────────────►│ Upload→SKImage│
 │ (per source)│  Channel<>  │ (per source)│   bounded queue   │  (GPU)        │
 └────────────┘             └────────────┘                   └──────┬────────┘
                                                                     │ SKImage handle
 ┌──────────────────────────── render/compose thread ───────────────▼────────┐
 │  pull frames for time t → RenderFrame() → present to Avalonia preview      │
 └────────────────────────────────────────────────────────────────────────────┘
 ┌──────────── audio thread (device callback = MASTER CLOCK) ─────────────────┐
 │  mix buffer for [t, t+n) → write to device → advance master sample clock   │
 └────────────────────────────────────────────────────────────────────────────┘
```

**Threading model:**

- **One demux + decode worker per active source**, feeding a **bounded** `Channel<T>` /
  ring buffer (backpressure prevents runaway memory and unbounded read-ahead).
- **One render/compose thread** consuming decoded video frames, running `RenderFrame`, and
  presenting. Runs on/with Avalonia's render thread via the custom draw lease (§10).
- **One audio thread** = the device callback. It is the clock.
- **UI thread never blocks** on decode/render — it only issues invalidations and reads model.

**Master clock = audio.** Each audio callback reports how many samples have been played →
convert to ticks → that's `nowTicks`. The render thread, for each vsync/tick, asks for the
video frame whose PTS is nearest `nowTicks`:

- If the freshest decoded frame's time **< nowTicks − tolerance** → **drop** frames until
  caught up (video lagging).
- If it's **> nowTicks + tolerance** → **repeat** the current frame (video ahead; hold).
- Tolerance ≈ half a frame interval. Target preview latency 150–400 ms (buffering budget).

**Frame ownership across threads (critical):**

- A decoded `AVFrame` is reference-counted by libav. When a frame is handed from the decode
  worker to the uploader, **take a ref** (`av_frame_ref`) so the decoder may reuse its slot;
  **release** (`av_frame_unref`/`Dispose`) once the GPU upload completes.
- For the slice, the simplest correct path is **synchronous upload then unref**:
  decode → `swscale` to RGBA into a *native* buffer → wrap with `SKImage.FromPixels(pointer)`
  → draw (Skia uploads to GPU) → unref. The native buffer is pooled; no managed pixel array.
- **Frame pooling:** keep a small pool of reusable `Frame` objects and native RGBA scratch
  buffers per source. Decode into pooled frames; never allocate per frame.

**Seeking (scrub / jump):**

```
avformat_seek_file(streamIndex, target, AVSEEK_FLAG_BACKWARD)  // to keyframe ≤ target
avcodec_flush_buffers()                                        // reset decoder
decode-and-discard frames until PTS >= targetPTS               // land frame-accurate
```

Cache a keyframe index per source so scrubbing doesn't re-probe. Decoded frames near the
playhead are cached (small LRU) so single-frame scrubbing is responsive.

---

## 9. Effect parameters & animation

Even the slice's fade needs time-varying parameters, so the model bakes this in from day one:

- A parameter is an **`AnimatableValue`**: either a constant or a list of **keyframes**
  `{ Timecode, Value, Interpolation }`. `Evaluate(t)` returns the interpolated value.
- The render graph evaluates every effect's parameters at the frame's `t` before building the
  shader. A fade is just an `opacity` parameter with two keyframes (1→0 over a range), or a
  dedicated `Fade` effect with `start`/`duration`/`direction` params for ergonomics.
- This same mechanism later powers keyframed brightness, position, etc., with no model change.

---

## 10. Avalonia ↔ Skia GPU integration (the load-bearing seam)

Verified mechanism (Avalonia 12 / SkiaSharp 4.x):

- Implement an `ICustomDrawOperation` (or a `CompositionCustomVisualHandler` for continuous
  render-thread callbacks). Inside `Render`, get
  `context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))`, then `lease = feature.Lease()`.
- The lease exposes **`lease.GRContext`** (Avalonia's shared GPU context) and
  **`lease.SkCanvas`**. Producing our composited `SKImage`/`SKSurface` on **the same
  `GRContext`** means the preview frame is already on the compositor's GPU device — no CPU
  round-trip to present.
- This code runs on the **render thread**; the lease locks the context for thread safety. Our
  compose step must therefore run within that lease (or hand a GPU `SKImage` created on the
  same context).

**Uploading decoded pixels — the three options and our choice:**

| API | Copy? | Use |
|---|---|---|
| `SKImage.FromTexture(grContext, GRBackendTexture, …)` | none | Wrap an existing GPU texture (hardware-decoder output, DMA-BUF/D3D11 shared handle). **True zero-copy. Later optimization.** |
| `SKImage.FromPixels(info, ptr, rowBytes)` | no managed copy; Skia uploads to GPU on demand | Wrap a native RGBA buffer from swscale. **Slice default** — satisfies the no-managed-pixels rule; the GPU upload is unavoidable for software decode. |
| `SKImage.FromPixelCopy(…)` | full copy into Skia heap | **Avoid.** |

**Backend reality:** Avalonia defaults to OpenGL (via ANGLE) on Windows, OpenGL/Vulkan on
Linux, and **Metal (via ANGLE/MoltenVK) on macOS**; Avalonia 12 supports Vulkan. The slice
doesn't depend on which — `FromPixels` works on all, and the `ISkiaSharpApiLeaseFeature` lease
exposes the shared `GRContext` identically on each backend. True zero-copy hardware-decode interop
(DMA-BUF→Vulkan on Linux, D3D11 shared handle on Windows, IOSurface/VideoToolbox on macOS) is
backend-specific and deferred to the hardware-accel milestone (PLAN step 6).

---

## 11. Media layer detail (Sprocket.Media)

Using **Sdcb.FFmpeg 7.x** (library-level libav bindings). Key types: `FormatContext`,
`CodecContext`, `Packet` (`.Unref()`), `Frame` (`.Data` is `IntPtr[]`, `.Linesize` is
`int[]` — **direct native pointers, no managed copy**), pixel conversion via the swscale
wrapper, `MediaDictionary` for options.

- **`MediaSource`** wraps a `FormatContext`: probe (duration, streams, fps, sample rate),
  expose a video `IFrameSource` and an audio PCM source. The probe also reads the video stream's
  **color transfer / primaries / space** (the `AVColorTransferCharacteristic`/`color_primaries`/
  `color_space` codec-parameter fields) and the `AVFormatContext`/`AVStream` **metadata
  dictionary**, surfaced on `ProbedMediaInfo` so a log profile (D-Log) can be auto-detected on
  import ([§18](#18-log-media--color-management-d-log)).
- **Decode loop:** `ReadPacket` → `SendPacket` → `ReceiveFrame` (N frames per packet);
  `packet.Unref()` in `finally`. Reuse a pooled `Frame`.
- **Pixel conversion:** swscale YUV420p/NV12 → RGBA/BGRA into a pooled **native** buffer;
  pass `Frame.Data[0]` + `Frame.Linesize[0]` straight in — never marshal to `byte[]`.
- **Encoder** (export): mirror in reverse — frames → encoder → muxer; pick H.264 encoder
  (`libx264` software for the slice; `h264_nvenc`/`h264_vaapi` later).
- **Hardware accel (later, behind `IHardwareContext`):** `av_hwdevice_ctx_create(type…)`,
  attach to `CodecContext.HwDeviceContext`, set the decoder's `get_format` to the hw pixel
  format, decode to a GPU frame, then either `av_hwframe_transfer_data` (simple, costs a copy)
  or map to a GPU texture for zero-copy (`FromTexture`). Runtime probe of available device types
  per OS — **Windows:** D3D11VA / CUDA / QSV / DXVA2; **Linux:** VAAPI / CUDA / VDPAU;
  **macOS:** VideoToolbox — and **always** fall back to software when none is usable.

**Format coverage — import & export (PLAN step 21b).** Decode is format-agnostic: libav* opens the
common containers (MP4 / MOV / MKV / WebM / AVI / MXF / TS) and codecs (H.264, HEVC, AV1, VP9,
MPEG-2, ProRes, DNxHD/HR; audio AAC / MP3 / PCM / FLAC / AC-3 / Opus), including 10–12-bit,
4:2:2 / 4:4:4, HDR transfer, alpha, and variable-frame-rate sources — swscale normalises whatever
pixel format arrives into the pooled RGBA buffer, so nothing downstream is aware of the source
format. Export generalises the slice's hard-wired H.264/AAC encoder into a **container ×
video-codec × audio-codec matrix** (the back end the step-22 presets select from): muxer and
encoders chosen per export with quality/bitrate, pixel-format/bit-depth, and frame-rate controls;
hardware encoders (NVENC / QSV / AMF / VideoToolbox) behind `IHardwareContext` with a software
(x264 / x265 / SVT-AV1) fallback. The render graph is unchanged — only the muxer/encoder back end
varies (§5/§17). **Export resolution is capped at 4K for now** (≤ 3840×2160 UHD / 4096×2160 DCI;
5K/6K/8K may be enabled later) — an **export-side limit only**; import, the timeline, and sequence
canvas sizes are unrestricted.

**Preview vs. delivery codecs.** Encoding done *for playback* — proxies (§17) and
render-cache/freeze intermediates (§20) — optimises for **encode + decode speed and instant scrub,
not size or final quality**: prefer **all-intra** (no inter-frame dependencies) and **hardware**
encoders, and it is fine for the codec to **vary by host OS**, because these artifacts are local,
regenerable, and never shipped (export always re-renders full-res from originals, §17/§20).
Practical picks, chosen by a runtime encoder probe behind `IHardwareContext`: **macOS** ProRes
(proxy/LT) or HEVC/H.264 via VideoToolbox; **Windows** H.264/HEVC via NVENC / QSV / AMF (D3D11);
**Linux** VAAPI H.264/HEVC or NVENC; cross-platform CPU fallback **MJPEG or x264 *ultrafast*** (all
intra). Audio intermediates are simplest as **uncompressed PCM**. Because the choice only affects a
throwaway cache, it has **no bearing on export determinism** (§1.6).

**Native binaries (per-RID bundling).** `Sdcb.FFmpeg.runtime.windows-x64` NuGet supplies the
FFmpeg 7.1 DLLs on Windows. **On Linux and macOS, Sprocket must ship its own FFmpeg 7 shared
libraries** — there is no Sdcb.FFmpeg Linux or macOS runtime NuGet, and OS packages drift (Ubuntu
24.04 ships FFmpeg 6.1 = `libav*.so.60`, ABI-incompatible with Sdcb 7.0 which loads `…so.61`;
Homebrew tracks the latest major and Apple ships none). Bundle a FFmpeg 7.x shared build per RID:
- **Linux** (`linux-x64`): `libav*.so.61` etc. on the loader path / `LD_LIBRARY_PATH`.
- **macOS** (`osx-x64` and `osx-arm64`): `libav*.61.dylib` etc. next to the executable inside the
  `.app` bundle (`Contents/MacOS` or `Contents/Frameworks`, found via `@loader_path`/`DYLD_*`).
  Ship a build per architecture (Apple Silicon `arm64` is the default; `x64` for Intel Macs).

Sdcb constructs the versioned library name (`libavcodec.so.61`, `libavcodec.61.dylib`, etc.) from
its `LibraryVersionMap` and the OS loader resolves it. The Linux path was confirmed end-to-end in a
.NET 10 container: decode + SkSL + Skia render produced a **byte-identical PNG to the Windows
build**; the macOS path uses the same code and rests on packaging the dylibs (PLAN step 25) +
on-device verification. **Skia natives** come from `SkiaSharp.NativeAssets.{Win32,Linux,macOS}`
per RID; the managed `SkiaSharp` stays pinned to 3.119.4 (§14). **Licensing:** an x264-enabled
build is GPL (the verified Linux build was BtbN `gpl-shared`); choose the build and the product's
license deliberately before any distribution.

---

## 12. Persistence (Sprocket.Persistence)

- Serialize `Project` to **JSON** (`System.Text.Json`, source-generated for AOT-friendliness).
- Store **relative media paths** where possible + an absolute fallback; on load, if a
  `MediaRef` can't be resolved, mark it "offline" and prompt to relink — never fail the load.
- Version the schema (`"schemaVersion"`) from v1 for forward migration.
- The model is plain data (no native handles), so it serializes cleanly and round-trips in
  tests.

*Implemented (PLAN step 9): `ProjectSerializer` over a DTO layer kept separate from the domain model
(the model's constructors/read-only collections/`AnimatableValue` factory don't serialize directly,
and a distinct wire format lets the model evolve behind `schemaVersion`). Source-generated
`System.Text.Json`, camelCase, string enums. Relative paths are resolved against the project-file
directory and preferred when present; missing media loads offline rather than failing. An explicit
"offline" model flag + relink UI is deferred to the UI build-out (the load itself already tolerates it).*

---

## 13. Plugin system (deferred — designed-for, not built)

- Built-in effects already implement `IVideoEffect` (compile SkSL, build shader, declare typed
  params). Plugins implement the same contract.
- Load plugin assemblies in a **collectible `AssemblyLoadContext`** so they can be unloaded;
  isolate dependencies. Discover effects by attribute/interface scan.
- A future P/Invoke adapter can host OSS standards (OFX / frei0r) against their C ABI — again,
  no C++/CLI, so it stays cross-platform.
- Security/stability: plugins run in-process for v1 (trusted); an out-of-process host is a
  later option if untrusted plugins matter.

---

## 14. Verified library facts (mid-2026)

| Component | Version | Notes |
|---|---|---|
| .NET | 10 (LTS) | Server/Background GC; `Span`, SIMD intrinsics, source-gen JSON. Single managed build runs on `win-x64`/`linux-x64`/`osx-x64`/`osx-arm64`. |
| Avalonia | 12.0.5 | `ISkiaSharpApiLeaseFeature.Lease()` → `GrContext`+`SkCanvas`; `CompositionCustomVisualHandler`. Native desktop on Windows/Linux/**macOS** (Metal/OpenGL); Vulkan supported. |
| SkiaSharp | **3.119.4** | Pinned to Avalonia 12.0.5's transitive SkiaSharp so the lease's Skia types match (a newer feed loads a 2nd incompatible assembly). `SKRuntimeEffect.CreateShader` (SkSL), `SKImage.FromTexture`/`FromPixels`, GPU surfaces. Natives per RID via `SkiaSharp.NativeAssets.{Win32,Linux,macOS}`. |
| Sdcb.FFmpeg | 7.0.0 (FFmpeg 7.1) | `FormatContext`/`CodecContext`/`Packet`/`Frame`; `Frame.Data` = `IntPtr[]`. Win binaries via `Sdcb.FFmpeg.runtime.windows-x64`; **Linux & macOS must bundle FFmpeg 7 `.so`/`.dylib` (see §11)** — no Sdcb Linux/macOS runtime NuGet, OS versions vary. |
| Silk.NET.OpenAL | 2.23 | Cross-platform audio out behind `IAudioOutput`; `Silk.NET.OpenAL.Soft.Native` 1.23.1 ships `win`/`linux`/**`osx-x64`+`osx-arm64`** binaries. (Silk.NET 2.x in limited maintenance — abstracted; macOS CoreAudio is an optional later swap.) |

---

## 15. Cross-cutting concerns

- **Error handling:** the decode/render pipeline must degrade, not crash — a bad frame logs
  and is skipped; an offline media ref renders as a placeholder. Native interop is wrapped so
  FFmpeg error codes become typed exceptions at the `Sprocket.Media` boundary only.
- **Diagnostics:** structured logging (`Microsoft.Extensions.Logging`); counters for fps,
  dropped frames, decode-queue depth, A/V drift, GC gen0 rate — surfaced in a debug overlay.
- **Cancellation:** every worker honors a `CancellationToken`; seeking/stop drains queues
  cleanly and unrefs all in-flight frames (no leaked native buffers).

---

## 16. Risk register (architecture-specific)

| Risk | Mitigation |
|---|---|
| GC pause in the render/audio hot loop | No managed pixels (§1); pool frames/buffers; profile gen0 ≈ 0 in the spike. |
| A/V drift over long timelines | Tick-based time (§3); audio master clock (§8); rational frame rates. |
| Avalonia/Skia GPU lease misuse (wrong thread) | All compose work inside the render-thread lease (§10); never touch `GRContext` off-thread. |
| FFmpeg native frame leaks across threads | Strict ref/unref ownership protocol (§8); cancellation drains in-flight frames. |
| Zero-copy hardware interop complexity | Slice uses `FromPixels` (software path); defer `FromTexture`/DMA-BUF/D3D11/IOSurface to the hw milestone with software fallback retained. |
| FFmpeg GPL vs LGPL licensing | Decide build + product license before distribution (§11). |
| macOS packaging (`.app` bundle, FFmpeg dylibs, code signing/notarization) | Bundle per-RID FFmpeg 7 dylibs via the loader path (§11); produce a signed/notarized `.app` in the distribution step (PLAN step 25). Universal vs per-arch (`osx-arm64`/`osx-x64`) decided there. |
| Native-lib drift across the three OSes | Single managed build; only per-RID natives differ. CI builds/runs on win/linux/macOS runners; the headless render path is byte-identical across OSes (verified win↔linux). |

---

## 17. What this architecture defers (and why it's safe to)

The slice intentionally omits: multiple overlapping clips/transitions per track, hardware
decode/encode, OpenColorIO color grading, the plugin host, and proxy media. Each slots into
an existing seam without redesign — transitions extend clip resolution in the render graph;
hardware accel is a new `IFrameSource`/`IHardwareContext` impl; color grading is another
effect-chain stage; **log media (D-Log) is an input color-transform effect-chain stage** (§18);
plugins implement the existing `IVideoEffect`. The seams (`IFrameSource`,
`IVideoEffect`, `IAudioOutput`, `IHardwareContext`, `IClock`) exist precisely so these land as
additions, not rewrites.

**Proxy media** is a committed feature (see [BRIEF.md](BRIEF.md)), deferred to post-slice but
designed-for here. A proxy is a lower-resolution, edit-friendly re-encode of a source. It is
**another `IFrameSource` implementation** selected per `MediaRef`: when proxies are enabled and
present, preview/editing pull frames from the proxy (faster decode, lighter GPU load); **export
always pulls from the full-resolution original**, so proxies never affect output quality. Because
the render graph addresses sources through `IFrameSource` and clips reference a `MediaRefId`
(not a decoder), proxy generation + a "use proxies" toggle land as a Media-layer addition with
no change to `Sprocket.Core`. Determinism (§1.6) still holds — the *active* source for a given
render mode is a pure input to `RenderFrame`.

**Default-on without interrupting flow.** Proxies are an *optimization layer, never a gate*: the
per-`MediaRef` source resolver returns the **best source available for the current mode** — preview
takes a Ready proxy if one exists, otherwise the original; export always takes the original. So "use
proxies" defaults **on**, yet a just-imported clip previews from its original immediately and
**transparently** switches to the proxy once a background service finishes encoding it (per-`MediaRef`
state `None → Queued → Building → Ready/Failed`). That service runs a **bounded** worker pool off the
hot path with hardware / all-intra / OS-specific encoders (§11 "Preview vs. delivery codecs"), a
**priority queue** (timeline / playhead / active-sequence media first), and a **local, regenerable**
proxy store (the cache-dir family of §20 and the per-user sidecar of the collaboration split) that
survives restarts. Proxy **resolution is a fixed tier, not the live preview window** — sizing to a
constantly-resizing window would thrash an expensive, persisted artifact — defaulting to
`min(½ source, 1080p)` (the 1080p preview ceiling) and **skipping sources already light enough to
preview in real time**; the tier is a preference for weaker machines, and a >proxy-resolution view
(zoom 100/200 %, PLAN step 17) falls back to the original. A **draft-first two-tier** scheme (a fast
low-res proxy before the quality one) is deferred and conditional: since the original is the interim
fallback it only helps *heavy* sources, and it slots into the same best-available order
(quality > draft > original) as another `IFrameSource` with no redesign. Proxying a **composited
sequence** rather than a source clip is the render cache (§20), sharing this same background-encode +
fast-codec machinery.

**Later capabilities land the same way** — each is an addition on an existing seam, detailed in its
own section but noted here so the "additive, not a rewrite" invariant stays canonical:

- **Nested sequences / compound clips** — a sequence rendered as a clip is **another `IFrameSource` /
  `IPcmReader`** to the parent graph (the graph already turns a (timeline, t) into a frame, §5); Core
  gains `Project.Sequences[]` and a `Clip.SequenceId` source alongside `MediaRefId`, plus cycle
  detection. No render-graph redesign (PLAN step 19b).
- **Audio effects & VST3/AU plugins** — the audio analogue of the video effect chain: a new
  `IAudioEffect` seam reusing the pure-data `EffectInstance`/`AnimatableValue` model, executed in the
  mixer (§6); native VST3/AU hosting is reached through a **flat C-ABI bridge** per the no-C++/CLI rule
  (§1.4), exactly like the FFmpeg/Skia natives (§19, PLAN step 23b).
- **Render cache / pre-render ("freeze")** — **not new graph machinery** but memoization of the pure
  render function (§1.6), surfaced back through the same `IFrameSource`/`IPcmReader` source seams and
  invalidated by the existing command/model state; export still pulls full-res originals (§20, PLAN
  step 23c).
- **Color grading** — wheels / curves / qualifiers are further `IVideoEffect` effect-chain stages
  (§7); scopes read the rendered frame. Another effect-chain addition, like log media (§18, PLAN
  step 23d).
- **Collaboration-ready format & asset-link split** — persistence-layer only (§12): the diffable
  project file references sources by stable `MediaRef` **Id**, while each user's absolute asset paths
  move to a separate local sidecar, so a pulled project-file change never forces a relink. The Core
  model is untouched (PLAN step 19c).

---

## 18. Log media & color management (D-Log)

DJI drones (and most cinema cameras) record in a **logarithmic gamma** — for DJI, **D-Log**, with
the variants **D-Log M** (milder curve, newer drones) and **D-Log 2** (wider gamut). A log curve
compresses highlights and shadows into a flat, low-contrast image to preserve dynamic range; it
must be converted to a display color space (Rec.709) by a color transform before it looks correct.
Supporting it is fundamentally a **color-pipeline** problem, and Sprocket already has the right
seam for it.

**It lives on the effect chain, as an input transform.** A log decode is a new built-in
`IVideoEffect` (`builtin.colortransform`) realised as an `SKRuntimeEffect` (SkSL) fragment shader,
exactly like `Brightness`/`Fade` (§7). It is **prepended to the clip's effect stack** so the
log→Rec.709 decode runs *first*, ahead of any creative grade — the professional "input transform →
working space → grade" ordering — achieved with **zero new render-graph machinery**: it is simply
the effect at index 0, resolved and chained by `RenderGraph` like any other (§5 step 2d). Because
the same render graph drives preview and export (§5), the transform is identical in both; because
it is a pure function of (project, time) it preserves golden-frame determinism (§1.6, §6 of PLAN
verification).

**Why *not* FFmpeg's `lut3d` filter.** A tempting shortcut is to run FFmpeg's `lut3d`/`zscale`
filter and push the baked frames to an Avalonia `WriteableBitmap`. **Sprocket rejects this.** A
`WriteableBitmap` is a managed CPU bitmap, so an FFmpeg-filter→bitmap path reintroduces exactly the
**per-frame managed pixel copy + CPU round-trip** that §1 (the non-negotiable performance rule)
exists to forbid, and it splits color into a side pipeline that would make preview and export
diverge, breaking §5. FFmpeg's `lut3d` also only reads a LUT from a *file path* (forcing temp-file
extraction). All color math therefore stays **on the GPU in Skia**, consistent with every other
effect — FFmpeg's role remains decode/encode only (§11).

**3D LUTs in Skia (the mechanism).** The transform uses DJI's official `.cube` 3D LUTs (accurate,
and the only public source for the exact M/2 curves). Skia's `SKRuntimeEffect` has no native
3D-texture child, so the N³ LUT is **packed into a 2D texture** (N tiles of N×N — the standard
layout) supplied to the shader as a `uniform shader lut` child; the SkSL does manual **trilinear**
interpolation between the two bracketing blue slices. This keeps the transform a single GPU pass
and reuses the existing shader-graph chaining (effect N's `src` child = effect N-1's output, §7).
The same packed-LUT sampler serves any future creative LUT. The `.cube` files ship as
`EmbeddedResource`s in `Sprocket.Render` — the first bundled data asset (today all effects are
inline SkSL strings) — decoded once into the packed texture and cached for reuse.

**Variant detection.** DJI writes its color profile into container metadata. The Media-layer probe
reads the video stream's color transfer/primaries/space and the format/stream metadata dictionary
(§11) and surfaces them on `ProbedMediaInfo`; the app maps a detected `D-Log` / `D-Log M` /
`D-Log 2` profile to the matching embedded LUT and auto-inserts the input transform on import. When
detection is absent or wrong, the profile is a **manual per-clip override** in the Inspector
(PLAN step 16). The chosen transform is just an `EffectInstance`, so it serializes with the project
for free (§12); the new `ProbedMediaInfo` color fields are additive (nullable/defaulted, no schema
bump).

**Export & scopes.** Export **bakes** the transform in by default (it is an ordinary effect-chain
stage in the same graph) or, as a per-export toggle, **passes through** the original log encoding
for downstream grading. Waveform/monitor scopes (PLAN step 17) gain a **log ↔ transformed** toggle
so colorists can read either space.

**Upgrade path.** This LUT-based approach is the pragmatic 90% solution. A full scene-linear,
OpenColorIO/ACES color-managed pipeline remains the later upgrade (PLAN step 23); OCIO would slot
in as another effect-chain stage via a C-ABI P/Invoke wrapper — no C++/CLI — exactly as §7's color
note and §17 anticipate. Nothing here forecloses it.

---

## 19. Audio effects & plugin hosting (Sprocket.Audio)

The audio analogue of the §7 video effect chain. Today audio carries only per-clip gain + fade in
the mixer (§6); real audio work (EQ, compression, reverb) needs a chain, and Sprocket targets
third-party **VST3** and **Audio Unit** plugins as well as built-ins.

**The seam: `IAudioEffect` (Core), mirroring `IVideoEffect`.** The pure-data `EffectInstance`
(`EffectTypeId` + `AnimatableValue` parameters, §4/§9) is reused unchanged for audio — an audio
effect chain is an ordered `EffectInstance[]`. Chains attach at four scopes, processed in order:
**clip → track → sequence/bus → master**. Core stays FFmpeg-free and DSP-free; it only describes
*which* effects in *what* order with *what* (possibly keyframed) parameters.

**Execution in the mixer (§6).** After the mixer pulls and sums a track's PCM for the current
buffer, it runs each chain as an **in-place block DSP pass** over the per-buffer native float32
accumulator — block-based and **allocation-free on the audio thread** (§1). Each effect sees
`(sampleRate, channelLayout, frameCount)` and a deadline. Plugins that report **processing latency**
are compensated (plugin delay compensation) so effected tracks stay aligned — important because
audio is the **master clock** (§6/§8), so an uncompensated latency would skew A/V sync.

**Built-in managed effects first.** Parametric EQ (biquads), compressor, reverb, and gain/pan as
pure C# DSP (`System.Numerics` SIMD where it helps) — no native dependency, cross-platform, and
**deterministic** (golden-PCM testable like the render graph).

**Native plugin hosting (VST3 + AU) — via a C-ABI bridge, no C++/CLI.** The VST3 SDK is C++/COM-style
and Audio Units are Objective-C; both violate the managed-only rule if bound directly. Per §1.4 each
format is reached through a thin **native bridge shared library** (`sprocket_vst3host` /
`sprocket_auhost`) that wraps the vendor SDK and exposes a **flat C ABI** — `scan`, `instantiate`,
`setActive`, `process(float** in, float** out, int frames)`, `get/setParameter`, `get/setState`,
`openEditor(nativeWindowHandle)` — exactly the way the FFmpeg/Skia natives are consumed. C# P/Invokes
that ABI; one managed build serves all OSes, only the per-RID bridge libs differ (VST3 on all three,
AU on macOS only; bundled with the natives in PLAN steps 24–25).

**Threading.** Plugin **scan / instantiate / setState** run **off** the audio thread (a background
plugin service); only `process` and parameter ramps touch the audio thread. A plugin's **own editor
GUI** runs on the UI thread, embedded via the OS window handle the bridge accepts
(HWND / NSView / X11 window); parameter edits from the GUI are marshalled to the audio thread
lock-free.

**Automation.** Effect/plugin parameters are `AnimatableValue`s (§9), so they **keyframe like any
effect** (PLAN step 16d). The mixer evaluates them per block (sample-accurate ramp within a block)
and pushes them to the plugin via `setParameter` before `process`.

**Persistence (§12).** An audio `EffectInstance` serializes as plugin id (format + vendor UID) + its
automation + an **opaque state blob** (VST3 component+controller state / AU `ClassInfo`, base64).
The blob is opaque to Core — bytes in, bytes out. A plugin that isn't installed loads **offline**:
the chain **bypasses** it (passthrough) and flags it for relink rather than failing the load (§15).

**Determinism caveat.** Built-in effects are deterministic and unit-tested. **Native plugins are not
guaranteed** bit-identical across versions/hosts, so they are excluded from golden-audio tests — and
this is a second reason to **pre-render** their output (§20). **Licensing:** the VST3 SDK is
GPLv3-or-Steinberg-dual-licensed; decide the build/product license before distribution, as with
FFmpeg (§11/§16).

---

## 20. Render cache / pre-render ("freeze" & render in-to-out)

Nested sequences (§5 + PLAN step 19b), adjustment-layer spans (PLAN step 19), deep effect chains,
and audio plugin chains (§19) can make a frame or buffer expensive to compute on **every** playback
pass. NLEs solve this with **preview render files**: compute a range once, replay the cache,
invalidate on edit. Sprocket can do the same with **no new render-graph machinery**, because of one
property it already has.

**Why it is sound: the graph is a pure function.** `RenderFrame`/the audio graph are **deterministic
functions of (project, t) with no hidden state** (§5, §6, §1.6). A cached output is therefore valid
*exactly while the inputs that produced it are unchanged*. The same determinism that makes
golden-frame testing possible makes caching safe.

**The mechanism reuses the existing source seams (the elegant part).** A pre-rendered range is
exposed back to the parent graph as **just another `IFrameSource` (video) / `IPcmReader` (audio)** —
the identical seam used by media, **proxies** (§17), and nested sequences. The render graph never
learns about caching; it asks a source for frames/PCM at `t`, and that source happens to be a
rendered intermediate.

- **Video:** render the subgraph once to a **fast all-intra intermediate** on disk (via the existing
  `MediaEncoder`, §11) for longer ranges, or a **bounded GPU/host texture ring** for short scrub
  ranges, and read it back through a cache `IFrameSource`. Pixels stay native/GPU (§1) — the
  intermediate decodes through the normal native path, never a managed per-frame array. The
  intermediate codec is chosen for **speed and may vary by host OS** (§11 "Preview vs. delivery
  codecs") — the cache is local and regenerable, so this never affects export.
- **Audio:** render a clip/track/sequence/bus chain once to **cached PCM** (disk or memory) and read
  it back through a cache `IPcmReader`. This is **"freezing"** a track — especially valuable for the
  CPU-heavy and non-deterministic native plugins of §19.

**Keying & invalidation.** A cache entry is keyed by a **content hash of the cached subtree's
serializable state** (the same DTO that persists, §12) + time range + render settings (resolution,
sample rate). Every mutation goes through the command stack against pure-data model (§4), so any edit
re-hashes the affected subtree and marks its range **dirty**; invalidation is **exact** (no stale
frames survive an edit).

**Surface & storage.** A **render bar** over the timeline ruler shows ranges as rendered (cached &
valid) vs. needs-render (dirty) — the familiar green/yellow/red model — with *Render In to Out*,
*Render Selection*, *Render Audio*, and *Delete Render Files* commands; rendering runs in the
background (cancellable, §15). The cache is a **local, derived artifact** in a cache directory beside
the project — **not** in the diffable project file and **not** shared or merged (it belongs with the
per-user sidecar split, PLAN step 19c). It is always **safely discardable**: deleting it only forces
recompute; correctness never depends on it existing.

**Export stays authoritative.** Export **ignores the preview cache by default** and re-renders from
**full-resolution originals** (§17) so output is deterministic and full-quality — the same rule
proxies follow (preview accelerates; export is full-res). Reusing a cache produced at full quality
with matching settings is an opt-in optimization, never the default.

Net: pre-render is **memoization of a pure function**, surfaced through the existing `IFrameSource` /
`IPcmReader` seams and invalidated by the existing command/model state. It composes with proxies
(§17), nested sequences (§5), adjustment layers, and audio plugins (§19) with no redesign.
