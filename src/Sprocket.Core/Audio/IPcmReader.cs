using Sprocket.Core.Timing;

namespace Sprocket.Core.Audio;

/// <summary>
/// The seam between the audio mixer (Sprocket.Audio) and the Media layer's audio decode: pull decoded PCM
/// for one source as <b>interleaved float32 at the project's sample rate and channel count</b>, in sample
/// order, with seeking. This is the audio analogue of <c>IFrameSource</c> for video (ARCHITECTURE.md §5, §6).
/// </summary>
/// <remarks>
/// The reader owns the resampling, so the mixer only ever sums uniform buffers (§6). Audio samples are the
/// one data class explicitly allowed to cross onto the managed heap (§1 — the no-managed-pixels rule is about
/// <em>video frames</em>); callers still pass pooled spans so steady-state mixing stays allocation-free.
/// Not thread-safe: a single reader is driven by one mixing thread.
/// </remarks>
public interface IPcmReader : IDisposable
{
    /// <summary>Channels in the interleaved output — the project channel count.</summary>
    int Channels { get; }

    /// <summary>Sample rate of the output in Hz — the project sample rate.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Fills <paramref name="destinationInterleaved"/> with PCM from the current read position and advances
    /// it. The span length must be a whole number of sample-frames (a multiple of <see cref="Channels"/>);
    /// returns the number of <em>sample-frames</em> written, which is short of capacity only at end of
    /// stream (0 = nothing left until the next <see cref="SeekTo"/>).
    /// </summary>
    int Read(Span<float> destinationInterleaved);

    /// <summary>Repositions so the next <see cref="Read"/> returns samples at/just after <paramref name="sourceTime"/>
    /// (a time within the source). Resets any end-of-stream state.</summary>
    void SeekTo(Timecode sourceTime);
}
