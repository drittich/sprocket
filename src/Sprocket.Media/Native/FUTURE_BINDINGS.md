# Future binding surface (PLAN.md roadmap)

The hand-rolled binding is **curated** — it exposes exactly what Sprocket calls today. This is the
roadmap of what later PLAN steps will add, with the FFmpeg 8.1 x64 struct offsets for those fields
**already captured** (from FFmpeg.AutoGen 8.1, the dev-time layout oracle — see the offset list and
regen procedure in `SPIKE_RESULTS.md`) so each future addition is just "add a `[LibraryImport]` line in
`LibAv.cs` + paste the recorded `[FieldOffset]` into `AvStructs.cs`" — no need to re-run the oracle until
a new FFmpeg **major**. Always confirm against the byte-identical/golden gate.

## What needs NO new binding (handled elsewhere)
- **Proxy media (18)** — shells out to the `ffmpeg` CLI (in-process concurrent muxing crashed; see PLAN).
- **Thumbnails / waveforms (15)** — reuse `MediaSource`/`AudioSource` decode (already bound).
- **Transitions (25), generators/adjustment (19), sequences (23), multicam (24), ripple/roll (22),
  retime (21), markers (20), interchange/EDL/FCPXML (28)** — render-graph / model only, no FFmpeg.
- **Color grading (34), log/HDR transforms (37 color math)** — SkSL/Skia on the GPU, explicitly NOT
  FFmpeg `lut3d` (that would break §1/§5). No binding.
- **Audio loudness/LUFS (30)** — planned as managed BS.1770 DSP on the §6 audio path. Only needs
  libavfilter if implemented via the `ebur128` filter (a whole new avfilter surface — defer unless chosen).

## What needs new binding surface

### Step 37 — log/HDR + Step 27 — HDR-transfer probe  (LOW effort)
Read color metadata in `MediaSource.Probe`, extend `ProbedMediaInfo`.
- Functions: `av_dict_get` (avutil) for `AVStream.metadata` / `AVFormatContext.metadata`.
- `AVCodecParameters` fields: `color_range=100`, `color_primaries=104`, `color_trc=108`, `color_space=112`.
- `AVStream.metadata=80`, `AVFormatContext.metadata=192`.
- More `AVPixelFormat`/`AVColorTransferCharacteristic` int constants (trivial).

### Step 27 — export codec matrix  (LOW effort, mostly constants)
HEVC/AV1/VP9/ProRes/DNxHD + MP3/FLAC/AC-3/Opus/PCM; MOV/MKV/WebM/AVI/MXF/TS.
- Already have `avcodec_find_encoder` / `_by_name` and `avformat_alloc_output_context2(formatName,…)`.
- Add codec-id constants + more pixel/sample formats (10–12 bit, 4:2:2/4:4:4) — int consts only.
- Finer encoder control via `AVCodecContext` fields: `profile=688`, `level=692`, `max_b_frames=200`,
  `qmin=436`, `qmax=440`, `global_quality=420` (plus `av_dict_set`, already bound, for `crf`/`preset`).

### Step 27 / 32 — hardware encode (NVENC/QSV/AMF/VideoToolbox) & render cache  (MEDIUM effort)
- Functions (avutil): `av_hwframe_ctx_alloc`, `av_hwframe_ctx_init`, `av_hwframe_get_buffer`
  (`av_hwframe_transfer_data`, `av_buffer_ref/unref` already bound; reuse `IHardwareContext`).
- `AVCodecContext.hw_frames_ctx=552` (set on the encoder; `hw_device_ctx=560` already bound).
- New `AVHWFramesContext` view (sizeof 80): `initial_pool_size=56`, `format=60`, `sw_format=64`,
  `width=68`, `height=72`.
- `AVCodecContext` color out fields if tagging HDR output: `color_primaries=144`, `color_trc=148`,
  `colorspace=152`, `color_range=156`.

### Possible — rotation / display matrix (phone video, implied by step 27 robustness)
- `av_frame_get_side_data` / `av_packet_side_data_get` + `av_display_rotation_get`, side-data type
  `AV_FRAME_DATA_DISPLAYMATRIX`. Add when VFR/rotation handling is prioritized.
- `AVFrame` misc already captured: `pict_type=120`, `flags=276`.

### Step 31 — VST3/AU audio plugins
- Not FFmpeg — separate native C-ABI bridge shims per format (see PLAN §31 / ARCHITECTURE §13).
