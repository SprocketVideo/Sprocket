using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// The colour band of a <see cref="Marker"/>, for visual coding on the ruler / clip body and grouping in the
/// markers panel (PLAN.md step 20). The set mirrors the standard NLE marker palette; <see cref="Blue"/> is the
/// default.
/// </summary>
public enum MarkerColor
{
    /// <summary>The default marker colour.</summary>
    Blue,
    Cyan,
    Green,
    Yellow,
    Orange,
    Red,
    Magenta,
    Purple,
    White,
}

/// <summary>
/// A review/coordination annotation pinned to a point (or span) in time (PLAN.md step 20, UI.md §3.6). Markers
/// live either on the <see cref="Timeline"/> (sequence markers, times = timeline positions) or on a
/// <see cref="Clip"/> (clip markers, times = positions within the clip's source). They are added / moved /
/// deleted through the command stack so they are undoable, drawn on the ruler / clip body, navigable, and listed
/// in the markers panel. Mutable plain data like <see cref="Clip"/>, so edits are simple field captures for undo.
/// </summary>
public sealed class Marker
{
    /// <summary>Creates a marker at <paramref name="time"/>. A non-zero <paramref name="duration"/> makes it a
    /// span marker (a range rather than a point).</summary>
    public Marker(Timecode time, string name = "", string comment = "",
        MarkerColor color = MarkerColor.Blue, Timecode duration = default)
    {
        if (duration < Timecode.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "A marker's span cannot be negative.");
        Time = time;
        Name = name ?? string.Empty;
        Comment = comment ?? string.Empty;
        Color = color;
        Duration = duration;
    }

    /// <summary>Where the marker sits (a timeline position for a sequence marker, a source position for a clip
    /// marker).</summary>
    public Timecode Time { get; set; }

    /// <summary>A short label (may be empty).</summary>
    public string Name { get; set; }

    /// <summary>A longer comment / note (may be empty).</summary>
    public string Comment { get; set; }

    /// <summary>The marker's colour band.</summary>
    public MarkerColor Color { get; set; }

    /// <summary>The marker's span length. <see cref="Timecode.Zero"/> means a point marker.</summary>
    public Timecode Duration { get; set; }

    /// <summary>Whether this marker covers a range rather than a single instant.</summary>
    public bool IsSpan => Duration > Timecode.Zero;

    /// <summary>The exclusive end of a span marker (== <see cref="Time"/> for a point marker).</summary>
    public Timecode End => Time + Duration;

    /// <summary>An independent copy (e.g. for clipboard / blade carry-over).</summary>
    public Marker Clone() => new(Time, Name, Comment, Color, Duration);
}
