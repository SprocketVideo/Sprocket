using System;
using Sprocket.Core.Timing;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// Pure geometry for the media-bin hover-scrub filmstrip (Premiere hover-scrub / Resolve live preview): where to
/// sample each strip frame in the source, and which strip slot the pointer is over. Kept free of Avalonia / decode
/// so it can be unit-tested headlessly (the decode + gesture wiring rest on manual verification, as in steps 15/17).
/// </summary>
public static class FilmstripMath
{
    /// <summary>Default number of evenly spaced frames in a hover-scrub strip.</summary>
    public const int DefaultFrames = 16;

    /// <summary>
    /// The sample time at the centre of slice <paramref name="index"/> of <paramref name="count"/> equal slices
    /// across <paramref name="duration"/> — i.e. <c>(index + 0.5) / count</c> of the way in. Sampling slice centres
    /// (rather than edges) avoids a black/leader frame at 0 and the EOF frame at the end.
    /// </summary>
    public static Timecode SampleTime(Timecode duration, int count, int index)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be positive.");
        if (index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(nameof(index), "index must be in [0, count).");

        // Integer math on ticks (no double seconds — ARCHITECTURE §3): (2*index + 1) * duration / (2*count).
        long ticks = duration.Ticks * (2L * index + 1) / (2L * count);
        return new Timecode(ticks);
    }

    /// <summary>
    /// The strip slot under pointer <paramref name="x"/> over a poster <paramref name="width"/> px wide, split into
    /// <paramref name="count"/> equal slices. Clamped to <c>[0, count-1]</c>; returns <c>-1</c> when
    /// <paramref name="width"/> ≤ 0 (no meaningful slot).
    /// </summary>
    public static int SlotAt(double x, double width, int count)
    {
        if (width <= 0 || count <= 0)
            return -1;
        int slot = (int)(x / width * count);
        return Math.Clamp(slot, 0, count - 1);
    }
}
