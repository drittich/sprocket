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
4. **One managed codebase, two OSes.** Platform differences (audio backend, hardware-accel
   device, file dialogs) sit behind C# interfaces selected at runtime. **No C++/CLI** — all
   native interop is P/Invoke against a C ABI. Inherently-divergent paths (hardware accel)
   always have a software fallback.
5. **The same render graph serves preview and export.** Preview = the graph driven in real
   time at 1080p/proxy with frame-dropping allowed. Export = the same graph driven offline,
   deterministically, at full resolution with no drops. Effects are written once.
6. **Determinism for export and tests.** Given a project + a time, the rendered frame is a
   pure function of the inputs. This is what makes golden-frame testing possible.

---

## 2. Module / project layout

```
Sprocket.sln
├── src/
│   ├── Sprocket.Core         // timeline model, render graph, time model. No native deps. No UI.
│   ├── Sprocket.Media        // FFmpeg (Sdcb.FFmpeg) interop: decode, seek, encode, hw-accel.
│   ├── Sprocket.Render       // SkiaSharp compositing + effects (SKRuntimeEffect shaders).
│   ├── Sprocket.Audio        // mixer + IAudioOutput (Silk.NET.OpenAL). Master clock.
│   ├── Sprocket.Playback     // playback engine: scheduling, ring buffers, A/V sync, transport.
│   ├── Sprocket.Persistence  // project (de)serialization to JSON.
│   ├── Sprocket.Plugins      // (later) IVideoEffect contract + AssemblyLoadContext host.
│   └── Sprocket.App          // Avalonia UI (MVVM): timeline control, preview surface, panels.
└── tests/
    ├── Sprocket.Core.Tests        // headless: render-graph resolution, trim, fades, time math.
    ├── Sprocket.Media.Tests       // decode/seek correctness against known fixtures.
    └── Sprocket.Render.Tests      // golden-frame: effects produce expected pixels.
```

**Dependency direction (acyclic):**

```
Sprocket.App ──► Sprocket.Playback ──► Sprocket.Render ──► Sprocket.Core
     │              │      │              │
     │              │      └──► Sprocket.Audio ──► Sprocket.Core
     │              └──► Sprocket.Media ──────────► Sprocket.Core
     └──► Sprocket.Persistence ──► Sprocket.Core
                Sprocket.Plugins ──► Sprocket.Core
```

**`Sprocket.Core` is the keystone and depends on nothing.** It defines the data model and the
*abstractions* the render graph calls into (`IFrameSource`, `IVideoEffect`, `IClock`). The
Media/Render/Audio layers provide concrete implementations. This keeps the model testable
headlessly and keeps native/GPU concerns out of the domain types.

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
slice). When OpenColorIO is added later it sits as a stage in the effect chain via a C-ABI
P/Invoke wrapper — no C++/CLI.

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

**Backend reality:** Avalonia defaults to OpenGL (via ANGLE) on Windows and OpenGL/Vulkan on
Linux; Avalonia 12 supports Vulkan. The slice doesn't depend on which — `FromPixels` works on
all. True zero-copy hardware-decode interop (DMA-BUF→Vulkan on Linux, D3D11 shared handle on
Windows) is backend-specific and deferred to the hardware-accel milestone (PLAN step 6).

---

## 11. Media layer detail (Sprocket.Media)

Using **Sdcb.FFmpeg 7.x** (library-level libav bindings). Key types: `FormatContext`,
`CodecContext`, `Packet` (`.Unref()`), `Frame` (`.Data` is `IntPtr[]`, `.Linesize` is
`int[]` — **direct native pointers, no managed copy**), pixel conversion via the swscale
wrapper, `MediaDictionary` for options.

- **`MediaSource`** wraps a `FormatContext`: probe (duration, streams, fps, sample rate),
  expose a video `IFrameSource` and an audio PCM source.
- **Decode loop:** `ReadPacket` → `SendPacket` → `ReceiveFrame` (N frames per packet);
  `packet.Unref()` in `finally`. Reuse a pooled `Frame`.
- **Pixel conversion:** swscale YUV420p/NV12 → RGBA/BGRA into a pooled **native** buffer;
  pass `Frame.Data[0]` + `Frame.Linesize[0]` straight in — never marshal to `byte[]`.
- **Encoder** (export): mirror in reverse — frames → encoder → muxer; pick H.264 encoder
  (`libx264` software for the slice; `h264_nvenc`/`h264_vaapi` later).
- **Hardware accel (later, behind `IHardwareContext`):** `av_hwdevice_ctx_create(type…)`,
  attach to `CodecContext.HwDeviceCtx`, decode to GPU frame, then either
  `av_hwframe_transfer_data` (simple, costs a copy) or map to a GPU texture for zero-copy
  (`FromTexture`). Runtime probe of available device types; **always** fall back to software.

**Native binaries (verified on both OSes):** `Sdcb.FFmpeg.runtime.windows-x64` NuGet supplies
the FFmpeg 7.1 DLLs on Windows. **On Linux, Sprocket must ship its own FFmpeg 7 `.so` files** —
there is no Sdcb.FFmpeg Linux runtime NuGet, and distro packages drift (Ubuntu 24.04 ships
FFmpeg 6.1 = `libav*.so.60`, ABI-incompatible with Sdcb 7.0 which loads `…so.61`). Bundle a
FFmpeg 7.x shared build next to the app (or on the loader path / `LD_LIBRARY_PATH`); Sdcb
constructs the versioned soname (`libavcodec.so.61`, etc.) from its `LibraryVersionMap` and the
OS loader resolves it. This was confirmed end-to-end in a .NET 10 container: decode + SkSL +
Skia render produced a **byte-identical PNG to the Windows build**. **Licensing:** an x264-enabled
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
| .NET | 10 (LTS) | Server/Background GC; `Span`, SIMD intrinsics, source-gen JSON. |
| Avalonia | 12.0.5 | `ISkiaSharpApiLeaseFeature.Lease()` → `GrContext`+`SkCanvas`; `CompositionCustomVisualHandler`. Vulkan supported. |
| SkiaSharp | **3.119.4** | Pinned to Avalonia 12.0.5's transitive SkiaSharp so the lease's Skia types match (a newer feed loads a 2nd incompatible assembly). `SKRuntimeEffect.CreateShader` (SkSL), `SKImage.FromTexture`/`FromPixels`, GPU surfaces. |
| Sdcb.FFmpeg | 7.0.0 (FFmpeg 7.1) | `FormatContext`/`CodecContext`/`Packet`/`Frame`; `Frame.Data` = `IntPtr[]`. Win binaries via `Sdcb.FFmpeg.runtime.windows-x64`; **Linux must bundle FFmpeg 7 `.so` (see §11)** — no Sdcb Linux runtime NuGet, distro versions vary. |
| Silk.NET.OpenAL | 2.23 | Cross-platform audio out; behind `IAudioOutput`. (Silk.NET 2.x in limited maintenance — abstract it.) |

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
| Zero-copy hardware interop complexity | Slice uses `FromPixels` (software path); defer `FromTexture`/DMA-BUF/D3D11 to the hw milestone with software fallback retained. |
| FFmpeg GPL vs LGPL licensing | Decide build + product license before distribution (§11). |

---

## 17. What this architecture defers (and why it's safe to)

The slice intentionally omits: multiple overlapping clips/transitions per track, hardware
decode/encode, OpenColorIO color grading, the plugin host, and proxy media. Each slots into
an existing seam without redesign — transitions extend clip resolution in the render graph;
hardware accel is a new `IFrameSource`/`IHardwareContext` impl; color grading is another
effect-chain stage; plugins implement the existing `IVideoEffect`. The seams (`IFrameSource`,
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
