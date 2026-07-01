using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Export;

/// <summary>The delivery container a project is exported to (PLAN.md step 27). Each maps to one FFmpeg muxer.</summary>
public enum ExportContainer { Mp4, Mov, Mkv, WebM, Avi, MpegTs }

/// <summary>The video codec used in the exported stream. Software encoders are the deterministic default; a
/// hardware encoder (NVENC/QSV/…) slots in behind the same <see cref="VideoCodecInfo.EncoderName"/> shape (§11).</summary>
public enum ExportVideoCodec { H264, Hevc, Av1, Vp9, Mpeg2, ProRes }

/// <summary>The audio codec used in the exported stream.</summary>
public enum ExportAudioCodec { Aac, Mp3, Pcm, Flac, Ac3, Opus }

/// <summary>A coarse quality tier for constant-quality (CRF) encoding, mapped to a per-codec CRF value. Explicit
/// bit rates on <see cref="ExportOptions"/> override this.</summary>
public enum ExportQuality { High, Medium, Low }

/// <summary>How the exported video stream is encoded (PLAN.md step 29). Orthogonal to the codec, matching how
/// leading NLEs (Premiere's "Software / Hardware Encoding", Resolve's encoder picker) present it.</summary>
public enum ExportAcceleration
{
    /// <summary>Encode with the deterministic software encoder (libx264/libx265/SVT-AV1/…). The default: final
    /// delivery stays bit-reproducible for golden-frame verification (ARCHITECTURE.md §5).</summary>
    Software,

    /// <summary>Prefer a platform GPU encoder (NVENC/QSV/AMF on Windows, VAAPI/NVENC on Linux, VideoToolbox on
    /// macOS), <b>automatically falling back to the software encoder</b> when none is available. Faster, but the
    /// bitstream is GPU-dependent — not bit-reproducible, so it is opt-in rather than the delivery default.</summary>
    Hardware,
}

/// <summary>Static metadata for one container: its FFmpeg muxer name, file extension, MIME type, and label.</summary>
/// <param name="Container">The enum value this describes.</param>
/// <param name="MuxerName">The FFmpeg muxer name passed to <c>avformat_alloc_output_context2</c>.</param>
/// <param name="Extension">The file extension including the dot (e.g. <c>".mp4"</c>).</param>
/// <param name="MimeType">The container's MIME type (for the save dialog's file-type filter).</param>
/// <param name="DisplayName">A human label for the UI.</param>
public readonly record struct ContainerInfo(
    ExportContainer Container, string MuxerName, string Extension, string MimeType, string DisplayName);

/// <summary>Static metadata for one video codec: the FFmpeg encoder to use and how to drive its quality.</summary>
/// <param name="Codec">The enum value this describes.</param>
/// <param name="EncoderName">The FFmpeg encoder name (<c>avcodec_find_encoder_by_name</c>).</param>
/// <param name="PixelFormat">The pixel format to encode in, or <see langword="null"/> to let the encoder pick.</param>
/// <param name="SupportsCrf">Whether the encoder takes a <c>crf</c> quality option (else a bit rate is used).</param>
/// <param name="DefaultPreset">The <c>preset</c> option value to pass, or <see langword="null"/> when the encoder
/// has none / would reject a named preset (e.g. SVT-AV1 wants a number).</param>
/// <param name="DisplayName">A human label for the UI.</param>
public readonly record struct VideoCodecInfo(
    ExportVideoCodec Codec, string EncoderName, string? PixelFormat, bool SupportsCrf, string? DefaultPreset, string DisplayName);

/// <summary>Static metadata for one audio codec: the FFmpeg encoder to use.</summary>
/// <param name="Codec">The enum value this describes.</param>
/// <param name="EncoderName">The FFmpeg encoder name (<c>avcodec_find_encoder_by_name</c>).</param>
/// <param name="Lossless">Whether the codec is lossless (a target bit rate is meaningless).</param>
/// <param name="DisplayName">A human label for the UI.</param>
public readonly record struct AudioCodecInfo(
    ExportAudioCodec Codec, string EncoderName, bool Lossless, string DisplayName);

/// <summary>
/// The container × video-codec × audio-codec chosen for an export (PLAN.md step 27). <c>default</c> is the most
/// compatible MP4 / H.264 / AAC, so <c>default(ExportOptions)</c> reproduces the step-8 behaviour exactly.
/// </summary>
public readonly record struct ExportFormat(
    ExportContainer Container = ExportContainer.Mp4,
    ExportVideoCodec VideoCodec = ExportVideoCodec.H264,
    ExportAudioCodec AudioCodec = ExportAudioCodec.Aac)
{
    /// <summary>The FFmpeg muxer name for the container (e.g. <c>"matroska"</c>).</summary>
    public string MuxerName => ExportCodecs.Container(Container).MuxerName;

    /// <summary>The output file extension including the leading dot (e.g. <c>".mkv"</c>).</summary>
    public string FileExtension => ExportCodecs.Container(Container).Extension;

    /// <summary>The container's MIME type.</summary>
    public string MimeType => ExportCodecs.Container(Container).MimeType;

    /// <summary>Whether the chosen video and audio codecs are both valid inside the chosen container.</summary>
    public bool IsValid =>
        ExportCodecs.Supports(Container, VideoCodec) && ExportCodecs.Supports(Container, AudioCodec);
}

/// <summary>A named, ready-to-use export configuration for the export dialog's dropdown — a familiar-NLE delivery
/// preset over the step-27 matrix (PLAN.md step 29). Beyond the container/codec/quality it can pin an output
/// <see cref="Resolution"/> and <see cref="FrameRate"/>; both <see langword="null"/> means "use the sequence's own"
/// (the common delivery case). Built-ins live in <see cref="ExportCodecs.Presets"/>; user-defined ones are persisted
/// by <see cref="ExportPresetStore"/>.</summary>
/// <param name="Name">The label shown in the export dialog.</param>
/// <param name="Format">The container/codec triple.</param>
/// <param name="Quality">The default quality tier for the preset.</param>
/// <param name="Resolution">An explicit output resolution, or <see langword="null"/> to keep the sequence's own
/// (still capped at 4K on export).</param>
/// <param name="FrameRate">An explicit output frame rate, or <see langword="null"/> to keep the sequence's own.</param>
public readonly record struct ExportPreset(
    string Name,
    ExportFormat Format,
    ExportQuality Quality,
    Resolution? Resolution = null,
    Rational? FrameRate = null)
{
    /// <summary>The <see cref="ExportOptions"/> this preset applies: its format, quality, and any resolution /
    /// frame-rate override. Burn-ins and handles are per-export review options, not part of a delivery preset.</summary>
    public ExportOptions ToOptions() => new(
        Format: Format, Quality: Quality, Resolution: Resolution, FrameRate: FrameRate);
}

/// <summary>
/// The static codec/container matrix: per-enum metadata plus the container→codec compatibility rules, and a
/// curated list of delivery presets. One source of truth for the encoder names, pixel formats, and valid
/// combinations the export path and its UI use (PLAN.md step 27).
/// </summary>
public static class ExportCodecs
{
    private static readonly ContainerInfo[] Containers =
    [
        new(ExportContainer.Mp4,    "mp4",      ".mp4",  "video/mp4",         "MP4"),
        new(ExportContainer.Mov,    "mov",      ".mov",  "video/quicktime",   "QuickTime (MOV)"),
        new(ExportContainer.Mkv,    "matroska", ".mkv",  "video/x-matroska",  "Matroska (MKV)"),
        new(ExportContainer.WebM,   "webm",     ".webm", "video/webm",        "WebM"),
        new(ExportContainer.Avi,    "avi",      ".avi",  "video/x-msvideo",   "AVI"),
        new(ExportContainer.MpegTs, "mpegts",   ".ts",   "video/mp2t",        "MPEG transport stream"),
    ];

    private static readonly VideoCodecInfo[] VideoCodecs =
    [
        new(ExportVideoCodec.H264,   "libx264",     null,           SupportsCrf: true,  DefaultPreset: "medium", "H.264 / AVC"),
        new(ExportVideoCodec.Hevc,   "libx265",     null,           SupportsCrf: true,  DefaultPreset: "medium", "H.265 / HEVC"),
        new(ExportVideoCodec.Av1,    "libsvtav1",   null,           SupportsCrf: true,  DefaultPreset: "8",      "AV1"),
        new(ExportVideoCodec.Vp9,    "libvpx-vp9",  null,           SupportsCrf: true,  DefaultPreset: null,     "VP9"),
        new(ExportVideoCodec.Mpeg2,  "mpeg2video",  null,           SupportsCrf: false, DefaultPreset: null,     "MPEG-2"),
        new(ExportVideoCodec.ProRes, "prores_ks",   "yuv422p10le",  SupportsCrf: false, DefaultPreset: null,     "Apple ProRes 422"),
    ];

    private static readonly AudioCodecInfo[] AudioCodecs =
    [
        new(ExportAudioCodec.Aac,  "aac",         Lossless: false, "AAC"),
        new(ExportAudioCodec.Mp3,  "libmp3lame",  Lossless: false, "MP3"),
        new(ExportAudioCodec.Pcm,  "pcm_s16le",   Lossless: true,  "PCM (16-bit)"),
        new(ExportAudioCodec.Flac, "flac",        Lossless: true,  "FLAC"),
        new(ExportAudioCodec.Ac3,  "ac3",         Lossless: false, "Dolby Digital (AC-3)"),
        new(ExportAudioCodec.Opus, "libopus",     Lossless: false, "Opus"),
    ];

    // Which video/audio codecs are valid in each container (the standard pairings NLEs offer).
    private static readonly Dictionary<ExportContainer, ExportVideoCodec[]> ContainerVideo = new()
    {
        [ExportContainer.Mp4]    = [ExportVideoCodec.H264, ExportVideoCodec.Hevc, ExportVideoCodec.Av1, ExportVideoCodec.Mpeg2],
        [ExportContainer.Mov]    = [ExportVideoCodec.H264, ExportVideoCodec.Hevc, ExportVideoCodec.Av1, ExportVideoCodec.ProRes, ExportVideoCodec.Mpeg2],
        [ExportContainer.Mkv]    = [ExportVideoCodec.H264, ExportVideoCodec.Hevc, ExportVideoCodec.Av1, ExportVideoCodec.Vp9, ExportVideoCodec.Mpeg2, ExportVideoCodec.ProRes],
        [ExportContainer.WebM]   = [ExportVideoCodec.Vp9, ExportVideoCodec.Av1],
        [ExportContainer.Avi]    = [ExportVideoCodec.H264, ExportVideoCodec.Mpeg2],
        [ExportContainer.MpegTs] = [ExportVideoCodec.H264, ExportVideoCodec.Hevc, ExportVideoCodec.Mpeg2],
    };

    private static readonly Dictionary<ExportContainer, ExportAudioCodec[]> ContainerAudio = new()
    {
        [ExportContainer.Mp4]    = [ExportAudioCodec.Aac, ExportAudioCodec.Mp3, ExportAudioCodec.Ac3],
        [ExportContainer.Mov]    = [ExportAudioCodec.Aac, ExportAudioCodec.Pcm, ExportAudioCodec.Ac3],
        [ExportContainer.Mkv]    = [ExportAudioCodec.Aac, ExportAudioCodec.Mp3, ExportAudioCodec.Pcm, ExportAudioCodec.Flac, ExportAudioCodec.Ac3, ExportAudioCodec.Opus],
        [ExportContainer.WebM]   = [ExportAudioCodec.Opus],
        [ExportContainer.Avi]    = [ExportAudioCodec.Mp3, ExportAudioCodec.Pcm],
        [ExportContainer.MpegTs] = [ExportAudioCodec.Aac, ExportAudioCodec.Ac3, ExportAudioCodec.Mp3],
    };

    /// <summary>The built-in delivery presets offered in the export dialog, most-common first. The first group keeps
    /// the sequence's own resolution / frame rate (format-only presets); the second pins a common web-delivery
    /// resolution (PLAN.md step 29), mirroring the "YouTube 1080p / 4K" presets the leading NLEs ship.</summary>
    public static readonly IReadOnlyList<ExportPreset> Presets =
    [
        new("MP4 · H.264 + AAC",       new(ExportContainer.Mp4,  ExportVideoCodec.H264,   ExportAudioCodec.Aac),  ExportQuality.High),
        new("MP4 · HEVC + AAC",        new(ExportContainer.Mp4,  ExportVideoCodec.Hevc,   ExportAudioCodec.Aac),  ExportQuality.High),
        new("MP4 · AV1 + AAC",         new(ExportContainer.Mp4,  ExportVideoCodec.Av1,    ExportAudioCodec.Aac),  ExportQuality.High),
        new("MOV · ProRes 422 + PCM",  new(ExportContainer.Mov,  ExportVideoCodec.ProRes, ExportAudioCodec.Pcm),  ExportQuality.High),
        new("MKV · HEVC + FLAC",       new(ExportContainer.Mkv,  ExportVideoCodec.Hevc,   ExportAudioCodec.Flac), ExportQuality.High),
        new("WebM · VP9 + Opus",       new(ExportContainer.WebM, ExportVideoCodec.Vp9,    ExportAudioCodec.Opus), ExportQuality.High),
        new("YouTube 4K (HEVC)",       new(ExportContainer.Mp4,  ExportVideoCodec.Hevc,   ExportAudioCodec.Aac),  ExportQuality.High,   new Resolution(3840, 2160)),
        new("YouTube 1080p (H.264)",   new(ExportContainer.Mp4,  ExportVideoCodec.H264,   ExportAudioCodec.Aac),  ExportQuality.High,   new Resolution(1920, 1080)),
        new("Web 720p (H.264)",        new(ExportContainer.Mp4,  ExportVideoCodec.H264,   ExportAudioCodec.Aac),  ExportQuality.Medium, new Resolution(1280, 720)),
    ];

    /// <summary>Container metadata for <paramref name="c"/>.</summary>
    public static ContainerInfo Container(ExportContainer c) => Containers[(int)c];

    /// <summary>Video-codec metadata for <paramref name="c"/>.</summary>
    public static VideoCodecInfo Video(ExportVideoCodec c) => VideoCodecs[(int)c];

    /// <summary>Audio-codec metadata for <paramref name="c"/>.</summary>
    public static AudioCodecInfo Audio(ExportAudioCodec c) => AudioCodecs[(int)c];

    /// <summary>Whether <paramref name="codec"/> is a valid video codec inside <paramref name="container"/>.</summary>
    public static bool Supports(ExportContainer container, ExportVideoCodec codec) =>
        ContainerVideo.TryGetValue(container, out ExportVideoCodec[]? v) && Array.IndexOf(v, codec) >= 0;

    /// <summary>Whether <paramref name="codec"/> is a valid audio codec inside <paramref name="container"/>.</summary>
    public static bool Supports(ExportContainer container, ExportAudioCodec codec) =>
        ContainerAudio.TryGetValue(container, out ExportAudioCodec[]? a) && Array.IndexOf(a, codec) >= 0;

    /// <summary>The video codecs valid in <paramref name="container"/> (for populating a codec dropdown).</summary>
    public static IReadOnlyList<ExportVideoCodec> VideoCodecsFor(ExportContainer container) =>
        ContainerVideo.TryGetValue(container, out ExportVideoCodec[]? v) ? v : [];

    /// <summary>The audio codecs valid in <paramref name="container"/>.</summary>
    public static IReadOnlyList<ExportAudioCodec> AudioCodecsFor(ExportContainer container) =>
        ContainerAudio.TryGetValue(container, out ExportAudioCodec[]? a) ? a : [];

    /// <summary>Maps a coarse quality tier to a CRF value (lower = better). Tuned per-family so H.264/HEVC and
    /// the AV1/VP9 encoders land at comparable perceptual quality for the same tier.</summary>
    public static int CrfFor(ExportVideoCodec codec, ExportQuality quality) => codec switch
    {
        // AV1/VP9 CRF scales differently (0–63); pick tier values that match the H.264 look.
        ExportVideoCodec.Av1 or ExportVideoCodec.Vp9 => quality switch
        {
            ExportQuality.High => 30,
            ExportQuality.Medium => 36,
            _ => 45,
        },
        // x264/x265 CRF (0–51).
        _ => quality switch
        {
            ExportQuality.High => 18,
            ExportQuality.Medium => 23,
            _ => 28,
        },
    };

    /// <summary>The hardware encoders to probe for <paramref name="codec"/> on the current OS, most-preferred first
    /// (PLAN.md step 29). Built as <c>{base}_{vendor}</c> — e.g. H.264 on Windows → <c>[h264_nvenc, h264_qsv,
    /// h264_amf]</c>, HEVC on macOS → <c>[hevc_videotoolbox]</c>. The list is passed to <c>MediaEncoder</c> as its
    /// hardware-candidate chain: it probes each in order and engages the first that opens, falling back to the
    /// software encoder when none do — so a name that this FFmpeg build or GPU doesn't provide is simply skipped.
    /// Empty when the codec has no hardware family.</summary>
    public static IReadOnlyList<string> HardwareEncoderCandidates(ExportVideoCodec codec)
    {
        if (HardwareBaseName(codec) is not { } baseName)
            return [];
        return [.. PlatformHardwareVendors().Select(vendor => $"{baseName}_{vendor}")];
    }

    /// <summary>The FFmpeg hardware-encoder base name for a codec (<c>"h264"</c>, <c>"hevc"</c>, …); combined with a
    /// vendor suffix to form an encoder name. <see langword="null"/> when the codec has no hardware family.</summary>
    private static string? HardwareBaseName(ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.H264 => "h264",
        ExportVideoCodec.Hevc => "hevc",
        ExportVideoCodec.Av1 => "av1",
        ExportVideoCodec.Vp9 => "vp9",
        ExportVideoCodec.Mpeg2 => "mpeg2",
        ExportVideoCodec.ProRes => "prores", // only prores_videotoolbox exists (macOS); elsewhere it falls back
        _ => null,
    };

    /// <summary>The GPU encoder vendor suffixes to try on the current OS, most-preferred first (PLAN.md step 29):
    /// Windows NVENC → QSV → AMF, Linux VAAPI → NVENC, macOS VideoToolbox.</summary>
    public static IReadOnlyList<string> PlatformHardwareVendors()
    {
        if (OperatingSystem.IsWindows()) return ["nvenc", "qsv", "amf"];
        if (OperatingSystem.IsMacOS()) return ["videotoolbox"];
        if (OperatingSystem.IsLinux()) return ["vaapi", "nvenc"];
        return [];
    }
}
