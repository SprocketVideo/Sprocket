namespace Sprocket.Core.Model;

/// <summary>How <see cref="ColorProfiles"/> converts a profile to Rec.709 — either by sampling a bundled
/// vendor 3D LUT (<see cref="Lut"/>, DJI) or by evaluating the vendor's own published transfer-function
/// formula + gamut matrix on the GPU (<see cref="Curve"/>, PLAN.md step 52). See <see cref="ColorProfileCurves"/>.</summary>
public enum ColorProfileKind
{
    /// <summary>Converted via a bundled vendor <c>.cube</c> LUT (<see cref="Sprocket.Render.ColorLuts"/> — Render project only).</summary>
    Lut,

    /// <summary>Converted via a closed-form curve + gamut matrix (<see cref="ColorProfileCurves"/>).</summary>
    Curve,
}

/// <summary>
/// The log/camera color profiles Sprocket understands (PLAN.md steps 37, 52). A profile names the encoding a
/// source was shot in; the <see cref="EffectTypeIds.ColorTransform"/> effect converts it to the working
/// space (Rec.709) — via a bundled vendor LUT for DJI (step 37) or a math curve for every other vendor
/// (step 52, <see cref="ColorProfileCurves"/>), since those vendors publish their curve's exact formula.
/// Profiles are identified by stable string ids (recorded on <c>ProbedMediaInfo.DetectedColorProfile</c>) and
/// addressed from the effect's numeric <see cref="EffectParamNames.SourceProfile"/> parameter by their index
/// in <see cref="All"/> — append-only, so persisted indices stay valid.
/// </summary>
public static class ColorProfiles
{
    /// <summary>DJI D-Log (10-bit, D-Gamut) — Mavic 3 / Inspire / Zenmuse cinema profile.</summary>
    public const string DjiDLog = "dji.dlog";

    /// <summary>DJI D-Log M (10-bit) — the consumer-drone/handheld log profile (Mavic 3/4, Air, Mini, Pocket).</summary>
    public const string DjiDLogM = "dji.dlogm";

    /// <summary>ARRI LogC3 (ARRI Wide Gamut 3) — ALEXA cameras through the ALEXA 35 (EI800 curve constants).</summary>
    public const string ArriLogC3 = "arri.logc3";

    /// <summary>ARRI LogC4 (ARRI Wide Gamut 4) — the exposure-index-independent curve introduced with the ALEXA 35.</summary>
    public const string ArriLogC4 = "arri.logc4";

    /// <summary>Sony S-Log3 (paired with S-Gamut3.Cine) — the current Sony cinema log profile.</summary>
    public const string SonySLog3 = "sony.slog3";

    /// <summary>Panasonic V-Log (V-Gamut) — VARICAM/LUMIX cinema profile; Leica licenses the same curve as L-Log.</summary>
    public const string PanasonicVLog = "panasonic.vlog";

    /// <summary>Canon Log 3 (Canon Cinema Gamut) — the current Canon Cinema EOS log profile.</summary>
    public const string CanonCLog3 = "canon.clog3";

    /// <summary>Blackmagic Film Generation 5 (Blackmagic Wide Gamut Generation 5) — current Blackmagic cameras.</summary>
    public const string BlackmagicFilm = "blackmagic.filmgen5";

    /// <summary>Fujifilm F-Log2 (Rec.2020 primaries) — the current Fujifilm cinema log profile.</summary>
    public const string FujifilmFLog2 = "fujifilm.flog2";

    /// <summary>Nikon N-Log (Rec.2020 primaries) — Z-series mirrorless cinema profile.</summary>
    public const string NikonNLog = "nikon.nlog";

    /// <summary>All known profile ids, in <see cref="EffectParamNames.SourceProfile"/> index order.
    /// Append-only: indices are persisted in project files.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        DjiDLog, DjiDLogM,
        ArriLogC3, ArriLogC4, SonySLog3, PanasonicVLog, CanonCLog3, BlackmagicFilm, FujifilmFLog2, NikonNLog,
    ];

    /// <summary>Human-readable names, parallel to <see cref="All"/> (Inspector dropdown labels).</summary>
    public static readonly IReadOnlyList<string> DisplayNames =
    [
        "DJI D-Log → Rec.709", "DJI D-Log M → Rec.709",
        "ARRI LogC3 → Rec.709", "ARRI LogC4 → Rec.709", "Sony S-Log3 → Rec.709",
        "Panasonic V-Log → Rec.709 (also Leica L-Log)", "Canon C-Log3 → Rec.709",
        "Blackmagic Film (Gen 5) → Rec.709", "Fujifilm F-Log2 → Rec.709", "Nikon N-Log → Rec.709",
    ];

    /// <summary>Whether a profile id converts via a bundled LUT or a math curve. Unknown ids report <see cref="ColorProfileKind.Curve"/>
    /// (harmless — <see cref="ColorProfileCurves"/> and <see cref="Sprocket.Render.ColorLuts"/> both no-op/pass-through on an unknown id).</summary>
    public static ColorProfileKind KindOf(string profileId) =>
        profileId is DjiDLog or DjiDLogM ? ColorProfileKind.Lut : ColorProfileKind.Curve;

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

    /// <summary>
    /// Best-effort detection of any known log profile — DJI (<see cref="DetectDjiLog"/>) plus the vendors
    /// added in PLAN.md step 52 — from a source's container/stream metadata tags. Tries each vendor's own
    /// conservative substring heuristic and returns the first hit. Same fallback as <see cref="DetectDjiLog"/>:
    /// none of these vendors tag a distinctive transfer characteristic either, so when detection misses, the
    /// user tags the clip manually via the Inspector's input-transform control.
    /// </summary>
    public static string DetectLogProfile(IEnumerable<KeyValuePair<string, string>> metadata)
    {
        List<KeyValuePair<string, string>> tags = metadata as List<KeyValuePair<string, string>> ?? metadata.ToList();

        string dji = DetectDjiLog(tags);
        if (dji.Length > 0)
            return dji;

        foreach ((_, string value) in tags)
        {
            string v = Normalize(value);
            if (v.Contains("logc4", StringComparison.Ordinal)) return ArriLogC4;
            if (v.Contains("logc3", StringComparison.Ordinal)) return ArriLogC3;
            if (v.Contains("slog3", StringComparison.Ordinal)) return SonySLog3;
            if (v.Contains("vlog", StringComparison.Ordinal)) return PanasonicVLog;
            if (v.Contains("clog3", StringComparison.Ordinal)) return CanonCLog3;
            if (v.Contains("flog2", StringComparison.Ordinal)) return FujifilmFLog2;
            if (v.Contains("nlog", StringComparison.Ordinal)) return NikonNLog;
            if (v.Contains("blackmagicfilm", StringComparison.Ordinal) || v.Contains("bmdfilm", StringComparison.Ordinal))
                return BlackmagicFilm;
        }
        return "";
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
