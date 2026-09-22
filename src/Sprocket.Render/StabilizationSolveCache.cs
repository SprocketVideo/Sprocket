using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;

namespace Sprocket.Render;

/// <summary>
/// A per-pipeline memo of <see cref="StabilizationSolver"/> results (plan/features/stabilization.md). The solve
/// is a pure, deterministic function of (motion track, settings, frame size), so a stabilized clip's every frame
/// shares one solution: the cache computes it on the first frame and reuses it for the rest, and re-solves only
/// when the track, the settings, or the frame size actually change (the "tune without re-analysing / re-solving"
/// behaviour, §5). A hit does no work and allocates nothing — the per-frame render then only binary-searches the
/// cached solution for its matrix.
/// </summary>
/// <remarks>
/// Not thread-safe: one instance is owned by one <see cref="SkiaEffectPipeline"/> and used from its render thread.
/// The cache is unbounded in principle (keyframed <c>smoothness</c>/<c>strength</c> produce a distinct settings
/// snapshot per value, so scrubbing a keyframed clip can accumulate entries); it is cleared wholesale when it
/// grows past <see cref="MaxEntries"/> so memory stays bounded without tracking recency.
/// </remarks>
internal sealed class StabilizationSolveCache
{
    private const int MaxEntries = 64;

    // The track is compared by reference (a MotionTrack has no value equality), the settings by value (so a
    // freshly-read but identical snapshot still hits), and the frame size by value.
    private readonly record struct Key(
        MotionTrack Track, StabilizationSettings Settings, int Width, int Height, Timecode? UsedIn, Timecode? UsedOut);

    private readonly Dictionary<Key, StabilizationSolution> _cache = new();

    /// <summary>How many solves were actually computed (cache misses) — see
    /// <see cref="SkiaEffectPipeline.StabilizationSolveCount"/>.</summary>
    public int SolveCount { get; private set; }

    /// <summary>The cached solution for (<paramref name="track"/>, <paramref name="settings"/>,
    /// <paramref name="width"/>×<paramref name="height"/>, the clip's used source range), solving and storing it
    /// on a miss.</summary>
    public StabilizationSolution Get(
        MotionTrack track, StabilizationSettings settings, int width, int height, Timecode? usedIn, Timecode? usedOut)
    {
        var key = new Key(track, settings, width, height, usedIn, usedOut);
        if (_cache.TryGetValue(key, out StabilizationSolution? cached))
            return cached;

        if (_cache.Count >= MaxEntries)
            _cache.Clear();

        StabilizationSolution solution = StabilizationSolver.Solve(track, settings, width, height, usedIn, usedOut);
        SolveCount++;
        _cache[key] = solution;
        return solution;
    }
}
