using Sprocket.Analysis.Features;
using Sprocket.Analysis.Motion;
using Sprocket.Core.Stabilization;
using Xunit;

namespace Sprocket.Analysis.Tests;

public class MotionEstimatorTests
{
    private const int Width = 320;
    private const int Height = 240;

    [Fact]
    public void Estimate_RecoversKnownSimilarity_ToSubPixelPrecision()
    {
        const double scale = 1.02;
        const double angleDeg = 1.0;
        const double angle = angleDeg * Math.PI / 180.0;
        const double tx = 3.5, ty = -2.25;

        GrayBuffer a = TestImages.Reference(Width, Height);
        GrayBuffer b = TestImages.WarpSimilarity(Width, Height, scale, angle, tx, ty);

        var est = new MotionEstimator();
        FrameMotion m = est.Estimate(a.View, b.View);

        Assert.True(m.FeatureCount > 40, $"only {m.FeatureCount} features tracked");
        Assert.True(m.Confidence > 0.8, $"low confidence {m.Confidence:F3}");

        // Translations come back normalised by width — convert to pixels to compare.
        Assert.True(Math.Abs(m.Tx * Width - tx) < 0.1, $"tx off by {Math.Abs(m.Tx * Width - tx):F4}px");
        Assert.True(Math.Abs(m.Ty * Width - ty) < 0.1, $"ty off by {Math.Abs(m.Ty * Width - ty):F4}px");

        double recoveredScale = Math.Exp(m.LogScale);
        Assert.True(Math.Abs(recoveredScale - scale) / scale < 0.001, $"scale {recoveredScale:F5} vs {scale}");

        double angleErrDeg = Math.Abs(m.Angle - angle) * 180.0 / Math.PI;
        Assert.True(angleErrDeg < 0.05, $"angle off by {angleErrDeg:F4}°");
    }

    [Fact]
    public void Estimate_PureTranslation_HasNegligibleScaleAndRotation()
    {
        GrayBuffer a = TestImages.Reference(Width, Height);
        GrayBuffer b = TestImages.WarpSimilarity(Width, Height, scale: 1.0, angleRad: 0.0, tx: 5.0, ty: 4.0);

        var est = new MotionEstimator();
        FrameMotion m = est.Estimate(a.View, b.View);

        Assert.True(Math.Abs(m.Tx * Width - 5.0) < 0.1);
        Assert.True(Math.Abs(m.Ty * Width - 4.0) < 0.1);
        Assert.True(Math.Abs(Math.Exp(m.LogScale) - 1.0) < 0.001);
        Assert.True(Math.Abs(m.Angle) < 0.001);
    }

    [Fact]
    public void Estimate_RecoversKnownHomography()
    {
        double[] h =
        [
            1.008, 0.006, 2.5,
            -0.005, 1.004, -2.0,
            0.00004, -0.00003, 1.0,
        ];
        double[] hInv = TestImages.Invert3x3(h);

        GrayBuffer a = TestImages.Reference(Width, Height);
        GrayBuffer b = TestImages.WarpHomography(Width, Height, hInv);

        var est = new MotionEstimator();
        FrameMotion m = est.Estimate(a.View, b.View);

        // Compare the recovered (normalised) homography against ground truth in pixel space.
        double rms = 0;
        int n = 0;
        for (int gx = 40; gx <= 280; gx += 40)
            for (int gy = 40; gy <= 200; gy += 40)
            {
                double w = h[6] * gx + h[7] * gy + 1.0;
                double ex = (h[0] * gx + h[1] * gy + h[2]) / w;
                double ey = (h[3] * gx + h[4] * gy + h[5]) / w;
                var (mx, my) = m.Homography.Map(gx / (double)Width, gy / (double)Width);
                double px = mx * Width, py = my * Width;
                rms += (px - ex) * (px - ex) + (py - ey) * (py - ey);
                n++;
            }
        rms = Math.Sqrt(rms / n);
        Assert.True(rms < 0.5, $"homography reprojection RMS {rms:F4}px too high");
    }

    [Fact]
    public void Estimate_UnrelatedFrames_ForwardBackwardFilterDropsMostTracks()
    {
        GrayBuffer a = TestImages.Reference(Width, Height);
        GrayBuffer b = TestImages.Noise(Width, Height, seed: 424242);

        // Track directly so we can measure the survivor fraction.
        var pyrA = new ImagePyramid();
        var pyrB = new ImagePyramid();
        pyrA.Build(a.View);
        pyrB.Build(b.View);

        var detector = new CornerDetector();
        var corners = new System.Numerics.Vector2[8 * 6 * 7];
        int count = detector.Detect(pyrA.Level(0), 8, 6, 7, corners);
        Assert.True(count > 40);

        var tracked = new System.Numerics.Vector2[count];
        var status = new bool[count];
        new LucasKanadeTracker().Track(pyrA, pyrB, corners.AsSpan(0, count), tracked, status);

        int survivors = 0;
        foreach (bool ok in status)
            if (ok) survivors++;

        Assert.True(survivors < count * 0.1, $"{survivors}/{count} tracks survived onto noise");
    }

    [Fact]
    public void Estimate_IsDeterministic()
    {
        GrayBuffer a = TestImages.Reference(Width, Height);
        GrayBuffer b = TestImages.WarpSimilarity(Width, Height, 1.015, 0.012, 2.0, -1.0);

        FrameMotion m1 = new MotionEstimator().Estimate(a.View, b.View);
        FrameMotion m2 = new MotionEstimator().Estimate(a.View, b.View);

        Assert.Equal(m1, m2);   // bit-identical record equality
    }

    [Fact]
    public void Estimate_SteadyState_AllocatesApproximatelyNothing()
    {
        GrayBuffer a = TestImages.Reference(Width, Height);
        GrayBuffer b = TestImages.WarpSimilarity(Width, Height, 1.01, 0.01, 2.0, -1.5);

        var est = new MotionEstimator();
        // Warm up: first calls JIT and allocate the reusable buffers.
        est.Estimate(a.View, b.View);
        est.Estimate(a.View, b.View);

        long before = GC.GetAllocatedBytesForCurrentThread();
        est.Estimate(a.View, b.View);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4096, $"steady-state estimate allocated {allocated} managed bytes");
    }
}
