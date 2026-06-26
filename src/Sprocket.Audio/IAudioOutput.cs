namespace Sprocket.Audio;

/// <summary>
/// The audio device seam (ARCHITECTURE.md §6, §14): a streaming output the mixer pushes interleaved float32
/// buffers into, which reports how many sample-frames it has actually played. That played-frame count is the
/// editor's <b>master clock</b> (§8) — everything else (video) syncs to it. The real implementation is
/// <see cref="OpenAlAudioOutput"/> (Silk.NET.OpenAL); it sits behind this interface so it can be swapped and
/// so the mixer/clock are testable against a fake.
/// </summary>
/// <remarks>
/// The output is push/poll, not callback-driven: a feeder keeps the device queue topped up while
/// <see cref="FreeFrames"/> reports headroom, and reads <see cref="PlayedFrames"/> to drive the clock. All
/// members are called from the single feeder/clock thread except <see cref="PlayedFrames"/>, which the render
/// thread may also read.
/// </remarks>
public interface IAudioOutput : IDisposable
{
    /// <summary>Opens the device for the given output format. Call once before any other member. Throws if no
    /// device is available (the caller falls back to a software clock).</summary>
    void Configure(int sampleRate, int channels);

    /// <summary>Output channel count (valid after <see cref="Configure"/>).</summary>
    int Channels { get; }

    /// <summary>Output sample rate in Hz (valid after <see cref="Configure"/>).</summary>
    int SampleRate { get; }

    /// <summary>
    /// Total sample-frames the device has actually played since <see cref="Configure"/> — monotonic, and the
    /// source of "now" for the master clock. Unaffected by <see cref="Flush"/> (flushing discards
    /// queued-but-unplayed audio; it does not rewind what was already heard).
    /// </summary>
    long PlayedFrames { get; }

    /// <summary>How many sample-frames can be enqueued right now without exceeding the device's buffer budget.
    /// The feeder mixes and enqueues only while this is at least one buffer's worth.</summary>
    int FreeFrames { get; }

    /// <summary>Submits interleaved float32 frames to the play queue. Length must be a multiple of
    /// <see cref="Channels"/> and fit within <see cref="FreeFrames"/>.</summary>
    void Enqueue(ReadOnlySpan<float> interleaved);

    /// <summary>Starts/resumes playback of queued audio.</summary>
    void Play();

    /// <summary>Pauses playback; <see cref="PlayedFrames"/> stops advancing until <see cref="Play"/>.</summary>
    void Pause();

    /// <summary>Discards all queued-but-unplayed audio (for a seek), so freshly mixed audio plays promptly.</summary>
    void Flush();
}
