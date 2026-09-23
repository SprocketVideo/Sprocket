using static Sprocket.Core.Model.EffectParamNames;

namespace Sprocket.Core.Model;

/// <summary>
/// The curated built-in creative looks (plan/features/looks-browser.md) — tier 2 of the ARCHITECTURE §18
/// preset taxonomy, listed by the Looks browser ahead of the user's own. Each look is a short stack over the
/// step-16/34 grading effects (Color, White Balance, Color Wheels, Curves), authored against normalized
/// Rec.709 footage and never carrying the tier-1 input color transform. Values stay deliberately moderate:
/// a look is a starting point that should read on most footage, not a finished grade for one shot.
/// Names are generic (no film-stock or product trademarks), matching the Black &amp; White preset rule.
/// </summary>
public static class LooksCatalog
{
    /// <summary>The browser sections, in display order (user looks follow under <see cref="Look.UserGroup"/>).</summary>
    public static IReadOnlyList<string> Groups { get; } = ["Cinematic", "Film", "Vintage", "Mood", "Clean"];

    /// <summary>Every built-in look, grouped in <see cref="Groups"/> order.</summary>
    public static IReadOnlyList<Look> BuiltIns { get; } =
    [
        // ── Cinematic ──
        Make("teal-orange", "Teal & Orange", "Cinematic",
            "The blockbuster split: cool teal shadows against warm skin and highlights.",
            Wheels(liftR: -0.03, liftG: 0.02, liftB: 0.06, gainR: 0.07, gainG: 0.02, gainB: -0.07),
            Color(contrast: 1.1, saturation: 1.05, vibrance: 0.1)),
        Make("blockbuster", "Blockbuster", "Cinematic",
            "Warm, punchy and high-contrast, with a gentle S-curve.",
            WhiteBalance(temperature: 28, tint: 3),
            Color(contrast: 1.2, saturation: 1.1),
            MasterCurve(shadows: -0.03, highlights: 0.02)),
        Make("cold-thriller", "Cold Thriller", "Cinematic",
            "Cool and slightly green, colour held back, with firm contrast.",
            WhiteBalance(temperature: -30, tint: -30),
            Color(contrast: 1.15, saturation: 0.7),
            Wheels(liftB: 0.02, gammaG: 0.02)),
        Make("bleach-bypass", "Bleach Bypass", "Cinematic",
            "The skipped-bleach print: low saturation, hard contrast, silvery highlights.",
            Color(exposure: -0.1, contrast: 1.3, saturation: 0.45),
            MasterCurve(shadows: -0.04, highlights: 0.03)),
        Make("warm-drama", "Warm Drama", "Cinematic",
            "Warm and moody: deep shadows, restrained colour, a red lean in the highlights.",
            WhiteBalance(temperature: 38),
            Color(contrast: 1.15, saturation: 0.85),
            Wheels(lift: -0.03, gammaR: 0.03, gammaB: -0.03, gainR: 0.03)),

        // ── Film ──
        Make("film-print", "Film Print", "Film",
            "A warm print stock: lifted blacks and softly rolled-off highlights.",
            WhiteBalance(temperature: 20),
            Color(contrast: 1.05, saturation: 0.9),
            MasterCurve(blacks: 0.03, shadows: -0.01, whites: -0.03),
            Wheels(gainR: 0.02, gainB: -0.02)),
        Make("faded-film", "Faded Film", "Film",
            "Aged, faded stock: milky blacks, flattened contrast, cool shadows.",
            Color(contrast: 0.9, saturation: 0.75),
            MasterCurve(blacks: 0.08, whites: -0.05),
            Wheels(liftB: 0.03)),
        Make("vivid-slide", "Vivid Slide", "Film",
            "Saturated reversal film: dense colour and deep, contrasty shadows.",
            Color(contrast: 1.1, saturation: 1.1, vibrance: 0.15),
            MasterCurve(shadows: -0.02, highlights: 0.01)),
        Make("cross-process", "Cross Process", "Film",
            "Slide film in negative chemistry: yellow highlights, blue-lifted blacks, strong contrast.",
            Curves(redShadows: -0.04, redHighlights: 0.04,
                   greenBlacks: 0.05, greenShadows: 0.02, greenHighlights: 0.05,
                   blueBlacks: 0.18, blueHighlights: -0.10),
            Color(contrast: 1.1, saturation: 1.1)),

        // ── Vintage ──
        Make("seventies", "Seventies", "Vintage",
            "Warm and golden with faded blacks — a sun-soaked 1970s print.",
            WhiteBalance(temperature: 35, tint: 8),
            Color(saturation: 0.8),
            MasterCurve(blacks: 0.05, whites: -0.05),
            Wheels(gammaR: 0.02, gainB: -0.06)),
        Make("instant-print", "Instant Print", "Vintage",
            "An instant-camera print: soft contrast, cyan-green shadows, warm highlights.",
            WhiteBalance(tint: 4),
            Color(contrast: 0.95, saturation: 0.85),
            MasterCurve(blacks: 0.06, whites: -0.03),
            Wheels(liftR: -0.02, liftG: 0.04, liftB: 0.04, gainR: 0.05, gainB: -0.05)),
        Make("sun-bleached", "Sun-Bleached", "Vintage",
            "Bright, washed-out and warm, like a photo left in the sun.",
            WhiteBalance(temperature: 35),
            Color(exposure: 0.2, contrast: 0.8, saturation: 0.55),
            MasterCurve(blacks: 0.06, whites: -0.04)),

        // ── Mood ──
        Make("golden-hour", "Golden Hour", "Mood",
            "Late-afternoon warmth: golden highlights and rich, lifted colour.",
            WhiteBalance(temperature: 35, tint: 6),
            Color(exposure: 0.1, vibrance: 0.1),
            Wheels(gainR: 0.05, gainG: 0.02, gainB: -0.06)),
        Make("winter", "Winter", "Mood",
            "A cold, pale winter light with muted colour.",
            WhiteBalance(temperature: -35),
            Color(exposure: 0.1, saturation: 0.55),
            MasterCurve(blacks: 0.03)),
        Make("moody-desaturated", "Moody Desaturated", "Mood",
            "Dark and muted: pulled-back colour, cool shadows, tamed highlights.",
            Color(exposure: -0.2, contrast: 1.15, saturation: 0.6),
            Wheels(lift: -0.02, liftB: 0.02),
            MasterCurve(highlights: -0.03)),
        Make("dreamy-pastel", "Dreamy Pastel", "Mood",
            "Soft and airy: low contrast, lifted blacks, pastel colour with a pink lean.",
            WhiteBalance(temperature: 5, tint: 22),
            Color(exposure: 0.25, contrast: 0.8, saturation: 0.8),
            MasterCurve(blacks: 0.08)),
        Make("code-green", "Code Green", "Mood",
            "A sickly digital green cast with hard contrast — surveillance and sci-fi.",
            WhiteBalance(temperature: -15, tint: -55),
            Color(contrast: 1.2, saturation: 0.7),
            Wheels(gammaG: 0.05)),

        // ── Clean ──
        Make("natural-punch", "Natural Punch", "Clean",
            "A subtle lift for flat footage: a touch of contrast and vibrance, nothing stylised.",
            Color(contrast: 1.1, vibrance: 0.25),
            MasterCurve(shadows: -0.02, highlights: 0.02)),
        Make("bright-airy", "Bright & Airy", "Clean",
            "Bright, clean and faintly warm — lifestyle and wedding footage.",
            WhiteBalance(temperature: 5),
            Color(exposure: 0.15, contrast: 0.9, saturation: 0.85),
            MasterCurve(blacks: 0.03, highlights: -0.03, whites: -0.02)),
        Make("soft-contrast", "Soft Contrast", "Clean",
            "Gently flattened contrast with softened blacks and whites.",
            Color(contrast: 0.85),
            MasterCurve(blacks: 0.04, highlights: 0.02)),
    ];

    /// <summary>The built-in look with id <paramref name="id"/>, or <see langword="null"/>.</summary>
    public static Look? Find(string id) => BuiltIns.FirstOrDefault(l => l.Id == id);

    private static Look Make(string slug, string name, string group, string description, params LookEntry[] entries) =>
        new(Look.BuiltInPrefix + slug, name, group, description, entries);

    // Entry builders: only non-neutral values are written, so each look reads as the handful of moves that make it.

    private static LookEntry Color(double exposure = 0, double contrast = 1, double saturation = 1, double vibrance = 0) =>
        Entry(EffectTypeIds.Color,
            (Exposure, exposure, 0), (Contrast, contrast, 1), (Saturation, saturation, 1), (Vibrance, vibrance, 0));

    private static LookEntry WhiteBalance(double temperature = 0, double tint = 0) =>
        Entry(EffectTypeIds.WhiteBalance, (Temperature, temperature, 0), (Tint, tint, 0));

    private static LookEntry Wheels(
        double lift = 0, double liftR = 0, double liftG = 0, double liftB = 0,
        double gamma = 0, double gammaR = 0, double gammaG = 0, double gammaB = 0,
        double gain = 0, double gainR = 0, double gainG = 0, double gainB = 0) =>
        Entry(EffectTypeIds.ColorWheels,
            (LiftMaster, lift, 0), (LiftR, liftR, 0), (LiftG, liftG, 0), (LiftB, liftB, 0),
            (GammaMaster, gamma, 0), (GammaR, gammaR, 0), (GammaG, gammaG, 0), (GammaB, gammaB, 0),
            (GainMaster, gain, 0), (GainR, gainR, 0), (GainG, gainG, 0), (GainB, gainB, 0));

    private static LookEntry MasterCurve(
        double blacks = 0, double shadows = 0, double mids = 0, double highlights = 0, double whites = 0) =>
        Entry(EffectTypeIds.Curves,
            (CurveMasterBlacks, blacks, 0), (CurveMasterShadows, shadows, 0), (CurveMasterMids, mids, 0),
            (CurveMasterHighlights, highlights, 0), (CurveMasterWhites, whites, 0));

    private static LookEntry Curves(
        double redShadows = 0, double redHighlights = 0,
        double greenBlacks = 0, double greenShadows = 0, double greenHighlights = 0,
        double blueBlacks = 0, double blueHighlights = 0) =>
        Entry(EffectTypeIds.Curves,
            (CurveRedShadows, redShadows, 0), (CurveRedHighlights, redHighlights, 0),
            (CurveGreenBlacks, greenBlacks, 0), (CurveGreenShadows, greenShadows, 0), (CurveGreenHighlights, greenHighlights, 0),
            (CurveBlueBlacks, blueBlacks, 0), (CurveBlueHighlights, blueHighlights, 0));

    private static LookEntry Entry(string effectTypeId, params (string Name, double Value, double Neutral)[] values)
    {
        var map = new Dictionary<string, double>();
        foreach ((string name, double value, double neutral) in values)
            if (value != neutral)
                map[name] = value;
        return new LookEntry(effectTypeId, map);
    }
}
