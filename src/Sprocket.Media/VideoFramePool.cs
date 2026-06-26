using System.Collections.Concurrent;

namespace Sprocket.Media;

/// <summary>
/// A small pool of reusable <see cref="VideoFrame"/> buffers for one source's fixed frame size. Keeps
/// the decode path free of per-frame managed and native allocation in steady state (ARCHITECTURE.md
/// §8 "frame pooling"): the decode worker <see cref="Rent"/>s a frame, fills its native buffer, and the
/// consumer <see cref="VideoFrame.Dispose"/>s it to return it here.
/// </summary>
/// <remarks>
/// Thread-safe for the single-producer/single-consumer ring-buffer pattern (one thread rents, another
/// returns) via a lock-free bag. The live frame count is bounded in practice by the ring buffer's
/// capacity plus the few frames in flight; the pool grows on demand and never blocks.
/// </remarks>
public sealed class VideoFramePool : IDisposable
{
    private readonly ConcurrentBag<VideoFrame> _free = new();
    private readonly int _width;
    private readonly int _height;
    private volatile bool _disposed;

    /// <summary>Creates a pool that vends RGBA frames of the given size.</summary>
    public VideoFramePool(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Frame size must be positive.");
        _width = width;
        _height = height;
    }

    /// <summary>Leases a frame, reusing a free one if available or allocating a new native buffer otherwise.</summary>
    public VideoFrame Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _free.TryTake(out VideoFrame? frame) ? frame : new VideoFrame(this, _width, _height);
    }

    /// <summary>Returns a frame to the pool. Called by <see cref="VideoFrame.Dispose"/>; returns false if the pool is gone.</summary>
    internal bool TryReturn(VideoFrame frame)
    {
        if (_disposed)
            return false;
        _free.Add(frame);
        return true;
    }

    /// <summary>Frees every pooled native buffer. Frames still rented out free themselves on disposal.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        while (_free.TryTake(out VideoFrame? frame))
            frame.FreeNative();
    }
}
