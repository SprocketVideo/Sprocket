using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>
/// A video frame already in a <see cref="MediaEncoder"/>'s input pixel format and size (yuv420p / nv12 / …), filled
/// by an <see cref="EncoderVideoConverter"/> and handed to <see cref="MediaEncoder.WriteVideoFrame(EncoderVideoFrame, long)"/>.
/// Created by <see cref="MediaEncoder.CreateVideoFrame"/> and usable only with that encoder. The pixels live in a
/// native FFmpeg buffer (ARCHITECTURE.md §1); a frame is reused across many writes.
/// </summary>
/// <remarks>This is what lets the export move the RGBA → encoder-format conversion off the single mux thread
/// (export-speed phase 3): each render worker converts into its own frames, and the mux thread only sends them.
/// A frame must not be converted into while it is being written. Internal — reached by Sprocket.Export through
/// InternalsVisibleTo, keeping the raw-pointer conversion off the public API.</remarks>
internal sealed class EncoderVideoFrame : IDisposable
{
    private bool _disposed;

    internal EncoderVideoFrame(MediaEncoder owner, AvFrameHandle frame)
    {
        Owner = owner;
        Frame = frame;
    }

    internal MediaEncoder Owner { get; }
    internal AvFrameHandle Frame { get; }

    /// <summary>Throws if this frame has been disposed (its native frame is freed; using it would pass a null
    /// <c>AVFrame*</c> to FFmpeg — a flush for the encoder, a crash for the scaler).</summary>
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        Frame.Dispose();
    }
}

/// <summary>
/// Converts composited RGBA8888 pixels into an <see cref="EncoderVideoFrame"/> — the exact staging copy + libswscale
/// conversion <see cref="MediaEncoder.WriteVideoFrame(nint, int, long)"/> performs, so a frame converted here encodes
/// byte-identically. Created by <see cref="MediaEncoder.CreateVideoConverter"/>. Each converter owns its own scaler
/// and staging buffer and is <b>not</b> thread-safe, but separate converters run concurrently — one per export render
/// worker (export-speed phase 3).
/// </summary>
internal sealed class EncoderVideoConverter : IDisposable
{
    private readonly MediaEncoder _owner;
    private readonly SwsScaler _scaler = new();
    private readonly AvFrameHandle _staging;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    internal EncoderVideoConverter(MediaEncoder owner, int width, int height)
    {
        _owner = owner;
        _width = width;
        _height = height;
        _staging = AvFrameHandle.CreateVideo(width, height, AvConst.PixFmtRgba, align: MediaEncoder.RgbaStagingAlign);
    }

    /// <summary>Converts the RGBA8888 pixels at <paramref name="rgbaPixels"/> (row stride <paramref name="rowBytes"/>,
    /// at least the encoder's width × 4, for the encoder's height) into <paramref name="destination"/>. The pixels are
    /// read during the call only.</summary>
    public void Convert(nint rgbaPixels, int rowBytes, EncoderVideoFrame destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        destination.ThrowIfDisposed();
        if (rgbaPixels == 0)
            throw new ArgumentNullException(nameof(rgbaPixels));
        if (rowBytes < _width * 4)
            throw new ArgumentOutOfRangeException(nameof(rowBytes), rowBytes, "The row stride is narrower than one RGBA row.");
        if (!ReferenceEquals(destination.Owner, _owner))
            throw new ArgumentException("The frame belongs to a different encoder.", nameof(destination));
        MediaEncoder.ConvertRgba(_scaler, _staging, destination.Frame, rgbaPixels, rowBytes, _height);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _scaler.Dispose();
        _staging.Dispose();
    }
}
