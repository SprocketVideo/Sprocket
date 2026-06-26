namespace Sprocket.Core.Timing;

/// <summary>
/// The source of "now" for playback. The playback engine reads the current timeline position from
/// this; during real playback the implementation is driven by the audio device callback (the master
/// clock — ARCHITECTURE.md §8), and tests can supply a deterministic fake clock.
/// </summary>
/// <remarks>
/// This is one of the seam abstractions <see cref="Sprocket.Core"/> defines and the Audio/Playback
/// layers implement, keeping the core model free of any device or threading concern.
/// </remarks>
public interface IClock
{
    /// <summary>The current timeline position.</summary>
    Timecode Now { get; }
}
