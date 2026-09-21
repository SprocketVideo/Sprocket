using System.Collections.Concurrent;

namespace Sprocket.Media;

/// <summary>
/// A small pool of reusable <see cref="GrayFrame"/> buffers for one analysis pass's fixed frame size
/// (plan/features/stabilization.md phase 3). Keeps the sequential analysis decode free of per-frame
/// managed and native allocation in steady state (ARCHITECTURE.md §1): the analyzer <see cref="Rent"/>s
/// a frame, the decoder fills its native buffer, and the analyzer <see cref="GrayFrame.Dispose"/>s it to
/// return it here. Mirrors <see cref="VideoFramePool"/>.
/// </summary>
/// <remarks>
/// Thread-safe for the single-producer/single-consumer pattern via a lock-free bag. The analyzer keeps at
/// most two frames live at once (the previous and current pair), so the pool stays tiny; it grows on
/// demand and never blocks.
/// </remarks>
public sealed class GrayFramePool : IDisposable
{
    private readonly ConcurrentBag<GrayFrame> _free = new();
    private readonly int _width;
    private readonly int _height;
    private volatile bool _disposed;

    /// <summary>Creates a pool that vends GRAY8 frames of the given size.</summary>
    public GrayFramePool(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Frame size must be positive.");
        _width = width;
        _height = height;
    }

    /// <summary>Frame width in pixels.</summary>
    public int Width => _width;

    /// <summary>Frame height in pixels.</summary>
    public int Height => _height;

    /// <summary>Leases a frame, reusing a free one if available or allocating a new native buffer otherwise.</summary>
    public GrayFrame Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _free.TryTake(out GrayFrame? frame) ? frame : new GrayFrame(this, _width, _height);
    }

    /// <summary>Returns a frame to the pool. Called by <see cref="GrayFrame.Dispose"/>; returns false if the pool is gone.</summary>
    internal bool TryReturn(GrayFrame frame)
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
        while (_free.TryTake(out GrayFrame? frame))
            frame.FreeNative();
    }
}
