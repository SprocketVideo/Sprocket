namespace Sprocket.Core.Stabilization;

/// <summary>
/// Turns a <see cref="MotionTrack"/> + <see cref="StabilizationSettings"/> into a
/// <see cref="StabilizationSolution"/> (plan/features/stabilization.md): integrate the inter-frame motion into
/// a camera path, smooth it per channel (uniform / adaptive / locked), blend by Strength, then choose the
/// framing zoom so the corrected frame covers the output. Pure and deterministic — no RNG, no IO — so the
/// same inputs always produce the same warp (the golden-frame requirement, ARCHITECTURE.md §5). This is the
/// "solve" tier: cheap and re-run on every parameter tweak without touching the (expensive) analysis.
/// </summary>
public static class StabilizationSolver
{
    /// <summary>How far the adaptive mode is allowed to shrink its smoothing window where it detects a
    /// deliberate camera move (1 = down to nothing). Below 1 so a fast pan is still lightly smoothed.</summary>
    private const double AdaptiveIntentShrink = 0.9;

    /// <summary>
    /// Solves stabilization for <paramref name="track"/> under <paramref name="settings"/> at the given frame
    /// size (the frame size sets the aspect used for border coverage / zoom). Returns an all-identity solution
    /// for an empty track.
    /// </summary>
    public static StabilizationSolution Solve(MotionTrack track, StabilizationSettings settings, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);

        int n = track.FrameCount;
        double aspY = frameWidth > 0 && frameHeight > 0 ? (double)frameHeight / frameWidth : 9.0 / 16.0;

        if (n == 0)
            return new StabilizationSolution([], [], [], [], 1.0, aspY);

        // 1. Integrate the inter-frame motion into a cumulative camera path (per channel).
        var rawTx = new double[n];
        var rawTy = new double[n];
        var rawLog = new double[n];
        var rawAngle = new double[n];
        for (int i = 1; i < n; i++)
        {
            FrameMotion m = track.Motions[i];
            rawTx[i] = rawTx[i - 1] + m.Tx;
            rawTy[i] = rawTy[i - 1] + m.Ty;
            rawLog[i] = rawLog[i - 1] + m.LogScale;
            rawAngle[i] = rawAngle[i - 1] + m.Angle;
        }

        double fps = track.FrameRate.Den > 0 && track.FrameRate.Num > 0
            ? (double)track.FrameRate.Num / track.FrameRate.Den
            : 0.0;
        double baseRadius = Math.Max(0.0, settings.Smoothness) * fps;

        bool translationOnly = settings.Method == StabilizationMethod.Translation;

        // 2. Per-channel smoothed (target) path.
        double[] targTx = SmoothChannel(rawTx, baseRadius * settings.PositionSmooth, settings.Mode, fps);
        double[] targTy = SmoothChannel(rawTy, baseRadius * settings.PositionSmooth, settings.Mode, fps);

        double[] targAngle;
        if (translationOnly)
            targAngle = (double[])rawAngle.Clone();
        else if (settings.LockRotation)
            targAngle = Constant(n, rawAngle[0]);
        else
            targAngle = SmoothChannel(rawAngle, baseRadius * settings.RotationSmooth, settings.Mode, fps);

        double[] targLog;
        if (translationOnly)
            targLog = (double[])rawLog.Clone();
        else
            targLog = settings.ScaleMode switch
            {
                ScaleMode.Preserve => (double[])rawLog.Clone(),
                ScaleMode.Lock => Constant(n, ScaleReference(rawLog, settings.ScaleLockRef)),
                _ => SmoothChannel(rawLog, baseRadius * settings.ScaleSmooth, settings.Mode, fps),
            };

        // 3. Strength blend: target = lerp(raw, smoothed, strength).
        double s = Math.Clamp(settings.Strength, 0.0, 1.0);
        for (int i = 0; i < n; i++)
        {
            targTx[i] = Lerp(rawTx[i], targTx[i], s);
            targTy[i] = Lerp(rawTy[i], targTy[i], s);
            targAngle[i] = Lerp(rawAngle[i], targAngle[i], s);
            targLog[i] = Lerp(rawLog[i], targLog[i], s);
        }

        // 4. Residual (what stabilization must undo) per channel.
        var resTx = new double[n];
        var resTy = new double[n];
        var resLog = new double[n];
        var resAngle = new double[n];
        for (int i = 0; i < n; i++)
        {
            resTx[i] = rawTx[i] - targTx[i];
            resTy[i] = rawTy[i] - targTy[i];
            resLog[i] = rawLog[i] - targLog[i];
            resAngle[i] = rawAngle[i] - targAngle[i];
        }

        // 5. Framing: choose a uniform zoom (and, if the crop budget binds, a residual damping λ) so the
        //    corrected frame covers the output.
        double lambda = 1.0;
        double zoom = 1.0;
        if (settings.Zoom)
        {
            double cap = 1.0 / Math.Clamp(settings.CroppingRatio, 0.5, 1.0);
            if (Covered(resTx, resTy, resLog, resAngle, 1.0, cap, aspY, n))
            {
                zoom = MinimalZoom(resTx, resTy, resLog, resAngle, cap, aspY, n);
            }
            else
            {
                zoom = cap;
                lambda = MaxLambda(resTx, resTy, resLog, resAngle, cap, aspY, n);
            }
        }

        // 6. Build the final paths and matrices with λ applied.
        var smoothed = new CameraPathSample[n];
        var raw = new CameraPathSample[n];
        var matrices = new double[n][];
        for (int i = 0; i < n; i++)
        {
            double dTx = resTx[i] * lambda;
            double dTy = resTy[i] * lambda;
            double dLog = resLog[i] * lambda;
            double dAngle = resAngle[i] * lambda;

            raw[i] = new CameraPathSample(rawTx[i], rawTy[i], rawLog[i], rawAngle[i]);
            smoothed[i] = new CameraPathSample(rawTx[i] - dTx, rawTy[i] - dTy, rawLog[i] - dLog, rawAngle[i] - dAngle);
            matrices[i] = BuildMatrix(dTx, dTy, dLog, dAngle, zoom);
        }

        return new StabilizationSolution(track.FramePts, raw, smoothed, matrices, zoom, aspY);
    }

    /// <summary>The output→source matrix for a residual similarity at the given zoom (centred, width-normalised
    /// isotropic coordinates). The zoom scales output coordinates toward the centre (linear part ÷ zoom); the
    /// residual translation is unchanged by the zoom since it is applied about the centre.</summary>
    private static double[] BuildMatrix(double dTx, double dTy, double dLog, double dAngle, double zoom)
    {
        double sc = Math.Exp(dLog);
        double cos = Math.Cos(dAngle);
        double sin = Math.Sin(dAngle);
        double invZoom = 1.0 / zoom;
        double a = sc * cos * invZoom;
        double b = -sc * sin * invZoom;
        double d = sc * sin * invZoom;
        double e = sc * cos * invZoom;
        return [a, b, dTx, d, e, dTy, 0, 0, 1];
    }

    /// <summary>Whether every frame's output rectangle, warped by residual×<paramref name="scale"/> at
    /// <paramref name="zoom"/>, stays inside the source rectangle (so no border shows).</summary>
    private static bool Covered(
        double[] resTx, double[] resTy, double[] resLog, double[] resAngle,
        double scale, double zoom, double aspY, int n)
    {
        const double eps = 1e-9;
        double hx = 0.5, hy = 0.5 * aspY;
        Span<(double X, double Y)> corners =
        [
            (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy),
        ];
        for (int i = 0; i < n; i++)
        {
            double[] m = BuildMatrix(resTx[i] * scale, resTy[i] * scale, resLog[i] * scale, resAngle[i] * scale, zoom);
            foreach ((double cx, double cy) in corners)
            {
                double sx = m[0] * cx + m[1] * cy + m[2];
                double sy = m[3] * cx + m[4] * cy + m[5];
                if (Math.Abs(sx) > hx + eps || Math.Abs(sy) > hy + eps)
                    return false;
            }
        }
        return true;
    }

    /// <summary>The minimal zoom in [1, <paramref name="cap"/>] that covers every frame (binary search; the
    /// caller has already confirmed the cap covers).</summary>
    private static double MinimalZoom(
        double[] resTx, double[] resTy, double[] resLog, double[] resAngle, double cap, double aspY, int n)
    {
        double lo = 1.0, hi = cap;
        for (int iter = 0; iter < 40; iter++)
        {
            double mid = 0.5 * (lo + hi);
            if (Covered(resTx, resTy, resLog, resAngle, 1.0, mid, aspY, n))
                hi = mid;
            else
                lo = mid;
        }
        return hi;
    }

    /// <summary>The largest residual-damping λ in [0, 1] that covers every frame at the capped zoom (binary
    /// search). λ = 0 (no stabilization) always covers, so a value exists.</summary>
    private static double MaxLambda(
        double[] resTx, double[] resTy, double[] resLog, double[] resAngle, double cap, double aspY, int n)
    {
        double lo = 0.0, hi = 1.0;
        for (int iter = 0; iter < 40; iter++)
        {
            double mid = 0.5 * (lo + hi);
            if (Covered(resTx, resTy, resLog, resAngle, mid, cap, aspY, n))
                lo = mid;
            else
                hi = mid;
        }
        return lo;
    }

    private static double[] SmoothChannel(double[] x, double radius, StabilizationMode mode, double fps) => mode switch
    {
        StabilizationMode.CameraLock => Constant(x.Length, Mean(x)),
        StabilizationMode.SmoothCamera => AdaptiveSmooth(x, radius, fps),
        _ => GaussianSmooth(x, radius),
    };

    /// <summary>Symmetric Gaussian low-pass with σ = <paramref name="radius"/>, truncated and renormalised at
    /// the ends. A non-positive radius is a pass-through (returns a copy).</summary>
    private static double[] GaussianSmooth(double[] x, double radius)
    {
        int n = x.Length;
        if (radius <= 0.0 || n == 0)
            return (double[])x.Clone();

        double sigma = radius;
        int r = (int)Math.Ceiling(sigma * 3);
        double twoSigma2 = 2.0 * sigma * sigma;
        var outp = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0, wsum = 0;
            int from = Math.Max(0, i - r), to = Math.Min(n - 1, i + r);
            for (int j = from; j <= to; j++)
            {
                int k = j - i;
                double w = Math.Exp(-(k * k) / twoSigma2);
                sum += w * x[j];
                wsum += w;
            }
            outp[i] = sum / wsum;
        }
        return outp;
    }

    /// <summary>
    /// Intent-preserving adaptive smoothing (FCP InertiaCam-like): where a sustained camera velocity indicates
    /// a deliberate pan/zoom, the Gaussian window is shrunk so the path follows the intended move instead of
    /// lagging it; where the motion is jitter around a still camera, the full window smooths it away.
    /// </summary>
    private static double[] AdaptiveSmooth(double[] x, double radius, double fps)
    {
        int n = x.Length;
        if (radius <= 0.0 || n < 3)
            return GaussianSmooth(x, radius);

        // Per-sample velocity.
        var vel = new double[n];
        for (int i = 1; i < n; i++)
            vel[i] = x[i] - x[i - 1];
        vel[0] = vel[1];

        int wide = Math.Max(1, (int)Math.Round(fps > 0 ? fps : 8));

        // Jitter scale = typical deviation of velocity from its local (wide-window) median — small on a clean
        // ramp (deliberate motion), large under jitter. Robust to the sustained slope itself.
        var localMedV = new double[n];
        var dev = new double[n];
        for (int i = 0; i < n; i++)
            localMedV[i] = MedianInWindow(vel, i, wide);
        for (int i = 0; i < n; i++)
            dev[i] = Math.Abs(vel[i] - localMedV[i]);
        double jitter = Median(dev) + 1e-9;

        var outp = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sustained = Math.Abs(localMedV[i]);
            double intent = sustained / (sustained + jitter);      // →1 for deliberate motion, →0 for jitter
            double sigma = radius * (1.0 - AdaptiveIntentShrink * intent);
            if (sigma < 1e-3)
            {
                outp[i] = x[i];
                continue;
            }
            int r = (int)Math.Ceiling(sigma * 3);
            double twoSigma2 = 2.0 * sigma * sigma;
            double sum = 0, wsum = 0;
            int from = Math.Max(0, i - r), to = Math.Min(n - 1, i + r);
            for (int j = from; j <= to; j++)
            {
                int k = j - i;
                double w = Math.Exp(-(k * k) / twoSigma2);
                sum += w * x[j];
                wsum += w;
            }
            outp[i] = sum / wsum;
        }
        return outp;
    }

    private static double ScaleReference(double[] rawLog, ScaleLockReference reference) => reference switch
    {
        ScaleLockReference.Tightest => Max(rawLog),   // most zoomed-in = largest scale
        ScaleLockReference.Widest => Min(rawLog),
        ScaleLockReference.FirstFrame => rawLog[0],
        _ => Median(rawLog),
    };

    private static double[] Constant(int n, double value)
    {
        var a = new double[n];
        Array.Fill(a, value);
        return a;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Mean(double[] x)
    {
        if (x.Length == 0)
            return 0;
        double sum = 0;
        foreach (double v in x)
            sum += v;
        return sum / x.Length;
    }

    private static double Max(double[] x)
    {
        double m = x[0];
        foreach (double v in x)
            if (v > m)
                m = v;
        return m;
    }

    private static double Min(double[] x)
    {
        double m = x[0];
        foreach (double v in x)
            if (v < m)
                m = v;
        return m;
    }

    private static double Median(double[] x)
    {
        if (x.Length == 0)
            return 0;
        var copy = (double[])x.Clone();
        Array.Sort(copy);
        int mid = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[mid] : 0.5 * (copy[mid - 1] + copy[mid]);
    }

    private static double MedianInWindow(double[] x, int center, int radius)
    {
        int from = Math.Max(0, center - radius), to = Math.Min(x.Length - 1, center + radius);
        int count = to - from + 1;
        Span<double> window = count <= 256 ? stackalloc double[count] : new double[count];
        for (int j = 0; j < count; j++)
            window[j] = x[from + j];
        window.Sort();
        int mid = count / 2;
        return count % 2 == 1 ? window[mid] : 0.5 * (window[mid - 1] + window[mid]);
    }
}
