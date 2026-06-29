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

    /// <summary>A proxy is wanted but not yet started.</summary>
    Queued,

    /// <summary>The proxy is being generated in the background.</summary>
    Building,

    /// <summary>A proxy file is ready; the preview can switch to it.</summary>
    Ready,

    /// <summary>Generation failed; preview stays on the original.</summary>
    Failed,
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
    /// Whether a source is worth proxying at <paramref name="tier"/>: it must have video, and only sources
    /// <em>heavier</em> than the preview ceiling benefit — a source already at or below 1080p previews in real
    /// time, so it is skipped (it previews on the original). The proxy must also be a genuine downscale of the
    /// source (a tier whose target equals the source size buys nothing).
    /// </summary>
    /// <remarks>
    /// This keys "light enough" off resolution alone, which is all <see cref="ProbedMediaInfo"/> carries today.
    /// Codec / bit-depth heaviness (HEVC, 10-bit, ProRes) is a later refinement (a fast draft tier, PLAN.md
    /// step 18) once the probe records those facts.
    /// </remarks>
    public static bool NeedsProxy(ProbedMediaInfo info, ProxyTier tier)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.HasVideo || info.Width <= 0 || info.Height <= 0)
            return false;

        // Already within the preview ceiling → real-time on the original, no proxy.
        if (info.Width <= CeilingWidth && info.Height <= CeilingHeight)
            return false;

        Resolution target = TargetResolution(info.Width, info.Height, tier);
        // Only worth it if the target is a real downscale (smaller area than the source).
        return target.Width > 0 && (long)target.Width * target.Height < (long)info.Width * info.Height;
    }

    private static int EvenFloor(double value)
    {
        int v = (int)Math.Floor(value);
        v -= v & 1; // drop to even
        return Math.Max(2, v);
    }
}
