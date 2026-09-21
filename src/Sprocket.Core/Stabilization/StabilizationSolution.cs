namespace Sprocket.Core.Stabilization;

/// <summary>One frame's camera-path state, per channel, in width-normalised units (translations as a
/// fraction of frame width, <see cref="LogScale"/> the natural log of scale, <see cref="Angle"/> in radians).
/// Exposed for the Inspector's camera-path graph (phase 6) and for verifying the solve.</summary>
public readonly record struct CameraPathSample(double Tx, double Ty, double LogScale, double Angle);

/// <summary>
/// The output of the <see cref="StabilizationSolver"/> for one motion track + settings: the raw and smoothed
/// (target) camera paths, the per-frame <c>output → source</c> warp the renderer applies, and the uniform zoom
/// chosen to hide the stabilised borders. A pure, deterministic function of (track, settings, frame size), so
/// preview and export produce identical frames (ARCHITECTURE.md §5). All matrices and paths are aligned to the
/// track's frames (index <c>i</c> ↔ <see cref="FramePts"/>[i]).
/// </summary>
public sealed class StabilizationSolution
{
    /// <summary>Builds a solution (used by <see cref="StabilizationSolver"/>).</summary>
    public StabilizationSolution(
        IReadOnlyList<long> framePts,
        IReadOnlyList<CameraPathSample> rawPath,
        IReadOnlyList<CameraPathSample> smoothedPath,
        IReadOnlyList<double[]> outputToSource,
        double appliedZoom,
        double frameAspectYOverX)
    {
        FramePts = framePts;
        RawPath = rawPath;
        SmoothedPath = smoothedPath;
        OutputToSource = outputToSource;
        AppliedZoom = appliedZoom;
        FrameAspectYOverX = frameAspectYOverX;
    }

    /// <summary>Each frame's source presentation time in ticks (ascending) — the renderer binary-searches this
    /// to map a render's source time to a frame index.</summary>
    public IReadOnlyList<long> FramePts { get; }

    /// <summary>The integrated (unsmoothed) camera path per frame.</summary>
    public IReadOnlyList<CameraPathSample> RawPath { get; }

    /// <summary>The target (smoothed / locked / strength-blended) camera path the frames are warped onto.</summary>
    public IReadOnlyList<CameraPathSample> SmoothedPath { get; }

    /// <summary>
    /// Per frame, the <c>output → source</c> 3×3 map as 9 row-major doubles (<c>[a b c; d e f; 0 0 1]</c>) in
    /// <b>centred, width-normalised isotropic</b> coordinates: an output point <c>(x, y)</c> (relative to the
    /// frame centre, both axes divided by the frame width) samples source point <c>(a·x+b·y+c, d·x+e·y+f)</c>.
    /// Identity when there is nothing to correct. The applied zoom is already folded in.
    /// </summary>
    public IReadOnlyList<double[]> OutputToSource { get; }

    /// <summary>The uniform zoom applied to hide the stabilised borders (1 = none). With Zoom on it is at most
    /// <c>1 / croppingRatio</c>; with Zoom off it is 1 (transparent borders are shown instead).</summary>
    public double AppliedZoom { get; }

    /// <summary>The frame's height/width ratio — the source/ output rectangle is
    /// <c>x ∈ [−0.5, 0.5]</c>, <c>y ∈ [−0.5·aspect, 0.5·aspect]</c> in the matrices' coordinate space.</summary>
    public double FrameAspectYOverX { get; }

    /// <summary>The number of frames.</summary>
    public int FrameCount => FramePts.Count;

    /// <summary>The identity <c>output → source</c> matrix (no correction).</summary>
    public static double[] Identity => [1, 0, 0, 0, 1, 0, 0, 0, 1];
}
