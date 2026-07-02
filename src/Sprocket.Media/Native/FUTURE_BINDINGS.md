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

### Step 37 — log/HDR + Step 27 — HDR-transfer probe  (✅ DONE)
Read color metadata in `MediaSource.Probe`, extend `ProbedMediaInfo`.
- **✅ HDR-transfer probe DONE (step 27):** `AVCodecParameters.color_trc=108` bound + `ColorTrcSmpte2084/AribStdB67`
  consts; `ProbedMediaInfo.IsHdr` set from it.
- **✅ Color-metadata probe DONE (step 37):** `AVCodecParameters.color_range=100`/`color_primaries=104`/
  `color_space=112` and `AVStream.metadata=80` pasted as recorded (`AVFormatContext.metadata=192` already existed);
  bound `av_dict_get` (empty-key + `AV_DICT_IGNORE_SUFFIX` iteration, new `AVDictionaryEntry` view) and the four
  `av_color_*_name` functions so the probe records names, never enum ints. Validated by the Media color-probe tests
  against a `setparams`-tagged fixture.
- **Still unbound (deferred with the export HDR-tagging work):** `AVCodecContext` color-out fields for HDR *output*
  tagging (`color_primaries=144`, `color_trc=148`, `colorspace=152`, `color_range=156`) — see the step-29 note below.

### Step 27 — export codec matrix  (✅ DONE)
HEVC/AV1/VP9/ProRes + MP3/FLAC/AC-3/Opus/PCM; MOV/MKV/WebM/AVI/MPEG-TS all shipped. Chose to select encoders +
pixel/sample formats **by name/negotiation** rather than baking codec-id/format enum tables:
- `avformat_alloc_output_context2(formatName,…)` (container override), `avcodec_find_encoder_by_name` (already bound).
- Added `av_get_pix_fmt`/`av_get_sample_fmt`/`av_sample_fmt_is_planar`/`av_get_pix_fmt_name`, `avcodec_get_name`,
  and **`avcodec_get_supported_config`** (the FFmpeg-8 replacement for the removed `AVCodec.pix_fmts`/`sample_fmts`)
  + `AvPixFmtDescriptor` chroma-log2/comp0-depth. Quality via `av_dict_set` `crf`/`preset` (already bound) + bit rate.
- Not needed after all: `AVCodecContext.profile/level/max_b_frames/qmin/qmax/global_quality` (private-option
  `av_dict` + the supported-config negotiation covered the matrix). Add them only if finer per-codec control is wanted.

### Step 27 / 29 — hardware encode (NVENC/QSV/AMF/VideoToolbox/VAAPI)  (✅ DONE, step 29)
Shipped as the final step-29 strand. The enabling seam (`MediaEncoder` picks encoders **by name**) was extended with
`VideoEncoderSettings.HardwareCandidates` — an ordered chain probed before the software `CodecName`, engaging the
first that opens and falling back to software otherwise (`MediaEncoder.IsHardwareVideo` reports which). The candidate
list is built per-OS by `ExportCodecs.HardwareEncoderCandidates` and gated on `ExportOptions.Acceleration`. Two feed
paths, chosen by the encoder's advertised pixel formats: encoders that list a CPU format (NVENC/QSV/AMF/VideoToolbox)
are handed an nv12 CPU frame and upload internally; device-surface-only encoders (VAAPI) take the `hw_frames_ctx`
upload path. Bound for it:
- Functions (avutil): **`av_hwframe_ctx_alloc`**, **`av_hwframe_ctx_init`**, **`av_hwframe_get_buffer`** added
  (`av_hwframe_transfer_data`, `av_buffer_ref/unref` were already bound; `IHardwareContext` / `HardwareDevice` reused).
- **`AVCodecContext.hw_frames_ctx=552`** bound (`hw_device_ctx=560` already was).
- **`AVHWFramesContext` view** added (`initial_pool_size=56`, `format=60`, `sw_format=64`, `width=68`, `height=72`),
  reached through a new minimal **`AvBufferRef`** view (`data=8`). `AV_PIX_FMT_FLAG_HWACCEL=(1<<3)` + `AV_PIX_FMT_NV12=23`
  added to `AvConst`.
- **Not done** — `AVCodecContext` color-out fields for HDR *output* tagging (`color_primaries=144`, `color_trc=148`,
  `colorspace=152`, `color_range=156`): unneeded for hardware encode; they belong with the step-37 log/HDR color work.

### Possible — rotation / display matrix (phone video, implied by step 27 robustness)
- `av_frame_get_side_data` / `av_packet_side_data_get` + `av_display_rotation_get`, side-data type
  `AV_FRAME_DATA_DISPLAYMATRIX`. Add when VFR/rotation handling is prioritized.
- `AVFrame` misc already captured: `pict_type=120`, `flags=276`.

### Step 31 — VST3/AU audio plugins
- Not FFmpeg — separate native C-ABI bridge shims per format (see PLAN §31 / ARCHITECTURE §13).
