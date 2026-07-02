using System.Runtime.InteropServices;

namespace Sprocket.Media.Native;

/// <summary>
/// Explicit-layout views over the FFmpeg 8.1 structs, declaring ONLY the fields Sprocket touches at their
/// exact x64 byte offsets. The offsets were read from the FFmpeg 8.1 headers (via the Phase-0 spike's
/// FFmpeg.AutoGen layout oracle, <c>--offsets</c>) and are validated end-to-end by the golden/byte-identical
/// tests. 64-bit layouts are uniform across win/linux/macOS on x64 and arm64 because FFmpeg uses
/// fixed-width types; a wrong-major FFmpeg is rejected up front by <see cref="FFmpegLoader"/>'s version guard.
/// </summary>
/// <remarks>
/// These are pointer views: instances are never allocated by us (FFmpeg's <c>*_alloc</c> functions own the
/// full struct); we only read/write the declared fields through the returned pointer, so the declared size
/// is irrelevant. <see cref="AvRational"/> and <see cref="AvChannelLayout"/> are the exceptions — passed by
/// value / filled in place, so they are declared in full.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct AvRational
{
    public int Num;
    public int Den;
    public AvRational(int num, int den) { Num = num; Den = den; }
}

/// <summary>AVChannelLayout (24 bytes): order, nb_channels, a union {mask|map}, opaque. Declared in full
/// because it is filled in place by <c>av_channel_layout_default</c> and passed by value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AvChannelLayout
{
    public int order;
    public int nb_channels;
    public ulong u_mask;     // union { uint64_t mask; AVChannelCustom* map; }
    public IntPtr opaque;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvFormatContext
{
    [FieldOffset(16)] public IntPtr oformat;   // AVOutputFormat*
    [FieldOffset(32)] public IntPtr pb;        // AVIOContext*
    [FieldOffset(44)] public uint nb_streams;
    [FieldOffset(48)] public IntPtr streams;   // AVStream**
    [FieldOffset(104)] public long duration;   // AV_TIME_BASE units
    [FieldOffset(192)] public IntPtr metadata; // AVDictionary*
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvOutputFormat
{
    [FieldOffset(44)] public int flags;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvStream
{
    [FieldOffset(8)] public int index;
    [FieldOffset(16)] public IntPtr codecpar;     // AVCodecParameters*
    [FieldOffset(32)] public AvRational time_base;
    [FieldOffset(48)] public long duration;
    [FieldOffset(80)] public IntPtr metadata;     // AVDictionary* — per-stream tags (color-metadata probe, step 37)
    [FieldOffset(88)] public AvRational avg_frame_rate;
    [FieldOffset(204)] public AvRational r_frame_rate;
}

/// <summary>AVDictionaryEntry: <c>{ char *key; char *value; }</c> — the view an <c>av_dict_get</c>
/// iteration returns while walking a format/stream metadata dictionary (PLAN.md step 37).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AvDictionaryEntry
{
    [FieldOffset(0)] public IntPtr key;
    [FieldOffset(8)] public IntPtr value;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvCodecParameters
{
    [FieldOffset(0)] public int codec_type;
    [FieldOffset(4)] public int codec_id;
    [FieldOffset(44)] public int format;
    [FieldOffset(48)] public long bit_rate;
    [FieldOffset(72)] public int width;
    [FieldOffset(76)] public int height;
    [FieldOffset(100)] public int color_range;      // enum AVColorRange — color-metadata probe (step 37)
    [FieldOffset(104)] public int color_primaries;  // enum AVColorPrimaries
    [FieldOffset(108)] public int color_trc;   // enum AVColorTransferCharacteristic — HDR-transfer probe (step 27)
    [FieldOffset(112)] public int color_space;      // enum AVColorSpace
    [FieldOffset(128)] public AvChannelLayout ch_layout;
    [FieldOffset(152)] public int sample_rate;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvCodecContext
{
    [FieldOffset(16)] public IntPtr codec;     // const AVCodec*
    [FieldOffset(24)] public int codec_id;
    [FieldOffset(56)] public long bit_rate;
    [FieldOffset(64)] public int flags;
    [FieldOffset(84)] public AvRational time_base;
    [FieldOffset(100)] public AvRational framerate;
    [FieldOffset(112)] public int width;
    [FieldOffset(116)] public int height;
    [FieldOffset(136)] public int pix_fmt;
    [FieldOffset(192)] public IntPtr get_format;   // AVPixelFormat (*)(AVCodecContext*, const AVPixelFormat*)
    [FieldOffset(332)] public int gop_size;
    [FieldOffset(344)] public int sample_rate;
    [FieldOffset(348)] public int sample_fmt;
    [FieldOffset(352)] public AvChannelLayout ch_layout;
    [FieldOffset(376)] public int frame_size;
    [FieldOffset(552)] public IntPtr hw_frames_ctx;   // AVBufferRef* — set on a GPU encoder taking device surfaces (step 29)
    [FieldOffset(560)] public IntPtr hw_device_ctx;   // AVBufferRef*
    [FieldOffset(656)] public int thread_count;
}

/// <summary>AVHWFramesContext (sizeof 80): the pool description for a hardware-encode frame set (PLAN.md step 29).
/// Reached through an <c>AVBufferRef*</c>'s <c>data</c> pointer (see <see cref="AvBufferRef"/>). We fill
/// <c>format</c> (the device pixel format, e.g. VAAPI), <c>sw_format</c> (the CPU format uploaded from, nv12),
/// <c>width</c>/<c>height</c>, and <c>initial_pool_size</c> before <c>av_hwframe_ctx_init</c>. Offsets recorded
/// from the FFmpeg 8.1 x64 layout (see <c>Native/FUTURE_BINDINGS.md</c>).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AvHwFramesContext
{
    [FieldOffset(56)] public int initial_pool_size;
    [FieldOffset(60)] public int format;      // enum AVPixelFormat — the hardware surface format
    [FieldOffset(64)] public int sw_format;   // enum AVPixelFormat — the CPU format uploaded from
    [FieldOffset(68)] public int width;
    [FieldOffset(72)] public int height;
}

/// <summary>AVBufferRef: <c>{ AVBuffer *buffer; uint8_t *data; size_t size; }</c>. We only ever read <c>data</c>
/// (offset 8) to reach the <see cref="AvHwFramesContext"/> an <c>av_hwframe_ctx_alloc</c> ref points at.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AvBufferRef
{
    [FieldOffset(8)] public IntPtr data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvCodec
{
    [FieldOffset(0)] public IntPtr name;   // const char*
}

/// <summary>AVPixFmtDescriptor: layout is <c>const char *name</c> (8) + three <c>uint8_t</c>
/// (nb_components/log2_chroma_w/log2_chroma_h) at 8–10, then <c>uint64_t flags</c> 8-byte-aligned at offset 16,
/// then <c>AVComponentDescriptor comp[4]</c> at 24 (each is five <c>int</c> = 20 bytes: plane/step/offset/shift/
/// depth, so <c>comp[0].depth</c> is at 24+16 = 40). Stable across FFmpeg majors. We read <c>flags</c> (alpha
/// test, step 26), the chroma log2s (even-dimension rule for the export pixel format, step 27), and
/// <c>comp[0].depth</c> (source bit-depth for the import probe, step 27).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AvPixFmtDescriptor
{
    [FieldOffset(8)] public byte nb_components;
    [FieldOffset(9)] public byte log2_chroma_w;
    [FieldOffset(10)] public byte log2_chroma_h;
    [FieldOffset(16)] public ulong flags;
    [FieldOffset(40)] public int comp0_depth;   // comp[0].depth — bits per component of the first channel
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvCodecHwConfig
{
    [FieldOffset(0)] public int pix_fmt;
    [FieldOffset(4)] public int methods;
    [FieldOffset(8)] public int device_type;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvPacket
{
    [FieldOffset(8)] public long pts;
    [FieldOffset(16)] public long dts;
    [FieldOffset(24)] public IntPtr data;
    [FieldOffset(32)] public int size;
    [FieldOffset(36)] public int stream_index;
    [FieldOffset(40)] public int flags;
    [FieldOffset(64)] public long duration;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AvFrame
{
    // data[8] @ 0 and linesize[8] @ 64 are arrays — accessed via pointer math in AvFrameHandle.
    [FieldOffset(104)] public int width;
    [FieldOffset(108)] public int height;
    [FieldOffset(112)] public int nb_samples;
    [FieldOffset(116)] public int format;
    [FieldOffset(136)] public long pts;
    [FieldOffset(180)] public int sample_rate;
    [FieldOffset(304)] public long best_effort_timestamp;
    [FieldOffset(384)] public AvChannelLayout ch_layout;
}

/// <summary>FFmpeg 8.1 enum values and macro constants Sprocket uses, baked from the spike's probe.</summary>
internal static class AvConst
{
    public const long NoPts = long.MinValue;           // AV_NOPTS_VALUE
    public const long AvTimeBase = 1_000_000;          // AV_TIME_BASE (µs)

    public const int MediaTypeVideo = 0;
    public const int MediaTypeAudio = 1;

    public const int PixFmtNone = -1;
    public const int PixFmtYuv420p = 0;
    public const int PixFmtRgba = 26;

    public const ulong PixFmtFlagAlpha = 1UL << 7;     // AV_PIX_FMT_FLAG_ALPHA
    public const ulong PixFmtFlagHwAccel = 1UL << 3;   // AV_PIX_FMT_FLAG_HWACCEL (a device-surface pixel format)

    public const int PixFmtNv12 = 23;                  // AV_PIX_FMT_NV12 — the CPU format hardware encoders upload from

    // HDR transfer characteristics (enum AVColorTransferCharacteristic) — used to flag HDR sources at probe.
    public const int ColorTrcSmpte2084 = 16;           // PQ (HDR10 / Dolby Vision base layer)
    public const int ColorTrcAribStdB67 = 18;          // HLG

    public const int SampleFmtNone = -1;
    public const int SampleFmtFlt = 3;
    public const int SampleFmtFltp = 8;

    public const int CodecIdH264 = 27;
    public const int CodecIdAac = 86018;

    // avcodec_get_supported_config() selectors (enum AVCodecConfig).
    public const int CodecConfigPixFormat = 0;
    public const int CodecConfigSampleFormat = 3;

    public const int DictIgnoreSuffix = 2;             // AV_DICT_IGNORE_SUFFIX — empty-key iteration of a dictionary

    public const int SeekBackward = 1;                 // AVSEEK_FLAG_BACKWARD
    public const int FmtGlobalHeader = 64;             // AVFMT_GLOBALHEADER
    public const int FmtNoFile = 1;                    // AVFMT_NOFILE
    public const int CodecFlagGlobalHeader = 0x0040_0000; // AV_CODEC_FLAG_GLOBAL_HEADER (4194304)
    public const int AvioFlagWrite = 2;                // AVIO_FLAG_WRITE
    public const int HwConfigMethodDeviceCtx = 1;      // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
    public const int SwsBilinear = 2;                  // SWS_BILINEAR

    public const int HwTypeNone = 0;
    public const int HwTypeVdpau = 1;
    public const int HwTypeCuda = 2;
    public const int HwTypeVaapi = 3;
    public const int HwTypeDxva2 = 4;
    public const int HwTypeQsv = 5;
    public const int HwTypeVideotoolbox = 6;
    public const int HwTypeD3d11va = 7;
}
