using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Phase 2 of Stabilization (the Core model, solver, and descriptor): the catalog registration, the
/// <see cref="MotionTrack"/> binary round-trip, the deterministic <see cref="StabilizationSolver"/>, the
/// <see cref="AnalysisKey"/> bucketing, and the source-frame context the render graph now carries on each
/// <see cref="ResolvedEffect"/> so a source-referenced stage can find its analysis.
/// </summary>
public class StabilizationTests
{
    // ── Catalog descriptor ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stabilization_Is_Registered_As_A_Video_Effect_With_A_Unique_Short_Code()
    {
        EffectDescriptor? stab = EffectCatalog.Find(EffectTypeIds.Stabilization);
        Assert.NotNull(stab);
        Assert.Equal("Stabilization", stab!.DisplayName);
        Assert.Equal(EffectCategory.Video, stab.Category);
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.Stabilization)); // a pipeline stage, not the mixer
        Assert.Equal("ST", stab.ShortCode);

        // Short codes are the tag prefix and must stay unique across the built-ins.
        List<string> codes = EffectCatalog.BuiltIns.Where(d => d.ShortCode is not null).Select(d => d.ShortCode!).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void Stabilization_Exposes_Its_Parameters_In_Inspector_Order()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.Stabilization)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.StabMode, EffectParamNames.Smoothness, EffectParamNames.Strength,
                EffectParamNames.StabMethod, EffectParamNames.PositionSmooth, EffectParamNames.RotationSmooth,
                EffectParamNames.ScaleMode, EffectParamNames.ScaleSmooth, EffectParamNames.ScaleLockRef,
                EffectParamNames.LockRotation, EffectParamNames.Zoom, EffectParamNames.CroppingRatio,
                EffectParamNames.DetailedAnalysis, EffectParamNames.ShowTrackPoints, EffectParamNames.HideBanner,
            },
            names);
    }

    [Fact]
    public void Stabilization_Dropdowns_Carry_Their_Choice_Lists()
    {
        EffectDescriptor stab = EffectCatalog.Find(EffectTypeIds.Stabilization)!;
        EffectParameterDescriptor P(string n) => stab.Parameters.Single(p => p.Name == n);

        Assert.Equal(ParameterKind.Dropdown, P(EffectParamNames.StabMode).Kind);
        Assert.Equal(StabilizationSettings.ModeChoices, P(EffectParamNames.StabMode).Choices);
        Assert.Equal(StabilizationSettings.MethodChoices, P(EffectParamNames.StabMethod).Choices);
        Assert.Equal(StabilizationSettings.ScaleModeChoices, P(EffectParamNames.ScaleMode).Choices);
        Assert.Equal(StabilizationSettings.ScaleLockRefChoices, P(EffectParamNames.ScaleLockRef).Choices);

        // The toggles are declared as toggles (0/1), not continuous sliders.
        foreach (string t in new[]
        {
            EffectParamNames.LockRotation, EffectParamNames.Zoom, EffectParamNames.DetailedAnalysis,
            EffectParamNames.ShowTrackPoints, EffectParamNames.HideBanner,
        })
            Assert.Equal(ParameterKind.Toggle, P(t).Kind);
    }

    [Fact]
    public void Stabilization_Descriptor_Defaults_Round_Trip_To_The_Settings_Default()
    {
        // A fresh instance's parameters, read back through FromResolvedEffect, must reproduce the canonical
        // Default settings — so the catalog defaults and the solver's defaults can never silently drift apart.
        EffectDescriptor stab = EffectCatalog.Find(EffectTypeIds.Stabilization)!;
        var values = stab.Parameters.ToDictionary(p => p.Name, p => p.Default);
        var resolved = new ResolvedEffect(EffectTypeIds.Stabilization, values);
        Assert.Equal(StabilizationSettings.Default, StabilizationSettings.FromResolvedEffect(resolved));
    }

    [Fact]
    public void Stabilization_Ships_The_Phase2_Presets()
    {
        EffectDescriptor stab = EffectCatalog.Find(EffectTypeIds.Stabilization)!;
        Assert.Same(StabilizationPresets.All, stab.Presets);
        Assert.Equal(
            new[]
            {
                "Default", "Gentle", "Strong", "Camera Lock / Tripod", "Handheld Look",
                "Fix Focus Breathing Only", "Horizon Lock",
            },
            stab.Presets.Select(p => p.Name).ToArray());

        // Presets set only solve parameters, never the analysis/workflow toggles (those are the user's).
        foreach (EffectPreset p in stab.Presets)
        {
            Assert.DoesNotContain(EffectParamNames.DetailedAnalysis, p.Values.Keys);
            Assert.DoesNotContain(EffectParamNames.ShowTrackPoints, p.Values.Keys);
            Assert.DoesNotContain(EffectParamNames.HideBanner, p.Values.Keys);
        }
    }

    // ── MotionTrack round-trip ──────────────────────────────────────────────────────────────────────────

    private static MotionTrack SampleTrack(bool withPoints)
    {
        var motions = new FrameMotion[]
        {
            FrameMotion.Identity,
            new(0.01, -0.02, 0.03, 0.004, Homography.Identity, 0.9, 120),
            new(-0.015, 0.008, -0.01, -0.002, new Homography(1.01, 0.002, 0.01, -0.003, 0.99, -0.02, 1e-4, -2e-4), 0.7, 80),
        };
        var pts = new long[] { 0, 8000, 16000 };
        IReadOnlyList<IReadOnlyList<FeaturePoint>>? points = withPoints
            ? new IReadOnlyList<FeaturePoint>[]
            {
                new FeaturePoint[] { new(0.1f, 0.2f) },
                new FeaturePoint[] { new(0.3f, 0.4f), new(0.5f, 0.6f) },
                Array.Empty<FeaturePoint>(),
            }
            : null;
        return new MotionTrack("src-identity", withPoints, new Timecode(0), new Timecode(160000),
            new Rational(30, 1), pts, motions, points);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MotionTrack_Round_Trips_Through_The_Binary_Format(bool withPoints)
    {
        MotionTrack original = SampleTrack(withPoints);
        using var ms = new MemoryStream();
        original.Write(ms);
        ms.Position = 0;
        MotionTrack read = MotionTrack.Read(ms);

        Assert.Equal(original.SourceIdentity, read.SourceIdentity);
        Assert.Equal(original.DetailedAnalysis, read.DetailedAnalysis);
        Assert.Equal(original.RangeStart, read.RangeStart);
        Assert.Equal(original.RangeEnd, read.RangeEnd);
        Assert.Equal(original.FrameRate, read.FrameRate);
        Assert.Equal(original.FramePts, read.FramePts);
        Assert.Equal(original.Motions, read.Motions); // record-struct value equality over every channel + homography

        if (withPoints)
        {
            Assert.NotNull(read.Points);
            for (int i = 0; i < original.FrameCount; i++)
                Assert.Equal(original.Points![i], read.Points![i]);
        }
        else
        {
            Assert.Null(read.Points);
        }
    }

    [Fact]
    public void MotionTrack_Read_Rejects_Bad_Magic()
    {
        using var ms = new MemoryStream();
        ms.Write("XXXX"u8);
        ms.Write(BitConverter.GetBytes(MotionTrack.FormatVersion));
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MotionTrack.Read(ms));
    }

    [Fact]
    public void MotionTrack_Read_Rejects_An_Unsupported_Version()
    {
        using var ms = new MemoryStream();
        ms.Write(MotionTrack.Magic);
        ms.Write(BitConverter.GetBytes(MotionTrack.FormatVersion + 1));
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MotionTrack.Read(ms));
    }

    // ── Solver ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A track of length <paramref name="n"/> whose per-frame inter-frame motion is supplied by
    /// <paramref name="motion"/> (frame 0 is always identity). 30 fps, unit source pts.</summary>
    private static MotionTrack Track(int n, Func<int, FrameMotion> motion)
    {
        var motions = new FrameMotion[n];
        var pts = new long[n];
        for (int i = 0; i < n; i++)
        {
            motions[i] = i == 0 ? FrameMotion.Identity : motion(i);
            pts[i] = (long)i * Timecode.TicksPerSecond / 30;
        }
        long end = n > 0 ? pts[^1] : 0;
        return new MotionTrack("s", false, new Timecode(0), new Timecode(end), new Rational(30, 1), pts, motions);
    }

    private static double Variance(IEnumerable<double> xs)
    {
        double[] a = xs.ToArray();
        double mean = a.Average();
        return a.Sum(v => (v - mean) * (v - mean)) / a.Length;
    }

    private static StabilizationSettings With(StabilizationSettings s, StabilizationMode? mode = null,
        double? smoothness = null, double? strength = null, StabilizationMethod? method = null,
        double? positionSmooth = null, double? rotationSmooth = null, ScaleMode? scaleMode = null,
        bool? lockRotation = null, bool? zoom = null) =>
        s with
        {
            Mode = mode ?? s.Mode,
            Smoothness = smoothness ?? s.Smoothness,
            Strength = strength ?? s.Strength,
            Method = method ?? s.Method,
            PositionSmooth = positionSmooth ?? s.PositionSmooth,
            RotationSmooth = rotationSmooth ?? s.RotationSmooth,
            ScaleMode = scaleMode ?? s.ScaleMode,
            LockRotation = lockRotation ?? s.LockRotation,
            Zoom = zoom ?? s.Zoom,
        };

    [Fact]
    public void Smooth_Motion_Lowers_Jitter_Variance_Versus_The_Raw_Path()
    {
        // A still camera with alternating ±jitter in X. The uniform Gaussian low-pass must flatten it.
        MotionTrack track = Track(60, i => new FrameMotion(i % 2 == 0 ? 0.02 : -0.02, 0, 0, 0, Homography.Identity, 1, 50));
        var settings = With(StabilizationSettings.Default, mode: StabilizationMode.SmoothMotion, smoothness: 0.5,
            strength: 1.0, zoom: false);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);
        double rawVar = Variance(sol.RawPath.Select(p => p.Tx));
        double smoothVar = Variance(sol.SmoothedPath.Select(p => p.Tx));
        Assert.True(smoothVar < rawVar * 0.25, $"smoothed variance {smoothVar} not well below raw {rawVar}");
    }

    [Fact]
    public void Adaptive_Follows_A_Deliberate_Pan_With_Less_Lag_Than_The_Uniform_Low_Pass()
    {
        // A pure constant-velocity pan (deliberate motion, no jitter). Adaptive smoothing shrinks its window on
        // the detected move so the target path tracks the ramp; the uniform Gaussian lags it (worst at the ends).
        MotionTrack track = Track(60, _ => new FrameMotion(0.01, 0, 0, 0, Homography.Identity, 1, 50));

        StabilizationSolution adaptive = StabilizationSolver.Solve(
            track, With(StabilizationSettings.Default, mode: StabilizationMode.SmoothCamera, smoothness: 0.6, strength: 1.0, zoom: false),
            1920, 1080);
        StabilizationSolution uniform = StabilizationSolver.Solve(
            track, With(StabilizationSettings.Default, mode: StabilizationMode.SmoothMotion, smoothness: 0.6, strength: 1.0, zoom: false),
            1920, 1080);

        double LagSsq(StabilizationSolution s) =>
            s.RawPath.Zip(s.SmoothedPath, (r, m) => (r.Tx - m.Tx) * (r.Tx - m.Tx)).Sum();

        Assert.True(LagSsq(adaptive) < LagSsq(uniform),
            $"adaptive lag {LagSsq(adaptive)} not below uniform lag {LagSsq(uniform)}");
    }

    [Fact]
    public void Camera_Lock_Holds_The_Path_Constant()
    {
        MotionTrack track = Track(40, i => new FrameMotion(0.005 * i, -0.003, 0.001, 0.0005, Homography.Identity, 1, 50));
        var settings = With(StabilizationSettings.Default, mode: StabilizationMode.CameraLock, strength: 1.0, zoom: false);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);
        // Zoom off ⇒ λ = 1 ⇒ the smoothed path is exactly the (constant) locked target.
        Assert.All(sol.SmoothedPath, p => Assert.Equal(sol.SmoothedPath[0].Tx, p.Tx, 9));
        Assert.All(sol.SmoothedPath, p => Assert.Equal(sol.SmoothedPath[0].Ty, p.Ty, 9));
    }

    [Fact]
    public void Scale_Lock_Holds_Scale_Constant_And_Leaves_Position_Untouched_At_Zero_Position_Smoothing()
    {
        MotionTrack track = Track(40, i => new FrameMotion(0.004, 0.002, 0.01 * (i % 3 - 1), 0, Homography.Identity, 1, 50));
        var settings = StabilizationSettings.Default with
        {
            Method = StabilizationMethod.Similarity,
            ScaleMode = ScaleMode.Lock,
            ScaleLockRef = ScaleLockReference.Tightest,
            PositionSmooth = 0.0,
            RotationSmooth = 0.0,
            Strength = 1.0,
            Zoom = false,
        };

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);
        // Scale channel is locked to a constant …
        Assert.All(sol.SmoothedPath, p => Assert.Equal(sol.SmoothedPath[0].LogScale, p.LogScale, 9));
        // … while position, with zero smoothing, is left exactly at the raw path (no correction).
        for (int i = 0; i < sol.FrameCount; i++)
        {
            Assert.Equal(sol.RawPath[i].Tx, sol.SmoothedPath[i].Tx, 9);
            Assert.Equal(sol.RawPath[i].Ty, sol.SmoothedPath[i].Ty, 9);
        }
    }

    [Fact]
    public void Lock_Horizon_Holds_The_Angle_Constant()
    {
        MotionTrack track = Track(40, _ => new FrameMotion(0.003, 0, 0, 0.01, Homography.Identity, 1, 50));
        var settings = With(StabilizationSettings.Default, method: StabilizationMethod.Similarity,
            lockRotation: true, strength: 1.0, zoom: false);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);
        Assert.All(sol.SmoothedPath, p => Assert.Equal(sol.RawPath[0].Angle, p.Angle, 9));
    }

    [Fact]
    public void Strength_Zero_Is_The_Identity_Correction()
    {
        MotionTrack track = Track(40, i => new FrameMotion(0.01 * i, -0.005, 0.002 * i, 0.001 * i, Homography.Identity, 1, 50));
        var settings = With(StabilizationSettings.Default, strength: 0.0);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);
        Assert.Equal(1.0, sol.AppliedZoom, 9);
        foreach (double[] m in sol.OutputToSource)
            for (int k = 0; k < 9; k++)
                Assert.Equal(StabilizationSolution.Identity[k], m[k], 9);
    }

    [Fact]
    public void Auto_Zoom_Crop_Fits_Every_Frame_Within_The_Cropping_Ratio()
    {
        // A drifting, jittering handheld shot that forces a real correction (and therefore a real zoom).
        MotionTrack track = Track(90, i =>
            new FrameMotion(0.006 + 0.01 * Math.Sin(i * 0.7), 0.004 * Math.Cos(i * 0.9), 0.002 * Math.Sin(i * 0.3),
                0.003 * Math.Sin(i * 0.5), Homography.Identity, 1, 50));
        double croppingRatio = 0.8;
        var settings = StabilizationSettings.Default with { Smoothness = 0.7, Strength = 1.0, Zoom = true, CroppingRatio = croppingRatio };

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);

        // The applied zoom never exceeds the crop budget cap …
        Assert.InRange(sol.AppliedZoom, 1.0, 1.0 / croppingRatio + 1e-9);

        // … and at that zoom every output corner maps back inside the source rectangle (no border shows).
        const double eps = 1e-6;
        double hx = 0.5, hy = 0.5 * sol.FrameAspectYOverX;
        (double X, double Y)[] corners = [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)];
        foreach (double[] m in sol.OutputToSource)
            foreach ((double cx, double cy) in corners)
            {
                double sx = m[0] * cx + m[1] * cy + m[2];
                double sy = m[3] * cx + m[4] * cy + m[5];
                Assert.True(Math.Abs(sx) <= hx + eps && Math.Abs(sy) <= hy + eps,
                    $"corner ({cx},{cy}) maps to ({sx},{sy}) outside the source rectangle");
            }
    }

    [Fact]
    public void Solve_Is_Deterministic()
    {
        MotionTrack track = Track(50, i => new FrameMotion(0.01 * Math.Sin(i), 0.008 * Math.Cos(i), 0.003, 0.002, Homography.Identity, 1, 50));
        var settings = StabilizationSettings.Default with { Smoothness = 0.6, Zoom = true };

        StabilizationSolution a = StabilizationSolver.Solve(track, settings, 1920, 1080);
        StabilizationSolution b = StabilizationSolver.Solve(track, settings, 1920, 1080);

        Assert.Equal(a.AppliedZoom, b.AppliedZoom); // exact (bit-identical), not within a tolerance
        for (int i = 0; i < a.FrameCount; i++)
            Assert.Equal(a.OutputToSource[i], b.OutputToSource[i]);
    }

    [Fact]
    public void Perspective_With_No_Projective_Motion_Is_Identical_To_Similarity()
    {
        // When every homography is the identity (no perspective wobble), the Perspective method must collapse
        // exactly onto the well-tested Similarity solve — the projective row contributes nothing.
        MotionTrack track = Track(60, i =>
            new FrameMotion(0.006 * Math.Sin(i * 0.3), 0.004 * Math.Cos(i * 0.4), 0.002 * Math.Sin(i * 0.2),
                0.003 * Math.Sin(i * 0.5), Homography.Identity, 1, 50));
        var baseSettings = StabilizationSettings.Default with { Smoothness = 0.6, Strength = 1.0, Zoom = true };

        StabilizationSolution sim = StabilizationSolver.Solve(track, baseSettings with { Method = StabilizationMethod.Similarity }, 1920, 1080);
        StabilizationSolution per = StabilizationSolver.Solve(track, baseSettings with { Method = StabilizationMethod.Perspective }, 1920, 1080);

        Assert.Equal(sim.AppliedZoom, per.AppliedZoom); // bit-identical
        for (int i = 0; i < sim.FrameCount; i++)
            Assert.Equal(sim.OutputToSource[i], per.OutputToSource[i]);
    }

    [Fact]
    public void Perspective_Engages_The_Projective_Row_And_Still_Covers_Every_Frame()
    {
        // A track that wobbles in the two projective degrees of freedom (perspective jitter a Similarity cannot
        // model). The Perspective solve must produce a genuinely projective correction (non-zero third row) and
        // still crop-fit every frame within the budget.
        MotionTrack track = Track(90, i => new FrameMotion(
            0.003 * Math.Sin(i * 0.3), 0.002 * Math.Cos(i * 0.25), 0, 0,
            new Homography(1, 0, 0, 0, 1, 0, 0.0025 * Math.Sin(i * 0.5), 0.0018 * Math.Cos(i * 0.4)), 1, 60));
        double croppingRatio = 0.7;
        var settings = StabilizationSettings.Default with
        {
            Method = StabilizationMethod.Perspective, Mode = StabilizationMode.SmoothMotion,
            Smoothness = 0.7, Strength = 1.0, Zoom = true, CroppingRatio = croppingRatio,
        };

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);

        // A real projective correction was applied on at least some frames (a Similarity solve would leave the
        // third row exactly [0 0 1] on every frame).
        bool projectiveEngaged = sol.OutputToSource.Any(m => Math.Abs(m[6]) > 1e-7 || Math.Abs(m[7]) > 1e-7);
        Assert.True(projectiveEngaged, "Perspective produced no projective correction");

        // The crop budget still holds — every output corner maps (through the full perspective divide) back
        // inside the source rectangle.
        Assert.InRange(sol.AppliedZoom, 1.0, 1.0 / croppingRatio + 1e-9);
        const double eps = 1e-6;
        double hx = 0.5, hy = 0.5 * sol.FrameAspectYOverX;
        (double X, double Y)[] corners = [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)];
        foreach (double[] m in sol.OutputToSource)
            foreach ((double cx, double cy) in corners)
            {
                double w = m[6] * cx + m[7] * cy + m[8];
                double sx = (m[0] * cx + m[1] * cy + m[2]) / w;
                double sy = (m[3] * cx + m[4] * cy + m[5]) / w;
                Assert.True(Math.Abs(sx) <= hx + eps && Math.Abs(sy) <= hy + eps,
                    $"corner ({cx},{cy}) maps to ({sx},{sy}) outside the source rectangle");
            }

        // Deterministic (same golden-frame guarantee as the similarity path).
        StabilizationSolution again = StabilizationSolver.Solve(track, settings, 1920, 1080);
        for (int i = 0; i < sol.FrameCount; i++)
            Assert.Equal(sol.OutputToSource[i], again.OutputToSource[i]);
    }

    [Fact]
    public void Fix_Focus_Breathing_Only_Preset_Flattens_A_Scale_Pump()
    {
        // The marquee case: a static camera whose scale pumps ±2 % (focus breathing). The shipped preset must
        // hold the scale constant (Scale Lock) while leaving pan/tilt/rotation untouched.
        MotionTrack track = Track(60, i => new FrameMotion(0, 0, 0.02 * Math.Sin(i * 0.4), 0, Homography.Identity, 1, 50));

        EffectPreset preset = StabilizationPresets.All.Single(p => p.Name == "Fix Focus Breathing Only");
        StabilizationSettings settings = StabilizationSettings.FromParameters(
            (name, fallback) => preset.Values.TryGetValue(name, out double v) ? v : fallback);
        Assert.Equal(ScaleMode.Lock, settings.ScaleMode);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);

        // Scale is flattened to a constant (the pump is gone) …
        Assert.All(sol.SmoothedPath, p => Assert.Equal(sol.SmoothedPath[0].LogScale, p.LogScale, 9));
        Assert.True(Variance(sol.SmoothedPath.Select(p => p.LogScale)) < 1e-12);
        // … while position stays exactly on the raw path (position/rotation smoothing are 0 in the preset).
        for (int i = 0; i < sol.FrameCount; i++)
        {
            Assert.Equal(sol.RawPath[i].Tx, sol.SmoothedPath[i].Tx, 9);
            Assert.Equal(sol.RawPath[i].Ty, sol.SmoothedPath[i].Ty, 9);
        }
    }

    [Fact]
    public void A_Zoom_About_The_Centre_Reported_About_The_TopLeft_Origin_Is_Not_A_Pan()
    {
        // The estimator fits dst = S·R·src + t about the frame's top-left corner, so a pure zoom about the centre
        // arrives as logScale s with t = (1 − e^s)·c — not t = 0. The solver must re-express that translation
        // about the centre: the integrated pan/tilt stays flat and Camera Lock adds no position correction. (Before
        // the fix the phantom pan was locked/smoothed like a real one, shifting the frame in step with every
        // scale change — focus breathing turned into a wobble.)
        const double aspY = 1080.0 / 1920.0;
        MotionTrack track = Track(60, i =>
        {
            double log = 0.01 * Math.Sin(i * 0.3);
            double s = Math.Exp(log);
            return new FrameMotion((1 - s) * 0.5, (1 - s) * 0.5 * aspY, log, 0, Homography.Identity, 1, 50);
        });
        var settings = With(StabilizationSettings.Default, mode: StabilizationMode.CameraLock, strength: 1.0, zoom: false);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);

        Assert.All(sol.RawPath, p => { Assert.Equal(0.0, p.Tx, 9); Assert.Equal(0.0, p.Ty, 9); });
        for (int i = 0; i < sol.FrameCount; i++)
        {
            Assert.Equal(0.0, sol.OutputToSource[i][2], 9); // no translation correction …
            Assert.Equal(0.0, sol.OutputToSource[i][5], 9);
        }
        Assert.True(Variance(sol.RawPath.Select(p => p.LogScale)) > 1e-6); // … while the scale channel still carries the pump
    }

    [Fact]
    public void A_Roll_About_The_Centre_Reported_About_The_TopLeft_Origin_Is_Not_A_Pan_Either()
    {
        // Same for rotation: about the top-left origin a roll about the centre carries t = (I − R)·c.
        const double aspY = 1080.0 / 1920.0;
        const double cx = 0.5, cy = 0.5 * aspY;
        MotionTrack track = Track(40, i =>
        {
            double a = 0.02 * Math.Sin(i * 0.5);
            double cos = Math.Cos(a), sin = Math.Sin(a);
            double tx = cx - (cos * cx - sin * cy);
            double ty = cy - (sin * cx + cos * cy);
            return new FrameMotion(tx, ty, 0, a, Homography.Identity, 1, 50);
        });
        var settings = With(StabilizationSettings.Default, mode: StabilizationMode.CameraLock, strength: 1.0, zoom: false);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 1920, 1080);

        Assert.All(sol.RawPath, p => { Assert.Equal(0.0, p.Tx, 9); Assert.Equal(0.0, p.Ty, 9); });
    }

    [Fact]
    public void A_True_Pan_Survives_Re_Centring_Unchanged()
    {
        // With no scale/rotation change the origin doesn't matter: a plain translation integrates as itself.
        MotionTrack track = Track(20, _ => new FrameMotion(0.004, -0.002, 0, 0, Homography.Identity, 1, 50));
        StabilizationSolution sol = StabilizationSolver.Solve(track, StabilizationSettings.Default, 1920, 1080);
        for (int i = 0; i < sol.FrameCount; i++)
        {
            Assert.Equal(0.004 * i, sol.RawPath[i].Tx, 9);
            Assert.Equal(-0.002 * i, sol.RawPath[i].Ty, 9);
        }
    }

    [Fact]
    public void Empty_Track_Solves_To_An_Empty_Identity_Solution()
    {
        MotionTrack track = Track(0, _ => FrameMotion.Identity);
        StabilizationSolution sol = StabilizationSolver.Solve(track, StabilizationSettings.Default, 1920, 1080);
        Assert.Equal(0, sol.FrameCount);
        Assert.Equal(1.0, sol.AppliedZoom, 9);
    }

    // ── AnalysisKey bucketing ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Analysis_Key_Buckets_Small_Trims_To_The_Same_Cache_File()
    {
        // Two trims a fraction of a second apart fall in the same padded/rounded bucket ⇒ one cache entry.
        AnalysisKey a = AnalysisKey.ForClipRange("src", false, Timecode.FromSeconds(10), Timecode.FromSeconds(20));
        AnalysisKey b = AnalysisKey.ForClipRange("src", false, Timecode.FromSeconds(10.4), Timecode.FromSeconds(19.6));
        Assert.Equal(a, b);
        Assert.Equal(a.CacheFileName, b.CacheFileName);
    }

    [Fact]
    public void Analysis_Key_Distinguishes_Source_Detail_And_Distant_Ranges()
    {
        AnalysisKey baseKey = AnalysisKey.ForClipRange("src", false, Timecode.FromSeconds(10), Timecode.FromSeconds(20));

        Assert.NotEqual(baseKey, AnalysisKey.ForClipRange("other", false, Timecode.FromSeconds(10), Timecode.FromSeconds(20)));
        Assert.NotEqual(baseKey, AnalysisKey.ForClipRange("src", true, Timecode.FromSeconds(10), Timecode.FromSeconds(20)));
        Assert.NotEqual(baseKey, AnalysisKey.ForClipRange("src", false, Timecode.FromSeconds(40), Timecode.FromSeconds(60)));
        Assert.NotEqual(baseKey.CacheFileName,
            AnalysisKey.ForClipRange("src", true, Timecode.FromSeconds(10), Timecode.FromSeconds(20)).CacheFileName);
    }

    // ── ResolvedEffect source-frame context ─────────────────────────────────────────────────────────────

    [Fact]
    public void Resolved_Effect_Carries_The_Source_Time_And_Media_For_A_Media_Clip()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var media = MediaRefId.New();
        var track = new VideoTrack();
        // Clip: source in-point 2s, out 6s, placed at timeline 10s.
        var clip = new Clip(media, Timecode.FromSeconds(2), Timecode.FromSeconds(6), Timecode.FromSeconds(10));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Stabilization));
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);

        // 1s into the clip ⇒ source time 3s.
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(11));
        VideoLayer layer = Assert.Single(plan.Layers);
        ResolvedEffect effect = Assert.Single(layer.Effects);

        Assert.Equal(media, effect.MediaRefId);
        Assert.Equal(Timecode.FromSeconds(3), effect.SourceTime);
        Assert.Equal(layer.SourceTime, effect.SourceTime); // same frame the layer samples
        Assert.Equal(Timecode.FromSeconds(11).Ticks, effect.FrameTime); // timeline ticks, unchanged
    }

    [Fact]
    public void Resolved_Effect_Has_No_Media_For_An_Adjustment_Layer()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var track = new VideoTrack();
        Clip clip = Clip.CreateAdjustment(Timecode.FromSeconds(5), Timecode.Zero);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness));
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);

        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, Timecode.FromSeconds(1));
        VideoLayer layer = Assert.Single(plan.Layers);
        ResolvedEffect effect = Assert.Single(layer.Effects);
        Assert.Null(effect.MediaRefId);
    }
}
