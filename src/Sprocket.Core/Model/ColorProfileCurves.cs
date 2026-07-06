namespace Sprocket.Core.Model;

/// <summary>A 3×3 row-major matrix converting a profile's native gamut (linear light) to Rec.709 primaries
/// (linear light) — see <see cref="ColorProfileCurves"/>.</summary>
public readonly record struct GamutMatrix(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    /// <summary>Applies the matrix to a linear-light RGB triple.</summary>
    public (double R, double G, double B) Apply(double r, double g, double b) => (
        M00 * r + M01 * g + M02 * b,
        M10 * r + M11 * g + M12 * b,
        M20 * r + M21 * g + M22 * b);
}

/// <summary>
/// The seven constants <see cref="ColorProfileCurves.CurveUniforms"/> needs to evaluate one profile's decode
/// curve, tagged with which shape (<see cref="Kind"/>) they belong to. This record is the single source of
/// truth both the C# reference math (<see cref="ColorProfileCurves.Decode"/>) and
/// <c>Sprocket.Render.SkiaEffectPipeline</c>'s GPU shader uniforms are built from, so the two can never drift
/// apart. Kinds 2–5 are each used by exactly one profile with no vendor-specific constants (the formula is
/// fully literal), so their <c>C0</c>–<c>C6</c> are unused/zero.
/// </summary>
public readonly record struct LogCurveUniforms(int Kind, double C0, double C1, double C2, double C3, double C4, double C5, double C6);

/// <summary>
/// The math side of the input color transform (PLAN.md step 52): closed-form decode curves + gamut
/// matrices for every log profile in <see cref="ColorProfiles.All"/> except the DJI pair, which stays
/// LUT-based (<see cref="Sprocket.Render.ColorLuts"/>) because DJI has never published its curve's formula.
/// Every other vendor here <em>does</em> publish an exact transfer-function formula and gamut primaries in
/// their own color-science whitepaper, so no vendor asset is bundled for these — <see cref="Decode"/> and
/// the matrices below are transcribed directly from each vendor's own specification (cited per profile).
/// <see cref="Sprocket.Render.SkiaEffectPipeline"/>'s curve shader is a float32 GPU port of this same math,
/// driven by the exact same <see cref="CurveUniforms"/> values — render tests assert the shader matches this
/// reference for sample inputs.
/// </summary>
public static class ColorProfileCurves
{
    // Curve shapes (Kind, matched by the SkSL shader's own branch order — keep the two in sync):
    //   0 = linear toe below a cut, log10 body above:  L = V<=e*cut+f ? (V-f)/e : (10^((V-d)/c)-b)/a
    //       (ARRI LogC3, Panasonic V-Log [a=1], Fujifilm F-Log2 — three vendors publish this exact shape)
    //   1 = ARRI LogC4: linear toe below V=0, log2 body above (log base 2, not 10)
    //   2 = Sony S-Log3: fully literal (10-bit code-value formula, no free constants)
    //   3 = Canon C-Log3: fully literal (symmetric three-piece log10)
    //   4 = Blackmagic Film Gen 5: fully literal (linear toe, natural-exp body)
    //   5 = Nikon N-Log: fully literal (cubic toe, natural-exp body, 10-bit code-value formula)

    // ARRI "LogC4 Logarithmic Color Space Specification" (1 May 2022): a, b, c, s, t are themselves
    // closed-form per ARRI's spec — computed once, not transcribed as decimals, so there is nothing to get
    // wrong copying digits out of the PDF.
    private static readonly double LogC4A = (Math.Pow(2, 18) - 16) / 117.45;
    private static readonly double LogC4B = (1023.0 - 95.0) / 1023.0;
    private static readonly double LogC4C = 95.0 / 1023.0;
    private static readonly double LogC4S = 7.0 * Math.Log(2.0) * Math.Pow(2.0, 7.0 - 14.0 * LogC4C / LogC4B) / (LogC4A * LogC4B);
    private static readonly double LogC4T = (Math.Pow(2.0, -14.0 * LogC4C / LogC4B + 6.0) - 64.0) / LogC4A;

    // ARRI "ALEXA Log C Curve Usage in VFX" (LogC3, EI 800): ARRI's own "cut" constant is defined in the
    // *scene-linear* domain — the decode threshold on the *encoded* signal is e·cut+f (per ARRI's own
    // decode formula), computed once here rather than re-derived inline every call.
    private const double LogC3Cut = 0.010591, LogC3A = 5.555556, LogC3B = 0.052272, LogC3C = 0.247190, LogC3D = 0.385537, LogC3E = 5.367655, LogC3F = 0.092809;
    private static readonly double LogC3Threshold = LogC3E * LogC3Cut + LogC3F;

    /// <summary>
    /// The <see cref="LogCurveUniforms"/> for a profile id, or <see langword="null"/> for an unknown id or a
    /// LUT-based profile (DJI). See the <see cref="ColorProfileCurves"/> class remarks for source citations.
    /// Kind 0's <c>C0</c> is always the decode threshold on the <em>encoded</em> signal (not a scene-linear
    /// cut) — Panasonic and Fujifilm publish that threshold directly; ARRI's own "cut" is scene-linear, so
    /// it is converted once above (<see cref="LogC3Threshold"/>) rather than inside the shared decode helper.
    /// </summary>
    public static LogCurveUniforms? CurveUniforms(string profileId) => profileId switch
    {
        ColorProfiles.ArriLogC3 => new LogCurveUniforms(0, C0: LogC3Threshold, C1: LogC3A, C2: LogC3B, C3: LogC3C, C4: LogC3D, C5: LogC3E, C6: LogC3F),

        // Panasonic "V-Log/V-Gamut Reference Manual" — same shape as LogC3 with a=1 (no scale inside the log argument).
        ColorProfiles.PanasonicVLog => new LogCurveUniforms(0, C0: 0.181, C1: 1.0, C2: 0.00873, C3: 0.241514, C4: 0.598206, C5: 5.6, C6: 0.125),

        // Fujifilm "F-Log2 Data Sheet" (Ver. 1.1) §2-3 — same ARRI-style shape.
        ColorProfiles.FujifilmFLog2 => new LogCurveUniforms(0, C0: 0.100686685370811, C1: 5.555556, C2: 0.064829, C3: 0.245281, C4: 0.384316, C5: 8.799461, C6: 0.092864),

        ColorProfiles.ArriLogC4 => new LogCurveUniforms(1, LogC4A, LogC4B, LogC4C, LogC4S, LogC4T, 0, 0),
        ColorProfiles.SonySLog3 => new LogCurveUniforms(2, 0, 0, 0, 0, 0, 0, 0),
        ColorProfiles.CanonCLog3 => new LogCurveUniforms(3, 0, 0, 0, 0, 0, 0, 0),
        ColorProfiles.BlackmagicFilm => new LogCurveUniforms(4, 0, 0, 0, 0, 0, 0, 0),
        ColorProfiles.NikonNLog => new LogCurveUniforms(5, 0, 0, 0, 0, 0, 0, 0),
        _ => null,
    };

    /// <summary>
    /// Decodes a profile's log-encoded, normalized (0–1) pixel value <paramref name="v"/> to scene-linear
    /// light in the profile's own native gamut (not yet Rec.709 — apply <see cref="GamutOf"/> next). Unknown
    /// profile ids (including the LUT-based DJI ones, which never call this) pass through unchanged.
    /// </summary>
    public static double Decode(string profileId, double v)
    {
        LogCurveUniforms? u = CurveUniforms(profileId);
        if (u is null)
            return v;

        (int kind, double c0, double c1, double c2, double c3, double c4, double c5, double c6) = u.Value;
        return kind switch
        {
            0 => Log10Toe(v, threshold: c0, a: c1, b: c2, c: c3, d: c4, e: c5, f: c6),
            1 => v < 0.0 ? v * c3 + c4 : (Math.Pow(2.0, 14.0 * (v - c2) / c1 + 6.0) - 64.0) / c0,

            // Sony "Technical Summary for S-Gamut3.Cine/S-Log3" — published in 10-bit code-value form.
            2 => v < 171.2102946929 / 1023.0
                ? (v * 1023.0 - 95.0) * 0.01125 / (171.2102946929 - 95.0)
                : Math.Pow(10.0, (v * 1023.0 - 420.0) / 261.5) * 0.19 - 0.01,

            // Canon Log 3 specification — a symmetric three-piece curve (log tail below black, linear middle, log tail above).
            3 => v < 0.097465473
                ? 0.9 * (1.0 - Math.Pow(10.0, (0.12783901 - v) / 0.36726845)) / 14.98325
                : v <= 0.15277891
                    ? 0.9 * (v - 0.12512219) / 1.9754798
                    : 0.9 * (Math.Pow(10.0, (v - 0.12240537) / 0.36726845) - 1.0) / 14.98325,

            // Blackmagic Design Generation 5 color science — linear toe, natural-exp body (not log10).
            4 => v < 8.283605932402494 * 0.005 + 0.09246575342465753
                ? (v - 0.09246575342465753) / 8.283605932402494
                : Math.Exp((v - 0.5300133392291939) / 0.08692876065491224) - 0.005494072432257808,

            // Nikon "N-Log Specification Document" (1 Sept 2018) — published in 10-bit code-value form; the
            // curve itself is a cubic toe + natural-exp body, unlike every other profile above.
            5 => NLogDecode(v * 1023.0),

            _ => v,
        };
    }

    private static double Log10Toe(double v, double threshold, double a, double b, double c, double d, double e, double f) =>
        v <= threshold ? (v - f) / e : (Math.Pow(10.0, (v - d) / c) - b) / a;

    private static double NLogDecode(double x) => x < 452.0
        ? Math.Pow(x / 650.0, 3.0) - 0.0075
        : Math.Exp((x - 619.0) / 150.0);

    /// <summary>
    /// The profile's native-gamut-to-Rec.709 (linear light) matrix, computed from the vendor's published CIE
    /// xy primaries + D65 white point via the standard primaries→RGB-to-XYZ method, combined with the
    /// Rec.709 XYZ-to-RGB matrix (no chromatic adaptation needed — every profile shares Rec.709's D65 white
    /// point). Returns the identity for an unknown profile id.
    /// </summary>
    public static GamutMatrix GamutOf(string profileId) => profileId switch
    {
        // ARRI Wide Gamut 3
        ColorProfiles.ArriLogC3 => new GamutMatrix(
            1.6175234363, -0.5372866222, -0.0802368141,
            -0.0705727409, 1.3346130623, -0.2640403214,
            -0.0211017280, -0.2269538752, 1.2480556033),

        // ARRI Wide Gamut 4
        ColorProfiles.ArriLogC4 => new GamutMatrix(
            1.8931234427, -0.7808815036, -0.1122419390,
            -0.2057003583, 1.3402574894, -0.1345571311,
            -0.0127057429, -0.1521848758, 1.1648906187),

        // Sony S-Gamut3.Cine
        ColorProfiles.SonySLog3 => new GamutMatrix(
            1.6269474097, -0.5401385389, -0.0868088709,
            -0.1785155271, 1.4179409275, -0.2394254003,
            -0.0444361150, -0.1959199662, 1.2403560812),

        // Panasonic V-Gamut
        ColorProfiles.PanasonicVLog => new GamutMatrix(
            1.8065758837, -0.6956972741, -0.1108786096,
            -0.1700903431, 1.3059552161, -0.1358648730,
            -0.0252057840, -0.1544683294, 1.1796741134),

        // Canon Cinema Gamut
        ColorProfiles.CanonCLog3 => new GamutMatrix(
            1.9238612959, -0.7987606632, -0.1251006327,
            -0.2043108482, 1.4958985098, -0.2915876616,
            -0.0236850210, -0.4201270110, 1.4438120320),

        // Blackmagic Wide Gamut Generation 5
        ColorProfiles.BlackmagicFilm => new GamutMatrix(
            1.5684244798, -0.5227054517, -0.0457191400,
            -0.0863597560, 1.3449478133, -0.2585611599,
            -0.0520418621, -0.2491415367, 1.3009172709),

        // Fujifilm F-Log2 gamut and Nikon N-Gamut are both defined as Rec.2020 primaries — same matrix.
        ColorProfiles.FujifilmFLog2 or ColorProfiles.NikonNLog => new GamutMatrix(
            1.6604910021, -0.5876411388, -0.0728498633,
            -0.1245504745, 1.1328998971, -0.0083494226,
            -0.0181507634, -0.1005788980, 1.1187296614),

        _ => new GamutMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1),
    };

    /// <summary>Standard sRGB/Rec.709 working-space encode (linear light → display-referred), matching the
    /// convention every other grading effect in <c>Sprocket.Render</c> uses (see <c>AcesFilmicEffect</c>).</summary>
    public static double EncodeRec709(double linear)
    {
        double c = Math.Clamp(linear, 0.0, 1.0);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
    }
}
