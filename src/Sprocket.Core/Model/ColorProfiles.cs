namespace Sprocket.Core.Model;

/// <summary>
/// The log/camera color profiles Sprocket understands (PLAN.md step 37). A profile names the encoding a
/// source was shot in; the <see cref="EffectTypeIds.ColorTransform"/> effect converts it to the working
/// space (Rec.709) by sampling the profile's bundled camera-vendor 3D LUT on the GPU. Profiles are
/// identified by stable string ids (recorded on <c>ProbedMediaInfo.DetectedColorProfile</c>) and addressed
/// from the effect's numeric <see cref="EffectParamNames.SourceProfile"/> parameter by their index in
/// <see cref="All"/> — append-only, so persisted indices stay valid.
/// </summary>
public static class ColorProfiles
{
    /// <summary>DJI D-Log (10-bit, D-Gamut) — Mavic 3 / Inspire / Zenmuse cinema profile.</summary>
    public const string DjiDLog = "dji.dlog";

    /// <summary>DJI D-Log M (10-bit) — the consumer-drone/handheld log profile (Mavic 3/4, Air, Mini, Pocket).</summary>
    public const string DjiDLogM = "dji.dlogm";

    /// <summary>All known profile ids, in <see cref="EffectParamNames.SourceProfile"/> index order.
    /// Append-only: indices are persisted in project files.</summary>
    public static readonly IReadOnlyList<string> All = [DjiDLog, DjiDLogM];

    /// <summary>Human-readable names, parallel to <see cref="All"/> (Inspector dropdown labels).</summary>
    public static readonly IReadOnlyList<string> DisplayNames = ["DJI D-Log → Rec.709", "DJI D-Log M → Rec.709"];

    /// <summary>The <see cref="EffectParamNames.SourceProfile"/> index for a profile id, or -1 when unknown.</summary>
    public static int IndexOf(string profileId)
    {
        for (int i = 0; i < All.Count; i++)
            if (string.Equals(All[i], profileId, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>The profile id at a (rounded, clamped) <see cref="EffectParamNames.SourceProfile"/> value.</summary>
    public static string FromIndex(double sourceProfile) =>
        All[Math.Clamp((int)Math.Round(sourceProfile), 0, All.Count - 1)];

    /// <summary>
    /// Best-effort detection of a DJI log profile from a source's container/stream metadata tags (probed by
    /// the Media layer). Returns a profile id from <see cref="All"/>, or <c>""</c> when nothing matches.
    /// Pure string policy so it is testable without FFmpeg. Detection is conservative — DJI does not tag a
    /// distinctive transfer characteristic, so the signal is metadata values naming the profile; when it
    /// misses, the user tags the clip manually via the Inspector's input-transform control (the same
    /// fallback Resolve/Premiere rely on, which have no auto-detect at all).
    /// </summary>
    public static string DetectDjiLog(IEnumerable<KeyValuePair<string, string>> metadata)
    {
        string detected = "";
        foreach ((_, string value) in metadata)
        {
            string v = Normalize(value);
            if (v.Contains("dlogm", StringComparison.Ordinal))
                return DjiDLogM;                          // the most specific signal wins immediately
            if (v.Contains("dlog", StringComparison.Ordinal))
                detected = DjiDLog;                       // keep scanning — a later tag may say D-Log M
        }
        return detected;
    }

    /// <summary>Lower-cases and strips separators so "D-Log M", "dlog_m" and "DLogM" all compare equal.</summary>
    private static string Normalize(string s)
    {
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
        {
            if (c is '-' or '_' or ' ' or '.')
                continue;
            buf[n++] = char.ToLowerInvariant(c);
        }
        return new string(buf[..n]);
    }
}
