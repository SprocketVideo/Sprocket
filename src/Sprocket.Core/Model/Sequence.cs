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
}
