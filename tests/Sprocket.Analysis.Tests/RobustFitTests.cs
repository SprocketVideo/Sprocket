using System.Numerics;
using Sprocket.Analysis.Features;
using Xunit;

namespace Sprocket.Analysis.Tests;

public class RobustFitTests
{
    [Fact]
    public void FitSimilarity_RecoversTransform_And_RejectsOutliers()
    {
        const double scale = 1.03, angle = 0.05, tx = 12.0, ty = -7.0;
        double a = scale * Math.Cos(angle), b = scale * Math.Sin(angle);

        // 70 inliers on a grid + 30 gross outliers.
        var src = new Vector2[100];
        var dst = new Vector2[100];
        var rng = new XorShiftRng(99);
        for (int i = 0; i < 70; i++)
        {
            float x = rng.NextInt(400), y = rng.NextInt(300);
            src[i] = new Vector2(x, y);
            dst[i] = new Vector2((float)(a * x - b * y + tx), (float)(b * x + a * y + ty));
        }
        for (int i = 70; i < 100; i++)
        {
            src[i] = new Vector2(rng.NextInt(400), rng.NextInt(300));
            dst[i] = new Vector2(rng.NextInt(400), rng.NextInt(300));   // random ⇒ outlier
        }

        var fitRng = new XorShiftRng(1);
        SimilarityFit fit = RobustFit.FitSimilarity(src, dst, ref fitRng, threshold: 1.5, iterations: 300);

        Assert.True(fit.Ok);
        Assert.Equal(70, fit.Inliers);                       // exactly the planted inliers
        Assert.Equal(0.70, fit.InlierRatio, 3);
        Assert.Equal(scale, Math.Exp(fit.LogScale), 4);
        Assert.Equal(angle, fit.Angle, 4);
        Assert.Equal(tx, fit.Tx, 3);
        Assert.Equal(ty, fit.Ty, 3);
    }

    [Fact]
    public void FitHomography_RecoversProjectiveTransform()
    {
        // A mild projective transform (row-major 3x3, m22 = 1).
        double[] h =
        [
            1.010, 0.010, 6.0,
            -0.008, 1.006, -4.0,
            0.00006, -0.00004, 1.0,
        ];

        var src = new Vector2[60];
        var dst = new Vector2[60];
        var rng = new XorShiftRng(7);
        for (int i = 0; i < 60; i++)
        {
            float x = rng.NextInt(400), y = rng.NextInt(300);
            src[i] = new Vector2(x, y);
            double w = h[6] * x + h[7] * y + 1.0;
            dst[i] = new Vector2((float)((h[0] * x + h[1] * y + h[2]) / w), (float)((h[3] * x + h[4] * y + h[5]) / w));
        }

        var fitRng = new XorShiftRng(3);
        HomographyFit fit = RobustFit.FitHomography(src, dst, ref fitRng, threshold: 1.0, iterations: 400);

        Assert.True(fit.Ok);
        Assert.True(fit.Inliers >= 58, $"only {fit.Inliers} inliers");

        // Reprojection error on independent points must be sub-pixel.
        double rms = 0;
        int n = 0;
        for (int gx = 40; gx <= 360; gx += 40)
            for (int gy = 40; gy <= 260; gy += 40)
            {
                double w = h[6] * gx + h[7] * gy + 1.0;
                double ex = (h[0] * gx + h[1] * gy + h[2]) / w;
                double ey = (h[3] * gx + h[4] * gy + h[5]) / w;
                var (mx, my) = fit.H.Map(gx, gy);
                rms += (mx - ex) * (mx - ex) + (my - ey) * (my - ey);
                n++;
            }
        rms = Math.Sqrt(rms / n);
        Assert.True(rms < 0.1, $"reprojection RMS {rms:F4}px too high");
    }

    [Fact]
    public void FitSimilarity_TooFewPoints_Fails()
    {
        var one = new Vector2[1];
        var rng = new XorShiftRng(1);
        SimilarityFit fit = RobustFit.FitSimilarity(one, one, ref rng, 1.5, 50);
        Assert.False(fit.Ok);
    }
}
