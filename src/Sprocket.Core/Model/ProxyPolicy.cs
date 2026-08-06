namespace Sprocket.Core.Model;

/// <summary>
/// The proxy resolution tier — the stable target a source's preview proxy is keyed to (PLAN.md step 18). It is a
/// <em>fixed</em> target, not the live preview-window size: proxies are expensive and persisted, so they key to a
/// stable resolution rather than re-generating as the window resizes. 1080p is the locked preview ceiling
/// (ARCHITECTURE.md §1 / the slice's decision), so every tier caps there — a higher proxy would be wasted.
/// </summary>
public enum ProxyTier
{
    /// <summary>Quarter of the source's longest dimension (for weak machines), still capped at 1080p.</summary>
    Quarter,

    /// <summary>Half of the source's dimensions — the default — capped at 1080p.</summary>
    Half,

    /// <summary>Full source size, capped at 1080p (no downscale below the ceiling).</summary>
    FullHd,
}

/// <summary>
/// The proxy <em>state</em> of one source: the lifecycle the background proxy service moves a <see cref="MediaRef"/>
/// through (PLAN.md step 18). This is runtime/cache state, <b>not</b> part of the serialized project model — a
/// proxy is a local, regenerable artifact, so the state lives in the proxy service, not the document.
/// </summary>
public enum ProxyState
{
    /// <summary>No proxy is needed (the source is already light enough to preview in real time).</summary>
    NotNeeded,

    /// <summary>
    /// A proxy <em>is</em> wanted, none is on disk, and none is scheduled — the resting state when proxies are
    /// off or paused, and where a source lands after its proxy is deleted or its tier changes. Distinct from
    /// <see cref="NotNeeded"/>, which means no proxy would ever help.
    /// </summary>
    NotGenerated,

    /// <summary>A proxy is wanted but not yet started.</summary>
    Queued,

    /// <summary>The proxy is being generated in the background.</summary>
    Building,

    /// <summary>A proxy file is ready; the preview can switch to it.</summary>
    Ready,

    /// <summary>Generation failed; preview stays on the original.</summary>
    Failed,

    /// <summary>
    /// The playback drop monitor observed this source struggling and <em>suggests</em> a proxy, but the user has
    /// not confirmed. Distinct from <see cref="NotGenerated"/> precisely so the automatic scheduling paths
    /// (enable, resume, rebuild-all, the next project load) <b>skip it</b>: a recommendation is built only when
    /// the user asks, via the row's Generate button. Confirming moves it to <see cref="NotGenerated"/> and then
    /// through the ordinary lifecycle.
    /// </summary>
    Recommended,
}

/// <summary>
/// Pure decisions for preview proxy generation (PLAN.md step 18, ARCHITECTURE.md §17): given a source's probed
/// facts and the chosen <see cref="ProxyTier"/>, what <em>target resolution</em> should its proxy be, and is a
/// proxy worth generating at all? No I/O — the proxy service and the cache layer build on these.
/// </summary>
public static class ProxyPolicy
{
    /// <summary>The locked preview resolution ceiling (1080p): a proxy is never larger than this on either axis.</summary>
    public const int CeilingWidth = 1920;

    /// <summary>The locked preview resolution ceiling (1080p): a proxy is never larger than this on either axis.</summary>
    public const int CeilingHeight = 1080;

    /// <summary>
    /// The target proxy resolution for a <paramref name="srcWidth"/>×<paramref name="srcHeight"/> source at
    /// <paramref name="tier"/>: the tier's scale factor applied to the source, then clamped under the 1080p
    /// ceiling preserving aspect, with both dimensions rounded down to even (yuv420p chroma needs even sizes)
    /// and floored at 2. Returns <see cref="Resolution"/> (0,0) for a non-positive source.
    /// </summary>
    public static Resolution TargetResolution(int srcWidth, int srcHeight, ProxyTier tier)
    {
        if (srcWidth <= 0 || srcHeight <= 0)
            return new Resolution(0, 0);

        double tierScale = tier switch
        {
            ProxyTier.Quarter => 0.25,
            ProxyTier.Half => 0.5,
            _ => 1.0,
        };

        // Never exceed the 1080p box; combine the tier scale with whatever extra downscale the ceiling demands.
        double ceilingScale = Math.Min((double)CeilingWidth / srcWidth, (double)CeilingHeight / srcHeight);
        double scale = Math.Min(tierScale, ceilingScale);

        int w = EvenFloor(srcWidth * scale);
        int h = EvenFloor(srcHeight * scale);
        return new Resolution(w, h);
    }

    /// <summary>
    /// Whether a source is worth proxying at <paramref name="tier"/> — a convenience wrapper over
    /// <see cref="Decide"/> for callers that only need the yes/no.
    /// </summary>
    public static bool NeedsProxy(ProbedMediaInfo info, ProxyTier tier) => Decide(info, tier).Wanted;

    /// <summary>
    /// The full static proxy decision for a source at <paramref name="tier"/>: whether a proxy is wanted, why
    /// (<see cref="ProxyReason"/>), and at what <see cref="ProxyDecision.Target"/> resolution. Sources above the
    /// preview ceiling proxy as before (<see cref="ProxyReason.OversizeResolution"/>, strict downscale required).
    /// At 1080p-class and above, formats that are expensive to decode also qualify: long-GOP HEVC/AV1/VP9
    /// (<see cref="ProxyReason.DemandingCodec"/>) and >8-bit or ≥4:2:2 sources
    /// (<see cref="ProxyReason.DeepColor"/>) — for those, a <em>same-resolution</em> target is allowed, because
    /// the codec/format conversion is itself the benefit. Easy-to-decode intra codecs (ProRes, DNx, MJPEG…)
    /// are exempt from the format rules (they seek and decode cheaply despite bandwidth) but still proxy when
    /// oversize. Frame rate is deliberately not a static input — high-fps footage that actually struggles is
    /// caught at runtime by the playback drop monitor (<see cref="ProxyReason.Performance"/>).
    /// </summary>
    public static ProxyDecision Decide(ProbedMediaInfo info, ProxyTier tier)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.HasVideo || info.Width <= 0 || info.Height <= 0)
            return new ProxyDecision(ProxyReason.None, new Resolution(0, 0));

        Resolution target = TargetResolution(info.Width, info.Height, tier);
        long srcArea = (long)info.Width * info.Height;
        long targetArea = (long)target.Width * target.Height;

        // Above the preview ceiling → today's rule: proxy iff the target is a real downscale.
        if (info.Width > CeilingWidth || info.Height > CeilingHeight)
        {
            return target.Width > 0 && targetArea < srcArea
                ? new ProxyDecision(ProxyReason.OversizeResolution, target)
                : new ProxyDecision(ProxyReason.None, target);
        }

        // Within the ceiling: only 1080p-class sources in demanding formats qualify. A same-size target is
        // allowed here (equality, not strict downscale) — the codec conversion is the point.
        if (IsAtOrAbove1080pClass(info.Width, info.Height) && !IsEasyIntraCodec(info.VideoCodec)
            && target.Width > 0 && targetArea <= srcArea)
        {
            if (IsDemandingCodec(info.VideoCodec))
                return new ProxyDecision(ProxyReason.DemandingCodec, target);
            if (info.BitDepth > 8 || IsFullOrHalfChroma(info))
                return new ProxyDecision(ProxyReason.DeepColor, target);
        }

        return new ProxyDecision(ProxyReason.None, target);
    }

    /// <summary>
    /// Whether a source's format is expensive to decode regardless of tier — a demanding long-GOP codec, or
    /// >8-bit / ≥4:2:2 outside the easy-intra set. This is the runtime drop-monitor's "difficult format"
    /// predicate: software-decoding one of these strengthens the case for a proxy recommendation.
    /// </summary>
    public static bool IsDemandingFormat(ProbedMediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.HasVideo || IsEasyIntraCodec(info.VideoCodec))
            return false;
        return IsDemandingCodec(info.VideoCodec) || info.BitDepth > 8 || IsFullOrHalfChroma(info);
    }

    /// <summary>
    /// Whether <paramref name="videoCodec"/> (the avcodec short name) is a long-GOP codec expensive enough to
    /// decode that 1080p-class sources warrant a proxy: HEVC/H.265, AV1, or VP9.
    /// </summary>
    public static bool IsDemandingCodec(string videoCodec) =>
        videoCodec is "hevc" or "h265" or "av1" or "vp9";

    /// <summary>
    /// Whether <paramref name="videoCodec"/> (the avcodec short name) is an intra-frame editing/mezzanine codec
    /// that decodes and seeks cheaply despite high bandwidth (ProRes, DNx, CineForm, MJPEG…). These are exempt
    /// from the format-based proxy rules — only resolution or observed playback performance proxies them.
    /// </summary>
    public static bool IsEasyIntraCodec(string videoCodec) =>
        videoCodec is "prores" or "prores_raw" or "dnxhd" or "cfhd" or "mjpeg" or "qtrle" or "ffv1";

    /// <summary>
    /// Whether a source carries 4:2:2 chroma or fuller (4:4:0, 4:2:2, 4:4:4, and RGB/GBR — all reported as
    /// <c>"444"</c>) — the subsampling consumer hardware decoders often cannot accelerate. Reads the probe's
    /// structured <see cref="ProbedMediaInfo.ChromaSubsampling"/> when present; for probes recorded before that
    /// field existed it falls back to <see cref="NameSuggestsFullOrHalfChroma"/> on the pixel-format name, so an
    /// old project still classifies the common <c>yuv422p*</c> / <c>yuv444p*</c> cases without a re-import.
    /// </summary>
    public static bool IsFullOrHalfChroma(ProbedMediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.ChromaSubsampling switch
        {
            "422" or "440" or "444" => true,
            "" => NameSuggestsFullOrHalfChroma(info.PixelFormatName),
            _ => false, // 400 monochrome, 410/411/420 subsampled — all cheap enough
        };
    }

    /// <summary>
    /// The legacy name-based chroma guess for probes that predate
    /// <see cref="ProbedMediaInfo.ChromaSubsampling"/>: true when the avutil pixel-format name spells out 4:2:2
    /// or 4:4:4 (<c>yuv422p10le</c>, <c>yuv444p</c>). <b>Deliberately conservative</b> — names like <c>nv16</c>
    /// (4:2:2) and <c>gbrp</c>/<c>rgb24</c> (full chroma) do not encode their subsampling, so this under-reports
    /// rather than guessing. New probes carry the structured field and never reach this path.
    /// </summary>
    public static bool NameSuggestsFullOrHalfChroma(string pixelFormatName) =>
        !string.IsNullOrEmpty(pixelFormatName)
        && (pixelFormatName.Contains("422", StringComparison.Ordinal)
            || pixelFormatName.Contains("444", StringComparison.Ordinal)
            || pixelFormatName.Contains("440", StringComparison.Ordinal));

    /// <summary>
    /// Whether a <paramref name="width"/>×<paramref name="height"/> source is 1080p-class or larger on either
    /// axis — the size threshold at which demanding formats warrant a proxy (vertical 1080×1920 and ultrawide
    /// sources qualify too).
    /// </summary>
    public static bool IsAtOrAbove1080pClass(int width, int height) =>
        width >= CeilingWidth || height >= CeilingHeight;

    private static int EvenFloor(double value)
    {
        int v = (int)Math.Floor(value);
        v -= v & 1; // drop to even
        return Math.Max(2, v);
    }
}

/// <summary>
/// Why a proxy is (or is not) wanted for a source — produced by <see cref="ProxyPolicy.Decide"/> for the static
/// reasons, or stamped by the proxy service when the runtime drop monitor recommends one. Runtime-only, like
/// <see cref="ProxyState"/> — never serialized into the project. Append-only.
/// </summary>
public enum ProxyReason
{
    /// <summary>No proxy is wanted.</summary>
    None,

    /// <summary>The source is larger than the 1080p preview ceiling (the original resolution rule).</summary>
    OversizeResolution,

    /// <summary>A long-GOP codec that is expensive to decode (HEVC/AV1/VP9) at 1080p-class or above.</summary>
    DemandingCodec,

    /// <summary>More than 8-bit depth or ≥4:2:2 chroma at 1080p-class or above (e.g. 10-bit H.264) — formats
    /// consumer hardware decoders often cannot accelerate.</summary>
    DeepColor,

    /// <summary>Runtime evidence: playback of the software-decoded original sustained dropped frames, so the
    /// drop monitor recommended a proxy. Never returned by <see cref="ProxyPolicy.Decide"/>.</summary>
    Performance,
}

/// <summary>
/// The outcome of <see cref="ProxyPolicy.Decide"/>: whether a proxy is <see cref="Wanted"/>, the
/// <see cref="Reason"/> why, and the <see cref="Target"/> resolution it would be built at.
/// </summary>
/// <param name="Reason">Why the proxy is wanted, or <see cref="ProxyReason.None"/>.</param>
/// <param name="Target">The tier's target resolution for the source (computed even when no proxy is wanted).</param>
public readonly record struct ProxyDecision(ProxyReason Reason, Resolution Target)
{
    /// <summary>True when a proxy should be generated for the source.</summary>
    public bool Wanted => Reason != ProxyReason.None;
}
