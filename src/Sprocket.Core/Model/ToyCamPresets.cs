using static Sprocket.Core.Model.EffectParamNames;

namespace Sprocket.Core.Model;

/// <summary>
/// The Toy Cassette Camera stage's factory looks (plan/features/toy-cassette-camera.md, phase 3), kept beside
/// <see cref="EffectCatalog"/> like <see cref="DayForNightPresets"/>. Each is a <em>complete</em> look — it sets
/// every picture parameter, so switching looks never leaves a stray value from the previous one — except
/// <see cref="EffectParamNames.Seed"/>, which every preset leaves untouched so a user's chosen noise pattern
/// survives a change of look (the Day for Night strength rule). The phase-5 one-tap stacks reference these by
/// name.
/// </summary>
public static class ToyCamPresets
{
    /// <summary>A camera in good order on fresh tape: the pixels, tone and border, with only a whisper of noise.</summary>
    public static EffectPreset Clean { get; } = new("Clean", new Dictionary<string, double>
    {
        [HorizontalPixels] = 120.0, [VerticalPixels] = 90.0, [PixelSoftness] = 0.12,
        [Contrast] = 1.1, [BlackCrush] = 0.05, [HighlightBloom] = 0.35,
        [SmearLength] = 0.25, [SmearThreshold] = 0.82,
        [NoiseLines] = 0.05, [Dropouts] = 0.0, [GrainAmount] = 0.2,
        [BorderSize] = 0.25, [BorderSoftness] = 0.2,
    }, "A well-kept camera on fresh tape: blocky soft pixels and the black border, with barely any tape noise.");

    /// <summary>A tape played to death: frequent noise lines and dropouts, heavier shimmer and contrast.</summary>
    public static EffectPreset WornTape { get; } = new("Worn Tape", new Dictionary<string, double>
    {
        [HorizontalPixels] = 120.0, [VerticalPixels] = 90.0, [PixelSoftness] = 0.25,
        [Contrast] = 1.25, [BlackCrush] = 0.1, [HighlightBloom] = 0.55,
        [SmearLength] = 0.4, [SmearThreshold] = 0.76,
        [NoiseLines] = 0.5, [Dropouts] = 0.45, [GrainAmount] = 0.4,
        [BorderSize] = 0.25, [BorderSoftness] = 0.25,
    }, "A cassette dubbed and replayed too often: streaking noise lines, white dropouts and a harsher picture.");

    /// <summary>Shooting indoors or at dusk: deep crushed shadows, heavy shimmer, lamps that bloom and trail.</summary>
    public static EffectPreset LowLight { get; } = new("Low Light", new Dictionary<string, double>
    {
        [HorizontalPixels] = 120.0, [VerticalPixels] = 90.0, [PixelSoftness] = 0.35,
        [Contrast] = 1.1, [BlackCrush] = 0.22, [HighlightBloom] = 0.8,
        [SmearLength] = 0.85, [SmearThreshold] = 0.6,
        [NoiseLines] = 0.3, [Dropouts] = 0.1, [GrainAmount] = 0.9,
        [BorderSize] = 0.25, [BorderSoftness] = 0.25,
    }, "A dim room or dusk: murky crushed shadows, strong sensor shimmer, and lights that bloom and leave long trails.");

    /// <summary>All looks, in picker order.</summary>
    public static IReadOnlyList<EffectPreset> All { get; } = [Clean, WornTape, LowLight];
}
