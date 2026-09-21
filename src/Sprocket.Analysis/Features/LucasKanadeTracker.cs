using System.Numerics;

namespace Sprocket.Analysis.Features;

/// <summary>
/// Pyramidal Lucas-Kanade optical-flow tracker (forward-additive, 21×21 window, ≤30 iterations per
/// level). Tracks sparse features from one frame to the next with sub-pixel accuracy and rejects
/// unreliable tracks with a forward-backward consistency check (a track is kept only if re-tracking
/// the matched point back onto the source lands within <see cref="ForwardBackwardThreshold"/> px of
/// where it started). Stateless aside from small stack scratch — safe to reuse and cheap.
/// </summary>
public sealed class LucasKanadeTracker
{
    /// <summary>Half-width of the integration window (window is 2·r+1 = 21 px).</summary>
    public const int WindowRadius = 10;

    /// <summary>Maximum refinement iterations per pyramid level.</summary>
    public const int MaxIterations = 30;

    /// <summary>Stop refining a level once the update is smaller than this (px).</summary>
    public const float ConvergenceEpsilon = 0.01f;

    /// <summary>Maximum tolerated forward-backward round-trip error (px).</summary>
    public const float ForwardBackwardThreshold = 0.5f;

    // Smallest structure-tensor eigenvalue for a window to be considered trackable (flat regions fail).
    private const float MinEigenvalue = 1e-3f;

    private const int WindowSize = 2 * WindowRadius + 1;
    private const int WindowArea = WindowSize * WindowSize;

    /// <summary>
    /// Tracks each point in <paramref name="pointsA"/> from pyramid <paramref name="a"/> to pyramid
    /// <paramref name="b"/>, writing matches into <paramref name="pointsB"/> and per-point success into
    /// <paramref name="status"/>. Failed points get <c>status = false</c> and their input position echoed.
    /// </summary>
    public void Track(ImagePyramid a, ImagePyramid b, ReadOnlySpan<Vector2> pointsA, Span<Vector2> pointsB, Span<bool> status)
    {
        Span<float> gx = stackalloc float[WindowArea];
        Span<float> gy = stackalloc float[WindowArea];
        Span<float> ia = stackalloc float[WindowArea];

        for (int i = 0; i < pointsA.Length; i++)
        {
            Vector2 pa = pointsA[i];
            if (TrackPoint(a, b, pa, gx, gy, ia, out Vector2 pb) &&
                TrackPoint(b, a, pb, gx, gy, ia, out Vector2 back) &&
                Vector2.Distance(pa, back) <= ForwardBackwardThreshold)
            {
                pointsB[i] = pb;
                status[i] = true;
            }
            else
            {
                pointsB[i] = pb;
                status[i] = false;
            }
        }
    }

    private static bool TrackPoint(ImagePyramid from, ImagePyramid to, Vector2 p,
        Span<float> gx, Span<float> gy, Span<float> ia, out Vector2 matched)
    {
        // Displacement in current-level pixels; starts at zero on the coarsest level.
        float dx = 0f, dy = 0f;

        for (int level = ImagePyramid.LevelCount - 1; level >= 0; level--)
        {
            float scale = 1f / (1 << level);
            float cx = p.X * scale;
            float cy = p.Y * scale;

            GrayImage src = from.Level(level);
            GrayImage dst = to.Level(level);

            // Precompute the window's source intensities, spatial gradients, and the 2x2 tensor G.
            float gxx = 0f, gxy = 0f, gyy = 0f;
            int k = 0;
            for (int wy = -WindowRadius; wy <= WindowRadius; wy++)
            {
                float sy = cy + wy;
                for (int wx = -WindowRadius; wx <= WindowRadius; wx++, k++)
                {
                    float sx = cx + wx;
                    float vx = (src.SampleBilinear(sx + 1, sy) - src.SampleBilinear(sx - 1, sy)) * 0.5f;
                    float vy = (src.SampleBilinear(sx, sy + 1) - src.SampleBilinear(sx, sy - 1)) * 0.5f;
                    gx[k] = vx;
                    gy[k] = vy;
                    ia[k] = src.SampleBilinear(sx, sy);
                    gxx += vx * vx;
                    gxy += vx * vy;
                    gyy += vy * vy;
                }
            }

            // Reject flat / edge-only windows (small min eigenvalue ⇒ ill-conditioned).
            float trace = gxx + gyy;
            float det = gxx * gyy - gxy * gxy;
            float minEig = 0.5f * (trace - MathF.Sqrt(MathF.Max(0f, trace * trace - 4f * det))) / WindowArea;
            if (minEig < MinEigenvalue || det <= 0f)
            {
                matched = p;
                return false;
            }
            float invDet = 1f / det;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                float bx = 0f, by = 0f;
                k = 0;
                for (int wy = -WindowRadius; wy <= WindowRadius; wy++)
                {
                    float sy = cy + wy + dy;
                    for (int wx = -WindowRadius; wx <= WindowRadius; wx++, k++)
                    {
                        float diff = ia[k] - dst.SampleBilinear(cx + wx + dx, sy);
                        bx += diff * gx[k];
                        by += diff * gy[k];
                    }
                }

                // Solve G · eta = b.
                float ex = (gyy * bx - gxy * by) * invDet;
                float ey = (gxx * by - gxy * bx) * invDet;
                dx += ex;
                dy += ey;

                if (ex * ex + ey * ey < ConvergenceEpsilon * ConvergenceEpsilon)
                    break;
            }

            if (level > 0)
            {
                dx *= 2f;
                dy *= 2f;
            }
        }

        matched = new Vector2(p.X + dx, p.Y + dy);
        return true;
    }
}
