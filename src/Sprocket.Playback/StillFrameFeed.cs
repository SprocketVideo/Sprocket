using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Playback;

/// <summary>
/// An <see cref="IVideoFrameFeed"/> for a single still image (PLAN.md step 42): it serves the one decoded frame
/// for any requested source time, instead of seeking/decoding per scrub like the ordinary decode ring. A still
/// clip is time-invariant — every timeline time inside it maps to the same picture — and its source has only one
/// frame, so the ring's "seek to the mapped source time" would run off the end and go black. This feed ignores
/// the seek target and hands out a fresh owned copy of the held frame once per seek cycle; the track player then
/// holds it (its end-of-stream latch) for the rest of the clip with zero further work.
/// </summary>
public sealed class StillFrameFeed : IVideoFrameFeed
{
    private readonly StillFrameSource _source;
    private volatile bool _served;   // has this seek cycle's frame been handed out yet?

    /// <summary>Wraps <paramref name="source"/>. The feed takes ownership and disposes it on <see cref="DisposeAsync"/>.</summary>
    public StillFrameFeed(StillFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public void Start() { /* already decoded up front */ }

    /// <inheritdoc />
    public ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        // One frame per seek cycle: serve it once, then report end-of-stream so the player holds it. A seek
        // (RequestSeek) re-arms it so the freshly-positioned player promotes the still again.
        if (_served)
            return ValueTask.FromResult<VideoFrame?>(null);
        _served = true;
        return ValueTask.FromResult<VideoFrame?>(_source.RentCopy());
    }

    /// <inheritdoc />
    public void RequestSeek(Timecode sourceTarget) => _served = false;   // time-invariant: only re-arm the serve

    /// <inheritdoc />
    public VideoDecodeInfo? DecodeInfo => _source.DecodeInfo;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _source.Dispose();
        return ValueTask.CompletedTask;
    }
}
