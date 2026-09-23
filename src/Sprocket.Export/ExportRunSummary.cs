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
/// <param name="VideoEncode">Pixel read-back plus <c>MediaEncoder.WriteVideoFrame</c>, including RGBA → encoder
/// pixel-format conversion and any hardware-device upload.</param>
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
public sealed record ExportRunSummary(
    ExportAcceleration RequestedAcceleration,
    string RequestedVideoEncoder,
    string ActualVideoEncoder,
    bool HardwareVideoEngaged,
    long VideoFrames,
    long AudioSampleFrames,
    ExportStageTimings Timings)
{
    /// <summary>Whether hardware encoding was requested but every GPU candidate failed, so the software encoder ran.</summary>
    public bool FellBackToSoftware =>
        RequestedAcceleration == ExportAcceleration.Hardware && !HardwareVideoEngaged;

    /// <summary>Whether this was an audio-only export (no video stream).</summary>
    public bool IsAudioOnly => ActualVideoEncoder.Length == 0;
}
