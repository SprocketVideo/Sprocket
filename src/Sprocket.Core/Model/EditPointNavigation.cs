using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Pure playhead navigation over edit points (PLAN.md step 61): given the current time, find the previous / next clip
/// start or end on a set of tracks — Premiere's Go to Previous / Next Edit Point (Up / Down arrow), which considers
/// the targeted tracks, with Shift considering every track. The same pattern as <see cref="MarkerNavigation"/>.
/// </summary>
public static class EditPointNavigation
{
    /// <summary>The latest clip start or end on <paramref name="tracks"/> strictly before <paramref name="t"/>, or
    /// <see langword="null"/> when none exists.</summary>
    public static Timecode? Previous(IEnumerable<Track> tracks, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Timecode? best = null;
        foreach (Timecode edit in EditPoints(tracks))
            if (edit < t && (best is null || edit > best.Value))
                best = edit;
        return best;
    }

    /// <summary>The earliest clip start or end on <paramref name="tracks"/> strictly after <paramref name="t"/>, or
    /// <see langword="null"/> when none exists.</summary>
    public static Timecode? Next(IEnumerable<Track> tracks, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Timecode? best = null;
        foreach (Timecode edit in EditPoints(tracks))
            if (edit > t && (best is null || edit < best.Value))
                best = edit;
        return best;
    }

    private static IEnumerable<Timecode> EditPoints(IEnumerable<Track> tracks)
    {
        foreach (Track track in tracks)
            foreach (Clip clip in track.Clips)
            {
                yield return clip.TimelineStart;
                yield return clip.TimelineEnd;
            }
    }
}
