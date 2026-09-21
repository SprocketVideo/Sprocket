namespace Sprocket.Core.Stabilization;

/// <summary>
/// A 3×3 projective transform with the bottom-right element fixed at 1 (8 degrees of freedom). Stores
/// the eight free coefficients; maps a point <c>(x,y)</c> to <c>(x',y')</c> with a perspective divide.
/// Coefficients are in whatever coordinate space the producer used (the motion estimator emits them in
/// normalised image coordinates — see <see cref="FrameMotion"/>).
/// </summary>
/// <remarks>
/// A pure-data motion primitive: it lives in Core (the keystone, ARCHITECTURE.md §2) so the stabilization
/// motion track (<see cref="MotionTrack"/>) and solver can carry it, and <c>Sprocket.Analysis</c>'s estimator —
/// which depends on Core — produces it. Core owns the shared type; Analysis owns the estimation of it.
/// </remarks>
public readonly record struct Homography(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21)
{
    /// <summary>The identity transform.</summary>
    public static Homography Identity => new(1, 0, 0, 0, 1, 0, 0, 0);

    /// <summary>Maps a point through the homography (with perspective divide).</summary>
    public (double X, double Y) Map(double x, double y)
    {
        double w = M20 * x + M21 * y + 1.0;
        double inv = w != 0.0 ? 1.0 / w : 0.0;
        return ((M00 * x + M01 * y + M02) * inv, (M10 * x + M11 * y + M12) * inv);
    }
}
