using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// What kind of source a <see cref="MediaRef"/> addresses (PLAN.md step 42). Ordinary movie/audio files
/// are <see cref="File"/>; the two stop-motion / VFX on-ramps are a folder of numbered stills imported as
/// one clip (<see cref="ImageSequence"/>) and a single still with a default on-timeline duration
/// (<see cref="Still"/>). The enum drives how the Media layer opens the source (the FFmpeg <c>image2</c>
/// demuxer vs. a plain container) and how the editor bounds trimming (a still is unbounded, like a
/// generator).
/// </summary>
public enum MediaKind
{
    /// <summary>An ordinary movie/audio file opened by container probing (the default).</summary>
    File,

    /// <summary>A folder of numbered stills opened as one video clip via FFmpeg's <c>image2</c> demuxer at a
    /// chosen frame rate (<see cref="MediaRef.SequencePattern"/> / <see cref="MediaRef.SequenceStartNumber"/> /
    /// <see cref="MediaRef.SequenceFrameCount"/>).</summary>
    ImageSequence,

    /// <summary>A single still image. Decoded once and held; its on-timeline headroom is unbounded (the
    /// probed <see cref="ProbedMediaInfo.Duration"/> is only the initial drop length).</summary>
    Still,
}

/// <summary>A stable, serialization-friendly identifier for an entry in the <see cref="MediaPool"/>.</summary>
public readonly record struct MediaRefId(Guid Value)
{
    /// <summary>Creates a fresh unique id.</summary>
    public static MediaRefId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Facts probed from a source media file (by the Media layer) at import time. Pure data so it
/// serializes with the project and is available to the core model without opening the file again.
/// </summary>
/// <param name="Duration">Total duration of the source.</param>
/// <param name="HasVideo">Whether the source has a usable video stream.</param>
/// <param name="FrameRate">Video frame rate (rational). <see cref="Rational.Zero"/> if no video.</param>
/// <param name="Width">Video width in pixels (0 if no video).</param>
/// <param name="Height">Video height in pixels (0 if no video).</param>
/// <param name="HasAudio">Whether the source has a usable audio stream.</param>
/// <param name="SampleRate">Audio sample rate in Hz (0 if no audio).</param>
/// <param name="Channels">Audio channel count (0 if no audio).</param>
/// <param name="HasAlpha">Whether the source's video carries an alpha channel (e.g. ProRes 4444, QuickTime
/// Animation / <c>qtrle</c>, PNG). Drives the premultiplied-alpha compositing path (PLAN.md step 26) and the
/// media-bin "Alpha" badge (UI.md §3.3). Defaults to <see langword="false"/> so audio-only / opaque sources
/// and pre-step-26 callers are unaffected.</param>
/// <param name="VideoCodec">Canonical short name of the source video codec (e.g. <c>"h264"</c>, <c>"hevc"</c>,
/// <c>"prores"</c>), or <c>""</c> when there is no video. Informational (media-bin display, PLAN.md step 27).</param>
/// <param name="AudioCodec">Canonical short name of the source audio codec (e.g. <c>"aac"</c>, <c>"flac"</c>),
/// or <c>""</c> when there is no audio.</param>
/// <param name="PixelFormatName">The source video pixel-format name (e.g. <c>"yuv420p"</c>, <c>"yuv422p10le"</c>),
/// or <c>""</c> when there is no video — conveys chroma subsampling and bit depth compactly.</param>
/// <param name="BitDepth">Bits per component of the source video (8/10/12), or 8 when unknown / no video.</param>
/// <param name="IsHdr">Whether the source declares an HDR transfer function (PQ / HLG). Informational for now
/// (an HDR-aware tone-map is a later color step); surfaced as a media-bin badge.</param>
/// <param name="IsVariableFrameRate">Heuristic flag that the source is variable-frame-rate (its average and base
/// frame rates disagree) — VFR sources are decoded frame-accurately by PTS regardless, so this is informational.</param>
/// <param name="ColorRange">The declared color range as its canonical FFmpeg name (<c>"tv"</c> / <c>"pc"</c>),
/// or <c>""</c> when unspecified — color-metadata probe (PLAN.md step 37). Informational.</param>
/// <param name="ColorPrimaries">The declared color primaries name (e.g. <c>"bt709"</c>, <c>"bt2020"</c>),
/// or <c>""</c> when unspecified.</param>
/// <param name="ColorTransfer">The declared transfer characteristic name (e.g. <c>"bt709"</c>, <c>"smpte2084"</c>,
/// <c>"arib-std-b67"</c>), or <c>""</c> when unspecified. <see cref="IsHdr"/> is derived from the same field.</param>
/// <param name="ColorSpace">The declared color space / matrix name (e.g. <c>"bt709"</c>, <c>"bt2020nc"</c>),
/// or <c>""</c> when unspecified.</param>
/// <param name="DetectedColorProfile">A log profile auto-detected from the container/stream metadata at import
/// (a <see cref="ColorProfiles"/> id, e.g. <see cref="ColorProfiles.DjiDLog"/>), or <c>""</c> when none was
/// recognised. Drives the automatic prepend of the <see cref="EffectTypeIds.ColorTransform"/> input transform
/// when the media is placed on the timeline; a manual per-clip tag via the Inspector is the fallback.</param>
public sealed record ProbedMediaInfo(
    Timecode Duration,
    bool HasVideo,
    Rational FrameRate,
    int Width,
    int Height,
    bool HasAudio,
    int SampleRate,
    int Channels,
    bool HasAlpha = false,
    string VideoCodec = "",
    string AudioCodec = "",
    string PixelFormatName = "",
    int BitDepth = 8,
    bool IsHdr = false,
    bool IsVariableFrameRate = false,
    string ColorRange = "",
    string ColorPrimaries = "",
    string ColorTransfer = "",
    string ColorSpace = "",
    string DetectedColorProfile = "");

/// <summary>
/// A reference to an imported source file, addressed by a stable <see cref="MediaRefId"/>.
/// The source is read-only; clips refer to it by id (ARCHITECTURE.md §4). The path is stored so
/// persistence can relink offline media on load (§12).
/// </summary>
public sealed class MediaRef
{
    /// <summary>Creates a media reference.</summary>
    public MediaRef(MediaRefId id, string absolutePath, ProbedMediaInfo info)
    {
        Id = id;
        AbsolutePath = absolutePath;
        Info = info;
    }

    /// <summary>Stable id used by clips to address this source.</summary>
    public MediaRefId Id { get; }

    /// <summary>
    /// Absolute path to the source on disk at import time. For an <see cref="MediaKind.ImageSequence"/> this
    /// is the resolved first-frame path; the printf pattern the Media layer feeds the <c>image2</c> demuxer is
    /// <see cref="SequencePattern"/>.
    /// </summary>
    public string AbsolutePath { get; set; }

    /// <summary>Streams/duration/format probed at import.</summary>
    public ProbedMediaInfo Info { get; set; }

    /// <summary>What kind of source this is (PLAN.md step 42). Defaults to <see cref="MediaKind.File"/>, so
    /// ordinary imports and pre-step-42 projects are unaffected.</summary>
    public MediaKind Kind { get; set; } = MediaKind.File;

    /// <summary>
    /// For an <see cref="MediaKind.ImageSequence"/>, the printf-style absolute path pattern (e.g.
    /// <c>C:\shot\frame_%04d.png</c>) the FFmpeg <c>image2</c> demuxer expands; <see langword="null"/> for
    /// other kinds. Core stores the string only — no IO (ARCHITECTURE.md §4).
    /// </summary>
    public string? SequencePattern { get; set; }

    /// <summary>For an <see cref="MediaKind.ImageSequence"/>, the number of the first frame in the run (the
    /// <c>start_number</c> the demuxer begins at). Unused for other kinds.</summary>
    public int SequenceStartNumber { get; set; }

    /// <summary>For an <see cref="MediaKind.ImageSequence"/>, the count of contiguous numbered frames in the
    /// run. With the intrinsic <see cref="ProbedMediaInfo.FrameRate"/> this is the single source of truth for
    /// the sequence's <see cref="ProbedMediaInfo.Duration"/> (<c>count / fps</c>). Unused for other kinds.</summary>
    public int SequenceFrameCount { get; set; }

    /// <summary>
    /// Whether the source's on-timeline headroom is unbounded — a <see cref="MediaKind.Still"/> extends freely
    /// like a step-19 generator, so trim/drop clamps must not cap it at <see cref="ProbedMediaInfo.Duration"/>
    /// (PLAN.md step 42).
    /// </summary>
    public bool HasUnboundedDuration => Kind == MediaKind.Still;
}
