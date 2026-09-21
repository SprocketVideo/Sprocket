namespace Sprocket.Core.Stabilization;

/// <summary>
/// The estimated global camera motion between one analysed frame and the next, in <b>normalised image
/// coordinates</b> (translations expressed as a fraction of the analysis width, so the track is
/// resolution-independent and survives a change of analysis resolution). Carries both a similarity
/// decomposition (translation / log-scale / rotation — what most solve modes use) and the full
/// homography (for the Perspective method), plus a confidence for weighting and gap-filling.
/// </summary>
/// <remarks>
/// A pure-data motion primitive in Core (the keystone, ARCHITECTURE.md §2): the stabilization motion track
/// (<see cref="MotionTrack"/>) stores one per analysed frame pair, the <see cref="StabilizationSolver"/>
/// integrates them into a camera path, and <c>Sprocket.Analysis</c>'s <c>MotionEstimator</c> — which
/// references Core — produces them. Core owns the shared type; Analysis owns its estimation.
/// </remarks>
/// <param name="Tx">Horizontal translation, fraction of analysis width.</param>
/// <param name="Ty">Vertical translation, fraction of analysis width.</param>
/// <param name="LogScale">Natural log of the uniform scale factor (0 = no scale change).</param>
/// <param name="Angle">Rotation in radians.</param>
/// <param name="Homography">Full projective motion in normalised coordinates.</param>
/// <param name="Confidence">Inlier ratio of the similarity fit in <c>[0,1]</c> (0 ⇒ unreliable / interpolated).</param>
/// <param name="FeatureCount">Number of features that survived tracking and fed the fit.</param>
public readonly record struct FrameMotion(
    double Tx, double Ty, double LogScale, double Angle,
    Homography Homography, double Confidence, int FeatureCount)
{
    /// <summary>No motion (identity) with zero confidence — used for the first frame and dropped-out gaps.</summary>
    public static FrameMotion Identity => new(0, 0, 0, 0, Homography.Identity, 0, 0);
}
