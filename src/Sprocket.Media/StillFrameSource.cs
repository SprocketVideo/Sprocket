using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>
/// A single still image decoded <em>once</em> and held, vending owned RGBA copies on demand (PLAN.md step 42).
/// A still clip shows the same frame across its whole (unbounded) timeline span, so re-seeking and re-decoding
/// the image every scrub — as the ordinary decode ring would — is wasted work; worse, the ring would try to
/// seek past the source's single frame and go black. This decodes the frame up front, then serves cheap pooled
/// copies (one memcpy each) that the caller owns and disposes like any other <see cref="VideoFrame"/>.
/// </summary>
/// <remarks>
/// The held master frame keeps the pixels alive for the source's life; <see cref="RentCopy"/> leases a fresh
/// frame from the same pool and copies the pixels in, so the consumer's normal <see cref="VideoFrame.Dispose"/>
/// (return-to-pool) never disturbs the master. Not thread-safe for concurrent <see cref="RentCopy"/> — the
/// playback feed calls it from one pump thread.
/// </remarks>
public sealed unsafe class StillFrameSource : IDisposable
{
    private readonly VideoFramePool _pool;
    private readonly VideoFrame _master;   // the one decoded frame, held (never returned to the pool) until Dispose
    private bool _disposed;

    private StillFrameSource(VideoFramePool pool, VideoFrame master, VideoDecodeInfo decodeInfo)
    {
        _pool = pool;
        _master = master;
        DecodeInfo = decodeInfo;
    }

    /// <summary>How the still decoded (codec + hardware device) — for the diagnostics overlay.</summary>
    public VideoDecodeInfo DecodeInfo { get; }

    /// <summary>The decoded frame's width in pixels.</summary>
    public int Width => _master.Width;

    /// <summary>The decoded frame's height in pixels.</summary>
    public int Height => _master.Height;

    /// <summary>Opens <paramref name="request"/> as a still, decodes its first frame, and holds it. Throws if the
    /// source has no decodable frame.</summary>
    public static StillFrameSource Decode(MediaOpenRequest request, HardwareAccelMode hwAccel = HardwareAccelMode.Disabled)
    {
        // Software decode: a still is one frame, so GPU decode/download would only add setup cost.
        using MediaSource source = MediaSource.Open(request, hwAccel);
        var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        try
        {
            if (!source.TryDecodeNextFrame(pool, out VideoFrame? master))
                throw new InvalidOperationException($"No decodable frame in still '{request.Path}'.");
            return new StillFrameSource(pool, master, source.DecodeInfo);
        }
        catch
        {
            pool.Dispose();
            throw;
        }
    }

    /// <summary>Leases a fresh copy of the held frame from the pool. The caller owns it and MUST
    /// <see cref="VideoFrame.Dispose"/> it (which returns it to the pool for reuse).</summary>
    public VideoFrame RentCopy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrame copy = _pool.Rent();

        int srcStride = _master.RowBytes;
        int dstStride = copy.RowBytes;
        int rowBytes = Math.Min(srcStride, dstStride);  // both are RGBA8888 at the same size; strides normally match
        byte* src = (byte*)_master.Pixels;
        byte* dst = (byte*)copy.Pixels;
        for (int y = 0; y < _master.Height; y++)
            Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, rowBytes);

        copy.Pts = _master.Pts;
        copy.HasAlpha = _master.HasAlpha;
        return copy;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _master.FreeNative();   // held out of the pool, so free its buffer explicitly
        _pool.Dispose();        // frees every other pooled buffer
    }
}
