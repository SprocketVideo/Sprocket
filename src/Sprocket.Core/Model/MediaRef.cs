using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

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
public sealed record ProbedMediaInfo(
    Timecode Duration,
    bool HasVideo,
    Rational FrameRate,
    int Width,
    int Height,
    bool HasAudio,
    int SampleRate,
    int Channels,
    bool HasAlpha = false);

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

    /// <summary>Absolute path to the source on disk at import time.</summary>
    public string AbsolutePath { get; set; }

    /// <summary>Streams/duration/format probed at import.</summary>
    public ProbedMediaInfo Info { get; set; }
}
