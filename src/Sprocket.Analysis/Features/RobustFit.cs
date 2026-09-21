using System.Numerics;

namespace Sprocket.Analysis.Features;

/// <summary>Result of a RANSAC similarity (4-DOF) fit between two point sets.</summary>
public readonly record struct SimilarityFit(
    double Tx, double Ty, double LogScale, double Angle, int Inliers, double InlierRatio, bool Ok)
{
    /// <summary>A failed fit (too few / degenerate correspondences).</summary>
    public static SimilarityFit Failed => new(0, 0, 0, 0, 0, 0, false);
}

/// <summary>Result of a RANSAC homography (8-DOF) fit between two point sets.</summary>
public readonly record struct HomographyFit(Homography H, int Inliers, double InlierRatio, bool Ok)
{
    /// <summary>A failed fit.</summary>
    public static HomographyFit Failed => new(Homography.Identity, 0, 0, false);
}

/// <summary>
/// Robust (RANSAC) model fitting between corresponding point sets. Fits a 4-DOF similarity
/// (translation + uniform scale + rotation) and an 8-DOF homography (normalised DLT), each with a
/// least-squares refit over the recovered inlier set. Deterministic given the same inputs (seeded
/// <see cref="XorShiftRng"/>). All maths is done in <c>double</c>; inputs/outputs are in the caller's
/// coordinate space (the estimator passes pixel coordinates).
/// </summary>
public static class RobustFit
{
    private const int SimilaritySampleSize = 2;
    private const int HomographySampleSize = 4;

    /// <summary>
    /// Fits a similarity transform mapping <paramref name="src"/> onto <paramref name="dst"/>.
    /// <paramref name="threshold"/> is the inlier reprojection distance (same units as the points).
    /// </summary>
    public static SimilarityFit FitSimilarity(
        ReadOnlySpan<Vector2> src, ReadOnlySpan<Vector2> dst,
        ref XorShiftRng rng, double threshold, int iterations)
    {
        int n = src.Length;
        if (n < SimilaritySampleSize)
            return SimilarityFit.Failed;

        double thr2 = threshold * threshold;
        Span<int> sample = stackalloc int[SimilaritySampleSize];

        double bestA = 1, bestB = 0, bestTx = 0, bestTy = 0;
        int bestInliers = -1;

        for (int it = 0; it < iterations; it++)
        {
            PickSample(ref rng, n, sample);
            if (!SolveSimilarityMinimal(src, dst, sample, out double a, out double b, out double tx, out double ty))
                continue;

            int inliers = CountSimilarityInliers(src, dst, a, b, tx, ty, thr2);
            if (inliers > bestInliers)
            {
                bestInliers = inliers;
                bestA = a; bestB = b; bestTx = tx; bestTy = ty;
            }
        }

        if (bestInliers < SimilaritySampleSize)
            return SimilarityFit.Failed;

        // Least-squares refit over the best inlier set, then a final inlier recount.
        if (RefitSimilarity(src, dst, bestA, bestB, bestTx, bestTy, thr2,
                out double ra, out double rb, out double rtx, out double rty))
        {
            bestA = ra; bestB = rb; bestTx = rtx; bestTy = rty;
        }
        int finalInliers = CountSimilarityInliers(src, dst, bestA, bestB, bestTx, bestTy, thr2);

        double logScale = 0.5 * Math.Log(bestA * bestA + bestB * bestB);
        double angle = Math.Atan2(bestB, bestA);
        return new SimilarityFit(bestTx, bestTy, logScale, angle, finalInliers, (double)finalInliers / n, true);
    }

    /// <summary>Fits a homography mapping <paramref name="src"/> onto <paramref name="dst"/> (normalised DLT + RANSAC).</summary>
    public static HomographyFit FitHomography(
        ReadOnlySpan<Vector2> src, ReadOnlySpan<Vector2> dst,
        ref XorShiftRng rng, double threshold, int iterations)
    {
        int n = src.Length;
        if (n < HomographySampleSize)
            return HomographyFit.Failed;

        double thr2 = threshold * threshold;
        Span<int> sample = stackalloc int[HomographySampleSize];

        Homography best = Homography.Identity;
        int bestInliers = -1;

        for (int it = 0; it < iterations; it++)
        {
            PickSample(ref rng, n, sample);
            if (!SolveHomography(src, dst, sample, HomographySampleSize, out Homography h))
                continue;

            int inliers = CountHomographyInliers(src, dst, h, thr2);
            if (inliers > bestInliers)
            {
                bestInliers = inliers;
                best = h;
            }
        }

        if (bestInliers < HomographySampleSize)
            return HomographyFit.Failed;

        // Refit over all inliers. Bounded stackalloc (no heap) for realistic feature counts; for an
        // extreme correspondence count we simply skip the polish and keep the RANSAC estimate.
        if (n <= 8192)
        {
            Span<int> inlierIdx = stackalloc int[n];
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                var (mx, my) = best.Map(src[i].X, src[i].Y);
                double ex = mx - dst[i].X, ey = my - dst[i].Y;
                if (ex * ex + ey * ey <= thr2)
                    inlierIdx[m++] = i;
            }
            if (m >= HomographySampleSize && SolveHomography(src, dst, inlierIdx[..m], m, out Homography refined))
            {
                int refinedInliers = CountHomographyInliers(src, dst, refined, thr2);
                if (refinedInliers >= bestInliers)
                {
                    best = refined;
                    bestInliers = refinedInliers;
                }
            }
        }

        return new HomographyFit(best, bestInliers, (double)bestInliers / n, true);
    }

    // ---- similarity helpers -------------------------------------------------

    private static bool SolveSimilarityMinimal(
        ReadOnlySpan<Vector2> src, ReadOnlySpan<Vector2> dst, ReadOnlySpan<int> sample,
        out double a, out double b, out double tx, out double ty)
    {
        int i0 = sample[0], i1 = sample[1];
        double x1 = src[i0].X, y1 = src[i0].Y, x2 = src[i1].X, y2 = src[i1].Y;
        double u1 = dst[i0].X, v1 = dst[i0].Y, u2 = dst[i1].X, v2 = dst[i1].Y;

        double dx = x2 - x1, dy = y2 - y1;
        double denom = dx * dx + dy * dy;
        if (denom < 1e-9)
        {
            a = b = tx = ty = 0;
            return false;
        }
        double dxp = u2 - u1, dyp = v2 - v1;
        a = (dx * dxp + dy * dyp) / denom;
        b = (dx * dyp - dy * dxp) / denom;
        tx = u1 - (a * x1 - b * y1);
        ty = v1 - (b * x1 + a * y1);
        return true;
    }

    private static int CountSimilarityInliers(
        ReadOnlySpan<Vector2> src, ReadOnlySpan<Vector2> dst,
        double a, double b, double tx, double ty, double thr2)
    {
        int count = 0;
        for (int i = 0; i < src.Length; i++)
        {
            double px = a * src[i].X - b * src[i].Y + tx;
            double py = b * src[i].X + a * src[i].Y + ty;
            double ex = px - dst[i].X, ey = py - dst[i].Y;
            if (ex * ex + ey * ey <= thr2)
                count++;
        }
        return count;
    }

    private static bool RefitSimilarity(
        ReadOnlySpan<Vector2> src, ReadOnlySpan<Vector2> dst,
        double a0, double b0, double tx0, double ty0, double thr2,
        out double a, out double b, out double tx, out double ty)
    {
        // Accumulate centred sums over the current inliers.
        double sx = 0, sy = 0, sxp = 0, syp = 0;
        int nIn = 0;
        for (int i = 0; i < src.Length; i++)
        {
            double px = a0 * src[i].X - b0 * src[i].Y + tx0;
            double py = b0 * src[i].X + a0 * src[i].Y + ty0;
            double ex = px - dst[i].X, ey = py - dst[i].Y;
            if (ex * ex + ey * ey > thr2)
                continue;
            sx += src[i].X; sy += src[i].Y; sxp += dst[i].X; syp += dst[i].Y;
            nIn++;
        }
        if (nIn < SimilaritySampleSize)
        {
            a = a0; b = b0; tx = tx0; ty = ty0;
            return false;
        }
        double mx = sx / nIn, my = sy / nIn, mxp = sxp / nIn, myp = syp / nIn;

        double sc = 0, ss = 0, sd = 0;
        for (int i = 0; i < src.Length; i++)
        {
            double px = a0 * src[i].X - b0 * src[i].Y + tx0;
            double py = b0 * src[i].X + a0 * src[i].Y + ty0;
            double ex = px - dst[i].X, ey = py - dst[i].Y;
            if (ex * ex + ey * ey > thr2)
                continue;
            double ux = src[i].X - mx, uy = src[i].Y - my;
            double vx = dst[i].X - mxp, vy = dst[i].Y - myp;
            sc += ux * vx + uy * vy;
            ss += ux * vy - uy * vx;
            sd += ux * ux + uy * uy;
        }
        if (sd < 1e-9)
        {
            a = a0; b = b0; tx = tx0; ty = ty0;
            return false;
        }
        a = sc / sd;
        b = ss / sd;
        tx = mxp - (a * mx - b * my);
        ty = myp - (b * mx + a * my);
        return true;
    }

    // ---- homography helpers -------------------------------------------------

    private static bool SolveHomography(
        ReadOnlySpan<Vector2> src, ReadOnlySpan<Vector2> dst, ReadOnlySpan<int> idx, int count,
        out Homography h)
    {
        h = Homography.Identity;

        // Hartley normalisation of both point sets (centre, scale mean distance to sqrt(2)).
        if (!NormTransform(src, idx, count, out double ssc, out double scx, out double scy) ||
            !NormTransform(dst, idx, count, out double dsc, out double dcx, out double dcy))
            return false;

        // Accumulate normal equations A^T A x = A^T c for the 8 unknowns (h33 fixed = 1).
        Span<double> ata = stackalloc double[64];
        Span<double> atc = stackalloc double[8];
        ata.Clear();
        atc.Clear();
        Span<double> row = stackalloc double[8];

        for (int j = 0; j < count; j++)
        {
            int i = idx[j];
            double x = ssc * (src[i].X - scx), y = ssc * (src[i].Y - scy);
            double u = dsc * (dst[i].X - dcx), v = dsc * (dst[i].Y - dcy);

            // Row for u: [x y 1 0 0 0 -x*u -y*u] = u
            row[0] = x; row[1] = y; row[2] = 1; row[3] = 0; row[4] = 0; row[5] = 0; row[6] = -x * u; row[7] = -y * u;
            Accumulate(ata, atc, row, u);
            // Row for v: [0 0 0 x y 1 -x*v -y*v] = v
            row[0] = 0; row[1] = 0; row[2] = 0; row[3] = x; row[4] = y; row[5] = 1; row[6] = -x * v; row[7] = -y * v;
            Accumulate(ata, atc, row, v);
        }

        Span<double> sol = stackalloc double[8];
        if (!SolveLinear(ata, atc, 8, sol))
            return false;

        // Normalised homography Hn (m22 = 1), then denormalise: H = Tdst^-1 · Hn · Tsrc.
        // Tsrc = [[ssc,0,-ssc*scx],[0,ssc,-ssc*scy],[0,0,1]]
        // Tdst^-1 = [[1/dsc,0,dcx],[0,1/dsc,dcy],[0,0,1]]
        // (Plain stackalloc + assignment, not a collection initializer, so Debug builds don't spill a
        // temporary array onto the managed heap on this hot path.)
        Span<double> hn = stackalloc double[9];
        hn[0] = sol[0]; hn[1] = sol[1]; hn[2] = sol[2];
        hn[3] = sol[3]; hn[4] = sol[4]; hn[5] = sol[5];
        hn[6] = sol[6]; hn[7] = sol[7]; hn[8] = 1.0;

        Span<double> ts = stackalloc double[9];
        ts[0] = ssc; ts[1] = 0; ts[2] = -ssc * scx;
        ts[3] = 0; ts[4] = ssc; ts[5] = -ssc * scy;
        ts[6] = 0; ts[7] = 0; ts[8] = 1;

        Span<double> tdInv = stackalloc double[9];
        tdInv[0] = 1.0 / dsc; tdInv[1] = 0; tdInv[2] = dcx;
        tdInv[3] = 0; tdInv[4] = 1.0 / dsc; tdInv[5] = dcy;
        tdInv[6] = 0; tdInv[7] = 0; tdInv[8] = 1;

        // M = Hn · Tsrc
        Span<double> m = stackalloc double[9];
        Mul3x3(hn, ts, m);
        Span<double> full = stackalloc double[9];
        Mul3x3(tdInv, m, full);

        double w = full[8];
        if (Math.Abs(w) < 1e-12)
            return false;
        double inv = 1.0 / w;
        h = new Homography(
            full[0] * inv, full[1] * inv, full[2] * inv,
            full[3] * inv, full[4] * inv, full[5] * inv,
            full[6] * inv, full[7] * inv);
        return true;
    }

    private static bool NormTransform(ReadOnlySpan<Vector2> pts, ReadOnlySpan<int> idx, int count,
        out double scale, out double cx, out double cy)
    {
        double sx = 0, sy = 0;
        for (int j = 0; j < count; j++) { sx += pts[idx[j]].X; sy += pts[idx[j]].Y; }
        cx = sx / count;
        cy = sy / count;
        double meanDist = 0;
        for (int j = 0; j < count; j++)
        {
            double dx = pts[idx[j]].X - cx, dy = pts[idx[j]].Y - cy;
            meanDist += Math.Sqrt(dx * dx + dy * dy);
        }
        meanDist /= count;
        if (meanDist < 1e-9)
        {
            scale = 0;
            return false;
        }
        scale = Math.Sqrt(2.0) / meanDist;
        return true;
    }

    private static void Accumulate(Span<double> ata, Span<double> atc, ReadOnlySpan<double> row, double target)
    {
        for (int r = 0; r < 8; r++)
        {
            atc[r] += row[r] * target;
            int baseR = r * 8;
            for (int c = 0; c < 8; c++)
                ata[baseR + c] += row[r] * row[c];
        }
    }

    private static int CountHomographyInliers(
        ReadOnlySpan<Vector2> src, ReadOnlySpan<Vector2> dst, Homography h, double thr2)
    {
        int count = 0;
        for (int i = 0; i < src.Length; i++)
        {
            var (mx, my) = h.Map(src[i].X, src[i].Y);
            double ex = mx - dst[i].X, ey = my - dst[i].Y;
            if (ex * ex + ey * ey <= thr2)
                count++;
        }
        return count;
    }

    // ---- linear algebra -----------------------------------------------------

    /// <summary>Gaussian elimination with partial pivoting for an n×n system (row-major A). Returns false if singular.</summary>
    private static bool SolveLinear(Span<double> a, Span<double> b, int n, Span<double> x)
    {
        for (int col = 0; col < n; col++)
        {
            // Partial pivot.
            int pivot = col;
            double best = Math.Abs(a[col * n + col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(a[r * n + col]);
                if (v > best) { best = v; pivot = r; }
            }
            if (best < 1e-12)
                return false;
            if (pivot != col)
            {
                for (int c = 0; c < n; c++)
                    (a[col * n + c], a[pivot * n + c]) = (a[pivot * n + c], a[col * n + c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            // Eliminate.
            double diag = a[col * n + col];
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r * n + col] / diag;
                if (f == 0) continue;
                for (int c = col; c < n; c++)
                    a[r * n + c] -= f * a[col * n + c];
                b[r] -= f * b[col];
            }
        }
        // Back-substitute.
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = b[r];
            for (int c = r + 1; c < n; c++)
                sum -= a[r * n + c] * x[c];
            x[r] = sum / a[r * n + r];
        }
        return true;
    }

    private static void Mul3x3(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> o)
    {
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                o[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
    }

    private static void PickSample(ref XorShiftRng rng, int n, Span<int> sample)
    {
        for (int i = 0; i < sample.Length; i++)
        {
            int candidate;
            bool duplicate;
            do
            {
                candidate = rng.NextInt(n);
                duplicate = false;
                for (int j = 0; j < i; j++)
                    if (sample[j] == candidate) { duplicate = true; break; }
            }
            while (duplicate);
            sample[i] = candidate;
        }
    }
}
