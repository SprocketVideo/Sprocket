using Sprocket.Core.Timing;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>
/// One decoded video frame downscaled to single-plane 8-bit grayscale (GRAY8), living in a native
/// FFmpeg <c>AVFrame</c> buffer — the input the motion analyzer tracks features on
/// (plan/features/stabilization.md phase 3). Like <see cref="VideoFrame"/>, the pixels are exposed by
/// <em>pointer</em> (<see cref="Pixels"/>) and never copied to the managed heap (ARCHITECTURE.md §1);
/// the analyzer wraps them with a <c>GrayImage</c> span for tracking.
/// </summary>
/// <remarks>
/// A frame is leased from a <see cref="GrayFramePool"/> and its native buffer is reused, so callers MUST
/// keep the frame alive (undisposed) for exactly as long as something reads its pixels, then
/// <see cref="Dispose"/> it to return it to the pool. Disposing after the pool is gone simply frees the
/// native buffer. Not thread-safe for concurrent use of a single instance.
/// </remarks>
public sealed class GrayFrame : IDisposable
{
    private readonly GrayFramePool? _pool;
    private readonly AvFrameHandle _gray;
    private bool _disposed;

    internal GrayFrame(GrayFramePool? pool, int width, int height)
    {
        _pool = pool;
        Width = width;
        Height = height;
        _gray = AvFrameHandle.CreateVideo(width, height, AvConst.PixFmtGray8, align: 4);
    }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Presentation time of this frame within its source media.</summary>
    public Timecode Pts { get; internal set; }

    /// <summary>Pointer to the single plane of GRAY8 pixels. Valid until <see cref="Dispose"/>.</summary>
    public IntPtr Pixels => _gray.Data(0);

    /// <summary>Bytes per row (stride) of the grayscale buffer; may exceed <c>Width</c> due to alignment.</summary>
    public int RowBytes => _gray.Linesize(0);

    /// <summary>The underlying native frame, for the decoder's swscale destination (Media-internal).</summary>
    internal AvFrameHandle Native => _gray;

    /// <summary>Returns this frame to its pool for reuse, or frees the native buffer if it has no pool.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_pool is not null && _pool.TryReturn(this))
            return;

        _disposed = true;
        _gray.Dispose();
    }

    /// <summary>Frees the native buffer unconditionally. Called by the pool when it is itself disposed.</summary>
    internal void FreeNative()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gray.Dispose();
    }
}
