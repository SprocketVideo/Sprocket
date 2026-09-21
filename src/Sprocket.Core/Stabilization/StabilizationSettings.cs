using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Core.Stabilization;

/// <summary>How the camera path is smoothed (the Mode dropdown, plan/features/stabilization.md).</summary>
public enum StabilizationMode
{
    /// <summary>Adaptive, intent-preserving smoothing (FCP InertiaCam-like): follows deliberate pans/zooms,
    /// smooths the jitter around them. The default.</summary>
    SmoothCamera = 0,

    /// <summary>Uniform Gaussian low-pass of the whole path (FCP SmoothCam / Warp's smooth).</summary>
    SmoothMotion = 1,

    /// <summary>Tripod: the path is held constant (its mean), removing all camera movement.</summary>
    CameraLock = 2,
}

/// <summary>The motion model the solve uses (the Method dropdown).</summary>
public enum StabilizationMethod
{
    /// <summary>Translation only (2-DOF).</summary>
    Translation = 0,

    /// <summary>Similarity: translation + rotation + uniform scale (4-DOF). The default.</summary>
    Similarity = 1,

    /// <summary>Perspective: the similarity channels plus the tracked homography's perspective row, so the solve
    /// can undo perspective wobble (parallax / lens tilt) that a similarity cannot. Full mesh (Subspace) warping
    /// is a follow-on.</summary>
    Perspective = 2,
}

/// <summary>How the scale channel is handled (the Scale dropdown).</summary>
public enum ScaleMode
{
    /// <summary>Smoothed like the other channels (scaled by <see cref="StabilizationSettings.ScaleSmooth"/>).</summary>
    Smooth = 0,

    /// <summary>Keep the original scale change (no scale correction).</summary>
    Preserve = 1,

    /// <summary>Remove all scale change relative to <see cref="StabilizationSettings.ScaleLockRef"/> — the
    /// focus-breathing fix.</summary>
    Lock = 2,
}

/// <summary>Which scale the <see cref="ScaleMode.Lock"/> mode holds every frame to.</summary>
public enum ScaleLockReference
{
    /// <summary>The most zoomed-in scale in the range (largest scale) — never scales down, so no borders.</summary>
    Tightest = 0,

    /// <summary>The widest (smallest) scale in the range.</summary>
    Widest = 1,

    /// <summary>The first frame's scale.</summary>
    FirstFrame = 2,

    /// <summary>The median scale over the range.</summary>
    Median = 3,
}

/// <summary>
/// The stabilization solve settings, read from a clip's stabilization <see cref="EffectInstance"/> at a
/// frame's time (plan/features/stabilization.md). A pure, immutable value — the solver
/// (<see cref="StabilizationSolver"/>) is a deterministic function of (motion track, settings), which is
/// what makes the corrected frame identical in preview and export (ARCHITECTURE.md §5). Analysis-only
/// controls that don't affect the solve (Show Track Points, Hide Banner) are not carried here.
/// </summary>
public sealed record StabilizationSettings(
    StabilizationMode Mode,
    double Smoothness,
    double Strength,
    StabilizationMethod Method,
    double PositionSmooth,
    double RotationSmooth,
    ScaleMode ScaleMode,
    double ScaleSmooth,
    ScaleLockReference ScaleLockRef,
    bool LockRotation,
    bool Zoom,
    double CroppingRatio,
    bool DetailedAnalysis)
{
    /// <summary>The Mode dropdown choice labels, indexed by <see cref="StabilizationMode"/>.</summary>
    public static IReadOnlyList<string> ModeChoices { get; } = ["Smooth Camera", "Smooth Motion", "Camera Lock"];

    /// <summary>The Method dropdown choice labels, indexed by <see cref="StabilizationMethod"/>.</summary>
    public static IReadOnlyList<string> MethodChoices { get; } = ["Translation", "Similarity", "Perspective"];

    /// <summary>The Scale dropdown choice labels, indexed by <see cref="Stabilization.ScaleMode"/>.</summary>
    public static IReadOnlyList<string> ScaleModeChoices { get; } = ["Smooth", "Preserve", "Lock"];

    /// <summary>The Lock Reference dropdown choice labels, indexed by <see cref="ScaleLockReference"/>.</summary>
    public static IReadOnlyList<string> ScaleLockRefChoices { get; } = ["Tightest", "Widest", "First Frame", "Median"];

    /// <summary>The descriptor defaults, used when a caller has no resolved effect (and by tests).</summary>
    public static StabilizationSettings Default { get; } = new(
        Mode: StabilizationMode.SmoothCamera,
        Smoothness: 0.5,
        Strength: 1.0,
        Method: StabilizationMethod.Similarity,
        PositionSmooth: 1.0,
        RotationSmooth: 1.0,
        ScaleMode: ScaleMode.Smooth,
        ScaleSmooth: 1.0,
        ScaleLockRef: ScaleLockReference.Tightest,
        LockRotation: false,
        Zoom: true,
        CroppingRatio: 0.8,
        DetailedAnalysis: false);

    /// <summary>
    /// Reads the settings out of a resolved stabilization effect (dropdowns come back as their choice index,
    /// toggles as 0/1 read with a ≥ 0.5 threshold). Values are clamped to their declared ranges so an
    /// out-of-range keyframe or a hand-edited project can't destabilise the solve. Falls back to
    /// <see cref="Default"/> for any parameter the effect doesn't set.
    /// </summary>
    public static StabilizationSettings FromResolvedEffect(ResolvedEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return FromParameters(effect.Get);
    }

    /// <summary>
    /// Builds the settings from a parameter reader <paramref name="get"/> (name, fallback) → value — the shared core
    /// of <see cref="FromResolvedEffect"/>, also used by the Inspector to solve the camera-path graph / applied-zoom
    /// readout directly off an <c>EffectInstance</c> (evaluated at the current source time). Same clamps apply.
    /// </summary>
    public static StabilizationSettings FromParameters(Func<string, double, double> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        return new StabilizationSettings(
            Mode: (StabilizationMode)ChoiceIndex(get, EffectParamNames.StabMode, ModeChoices.Count, (int)Default.Mode),
            Smoothness: Clamp01(get(EffectParamNames.Smoothness, Default.Smoothness)),
            Strength: Clamp01(get(EffectParamNames.Strength, Default.Strength)),
            Method: (StabilizationMethod)ChoiceIndex(get, EffectParamNames.StabMethod, MethodChoices.Count, (int)Default.Method),
            PositionSmooth: ClampMultiplier(get(EffectParamNames.PositionSmooth, Default.PositionSmooth)),
            RotationSmooth: ClampMultiplier(get(EffectParamNames.RotationSmooth, Default.RotationSmooth)),
            ScaleMode: (ScaleMode)ChoiceIndex(get, EffectParamNames.ScaleMode, ScaleModeChoices.Count, (int)Default.ScaleMode),
            ScaleSmooth: ClampMultiplier(get(EffectParamNames.ScaleSmooth, Default.ScaleSmooth)),
            ScaleLockRef: (ScaleLockReference)ChoiceIndex(get, EffectParamNames.ScaleLockRef, ScaleLockRefChoices.Count, (int)Default.ScaleLockRef),
            LockRotation: Toggle(get, EffectParamNames.LockRotation, Default.LockRotation),
            Zoom: Toggle(get, EffectParamNames.Zoom, Default.Zoom),
            CroppingRatio: Math.Clamp(get(EffectParamNames.CroppingRatio, Default.CroppingRatio), 0.5, 1.0),
            DetailedAnalysis: Toggle(get, EffectParamNames.DetailedAnalysis, Default.DetailedAnalysis));
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    private static double ClampMultiplier(double v) => Math.Clamp(v, 0.0, 2.0);

    private static bool Toggle(Func<string, double, double> get, string name, bool fallback) =>
        get(name, fallback ? 1.0 : 0.0) >= 0.5;

    private static int ChoiceIndex(Func<string, double, double> get, string name, int count, int fallback)
    {
        int index = (int)Math.Round(get(name, fallback), MidpointRounding.AwayFromZero);
        return Math.Clamp(index, 0, count - 1);
    }
}
