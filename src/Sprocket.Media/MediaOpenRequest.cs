using System.Globalization;
using Sprocket.Core.Model;

namespace Sprocket.Media;

/// <summary>
/// How to open one source with the Media layer — a plain container file, or an image sequence via FFmpeg's
/// <c>image2</c> demuxer with its frame rate / start number (PLAN.md step 42). Derived from a
/// <see cref="MediaRef"/> by <see cref="FromMediaRef"/> so every open call site can be swept through one
/// mapper keyed off <see cref="MediaRef.Kind"/> rather than each re-deciding how to open a sequence.
/// </summary>
/// <param name="Path">The path (or printf pattern for an image sequence) handed to the demuxer.</param>
/// <param name="InputFormatName">The demuxer short name to force (e.g. <c>"image2"</c>), or
/// <see langword="null"/> to let FFmpeg probe by extension (ordinary files).</param>
/// <param name="Options">Demuxer options (e.g. <c>framerate</c> / <c>start_number</c> / <c>pattern_type</c>),
/// or <see langword="null"/> for none.</param>
public readonly record struct MediaOpenRequest(
    string Path,
    string? InputFormatName = null,
    IReadOnlyList<KeyValuePair<string, string>>? Options = null)
{
    /// <summary>A plain open by path — ordinary container files, and single stills (which the <c>image2</c>
    /// demuxer already auto-detects for one image).</summary>
    public static MediaOpenRequest ForPath(string path) => new(path);

    /// <summary>
    /// Builds the open request for <paramref name="media"/>. An <see cref="MediaKind.ImageSequence"/> opens its
    /// <see cref="MediaRef.SequencePattern"/> through the <c>image2</c> demuxer at the intrinsic
    /// <see cref="ProbedMediaInfo.FrameRate"/> and <see cref="MediaRef.SequenceStartNumber"/>; every other kind
    /// (including <see cref="MediaKind.Still"/>) opens its <see cref="MediaRef.AbsolutePath"/> plainly.
    /// </summary>
    public static MediaOpenRequest FromMediaRef(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Kind != MediaKind.ImageSequence)
            return ForPath(media.AbsolutePath);

        string pattern = media.SequencePattern
            ?? throw new InvalidOperationException("An image-sequence media reference has no SequencePattern.");
        Core.Timing.Rational fps = media.Info.FrameRate;
        var options = new KeyValuePair<string, string>[]
        {
            new("framerate", $"{fps.Num}/{fps.Den}"),
            new("start_number", media.SequenceStartNumber.ToString(CultureInfo.InvariantCulture)),
            new("pattern_type", "sequence"),
        };
        return new MediaOpenRequest(pattern, "image2", options);
    }
}
