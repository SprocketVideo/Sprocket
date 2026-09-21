using static Sprocket.Core.Model.EffectParamNames;

namespace Sprocket.Core.Model;

/// <summary>
/// The Black &amp; White effect's factory preset library (plan/features/black-and-white.md), kept beside
/// <see cref="EffectCatalog"/> so the catalog file stays readable. Phase 4 ships the <b>non-film</b> families —
/// <see cref="Neutral"/>, <see cref="Filters"/>, <see cref="Toning"/> and <see cref="Cinematic"/>; the 19
/// film-stock presets (with their "Inspired by …" descriptions) land in phase 5.
///
/// <para><b>Layering by scope (the Studio Reverb rule, generalised):</b> every preset leaves
/// <see cref="EffectParamNames.Mix"/> (Amount) untouched so switching looks keeps the user's dry/wet. Beyond
/// that, each family touches only the parameters in its scope so presets from different families layer
/// sensibly: <see cref="Filters"/> set only conversion (filter + mixer), <see cref="Toning"/> set only the
/// finishing tint, the tonal members of <see cref="Neutral"/> set only the film tone response — so you can pick
/// a filter, then a toning, then a contrast look and each keeps the others. Only the <c>Neutral ▸ Neutral</c>
/// reset and the complete <see cref="Cinematic"/> looks span all groups.</para>
/// </summary>
public static class BlackWhitePresets
{
    // Family separator matching the plan's "Family ▸ Name" convention (U+25B8); the Inspector's preset combo
    // groups visually on the shared prefix.
    private const string Sep = " ▸ ";

    /// <summary>
    /// Tonal baselines. <c>Neutral</c> is the full reset (every conversion/film/finishing parameter back to its
    /// default, Mix left alone); the rest set only the film tone response so they layer over a filter/toning.
    /// </summary>
    public static IReadOnlyList<EffectPreset> Neutral { get; } =
    [
        // The reset: everything but Mix back to default (mirrors the descriptor defaults).
        new EffectPreset("Neutral" + Sep + "Neutral", new Dictionary<string, double>
        {
            [FilterHue] = 0.0, [FilterStrength] = 0.0,
            [MixReds] = 0.0, [MixOranges] = 0.0, [MixYellows] = 0.0, [MixGreens] = 0.0,
            [MixAquas] = 0.0, [MixBlues] = 0.0, [MixPurples] = 0.0, [MixMagentas] = 0.0,
            [Exposure] = 0.0, [Contrast] = 1.0, [Shadows] = 0.0, [Highlights] = 0.0,
            [GrainAmount] = 0.0, [GrainSize] = 1.0, [GrainSeedLock] = 0.0,
            [ToneHue] = 35.0, [ToneStrength] = 0.0,
            [SplitShadowHue] = 35.0, [SplitHighlightHue] = 210.0, [SplitStrength] = 0.0, [SplitBalance] = 0.0,
            [VignetteAmount] = 0.0, [VignetteSize] = 0.7, [VignetteSoftness] = 0.5,
        }),
        Tone("Neutral" + Sep + "Flat / Low Contrast", contrast: 0.75, shadows: 0.2, highlights: -0.2),
        Tone("Neutral" + Sep + "High Contrast", contrast: 1.45, shadows: -0.2, highlights: 0.15),
        Tone("Neutral" + Sep + "High Key", exposure: 0.6, contrast: 0.8, shadows: 0.3, highlights: 0.3),
        Tone("Neutral" + Sep + "Low Key", exposure: -0.5, contrast: 1.3, shadows: -0.4, highlights: -0.1),
        Tone("Neutral" + Sep + "Maximum White", exposure: 0.4, contrast: 1.2, highlights: 0.5),
        Tone("Neutral" + Sep + "Maximum Black", exposure: -0.4, contrast: 1.2, shadows: -0.5),
    ];

    /// <summary>
    /// Optical colour filters (Wratten-style): a virtual filter over the lens changes how colours map to tone —
    /// a red filter darkens blue skies, a green filter lightens foliage and skin. Conversion params only.
    /// </summary>
    public static IReadOnlyList<EffectPreset> Filters { get; } =
    [
        Filter("Filters" + Sep + "Yellow (8)", hue: 60.0, strength: 0.6),
        Filter("Filters" + Sep + "Orange (16)", hue: 30.0, strength: 0.7),
        Filter("Filters" + Sep + "Red (25)", hue: 0.0, strength: 0.75),
        Filter("Filters" + Sep + "Deep Red (29)", hue: 0.0, strength: 0.9),
        Filter("Filters" + Sep + "Green (11)", hue: 120.0, strength: 0.65),
        Filter("Filters" + Sep + "Blue (47)", hue: 240.0, strength: 0.7),
        // Infrared: foliage/greens render near-white, blue skies near-black, with a slight highlight glow — the
        // one filter preset that also reaches into the mixer and a touch of Highlights to fake the IR halation.
        new EffectPreset("Filters" + Sep + "Infrared", new Dictionary<string, double>
        {
            [FilterHue] = 0.0, [FilterStrength] = 0.0,
            [MixReds] = 40.0, [MixOranges] = 55.0, [MixYellows] = 70.0, [MixGreens] = 90.0,
            [MixAquas] = -20.0, [MixBlues] = -85.0, [MixPurples] = -60.0, [MixMagentas] = 0.0,
            [Highlights] = 0.3,
        }),
    ];

    /// <summary>
    /// Darkroom tonings — a single tint or a split-tone (shadows one hue, highlights another). Finishing params
    /// only, so a toning layers over any filter/film choice.
    /// </summary>
    public static IReadOnlyList<EffectPreset> Toning { get; } =
    [
        SingleTone("Toning" + Sep + "Sepia", hue: 35.0, strength: 0.6),
        SingleTone("Toning" + Sep + "Warm Sepia", hue: 28.0, strength: 0.7),
        SingleTone("Toning" + Sep + "Cool Sepia", hue: 45.0, strength: 0.45),
        // Selenium: a subtle purple-brown — cool purple shadows against warm highlights, gently applied.
        SplitTone("Toning" + Sep + "Selenium", shadowHue: 295.0, highlightHue: 30.0, strength: 0.3, balance: -0.2),
        SingleTone("Toning" + Sep + "Cyanotype", hue: 210.0, strength: 0.7),
        SingleTone("Toning" + Sep + "Platinum / Palladium", hue: 40.0, strength: 0.25),
        SingleTone("Toning" + Sep + "Coffee / Antique", hue: 25.0, strength: 0.55),
        SingleTone("Toning" + Sep + "Gold Tone", hue: 48.0, strength: 0.5),
        SplitTone("Toning" + Sep + "Split — Warm Shadows / Cool Highlights",
            shadowHue: 35.0, highlightHue: 210.0, strength: 0.5, balance: 0.0),
        SplitTone("Toning" + Sep + "Split — Cool Shadows / Warm Highlights",
            shadowHue: 210.0, highlightHue: 35.0, strength: 0.5, balance: 0.0),
    ];

    /// <summary>
    /// Complete cinematic looks spanning conversion + film + finishing — pick one as a starting point rather
    /// than layering. Each sets the parameters that define it (and leaves Mix alone).
    /// </summary>
    public static IReadOnlyList<EffectPreset> Cinematic { get; } =
    [
        new EffectPreset("Cinematic" + Sep + "Film Noir", new Dictionary<string, double>
        {
            [FilterHue] = 0.0, [FilterStrength] = 0.7, [Contrast] = 1.5, [Shadows] = -0.45, [Highlights] = 0.1,
            [VignetteAmount] = -0.6, [VignetteSize] = 0.6, [VignetteSoftness] = 0.5,
        }),
        new EffectPreset("Cinematic" + Sep + "Silent Era", new Dictionary<string, double>
        {
            [Contrast] = 0.85, [Shadows] = 0.15,
            [GrainAmount] = 0.6, [GrainSize] = 2.5, [GrainSeedLock] = 0.0,
            [ToneHue] = 35.0, [ToneStrength] = 0.5,
            [VignetteAmount] = -0.4, [VignetteSize] = 0.65, [VignetteSoftness] = 0.6,
        }),
        new EffectPreset("Cinematic" + Sep + "Newsreel", new Dictionary<string, double>
        {
            [Contrast] = 0.9, [GrainAmount] = 0.35, [GrainSize] = 1.8, [VignetteAmount] = -0.2,
        }),
        new EffectPreset("Cinematic" + Sep + "Modern Cinema", new Dictionary<string, double>
        {
            [Contrast] = 1.35, [Shadows] = -0.1, [Highlights] = 0.05,
            [GrainAmount] = 0.0, [VignetteAmount] = -0.15, [VignetteSize] = 0.8,
        }),
        new EffectPreset("Cinematic" + Sep + "Documentary", new Dictionary<string, double>
        {
            [Contrast] = 1.05, [GrainAmount] = 0.2, [GrainSize] = 1.5,
        }),
        new EffectPreset("Cinematic" + Sep + "Street / Pushed", new Dictionary<string, double>
        {
            [Exposure] = 0.3, [Contrast] = 1.4, [Shadows] = 0.15,
            [GrainAmount] = 0.55, [GrainSize] = 2.0,
        }),
        new EffectPreset("Cinematic" + Sep + "Fashion High Key", new Dictionary<string, double>
        {
            [Exposure] = 0.7, [Contrast] = 0.85, [Shadows] = 0.3, [Highlights] = 0.35,
            [GrainAmount] = 0.1, [GrainSize] = 1.0,
        }),
        new EffectPreset("Cinematic" + Sep + "Dramatic Landscape", new Dictionary<string, double>
        {
            [FilterHue] = 0.0, [FilterStrength] = 0.75, [Contrast] = 1.3, [Shadows] = -0.4,
            [VignetteAmount] = -0.3, [VignetteSize] = 0.75, [VignetteSoftness] = 0.5,
        }),
        new EffectPreset("Cinematic" + Sep + "Portrait", new Dictionary<string, double>
        {
            [FilterHue] = 30.0, [FilterStrength] = 0.3,
            [MixOranges] = 30.0, [MixGreens] = 15.0,
            [Highlights] = -0.15, [GrainAmount] = 0.12, [GrainSize] = 1.0,
        }),
    ];

    /// <summary>Every non-film preset, in Inspector order (Neutral, Filters, Toning, Cinematic).</summary>
    public static IReadOnlyList<EffectPreset> All { get; } =
    [
        .. Neutral,
        .. Filters,
        .. Toning,
        .. Cinematic,
    ];

    // Film tone-response look: sets only the film group (Exposure/Contrast/Shadows/Highlights), leaving
    // conversion and finishing untouched so it layers over a filter and a toning.
    private static EffectPreset Tone(
        string name, double exposure = 0.0, double contrast = 1.0, double shadows = 0.0, double highlights = 0.0) =>
        new(name, new Dictionary<string, double>
        {
            [Exposure] = exposure, [Contrast] = contrast, [Shadows] = shadows, [Highlights] = highlights,
        });

    // Optical filter: conversion params only.
    private static EffectPreset Filter(string name, double hue, double strength) =>
        new(name, new Dictionary<string, double> { [FilterHue] = hue, [FilterStrength] = strength });

    // Single-tone tint: finishing params only.
    private static EffectPreset SingleTone(string name, double hue, double strength) =>
        new(name, new Dictionary<string, double> { [ToneHue] = hue, [ToneStrength] = strength });

    // Split tone: finishing params only.
    private static EffectPreset SplitTone(string name, double shadowHue, double highlightHue, double strength, double balance) =>
        new(name, new Dictionary<string, double>
        {
            [SplitShadowHue] = shadowHue, [SplitHighlightHue] = highlightHue,
            [SplitStrength] = strength, [SplitBalance] = balance,
        });
}
