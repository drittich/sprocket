# Phase 0 — FFmpeg-8 binding spike results

Three-arm de-risk spike for migrating `Sprocket.Media` off the dormant **Sdcb.FFmpeg 7.0.0** to a
FFmpeg-8-tracking binding. Each arm ran the **same representative round-trip** against the same
bundled FFmpeg 8.1 shared natives (BtbN `win64-gpl-shared`, sonames avcodec-**62** / avutil-**60** /
swscale-**9** / swresample-**6**):

> open + probe + `av_find_best_stream` → decode first frame → `sws_scale` YUV→RGBA (native pointer,
> ARCHITECTURE §1) → `sws_scale` RGBA→YUV420P → H.264 (libx264) encode → write elementary stream.

> **Where the spike code lives.** The throwaway three-arm project (`src/Sprocket.Spike.Bindings`,
> referencing FFmpeg.AutoGen 8.1 + Flyleaf 8.1) was **removed from the tree after the decision** so the
> shipping repo carries no dev-only binding NuGets. It is preserved in git history on the
> `ffmpeg8-migration` branch / this migration commit's parent — restore it from there to re-run the
> round-trip or the offset probe (see *Regenerating offsets* below).

## Measured results (320×240 fixture, Windows x64)

| Arm | RGBA hash | H.264 hash | Gen0/frame | decode | encode | Extra deployed assembly |
|---|---|---|---|---|---|---|
| **Hand-rolled `[LibraryImport]`** | `1d3db4c7…` | `66b17f68…` | **0** | ~1.2 ms | ~4.0 ms | **0 KB** (in-app, ~80-line surface) |
| FFmpeg.AutoGen 8.1.0 | `1d3db4c7…` | `66b17f68…` | 0 | ~1.2 ms | ~4.0 ms | **632 KB** (`FFmpeg.AutoGen.dll`) |
| Flyleaf.FFmpeg.Bindings 8.1.0 | `1d3db4c7…` | `66b17f68…` | 0 | ~1.2 ms | ~4.0 ms | **320 KB** (`Flyleaf.FFmpeg.Bindings.dll`) |

- **RGBA byte-identical across all three: PASS** (`1d3db4c788bb26f9`). This also validates the
  hand-rolled struct offsets and pixel-format constants — a wrong offset would corrupt the output or crash.
- **H.264 byte-identical across all three: PASS** (`66b17f68eea1e391`, 2987 B; `thread_count=1`).
- **Zero Gen0 GC per frame on all three** — §1 (no managed pixel allocation) holds; decoded pixels stay
  in native AVFrame buffers, handed off by pointer.

## Decision criteria

| Criterion | Hand-rolled | AutoGen 8.1 | Flyleaf 8.1 |
|---|---|---|---|
| **Native sourcing per RID** (gating) | identical — all BYO; none bundle natives. win-x64 proven; linux-x64 = BtbN; **macOS = the hard one, equally for all three** | same | same |
| **Runtime performance** | identical (native + Skia dominate; §1 holds) | identical | identical |
| **Footprint / AOT** | **0 KB extra; `[LibraryImport]` = source-generated marshalling, canonical AOT/trim-safe; smallest trim surface** | +632 KB; classic `[DllImport]` + `RootPath` dynamic loader (fn-pointer indirection) | +320 KB; CppSharp-generated |
| **API surface exposed** | exactly what's used (~25 fns, 6 structs) | ~3045 members | ~3345 members |
| **HW-accel surface** | add ~6 `av_hwdevice_*`/`get_format` fns to the curated set (bounded) | already present | already present |
| **Maintenance / cadence control** | **self-owned; regen ≈ once per FFmpeg *major* (~yearly); zero maintainer dependency** | active now, but reintroduces maintainer-cadence dependency — the exact risk that stranded us on Sdcb | active now, same dependency |
| **Ergonomics / LOC now** | must curate the surface (bounded; ~hundreds of lines for the full app) | whole API free | whole API free |

## Key findings

1. **Binding choice is not a runtime-perf lever.** All three produce byte-identical output with
   identical timing and zero per-frame allocation — the heavy work is native libav*/libx264/Skia.
2. **The hand-rolled risk is struct layouts, and it is fully mitigable.** Function P/Invokes are
   trivial; the danger is field offsets. The exact x64 offsets are derivable from FFmpeg.AutoGen's
   generator-produced layouts and baked into `[FieldOffset]` structs (`AvStructs.cs`). 64-bit layouts
   are uniform across win/linux/macOS x64+arm64 because FFmpeg uses fixed-width types. The
   **byte-identical gate is the standing correctness check** for any future regen.
3. **macOS shared-FFmpeg-8 sourcing is the real remaining risk — and it is identical for all three.**
   The binding choice does not change it; Phase 8 settled it (bundle a shared FFmpeg 8 build per RID +
   `install_name_tool` → `@loader_path` rewrite on a macOS build host).

## Recommendation (adopted): **Hand-rolled curated `[LibraryImport]`**

It is the only arm that satisfies the owner's two explicit priorities — **cadence control** (own the
binding; regen per FFmpeg major; never be stranded by a dormant upstream again, the failure that
prompted this) and **footprint/AOT** (0 KB extra, source-generated blittable marshalling). Runtime
performance and output are provably identical to the libraries, so nothing is sacrificed there. The
historical objection to hand-rolling — getting struct layouts right — is retired by the
derived-offsets + byte-identical-verification workflow proven here.

The bounded curation cost and manual native-lifetime management are contained by the **thin RAII
layer** (`Handles.cs` / `SwsScaler` / `SwrResampler`).

## Regenerating offsets for a new FFmpeg major

The offsets in `AvStructs.cs` are pinned to **FFmpeg 8.1 x64** (`FFmpegLoader` enforces libavcodec
major **62**). A SONAME bump (i.e. a new FFmpeg **major** — minors are ABI-compatible) is the only
trigger for a regen. FFmpeg.AutoGen is the **dev-time layout oracle** for that — it is *not* a runtime
dependency. To regen:

1. Restore the `src/Sprocket.Spike.Bindings` project from git history (or recreate a throwaway console
   app referencing the new `FFmpeg.AutoGen` major).
2. For each struct/field the binding reads, dump the layout with
   `System.Runtime.InteropServices.Marshal.OffsetOf<T>("field")` and `Unsafe.SizeOf<T>()` against
   AutoGen's generated structs (the spike's `OffsetProbe.Dump()` printed exactly this set, plus the
   enum/const int values — `AV_PIX_FMT_*`, `AV_CODEC_ID_*`, `AV_HWDEVICE_TYPE_*`, `AVSEEK_FLAG_BACKWARD`,
   `AV_CODEC_FLAG_GLOBAL_HEADER`, etc.).
3. Paste the new `[FieldOffset]` values into `AvStructs.cs` and bump the guarded major in
   `FFmpegLoader`.
4. Re-run the byte-identical/golden gate (`dotnet test` — the recorded RGBA `1d3db4c788bb26f9` /
   H.264 `66b17f68eea1e391` hashes are the standing check) on Windows **and** Linux.
