using static Sprocket.Core.Model.EffectParamNames;

namespace Sprocket.Core.Model;

/// <summary>
/// The Day for Night effect's factory looks (plan/features/special-effects.md, phase 4), kept beside
/// <see cref="EffectCatalog"/> like <see cref="BlackWhitePresets"/>. Each is a <em>complete</em> look — it sets
/// every grade parameter, so switching looks never leaves a stray value from the previous one — except
/// <see cref="EffectParamNames.NightStrength"/>, which every preset leaves untouched so the user's blend
/// survives a change of look (the Studio Reverb Mix rule).
/// </summary>
public static class DayForNightPresets
{
    /// <summary>The descriptor defaults as a preset — a general-purpose night and the reset after experimenting.</summary>
    public static EffectPreset Standard { get; } = new("Standard", new Dictionary<string, double>
    {
        [Exposure] = -2.0, [SkyDarken] = 0.6, [HighlightRolloff] = 0.6, [ShadowFloor] = 0.02,
        [Saturation] = 0.4, [MoonlightTint] = 0.6, [MoonlightHue] = 215.0,
        [PracticalLights] = 0.5, [ProtectSkin] = 0.3, [VignetteAmount] = 0.3,
    }, "A general-purpose moonlit night — the effect's defaults.");

    /// <summary>A landscape or establishing shot: lots of sky, few practicals, deep underexposure.</summary>
    public static EffectPreset ExteriorWide { get; } = new("Exterior Wide", new Dictionary<string, double>
    {
        [Exposure] = -2.4, [SkyDarken] = 0.9, [HighlightRolloff] = 0.7, [ShadowFloor] = 0.02,
        [Saturation] = 0.3, [MoonlightTint] = 0.6, [MoonlightHue] = 215.0,
        [PracticalLights] = 0.3, [ProtectSkin] = 0.2, [VignetteAmount] = 0.35,
    }, "Wide exteriors and landscapes: a heavily darkened sky, deep exposure, little colour.");

    /// <summary>An urban shot with people and lamps: lighter touch, practicals and faces kept alive.</summary>
    public static EffectPreset StreetScene { get; } = new("Street Scene", new Dictionary<string, double>
    {
        [Exposure] = -1.9, [SkyDarken] = 0.6, [HighlightRolloff] = 0.6, [ShadowFloor] = 0.02,
        [Saturation] = 0.4, [MoonlightTint] = 0.5, [MoonlightHue] = 210.0,
        [PracticalLights] = 0.9, [ProtectSkin] = 0.5, [VignetteAmount] = 0.25,
    }, "Streets with people and lamps: lit windows and street lights glow, faces keep their colour.");

    /// <summary>The stylised Hollywood night: a strong blue cast over a lifted, tinted floor.</summary>
    public static EffectPreset BlueMoon { get; } = new("Blue Moon", new Dictionary<string, double>
    {
        [Exposure] = -2.2, [SkyDarken] = 0.7, [HighlightRolloff] = 0.6, [ShadowFloor] = 0.04,
        [Saturation] = 0.25, [MoonlightTint] = 1.0, [MoonlightHue] = 218.0,
        [PracticalLights] = 0.4, [ProtectSkin] = 0.2, [VignetteAmount] = 0.4,
    }, "The classic stylised movie night: a strong blue cast and a lifted, moonlit shadow floor.");

    /// <summary>All looks, in picker order.</summary>
    public static IReadOnlyList<EffectPreset> All { get; } = [Standard, ExteriorWide, StreetScene, BlueMoon];
}
