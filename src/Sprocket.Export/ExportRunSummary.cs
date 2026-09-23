namespace Sprocket.Export;

/// <summary>
/// Where a completed export's time went (export-speed phase 1), measured with the monotonic
/// <see cref="System.Diagnostics.Stopwatch"/> clock. The per-stage fields are accumulated over the run and do not
/// sum to <see cref="Total"/>: <see cref="Total"/> also covers setup (encoder/device open, surface creation) and the
/// muxer finalize / trailer write, which no per-frame stage owns.
/// </summary>
/// <param name="VideoDecode">Source seek + decode inside the export frame providers (cache hits are free), including
/// reverse-clip GOP window refills.</param>
/// <param name="VideoRender">Render-graph resolution, effect setup, raster compositing, burn-ins, and flush — the
/// render call's elapsed time minus the decode it triggered.</param>
/// <param name="VideoEncode">RGBA → encoder pixel-format conversion (on the render workers since export-speed phase 3,
/// summed across them) plus <c>MediaEncoder.WriteVideoFrame</c> on the mux thread, including any hardware-device
/// upload.</param>
/// <param name="AudioMix"><c>AudioMixer.MixInto</c> only.</param>
/// <param name="AudioEncode"><c>MediaEncoder.WriteAudioFrame</c> only.</param>
/// <param name="Total">From the start of setup through a successful <c>MediaEncoder.Finish</c>.</param>
public readonly record struct ExportStageTimings(
    TimeSpan VideoDecode,
    TimeSpan VideoRender,
    TimeSpan VideoEncode,
    TimeSpan AudioMix,
    TimeSpan AudioEncode,
    TimeSpan Total);

/// <summary>
/// The measured facts of one <b>successful</b> export (export-speed phase 1): what was asked for, which encoder
/// actually ran, how much was written, and where the time went. Returned by
/// <see cref="VideoExporter.ExportWithSummary"/> only after the output is finalized — a cancelled or failed export
/// throws instead, so a summary never describes a partial file.
/// </summary>
/// <param name="RequestedAcceleration">The acceleration the options asked for.</param>
/// <param name="RequestedVideoEncoder">The software-family encoder for the chosen codec (e.g. <c>libx264</c>); the
/// hardware probe is an ordered candidate chain, so this plus <see cref="RequestedAcceleration"/> is the request.
/// Empty for an audio-only export.</param>
/// <param name="ActualVideoEncoder">The encoder that actually opened (<c>MediaEncoder.VideoEncoderName</c>), e.g.
/// <c>h264_nvenc</c> or <c>libx264</c>. Empty for an audio-only export.</param>
/// <param name="HardwareVideoEngaged">Whether a hardware encoder opened (<c>MediaEncoder.IsHardwareVideo</c>) — never
/// inferred from the encoder name.</param>
/// <param name="VideoFrames">Video frames written.</param>
/// <param name="AudioSampleFrames">Audio sample frames (per-channel samples) written; 0 when audio was skipped.</param>
/// <param name="Timings">Per-stage and total elapsed time.</param>
/// <param name="Mode">The export pipeline that ran (export-speed phase 3): Final or Fast Export.</param>
/// <param name="Decode">How the video sources decoded — hardware vs software per source, and any mid-export fallback.
/// All-software (with nothing requested) unless Fast Export's GPU-decode opt-in is on; empty for an audio-only
/// export.</param>
/// <param name="SoftwarePreset">The <c>preset</c> the software video encoder ran with (e.g. <c>medium</c>, or Fast
/// Export's speed-first <c>veryfast</c>), or <see langword="null"/> when a GPU encoder engaged, the codec takes no
/// preset, or the export is audio-only.</param>
public sealed record ExportRunSummary(
    ExportAcceleration RequestedAcceleration,
    string RequestedVideoEncoder,
    string ActualVideoEncoder,
    bool HardwareVideoEngaged,
    long VideoFrames,
    long AudioSampleFrames,
    ExportStageTimings Timings,
    ExportMode Mode = ExportMode.Final,
    ExportDecodeSummary Decode = default,
    string? SoftwarePreset = null)
{
    /// <summary>Whether hardware encoding was requested but every GPU candidate failed, so the software encoder ran.</summary>
    public bool FellBackToSoftware =>
        RequestedAcceleration == ExportAcceleration.Hardware && !HardwareVideoEngaged;

    /// <summary>Whether this was an audio-only export (no video stream).</summary>
    public bool IsAudioOnly => ActualVideoEncoder.Length == 0;
}

/// <summary>
/// How an export's video sources decoded (export-speed phase 3). Counted per distinct source media — a source opened
/// by several render workers counts once: as hardware only if every instance decoded on the GPU to the end, and as a
/// fallback if any instance's GPU decoder failed mid-export and was reopened in software. Stills are excluded (a
/// single frame decoded once, always in software). All zero for an audio-only export.
/// </summary>
/// <param name="HardwareRequested">Whether GPU decode was attempted — Fast Export with the
/// <see cref="ExportGpuDecode"/> opt-in. False otherwise: Final Export always decodes in software for reproducible
/// output, and Fast Export does by default (it measured faster).</param>
/// <param name="HardwareSources">Sources that decoded on the GPU for the whole export.</param>
/// <param name="SoftwareSources">Sources that decoded in software — no GPU decoder for their codec / device, GPU decode
/// not requested or disabled, or a mid-export fallback (those are also counted in <see cref="FallbackSources"/>).</param>
/// <param name="FallbackSources">Sources whose GPU decoder opened but failed during the export and were reopened in
/// software, resuming at the same frame.</param>
/// <param name="HardwareDevice">The GPU device type that decoded — or, for sources that fell back, failed to (e.g.
/// <c>d3d11va</c>, <c>cuda</c>) — or <see langword="null"/> when no source opened on the GPU.</param>
/// <param name="DisabledByUser">Whether GPU decode was requested but switched off by the <c>SPROCKET_HWACCEL</c>
/// environment override.</param>
public readonly record struct ExportDecodeSummary(
    bool HardwareRequested,
    int HardwareSources,
    int SoftwareSources,
    int FallbackSources,
    string? HardwareDevice,
    bool DisabledByUser = false)
{
    /// <summary>Total video sources decoded (stills excluded).</summary>
    public int Sources => HardwareSources + SoftwareSources;
}
