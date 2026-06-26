using System.Diagnostics.CodeAnalysis;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>
/// Opens one source media file with library-level FFmpeg (Sdcb.FFmpeg), probes its streams, and decodes
/// its video stream to native RGBA frames with frame-accurate seeking (ARCHITECTURE.md §11). This is the
/// concrete backing for the timeline's source media; the playback layer wraps it in a ring buffer
/// (<see cref="VideoDecodeRing"/>) and the render graph reaches it through the <c>IFrameSource</c> seam.
/// </summary>
/// <remarks>
/// <para><b>Not thread-safe.</b> A <see cref="MediaSource"/> holds a single decoder and reusable packet/
/// frame; all of <see cref="TryDecodeNextFrame"/> and <see cref="SeekTo"/> must run on one thread (the
/// decode worker). The decoded pixels live in the pool's native buffers — never on the managed heap.</para>
/// <para>Decode model (§11): <c>ReadFrame → SendPacket → ReceiveFrame</c>, draining the decoder before
/// feeding the next packet, then a flush packet at end-of-input to emit buffered frames.</para>
/// </remarks>
public sealed class MediaSource : IDisposable
{
    private readonly FormatContext _format;
    private readonly CodecContext _decoder;
    private readonly MediaStream _videoStream;
    private readonly VideoFrameConverter _converter = new();
    private readonly Packet _packet = new();
    private readonly Frame _yuv = new();          // reusable decoder output (source pixel format)
    private readonly AVRational _videoTimeBase;
    private readonly int _videoIndex;

    private bool _inputEof;     // ReadFrame returned no more packets
    private bool _flushed;      // sent the end-of-stream flush packet to the decoder
    private long _discardBeforePts = MediaTime.NoPts; // decode-to-target: skip frames before this (seek)
    private bool _disposed;

    private MediaSource(FormatContext format, CodecContext decoder, MediaStream videoStream, ProbedMediaInfo info)
    {
        _format = format;
        _decoder = decoder;
        _videoStream = videoStream;
        _videoIndex = videoStream.Index;
        _videoTimeBase = videoStream.TimeBase;
        Info = info;
    }

    /// <summary>Streams/duration/format probed at open. Mirrors what is stored on the project's <see cref="MediaRef"/>.</summary>
    public ProbedMediaInfo Info { get; }

    /// <summary>Whether the source has a decodable video stream (always true for an opened <see cref="MediaSource"/>).</summary>
    public bool HasVideo => Info.HasVideo;

    /// <summary>
    /// Opens and probes <paramref name="path"/>, opening its video decoder. Throws if the file cannot be
    /// opened or has no decodable video stream (the slice is video-led; audio is probed but not decoded here).
    /// </summary>
    public static MediaSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FormatContext format = FormatContext.OpenInputUrl(path);
        try
        {
            format.LoadStreamInfo();

            MediaStream? videoStreamOrNull = format.FindBestStreamOrNull(AVMediaType.Video, -1, -1);
            if (videoStreamOrNull is null)
                throw new InvalidOperationException($"No video stream found in '{path}'.");

            MediaStream videoStream = videoStreamOrNull.Value;

            var decoder = new CodecContext(Codec.FindDecoderById(videoStream.Codecpar!.CodecId));
            decoder.FillParameters(videoStream.Codecpar);
            decoder.Open();

            ProbedMediaInfo info = Probe(format, videoStream, decoder);
            return new MediaSource(format, decoder, videoStream, info);
        }
        catch
        {
            format.Dispose();
            throw;
        }
    }

    private static ProbedMediaInfo Probe(FormatContext format, MediaStream videoStream, CodecContext decoder)
    {
        Rational frameRate = ReadFrameRate(videoStream);

        Timecode duration = format.Duration > 0
            ? MediaTime.FromMicroseconds(format.Duration)         // AV_TIME_BASE (µs) container duration
            : videoStream.Duration > 0
                ? MediaTime.ToTimecode(videoStream.Duration, videoStream.TimeBase)
                : Timecode.Zero;

        MediaStream? audioStream = format.FindBestStreamOrNull(AVMediaType.Audio, -1, -1);
        bool hasAudio = audioStream is not null;
        int sampleRate = 0, channels = 0;
        if (audioStream is { } audio)
        {
            CodecParameters audioPar = audio.Codecpar!;
            sampleRate = audioPar.SampleRate;
            channels = audioPar.ChLayout.nb_channels;
        }

        return new ProbedMediaInfo(
            Duration: duration,
            HasVideo: true,
            FrameRate: frameRate,
            Width: decoder.Width,
            Height: decoder.Height,
            HasAudio: hasAudio,
            SampleRate: sampleRate,
            Channels: channels);
    }

    /// <summary>Reads the video frame rate, preferring the average rate and falling back to the real base rate.</summary>
    private static Rational ReadFrameRate(MediaStream videoStream)
    {
        AVRational avg = videoStream.AvgFrameRate;
        if (avg.Num > 0 && avg.Den > 0)
            return new Rational(avg.Num, avg.Den);

        AVRational real = videoStream.RFrameRate;
        if (real.Num > 0 && real.Den > 0)
            return new Rational(real.Num, real.Den);

        return Rational.Zero;
    }

    /// <summary>
    /// Decodes the next video frame in presentation order into a frame leased from <paramref name="pool"/>.
    /// Returns false at end of stream. After <see cref="SeekTo"/>, frames before the seek target are decoded
    /// and discarded (without an RGBA conversion) so the first returned frame lands at/just after the target.
    /// </summary>
    public bool TryDecodeNextFrame(VideoFramePool pool, [NotNullWhen(true)] out VideoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pool);

        while (TryReceiveYuv())
        {
            long pts = FramePts(_yuv);

            // Decode-to-target: drop frames before the seek point. No swscale work for discarded frames.
            if (_discardBeforePts != MediaTime.NoPts && pts != MediaTime.NoPts && pts < _discardBeforePts)
                continue;
            _discardBeforePts = MediaTime.NoPts;

            VideoFrame rgba = pool.Rent();
            _converter.ConvertFrame(_yuv, rgba.Native, SWS.Bilinear);
            rgba.Pts = pts == MediaTime.NoPts ? Timecode.Zero : MediaTime.ToTimecode(pts, _videoTimeBase);
            frame = rgba;
            return true;
        }

        frame = null;
        return false;
    }

    /// <summary>
    /// Seeks so the next <see cref="TryDecodeNextFrame"/> returns the frame at/just after <paramref name="target"/>:
    /// seek to the keyframe at or before the target, flush the decoder, then arm decode-to-target discard
    /// (ARCHITECTURE.md §8 "seeking").
    /// </summary>
    public void SeekTo(Timecode target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long targetPts = MediaTime.ToStreamTimestamp(target, _videoTimeBase);

        // AVSEEK_FLAG.Backward: land on the keyframe at or before the target so the GOP decodes cleanly.
        _format.SeekFrame(targetPts, _videoIndex, AVSEEK_FLAG.Backward);
        FlushDecoder();

        _inputEof = false;
        _flushed = false;
        _discardBeforePts = targetPts;
    }

    /// <summary>Resets to the start of the stream (seek to time zero).</summary>
    public void Rewind() => SeekTo(Timecode.Zero);

    /// <summary>
    /// Pulls one decoded frame into <see cref="_yuv"/>, feeding packets / a flush packet as the decoder
    /// asks for more input. Returns false only at true end of stream.
    /// </summary>
    private bool TryReceiveYuv()
    {
        while (true)
        {
            CodecResult result = _decoder.ReceiveFrame(_yuv);
            switch (result)
            {
                case CodecResult.Success:
                    return true;
                case CodecResult.EOF:
                    return false;
                case CodecResult.Again:
                    if (!FeedDecoder())
                        return false; // flush already sent and decoder still hungry → nothing left
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Feeds the decoder its next input: the next video packet, or — once input is exhausted — a single
    /// flush (null) packet to drain buffered frames. Returns false when there is nothing left to feed.
    /// </summary>
    private bool FeedDecoder()
    {
        if (_inputEof)
        {
            if (_flushed)
                return false;
            _flushed = true;
            _decoder.SendPacket(null); // enter draining mode: emit any frames still held by the decoder
            return true;
        }

        while (_format.ReadFrame(_packet) == CodecResult.Success)
        {
            try
            {
                if (_packet.StreamIndex == _videoIndex)
                {
                    _decoder.SendPacket(_packet);
                    return true;
                }
            }
            finally
            {
                _packet.Unref();
            }
        }

        // No more packets: switch to draining on the next call.
        _inputEof = true;
        return FeedDecoder();
    }

    /// <summary>Resets decoder state after a seek so it does not emit pre-seek frames or stale references.</summary>
    private unsafe void FlushDecoder() => ffmpeg.avcodec_flush_buffers(_decoder);

    /// <summary>The frame's presentation timestamp, preferring the best-effort estimate over the raw PTS.</summary>
    private static long FramePts(Frame frame) =>
        frame.BestEffortTimestamp != MediaTime.NoPts ? frame.BestEffortTimestamp : frame.Pts;

    /// <summary>Closes the decoder and source. Pooled frames are owned by the pool, not by the source.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _packet.Dispose();
        _yuv.Dispose();
        _converter.Dispose();
        _decoder.Dispose();
        _format.Dispose();
    }
}
