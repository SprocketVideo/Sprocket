using System.Numerics;
using Sprocket.Analysis.Features;
using Sprocket.Core.Stabilization;

namespace Sprocket.Analysis.Motion;

/// <summary>
/// Estimates the global camera motion between a pair of grayscale frames: detect spatially-bucketed
/// Shi-Tomasi corners in the first frame, track them into the second with pyramidal Lucas-Kanade
/// (forward-backward filtered), then robustly fit both a similarity and a homography to the surviving
/// correspondences. The result is a <see cref="FrameMotion"/> in normalised coordinates.
/// </summary>
/// <remarks>
/// A single instance owns all working buffers (pyramids, detector, feature arrays) and reuses them
/// across <see cref="Estimate"/> calls, so steady-state estimation allocates ≈0 managed bytes
/// (ARCHITECTURE.md §1). Not thread-safe: give each analysis worker its own estimator.
/// </remarks>
public sealed class MotionEstimator
{
    // Fixed seed ⇒ RANSAC is deterministic run to run (golden-frame requirement).
    private const ulong RansacSeed = 0x5FE2_BADC_0FFE_EE00UL;
    private const int RansacIterations = 250;

    private readonly int _bucketsX;
    private readonly int _bucketsY;
    private readonly int _capPerBucket;
    private readonly double _inlierThresholdPx;

    private readonly ImagePyramid _pyramidA = new();
    private readonly ImagePyramid _pyramidB = new();
    private readonly CornerDetector _detector = new();
    private readonly LucasKanadeTracker _tracker = new();

    private Vector2[] _cornersA;
    private Vector2[] _trackedB;
    private bool[] _status;
    private Vector2[] _src;
    private Vector2[] _dst;

    /// <param name="bucketsX">Feature grid columns (default 8).</param>
    /// <param name="bucketsY">Feature grid rows (default 6).</param>
    /// <param name="capPerBucket">Max corners kept per grid cell (default 7 ⇒ ~300 features on an 8×6 grid).</param>
    /// <param name="inlierThresholdPx">RANSAC inlier reprojection distance in pixels.</param>
    public MotionEstimator(int bucketsX = 8, int bucketsY = 6, int capPerBucket = 7, double inlierThresholdPx = 1.5)
    {
        if (bucketsX <= 0 || bucketsY <= 0 || capPerBucket <= 0)
            throw new ArgumentOutOfRangeException(nameof(capPerBucket));

        _bucketsX = bucketsX;
        _bucketsY = bucketsY;
        _capPerBucket = capPerBucket;
        _inlierThresholdPx = inlierThresholdPx;

        int max = bucketsX * bucketsY * capPerBucket;
        _cornersA = new Vector2[max];
        _trackedB = new Vector2[max];
        _status = new bool[max];
        _src = new Vector2[max];
        _dst = new Vector2[max];
    }

    /// <summary>The upper bound on tracked features (grid cells × per-cell cap).</summary>
    public int MaxFeatures => _cornersA.Length;

    /// <summary>
    /// Estimates the motion from <paramref name="previous"/> to <paramref name="current"/>. The two
    /// images must have identical dimensions.
    /// </summary>
    public FrameMotion Estimate(GrayImage previous, GrayImage current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
            throw new ArgumentException("Frame pair must share dimensions.", nameof(current));

        int width = previous.Width;

        _pyramidA.Build(previous);
        _pyramidB.Build(current);

        int cornerCount = _detector.Detect(_pyramidA.Level(0), _bucketsX, _bucketsY, _capPerBucket, _cornersA);
        if (cornerCount < 2)
            return FrameMotion.Identity;

        _tracker.Track(_pyramidA, _pyramidB,
            _cornersA.AsSpan(0, cornerCount), _trackedB.AsSpan(0, cornerCount), _status.AsSpan(0, cornerCount));

        // Compact the surviving correspondences.
        int m = 0;
        for (int i = 0; i < cornerCount; i++)
        {
            if (!_status[i])
                continue;
            _src[m] = _cornersA[i];
            _dst[m] = _trackedB[i];
            m++;
        }
        if (m < 2)
            return FrameMotion.Identity;

        ReadOnlySpan<Vector2> src = _src.AsSpan(0, m);
        ReadOnlySpan<Vector2> dst = _dst.AsSpan(0, m);

        var rng = new XorShiftRng(RansacSeed);
        SimilarityFit sim = RobustFit.FitSimilarity(src, dst, ref rng, _inlierThresholdPx, RansacIterations);
        HomographyFit hom = RobustFit.FitHomography(src, dst, ref rng, _inlierThresholdPx, RansacIterations);

        if (!sim.Ok)
            return FrameMotion.Identity with { FeatureCount = m };

        double invW = 1.0 / width;
        Homography normalisedH = hom.Ok ? NormaliseByWidth(hom.H, width) : Homography.Identity;

        return new FrameMotion(
            Tx: sim.Tx * invW,
            Ty: sim.Ty * invW,
            LogScale: sim.LogScale,
            Angle: sim.Angle,
            Homography: normalisedH,
            Confidence: sim.InlierRatio,
            FeatureCount: m);
    }

    /// <summary>
    /// Rewrites a pixel-space homography into normalised coordinates (coordinate = pixel / width):
    /// <c>H_norm = N · H_px · N⁻¹</c> with <c>N = diag(1/w, 1/w, 1)</c>.
    /// </summary>
    private static Homography NormaliseByWidth(Homography h, int width)
    {
        double w = width;
        double iw = 1.0 / w;
        // Only the translation column (M02,M12) scales down by 1/w and the projective row (M20,M21)
        // scales up by w; the linear block is invariant under the isotropic N.
        return new Homography(
            h.M00, h.M01, h.M02 * iw,
            h.M10, h.M11, h.M12 * iw,
            h.M20 * w, h.M21 * w);
    }
}
