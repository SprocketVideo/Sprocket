using System.Diagnostics;

namespace Sprocket.Audio;

/// <summary>
/// A monotonic elapsed-time source for the audio engine's <em>software-fallback</em> clock — the timing used
/// when the output device is lost and cannot be reopened (ARCHITECTURE.md §8). Only deltas are meaningful; the
/// origin is arbitrary. The production implementation is <see cref="StopwatchTimeSource"/>; tests inject a
/// deterministic fake so software-fallback timing is verifiable without wall-clock flakiness.
/// </summary>
public interface ISoftwareTimeSource
{
    /// <summary>Monotonic elapsed time since an arbitrary origin.</summary>
    TimeSpan Elapsed { get; }
}

/// <summary>The default <see cref="ISoftwareTimeSource"/>: a <see cref="Stopwatch"/> started at construction.</summary>
public sealed class StopwatchTimeSource : ISoftwareTimeSource
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <inheritdoc />
    public TimeSpan Elapsed => _sw.Elapsed;
}
