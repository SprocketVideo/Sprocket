using System;
using System.Globalization;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>Which of a source's streams a drag from the Source monitor carries (PLAN.md step 61 phase 4).</summary>
public enum SourceStreams
{
    /// <summary>Video and audio — dragging the Source picture itself.</summary>
    Both,

    /// <summary>Video only — Premiere's "Drag Video Only" handle.</summary>
    Video,

    /// <summary>Audio only — Premiere's "Drag Audio Only" handle.</summary>
    Audio,
}

/// <summary>
/// The payload of a drag from the Source monitor (<see cref="DragFormats.SourceRange"/>, PLAN.md step 61 phase 4): the
/// media, the marked source range at the moment the drag began, and which streams it carries. Serialized as a small
/// invariant-culture string so it rides the same string drag format as the other payloads.
/// </summary>
/// <param name="MediaRefId">The dragged media.</param>
/// <param name="SourceIn">The source in point (media time).</param>
/// <param name="SourceOut">The source out point (media time, exclusive).</param>
/// <param name="Streams">The streams the drag carries.</param>
public sealed record SourceRangePayload(MediaRefId MediaRefId, Timecode SourceIn, Timecode SourceOut, SourceStreams Streams)
{
    /// <summary>Whether the drag carries the source's video (when it has any).</summary>
    public bool WantsVideo => Streams != SourceStreams.Audio;

    /// <summary>Whether the drag carries the source's audio (when it has any).</summary>
    public bool WantsAudio => Streams != SourceStreams.Video;

    /// <summary>The wire form: <c>guid|inTicks|outTicks|streams</c>.</summary>
    public string Format() => string.Join('|',
        MediaRefId.Value.ToString(),
        SourceIn.Ticks.ToString(CultureInfo.InvariantCulture),
        SourceOut.Ticks.ToString(CultureInfo.InvariantCulture),
        Streams.ToString());

    /// <summary>Parses <see cref="Format"/>'s output; <see langword="null"/> for anything malformed or an empty range.</summary>
    public static SourceRangePayload? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        string[] parts = text.Split('|');
        if (parts.Length != 4
            || !Guid.TryParse(parts[0], out Guid guid)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long inTicks)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long outTicks)
            || !Enum.TryParse(parts[3], out SourceStreams streams)
            || !Enum.IsDefined(streams)
            || inTicks < 0 || outTicks <= inTicks)
            return null;
        return new SourceRangePayload(new MediaRefId(guid), new Timecode(inTicks), new Timecode(outTicks), streams);
    }
}
