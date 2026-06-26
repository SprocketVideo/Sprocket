namespace Sprocket.Core.Timing;

/// <summary>
/// A transport-capable <see cref="IClock"/> — the source of "now" that the playback engine also drives
/// (play / pause / seek), as opposed to merely reading. The slice's stand-in software clock and the real
/// audio device master clock (ARCHITECTURE.md §8) both implement it, so the playback engine's pump code is
/// identical whether or not audio is present: it reads <see cref="IClock.Now"/> and issues transport calls,
/// never caring which clock is underneath.
/// </summary>
/// <remarks>
/// When audio is present the implementation is the audio engine, whose <see cref="IClock.Now"/> is derived
/// from the count of samples the device has actually played — making audio the master and video the
/// follower (§8). When there is no audio it is a wall-clock <c>SoftwareClock</c>. All members are callable
/// from the UI thread while <see cref="IClock.Now"/> is read from the render/pump thread.
/// </remarks>
public interface IMasterClock : IClock
{
    /// <summary>Whether the clock is currently advancing.</summary>
    bool IsRunning { get; }

    /// <summary>Starts (or resumes) advancing from the current position. Idempotent.</summary>
    void Start();

    /// <summary>Freezes the clock at the current position. Idempotent.</summary>
    void Pause();

    /// <summary>Jumps to <paramref name="position"/>, keeping the running/paused state.</summary>
    void Seek(Timecode position);
}
