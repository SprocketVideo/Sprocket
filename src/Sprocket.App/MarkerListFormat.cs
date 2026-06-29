using System;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// Pure formatting for the markers panel (PLAN.md step 20), split out like the other App helpers (e.g.
/// <c>InspectorFormat</c>, <c>MediaBadges</c>) so it is unit-testable without Avalonia. Produces the one-line
/// label for a marker row: its timecode plus its name (or a stable "Marker N" fallback when unnamed).
/// </summary>
public static class MarkerListFormat
{
    /// <summary>A one-line description of <paramref name="marker"/> at zero-based <paramref name="index"/> in the
    /// list: <c>"m:ss.cc · Name"</c>, or <c>"m:ss.cc · Marker N"</c> when it has no name. A span marker appends
    /// its length.</summary>
    public static string Describe(Marker marker, int index)
    {
        ArgumentNullException.ThrowIfNull(marker);
        string name = string.IsNullOrWhiteSpace(marker.Name) ? $"Marker {index + 1}" : marker.Name.Trim();
        string label = $"{Time(marker.Time)} · {name}";
        if (marker.IsSpan)
            label += $"  (+{Time(marker.Duration)})";
        return label;
    }

    private static string Time(Timecode t)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, t.ToSeconds()));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
    }
}
