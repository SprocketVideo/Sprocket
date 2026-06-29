using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Pure playhead navigation over a set of markers (PLAN.md step 20): given the current time, find the previous /
/// next marker so the transport's jump-to-previous / jump-to-next-marker lands on the nearest annotation — the
/// same pattern as <see cref="KeyframeNavigation"/>. Kept in Core (pure model reasoning, no UI) and unit-testable
/// headlessly. Callers pass marker times already in the navigation domain (timeline positions for sequence
/// markers).
/// </summary>
public static class MarkerNavigation
{
    /// <summary>The marker with the latest <see cref="Marker.Time"/> strictly before <paramref name="t"/>, or
    /// <see langword="null"/> when none exists.</summary>
    public static Marker? Previous(IEnumerable<Marker> markers, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(markers);
        Marker? best = null;
        foreach (Marker m in markers)
            if (m.Time < t && (best is null || m.Time > best.Time))
                best = m;
        return best;
    }

    /// <summary>The marker with the earliest <see cref="Marker.Time"/> strictly after <paramref name="t"/>, or
    /// <see langword="null"/> when none exists.</summary>
    public static Marker? Next(IEnumerable<Marker> markers, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(markers);
        Marker? best = null;
        foreach (Marker m in markers)
            if (m.Time > t && (best is null || m.Time < best.Time))
                best = m;
        return best;
    }
}
