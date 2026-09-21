using static Sprocket.Core.Model.EffectParamNames;

namespace Sprocket.Core.Model;

/// <summary>
/// The Stabilization effect's factory presets (plan/features/stabilization.md), kept beside
/// <see cref="EffectCatalog"/> so the catalog file stays readable. These are whole-look starting points —
/// unlike the layering effect presets, each sets the full set of solve controls it needs. The two focus
/// presets are the feature's headline: <c>Fix Focus Breathing Only</c> locks scale while leaving position and
/// rotation alone, and <c>Horizon Lock</c> removes rotation relative to the first frame.
///
/// <para>Presets set only <em>solve</em> parameters; the analysis/workflow toggles
/// (<see cref="EffectParamNames.DetailedAnalysis"/>, <see cref="EffectParamNames.ShowTrackPoints"/>,
/// <see cref="EffectParamNames.HideBanner"/>) are left to the user.</para>
/// </summary>
public static class StabilizationPresets
{
    /// <summary>All stabilization presets, in Inspector order.</summary>
    public static IReadOnlyList<EffectPreset> All { get; } =
    [
        // Balanced default: adaptive smoothing, full strength, auto-zoom to a 0.8 cropping budget.
        new EffectPreset("Default", new Dictionary<string, double>
        {
            [StabMode] = 0, [Smoothness] = 0.5, [Strength] = 1.0, [StabMethod] = 1,
            [PositionSmooth] = 1.0, [RotationSmooth] = 1.0,
            [ScaleMode] = 0, [ScaleSmooth] = 1.0, [ScaleLockRef] = 0,
            [LockRotation] = 0, [Zoom] = 1, [CroppingRatio] = 0.8,
        }),
        // Light touch: keeps the shot feeling natural.
        new EffectPreset("Gentle", new Dictionary<string, double>
        {
            [StabMode] = 0, [Smoothness] = 0.3, [Strength] = 0.8, [StabMethod] = 1,
            [PositionSmooth] = 1.0, [RotationSmooth] = 1.0, [ScaleMode] = 0, [ScaleSmooth] = 1.0,
            [Zoom] = 1, [CroppingRatio] = 0.9,
        }),
        // Aggressive smoothing for very shaky footage (spends more of the crop budget).
        new EffectPreset("Strong", new Dictionary<string, double>
        {
            [StabMode] = 0, [Smoothness] = 0.85, [Strength] = 1.0, [StabMethod] = 1,
            [PositionSmooth] = 1.0, [RotationSmooth] = 1.0, [ScaleMode] = 0, [ScaleSmooth] = 1.0,
            [Zoom] = 1, [CroppingRatio] = 0.7,
        }),
        // Tripod: remove all camera movement (locks the whole path).
        new EffectPreset("Camera Lock / Tripod", new Dictionary<string, double>
        {
            [StabMode] = 2, [Smoothness] = 1.0, [Strength] = 1.0, [StabMethod] = 1,
            [PositionSmooth] = 1.0, [RotationSmooth] = 1.0, [ScaleMode] = 0, [ScaleSmooth] = 1.0,
            [Zoom] = 1, [CroppingRatio] = 0.7,
        }),
        // Handheld look: smooth the jitter but keep some of the original movement (Resolve's Strength).
        new EffectPreset("Handheld Look", new Dictionary<string, double>
        {
            [StabMode] = 0, [Smoothness] = 0.5, [Strength] = 0.6, [StabMethod] = 1,
            [PositionSmooth] = 1.0, [RotationSmooth] = 1.0, [ScaleMode] = 0, [ScaleSmooth] = 1.0,
            [Zoom] = 1, [CroppingRatio] = 0.85,
        }),
        // Focus-breathing fix only: lock scale to the tightest framing, leave pan/tilt/rotation untouched.
        new EffectPreset("Fix Focus Breathing Only", new Dictionary<string, double>
        {
            [StabMode] = 0, [Strength] = 1.0, [StabMethod] = 1,
            [PositionSmooth] = 0.0, [RotationSmooth] = 0.0,
            [ScaleMode] = 2, [ScaleLockRef] = 0,
            [Zoom] = 1, [CroppingRatio] = 0.9,
        }),
        // Horizon lock: remove rotation relative to the first frame, smooth position normally.
        new EffectPreset("Horizon Lock", new Dictionary<string, double>
        {
            [StabMode] = 0, [Smoothness] = 0.5, [Strength] = 1.0, [StabMethod] = 1,
            [PositionSmooth] = 1.0, [RotationSmooth] = 1.0, [ScaleMode] = 0, [ScaleSmooth] = 1.0,
            [LockRotation] = 1, [Zoom] = 1, [CroppingRatio] = 0.85,
        }),
    ];
}
