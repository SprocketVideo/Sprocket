using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>A stable, serialization-friendly identifier for a <see cref="Sequence"/> in the project.</summary>
public readonly record struct SequenceId(Guid Value)
{
    /// <summary>Creates a fresh unique id.</summary>
    public static SequenceId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A named, editable timeline (PLAN.md step 23). A project holds one or more sequences; one is the
/// <see cref="Project.ActiveSequence"/> being edited. A sequence can be <b>placed inside another sequence as a
/// clip</b> (a nested sequence / compound clip): to the render graph that nested clip is just another source
/// that renders the child sequence's timeline at the requested time (ARCHITECTURE.md §5, §17), so editing a
/// child updates everywhere it is referenced — these are references, not copies.
/// </summary>
/// <remarks>
/// The sequence is a thin identity wrapper around the existing <see cref="Model.Timeline"/> (which already holds
/// the render format, tracks, and markers). Keeping the timeline as the content container means the slice's whole
/// timeline/render/playback/export stack — which addresses <c>project.Timeline</c> — works unchanged on the
/// active sequence; nesting is purely additive.
/// </remarks>
public sealed class Sequence
{
    /// <summary>Creates a sequence with the given id, name, and timeline.</summary>
    public Sequence(SequenceId id, string name, Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        Id = id;
        Name = name ?? string.Empty;
        Timeline = timeline;
    }

    /// <summary>Stable id used by clips (<see cref="Clip.SourceSequenceId"/>) to nest this sequence.</summary>
    public SequenceId Id { get; }

    /// <summary>Display name (e.g. "Sequence 1"). Renamed through the command stack so it stays undoable.</summary>
    public string Name { get; set; }

    /// <summary>The sequence's content: render format, tracks (z-ordered), and markers.</summary>
    public Timeline Timeline { get; }

    /// <summary>
    /// The sequence in point (the I key), or <see langword="null"/> when unset. With <see cref="MarkOut"/> it
    /// scopes Play / Render In to Out, the export range, and Lift / Extract. Per-sequence like the in/out marks
    /// of leading editors, so switching sequences restores each one's range; persisted with the project but
    /// kept out of the render graph (it never changes a frame). Set through <see cref="Commands.SetSequenceMarksCommand"/>.
    /// </summary>
    public Timecode? MarkIn { get; set; }

    /// <summary>The sequence out point (the O key), or <see langword="null"/> when unset. See <see cref="MarkIn"/>.</summary>
    public Timecode? MarkOut { get; set; }

    /// <summary>
    /// Source patching (PLAN.md step 61 phase 3): which tracks a Source-monitor Insert / Overwrite lands on — the
    /// <c>V1</c>/<c>A1</c> source indicators of leading editors. <see langword="null"/> means the default patch
    /// (see <see cref="ResolvePatch"/>). Per sequence, persisted with the project, kept out of the render graph.
    /// Set through <see cref="Commands.SetSourcePatchCommand"/>.
    /// </summary>
    public SourcePatch? SourcePatch { get; set; }

    /// <summary>
    /// The tracks the source streams are patched to right now. An unset patch — or a stream patched to a track no
    /// longer in the timeline (a deleted track; undoing the delete re-inserts the same track, so the patch comes
    /// back with it) — falls back to the default: the bottom video track and the first audio track. A stream
    /// explicitly un-patched resolves to <see langword="null"/>.
    /// </summary>
    public (VideoTrack? Video, AudioTrack? Audio) ResolvePatch()
    {
        SourcePatch patch = SourcePatch ?? Model.SourcePatch.Default;
        VideoTrack? video = patch.VideoUnpatched ? null
            : patch.Video is { } v && Timeline.Tracks.Contains(v) ? v : Timeline.VideoTracks.FirstOrDefault();
        AudioTrack? audio = patch.AudioUnpatched ? null
            : patch.Audio is { } a && Timeline.Tracks.Contains(a) ? a : Timeline.AudioTracks.FirstOrDefault();
        return (video, audio);
    }
}

/// <summary>
/// A sequence's source patch (PLAN.md step 61 phase 3): per stream, a specific track, the default track
/// (<see langword="null"/> track, not un-patched), or un-patched. Tracks are held by reference, so adding or
/// removing other tracks never re-points the patch.
/// </summary>
/// <param name="Video">The patched video track, or <see langword="null"/> for the default.</param>
/// <param name="Audio">The patched audio track, or <see langword="null"/> for the default.</param>
/// <param name="VideoUnpatched">Whether the video stream is un-patched (a source's video isn't edited in).</param>
/// <param name="AudioUnpatched">Whether the audio stream is un-patched.</param>
public sealed record SourcePatch(
    VideoTrack? Video = null, AudioTrack? Audio = null, bool VideoUnpatched = false, bool AudioUnpatched = false)
{
    /// <summary>The default patch: both streams on their default tracks.</summary>
    public static SourcePatch Default { get; } = new();
}
