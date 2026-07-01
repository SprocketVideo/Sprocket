using System.Reflection;
using Sprocket.Core.Timing;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>Settings for the exported video stream (PLAN.md step 27 codec matrix).</summary>
/// <param name="Width">Output frame width in pixels.</param>
/// <param name="Height">Output frame height in pixels.</param>
/// <param name="FrameRate">Output frame rate (the encoder time base is its reciprocal).</param>
/// <param name="CodecName">FFmpeg encoder name to use, e.g. <c>"libx264"</c>, <c>"libx265"</c>, <c>"libsvtav1"</c>,
/// <c>"libvpx-vp9"</c>, <c>"mpeg2video"</c>, <c>"prores_ks"</c>.</param>
/// <param name="PixelFormat">FFmpeg pixel-format name (e.g. <c>"yuv420p"</c>, <c>"yuv422p10le"</c>) the composited
/// RGBA is converted to before encoding, or <see langword="null"/> to let the encoder pick (yuv420p when it
/// supports it, else its first advertised format).</param>
/// <param name="BitRate">Target bit rate in bits/s, or <c>0</c> to use CRF quality (CRF-capable encoders) / a
/// resolution-scaled default (others).</param>
/// <param name="GopSize">Maximum I-frame (GOP key picture) interval in frames (<c>0</c> = encoder default).</param>
/// <param name="Crf">Constant-rate-factor quality (lower = better) used when <paramref name="BitRate"/> is 0 and
/// the encoder supports CRF. Ignored otherwise.</param>
/// <param name="Preset">Encoder speed/quality preset (x264/x265: <c>"medium"</c> etc.; SVT-AV1: a number), applied
/// when the encoder recognises a <c>preset</c> option. <see langword="null"/> leaves the encoder default.</param>
public readonly record struct VideoEncoderSettings(
    int Width, int Height, Rational FrameRate,
    string CodecName = "libx264",
    string? PixelFormat = null,
    long BitRate = 0,
    int GopSize = 0,
    int Crf = 20,
    string? Preset = "medium");

/// <summary>Settings for the exported audio stream (PLAN.md step 27 codec matrix).</summary>
/// <param name="SampleRate">Output sample rate in Hz.</param>
/// <param name="Channels">Output channel count.</param>
/// <param name="CodecName">FFmpeg encoder name, e.g. <c>"aac"</c>, <c>"libmp3lame"</c>, <c>"flac"</c>,
/// <c>"ac3"</c>, <c>"libopus"</c>, <c>"pcm_s16le"</c>.</param>
/// <param name="BitRate">Target bit rate in bits/s (<c>0</c> = a sensible default; ignored by lossless codecs).</param>
public readonly record struct AudioEncoderSettings(
    int SampleRate, int Channels,
    string CodecName = "aac",
    long BitRate = 0);

/// <summary>
/// Encodes composited RGBA frames + interleaved float PCM to a media file — the reverse of
/// <see cref="MediaSource"/>/<see cref="AudioSource"/> (ARCHITECTURE.md §11 "Encoder (export): mirror in
/// reverse"). This is the export path's muxer: all FFmpeg interop stays here in <c>Sprocket.Media</c>; the
/// orchestrator (<c>Sprocket.Export</c>) feeds it pixels and samples and never sees libav*.
/// </summary>
/// <remarks>
/// <para><b>Not thread-safe.</b> One encoder per output file, driven from a single export thread. The encoders
/// are selected <b>by name</b> (<c>avcodec_find_encoder_by_name</c>) so the container × video-codec × audio-codec
/// matrix (PLAN.md step 27) stays robust across FFmpeg builds without baking codec-id/pixel-format enum tables.
/// The pixel/sample format is <b>negotiated against the chosen encoder</b> (<c>avcodec_get_supported_config</c>):
/// video frames are converted RGBA → the encoder's pixel format with swscale, audio is converted from the
/// mixer's interleaved float to the encoder's sample format (planar or packed) with swresample, both at write
/// time. Hardware encode (<c>h264_nvenc</c> etc.) slots in as another encoder name behind this same shape (§11);
/// the software encoders (x264/x265/SVT-AV1/…) are the deterministic default the export path relies on.</para>
/// <para>Packets from the two encoders are interleaved by the muxer (<c>av_interleaved_write_frame</c>); the
/// caller feeds frames in roughly timeline order so the interleave stays within the muxer's buffer.
/// Call <see cref="Finish"/> exactly once to flush both encoders and write the trailer before disposal.</para>
/// </remarks>
public sealed unsafe class MediaEncoder : IDisposable
{
    private const long DefaultAudioBitRate = 192_000;

    // Encoders that take a `crf` (constant-rate-factor) quality option. Others get a default bit rate instead
    // when no explicit bit rate is given. Kept as a small allow-list so we never set `crf` on an encoder that
    // interprets it differently — the unknown option would otherwise silently confuse rate control.
    private static readonly HashSet<string> CrfEncoders = new(StringComparer.Ordinal)
    {
        "libx264", "libx264rgb", "libx265", "libvpx-vp9", "libvpx", "libsvtav1", "libaom-av1",
    };

    private readonly FormatContextHandle _format;

    // Video
    private readonly CodecContextHandle _videoEncoder;
    private readonly IntPtr _videoStream;        // AVStream* — time_base/index read LIVE (post-WriteHeader)
    private readonly AvRational _videoEncTimeBase;
    private readonly SwsScaler _converter = new();
    private readonly AvFrameHandle _rgbaFrame;   // staging: incoming RGBA copied here, then swscaled to enc pix fmt
    private readonly AvFrameHandle _encFrame;    // swscale destination + encoder input (chosen pixel format)
    private readonly int _width;
    private readonly int _height;

    // Audio (optional)
    private readonly CodecContextHandle? _audioEncoder;
    private readonly IntPtr _audioStream;        // AVStream*
    private readonly AvRational _audioEncTimeBase;
    private readonly AvFrameHandle? _audioFrame;     // encoder input, in the encoder's sample format
    private readonly SwrResampler? _swr;             // interleaved flt → the encoder's sample format (same rate)
    private readonly bool _audioPlanar;              // whether the encoder's sample format is planar
    private readonly int _channels;

    private readonly AvPacketHandle _packet = new();
    private bool _finished;
    private bool _disposed;

    private MediaEncoder(
        FormatContextHandle format,
        CodecContextHandle videoEncoder, IntPtr videoStream,
        AvFrameHandle rgbaFrame, AvFrameHandle encFrame,
        CodecContextHandle? audioEncoder, IntPtr audioStream,
        AvFrameHandle? audioFrame, SwrResampler? swr, bool audioPlanar, int channels)
    {
        _format = format;
        _videoEncoder = videoEncoder;
        _videoStream = videoStream;
        _videoEncTimeBase = videoEncoder.TimeBase;
        _rgbaFrame = rgbaFrame;
        _encFrame = encFrame;
        _width = videoEncoder.Width;
        _height = videoEncoder.Height;
        _audioEncoder = audioEncoder;
        _audioStream = audioStream;
        _audioEncTimeBase = audioEncoder?.TimeBase ?? default;
        _audioFrame = audioFrame;
        _swr = swr;
        _audioPlanar = audioPlanar;
        _channels = channels;
    }

    /// <summary>Whether the file has an audio stream (<see cref="WriteAudioFrame"/> is valid).</summary>
    public bool HasAudio => _audioEncoder is not null;

    /// <summary>The number of samples (per channel) the audio encoder wants per frame; pass exactly this many to
    /// <see cref="WriteAudioFrame"/> except for the final, shorter frame. Zero when there is no audio.</summary>
    public int AudioFrameSize { get; private set; }

    /// <summary>The encoder name actually engaged for video (e.g. <c>"libx264"</c>).</summary>
    public string VideoEncoderName => _videoEncoder.CodecName;

    /// <summary>The encoder name actually engaged for audio (e.g. <c>"aac"</c>), or empty when there is no audio.</summary>
    public string AudioEncoderName => _audioEncoder?.CodecName ?? "";

    /// <summary>
    /// Creates an encoder writing to <paramref name="path"/> with a video stream per <paramref name="video"/> and,
    /// when <paramref name="audio"/> is given, an audio stream per <paramref name="audio"/>. The container is
    /// <paramref name="containerFormat"/> (an FFmpeg muxer name such as <c>"mp4"</c>, <c>"mov"</c>,
    /// <c>"matroska"</c>, <c>"webm"</c>, <c>"avi"</c>, <c>"mpegts"</c>), or guessed from the file extension when
    /// <see langword="null"/>. Opens the output file and writes the header so the encoder is ready for
    /// <see cref="WriteVideoFrame"/>/<see cref="WriteAudioFrame"/>.
    /// </summary>
    public static MediaEncoder Create(
        string path, VideoEncoderSettings video, AudioEncoderSettings? audio = null, string? containerFormat = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (video.Width <= 0 || video.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(video), "Output dimensions must be positive.");
        if (video.FrameRate.Num <= 0 || video.FrameRate.Den <= 0)
            throw new ArgumentOutOfRangeException(nameof(video), "Output frame rate must be positive.");

        FFmpegLoader.EnsureBundledNativesLoaded();

        FormatContextHandle format = FormatContextHandle.AllocOutput(path, containerFormat);
        try
        {
            bool globalHeader = (format.OutputFlags & AvConst.FmtGlobalHeader) != 0;
            bool noFile = (format.OutputFlags & AvConst.FmtNoFile) != 0;

            (CodecContextHandle videoEncoder, IntPtr videoStream, AvFrameHandle rgbaFrame, AvFrameHandle encFrame) =
                OpenVideo(format, video, globalHeader);

            CodecContextHandle? audioEncoder = null;
            IntPtr audioStream = IntPtr.Zero;
            AvFrameHandle? audioFrame = null;
            SwrResampler? swr = null;
            bool audioPlanar = false;
            int channels = 0;
            int audioFrameSize = 0;
            if (audio is { } a)
            {
                (audioEncoder, audioStream, audioFrame, swr, audioPlanar, audioFrameSize) = OpenAudio(format, a, globalHeader);
                channels = a.Channels;
            }

            // Tag the file as Sprocket's output so players / ffprobe surface its provenance. Must be set
            // before WriteHeader — the muxer serializes the metadata dictionary as part of the header.
            WriteCreationMetadata(format);

            // Open the output file (unless the muxer is file-less) and write the container header. NOTE:
            // avformat_write_header may rewrite each stream's time_base (the muxer picks its own timescale),
            // so packet timestamps must be rescaled to the LIVE stream time_base read after this call — see
            // DrainPackets.
            if (!noFile)
                format.OpenOutputFile(path);
            format.WriteHeader();

            return new MediaEncoder(
                format, videoEncoder, videoStream, rgbaFrame, encFrame,
                audioEncoder, audioStream, audioFrame, swr, audioPlanar, channels)
            {
                AudioFrameSize = audioFrameSize,
            };
        }
        catch
        {
            format.Dispose();
            throw;
        }
    }

    /// <summary>The "encoded with" tag value, e.g. <c>"Sprocket 0.1.27"</c> — built once from the assembly version.</summary>
    private static readonly string EncoderTag = BuildEncoderTag();

    /// <summary>Writes container-level provenance metadata identifying Sprocket as the producing application.</summary>
    private static void WriteCreationMetadata(FormatContextHandle format)
    {
        // `encoder` is the conventional "creating software" tag (MP4 ©too atom; FFmpeg would otherwise
        // stamp its own "Lavf…"); `comment` carries a human-readable note most players surface.
        format.SetMetadata("encoder", EncoderTag);
        format.SetMetadata("comment", "Created with Sprocket");
    }

    private static string BuildEncoderTag()
    {
        Assembly asm = typeof(MediaEncoder).Assembly;
        string? version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString();
        // InformationalVersion can carry a "+<git-sha>" build suffix; drop it for a clean tag.
        int plus = version?.IndexOf('+') ?? -1;
        if (plus >= 0) version = version![..plus];
        return string.IsNullOrEmpty(version) ? "Sprocket" : $"Sprocket {version}";
    }

    private static (CodecContextHandle, IntPtr stream, AvFrameHandle rgba, AvFrameHandle enc) OpenVideo(
        FormatContextHandle format, VideoEncoderSettings v, bool globalHeader)
    {
        IntPtr codec = FindVideoEncoder(v.CodecName);

        CodecContextHandle encoder = CodecContextHandle.Alloc(codec);
        try
        {
            int pixFmt = ChooseVideoPixelFormat(encoder, codec, v.PixelFormat);
            ValidateEvenDimensions(pixFmt, v.Width, v.Height);

            encoder.Width = v.Width;
            encoder.Height = v.Height;
            encoder.PixFmt = pixFmt;
            // Time base = 1/fps so a frame's PTS is simply its frame index.
            encoder.TimeBase = new AvRational(v.FrameRate.Den, v.FrameRate.Num);
            encoder.Framerate = new AvRational(v.FrameRate.Num, v.FrameRate.Den);
            if (v.GopSize > 0) encoder.GopSize = v.GopSize;
            if (globalHeader) encoder.Flags |= AvConst.CodecFlagGlobalHeader;

            IntPtr options = IntPtr.Zero;
            if (v.BitRate > 0)
            {
                encoder.BitRate = v.BitRate;
            }
            else if (CrfEncoders.Contains(v.CodecName))
            {
                // CRF/preset drive quality when no explicit bitrate is set; deterministic for golden-frame tests.
                LibAv.av_dict_set(ref options, "crf", v.Crf.ToString(), 0);
            }
            else
            {
                // Non-CRF encoders (mpeg2video, …) need a target rate to size output; ProRes ignores it (profile-driven).
                encoder.BitRate = DefaultVideoBitRate(v.Width, v.Height, v.FrameRate);
            }
            if (!string.IsNullOrEmpty(v.Preset)) LibAv.av_dict_set(ref options, "preset", v.Preset, 0);
            encoder.Open(codec, options);   // frees the option dict, incl. any unconsumed keys

            IntPtr stream = format.NewStream(codec);
            var st = (AvStream*)stream;
            encoder.ParametersToStream(st->codecpar);
            st->time_base = encoder.TimeBase;

            AvFrameHandle rgba = AvFrameHandle.CreateVideo(v.Width, v.Height, AvConst.PixFmtRgba, align: 4);
            AvFrameHandle enc = AvFrameHandle.CreateVideo(v.Width, v.Height, pixFmt, align: 32);
            return (encoder, stream, rgba, enc);
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    /// <summary>Resolves the requested video encoder by name; for the default <c>libx264</c> it falls back to any
    /// H.264 encoder so a minimal FFmpeg build still exports MP4.</summary>
    private static IntPtr FindVideoEncoder(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        IntPtr codec = LibAv.avcodec_find_encoder_by_name(name);
        if (codec == IntPtr.Zero && name == "libx264")
            codec = LibAv.avcodec_find_encoder(AvConst.CodecIdH264);
        if (codec == IntPtr.Zero)
            throw new FFmpegException("avcodec_find_encoder_by_name", 0, $"no encoder named '{name}' in this FFmpeg build");
        return codec;
    }

    /// <summary>Picks the pixel format to encode in: the requested one if the encoder supports it, else yuv420p
    /// when supported (the most compatible), else the encoder's first advertised format. Throws when a specific
    /// format is requested but the encoder rejects it (so the export preset table stays honest).</summary>
    private static int ChooseVideoPixelFormat(CodecContextHandle encoder, IntPtr codec, string? requestedName)
    {
        int requested = AvConst.PixFmtNone;
        if (!string.IsNullOrEmpty(requestedName))
        {
            requested = LibAv.av_get_pix_fmt(requestedName);
            if (requested == AvConst.PixFmtNone)
                throw new ArgumentException($"Unknown pixel format '{requestedName}'.", nameof(requestedName));
        }

        int[] supported = GetSupportedConfigs(encoder, codec, AvConst.CodecConfigPixFormat);
        if (supported.Length == 0)
            return requested != AvConst.PixFmtNone ? requested : AvConst.PixFmtYuv420p; // encoder advertises no restriction

        if (requested != AvConst.PixFmtNone)
        {
            if (Array.IndexOf(supported, requested) >= 0)
                return requested;
            throw new ArgumentException(
                $"Encoder '{encoder.CodecName}' does not support pixel format '{requestedName}'.", nameof(requestedName));
        }

        return Array.IndexOf(supported, AvConst.PixFmtYuv420p) >= 0 ? AvConst.PixFmtYuv420p : supported[0];
    }

    /// <summary>Enforces the chroma-subsampling dimension rule for the chosen pixel format: a format that halves
    /// chroma horizontally (4:2:0/4:2:2) needs an even width; one that halves it vertically (4:2:0) needs an even
    /// height. 4:4:4 formats accept any size.</summary>
    private static void ValidateEvenDimensions(int pixFmt, int width, int height)
    {
        IntPtr desc = LibAv.av_pix_fmt_desc_get(pixFmt);
        if (desc == IntPtr.Zero)
            return;
        var d = (AvPixFmtDescriptor*)desc;
        if (d->log2_chroma_w > 0 && (width & 1) != 0)
            throw new ArgumentException("Output width must be even for this pixel format's chroma subsampling.", nameof(width));
        if (d->log2_chroma_h > 0 && (height & 1) != 0)
            throw new ArgumentException("Output height must be even for this pixel format's chroma subsampling.", nameof(height));
    }

    private static (CodecContextHandle, IntPtr stream, AvFrameHandle, SwrResampler, bool planar, int frameSize) OpenAudio(
        FormatContextHandle format, AudioEncoderSettings a, bool globalHeader)
    {
        if (a.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(a), "Audio sample rate must be positive.");
        if (a.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(a), "Audio channel count must be positive.");
        ArgumentException.ThrowIfNullOrEmpty(a.CodecName, nameof(a));

        IntPtr codec = LibAv.avcodec_find_encoder_by_name(a.CodecName);
        if (codec == IntPtr.Zero && a.CodecName == "aac")
            codec = LibAv.avcodec_find_encoder(AvConst.CodecIdAac);
        if (codec == IntPtr.Zero)
            throw new FFmpegException("avcodec_find_encoder_by_name", 0, $"no audio encoder named '{a.CodecName}'");

        AvChannelLayout layout = default;
        LibAv.av_channel_layout_default(&layout, a.Channels);
        CodecContextHandle encoder = CodecContextHandle.Alloc(codec);
        try
        {
            (int sampleFmt, bool planar) = ChooseAudioSampleFormat(encoder, codec);

            encoder.SampleFmt = sampleFmt;
            encoder.SampleRate = a.SampleRate;
            LibAv.av_channel_layout_copy(encoder.ChLayout, &layout);
            encoder.BitRate = a.BitRate > 0 ? a.BitRate : DefaultAudioBitRate;
            encoder.TimeBase = new AvRational(1, a.SampleRate);
            if (globalHeader) encoder.Flags |= AvConst.CodecFlagGlobalHeader;
            encoder.Open(codec);

            IntPtr stream = format.NewStream(codec);
            var ast = (AvStream*)stream;
            encoder.ParametersToStream(ast->codecpar);
            ast->time_base = encoder.TimeBase;

            // Some encoders report frame size 0 (any size accepted, e.g. PCM/FLAC); chunk at a standard size then.
            int frameSize = encoder.FrameSize > 0 ? encoder.FrameSize : 1024;

            var frame = new AvFrameHandle();
            frame.Format = sampleFmt;
            LibAv.av_channel_layout_copy(frame.ChLayout, &layout);
            frame.SampleRate = a.SampleRate;
            frame.NbSamples = frameSize;
            frame.GetBuffer(0);

            // Convert the mixer's interleaved float PCM to the encoder's sample format (deinterleave to planar,
            // or narrow to int; a same-rate/same-layout conversion so swr_convert returns samples 1:1).
            var swr = new SwrResampler();
            swr.Configure(&layout, sampleFmt, a.SampleRate, &layout, AvConst.SampleFmtFlt, a.SampleRate);

            return (encoder, stream, frame, swr, planar, frameSize);
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
        finally
        {
            LibAv.av_channel_layout_uninit(&layout);
        }
    }

    /// <summary>Picks the sample format to encode in: <c>fltp</c> (the mixer-friendly deinterleave, AAC/AC-3/MP3)
    /// when supported, else the encoder's first advertised format (e.g. <c>s16</c> for PCM/FLAC). Returns the
    /// format and whether it is planar (drives how many planes <see cref="WriteAudioFrame"/> feeds).</summary>
    private static (int fmt, bool planar) ChooseAudioSampleFormat(CodecContextHandle encoder, IntPtr codec)
    {
        int[] supported = GetSupportedConfigs(encoder, codec, AvConst.CodecConfigSampleFormat);
        int chosen =
            supported.Length == 0 ? AvConst.SampleFmtFltp
            : Array.IndexOf(supported, AvConst.SampleFmtFltp) >= 0 ? AvConst.SampleFmtFltp
            : supported[0];
        return (chosen, LibAv.av_sample_fmt_is_planar(chosen) == 1);
    }

    /// <summary>Reads the values an encoder advertises for a config kind (pixel or sample formats) via
    /// <c>avcodec_get_supported_config</c>. An empty result means the encoder does not restrict the config.</summary>
    private static int[] GetSupportedConfigs(CodecContextHandle encoder, IntPtr codec, int configKind)
    {
        int rc = LibAv.avcodec_get_supported_config(encoder.Ptr, codec, configKind, 0, out IntPtr arr, out int n);
        if (rc < 0 || arr == IntPtr.Zero || n <= 0)
            return [];
        var result = new int[n];
        for (int i = 0; i < n; i++)
            result[i] = ((int*)arr)[i];
        return result;
    }

    private static long DefaultVideoBitRate(int width, int height, Rational fps)
    {
        double f = fps.Den > 0 ? (double)fps.Num / fps.Den : 30.0;
        long est = (long)((long)width * height * f * 0.10); // ~0.1 bits/pixel/frame
        return Math.Max(est, 1_000_000);
    }

    /// <summary>
    /// Encodes one composited frame: the <see cref="_width"/>×<see cref="_height"/> RGBA8888 pixels at
    /// <paramref name="rgbaPixels"/> (row stride <paramref name="rowBytes"/>) at presentation index
    /// <paramref name="frameIndex"/> (its PTS in the encoder's 1/fps time base). The pixels are read during
    /// the call and need not outlive it.
    /// </summary>
    public void WriteVideoFrame(nint rgbaPixels, int rowBytes, long frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgbaPixels == 0)
            throw new ArgumentNullException(nameof(rgbaPixels));

        _rgbaFrame.MakeWritable();
        CopyRgbaIntoFrame(rgbaPixels, rowBytes);

        _encFrame.MakeWritable();
        _converter.Convert(_rgbaFrame, _encFrame);
        _encFrame.Pts = frameIndex;

        _videoEncoder.SendFrame(_encFrame);
        DrainPackets(_videoEncoder, _videoStream, _videoEncTimeBase);
    }

    /// <summary>Copies the incoming RGBA pixels (which may have a wider stride than the frame) into the staging
    /// frame's native buffer row by row — a native→native copy, allowed on the throughput-bound export path.</summary>
    private void CopyRgbaIntoFrame(nint rgbaPixels, int rowBytes)
    {
        byte* dst = (byte*)_rgbaFrame.Data(0);
        int dstStride = _rgbaFrame.Linesize(0);
        var src = (byte*)rgbaPixels;
        int copyBytes = Math.Min(rowBytes, dstStride);
        for (int y = 0; y < _height; y++)
            Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * dstStride, dstStride, copyBytes);
    }

    /// <summary>
    /// Encodes one buffer of interleaved float32 PCM (<paramref name="interleaved"/>; length must be a multiple
    /// of the channel count) starting at sample index <paramref name="startSampleIndex"/> (its PTS in the
    /// encoder's 1/sampleRate time base). Pass <see cref="AudioFrameSize"/> sample-frames per call except the last.
    /// </summary>
    public void WriteAudioFrame(ReadOnlySpan<float> interleaved, long startSampleIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioEncoder is null || _audioFrame is null || _swr is null)
            throw new InvalidOperationException("This encoder has no audio stream.");
        if (interleaved.Length == 0)
            return;

        int samples = interleaved.Length / _channels;
        _audioFrame.MakeWritable();
        _audioFrame.NbSamples = samples;
        _audioFrame.Pts = startSampleIndex;

        fixed (float* inPtr = interleaved)
        {
            byte** inPlanes = stackalloc byte*[8];
            inPlanes[0] = (byte*)inPtr; // interleaved input is a single plane

            // Planar output has one plane per channel; packed output has a single interleaved plane.
            int planeCount = _audioPlanar ? _channels : 1;
            byte** outPlanes = stackalloc byte*[8];
            for (int p = 0; p < planeCount; p++)
                outPlanes[p] = (byte*)_audioFrame.Data(p);

            int got = _swr.Convert(outPlanes, samples, inPlanes, samples);
            if (got < 0)
                throw new InvalidOperationException("Audio resample (sample-format convert) failed during export.");
            _audioFrame.NbSamples = got;
        }

        _audioEncoder.SendFrame(_audioFrame);
        DrainPackets(_audioEncoder, _audioStream, _audioEncTimeBase);
    }

    /// <summary>Flushes both encoders (drains buffered packets) and writes the container trailer. Idempotent.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
            return;
        _finished = true;

        _videoEncoder.SendFrame(null);
        DrainPackets(_videoEncoder, _videoStream, _videoEncTimeBase);

        if (_audioEncoder is not null)
        {
            _audioEncoder.SendFrame(null);
            DrainPackets(_audioEncoder, _audioStream, _audioEncTimeBase);
        }

        _format.WriteTrailer();
    }

    /// <summary>Pulls every ready packet out of <paramref name="encoder"/>, stamps it for the stream, and
    /// interleaves it into the muxer. Stops at <c>Again</c> (needs more input) or <c>EOF</c> (flushed).
    /// Reads the stream index + time_base LIVE from the AVStream — the muxer may have rewritten the
    /// time_base in <c>avformat_write_header</c>, so the pre-header value would be wrong.</summary>
    private void DrainPackets(CodecContextHandle encoder, IntPtr streamPtr, AvRational encTimeBase)
    {
        var st = (AvStream*)streamPtr;
        while (encoder.ReceivePacket(_packet) == CodecResult.Success)
        {
            _packet.StreamIndex = st->index;
            _packet.RescaleTs(encTimeBase, st->time_base);
            _format.InterleavedWriteFrame(_packet); // takes ownership and unrefs the packet
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _packet.Dispose();
        _converter.Dispose();
        _rgbaFrame.Dispose();
        _encFrame.Dispose();
        _videoEncoder.Dispose();

        _audioFrame?.Dispose();
        _audioEncoder?.Dispose();
        _swr?.Dispose();

        _format.Dispose();
    }
}
