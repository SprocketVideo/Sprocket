using System.Linq;
using Sprocket.Analysis.Motion;
using Sprocket.Core.Model;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Analysis.Tests;

/// <summary>
/// Integration tests for <see cref="MotionTrackAnalyzer"/> against real decoded fixtures
/// (plan/features/stabilization.md phase 3). Gated on the <c>ffmpeg</c> CLI + FFmpeg 8 natives, like
/// <c>Sprocket.Media.Tests</c>: they decode <see cref="StabFixtures"/> clips whose motion is analytically
/// known and assert the recovered <see cref="MotionTrack"/> matches it.
/// </summary>
public class MotionTrackAnalyzerTests
{
    // Source is 320 px wide, below the 480 px standard analysis width, so no upscale: analysis width = 320.
    private const int AnalysisWidth = 320;

    private static Timecode Seconds(double s) => new((long)Math.Round(s * Timecode.TicksPerSecond));

    private static MotionTrack Analyze(string path, StabilizationSettings? settings = null) =>
        MotionTrackAnalyzer.Analyze(
            MediaOpenRequest.ForPath(path),
            sourceIdentity: "fixture",
            from: Timecode.Zero,
            to: Seconds(StabFixtures.DurationSeconds + 1), // slack past the clip so every frame is analysed
            settings ?? StabilizationSettings.Default);

    [Fact]
    public void Static_Clip_Recovers_Near_Identity()
    {
        MotionTrack track = Analyze(StabFixtures.StaticPath);

        Assert.InRange(track.FrameCount, StabFixtures.FrameCount - 3, StabFixtures.FrameCount + 1);

        // A held still: every inter-frame estimate is essentially zero motion.
        double txPeak = PeakToPeak(CumulativePixels(track, m => m.Tx));
        double tyPeak = PeakToPeak(CumulativePixels(track, m => m.Ty));
        double scalePeak = PeakToPeak(CumulativeScale(track));

        Assert.True(txPeak < 2.0, $"static horizontal drift {txPeak:F2}px");
        Assert.True(tyPeak < 2.0, $"static vertical drift {tyPeak:F2}px");
        Assert.True(scalePeak < 0.02, $"static scale wobble {scalePeak:F4}");
    }

    [Fact]
    public void Shaking_Clip_Recovers_Sinusoidal_Translation()
    {
        MotionTrack track = Analyze(StabFixtures.ShakingPath);

        // Detrend to remove any slow integration drift, then compare the sinusoid's peak-to-peak swing to the
        // analytic amplitude (2×24 px horizontally, 2×16 px vertically).
        double txPeak = PeakToPeak(Detrend(CumulativePixels(track, m => m.Tx)));
        double tyPeak = PeakToPeak(Detrend(CumulativePixels(track, m => m.Ty)));

        Assert.InRange(txPeak, 2 * StabFixtures.ShakeAmplitudeX - 12, 2 * StabFixtures.ShakeAmplitudeX + 12);
        Assert.InRange(tyPeak, 2 * StabFixtures.ShakeAmplitudeY - 10, 2 * StabFixtures.ShakeAmplitudeY + 12);
    }

    [Fact]
    public void Zoom_Clip_Recovers_Scale_Wobble()
    {
        MotionTrack track = Analyze(StabFixtures.ZoomPath);

        double scalePeak = PeakToPeak(Detrend(CumulativeScale(track)));
        // The zoom oscillates by ±3 % ⇒ ~0.06 peak-to-peak in the recovered scale path.
        Assert.InRange(scalePeak, 0.03, 0.11);
    }

    [Fact]
    public void Detailed_Analysis_Flags_The_Track_And_Tracks_More_Features()
    {
        // The Detailed tier changes the cache key (via the track's DetailedAnalysis flag) and doubles the
        // per-bucket feature cap, so it must recover at least as many features as the standard pass. (The fixture
        // is 320 px wide — below both analysis widths — so only the feature-density difference shows here.)
        MotionTrack standard = Analyze(StabFixtures.ZoomPath, StabilizationSettings.Default);
        MotionTrack detailed = Analyze(StabFixtures.ZoomPath, StabilizationSettings.Default with { DetailedAnalysis = true });

        Assert.False(standard.DetailedAnalysis);
        Assert.True(detailed.DetailedAnalysis);

        double StdFeatures(MotionTrack t) => t.Motions.Skip(1).Average(m => (double)m.FeatureCount);
        Assert.True(StdFeatures(detailed) >= StdFeatures(standard),
            $"detailed features {StdFeatures(detailed):F1} < standard {StdFeatures(standard):F1}");
    }

    [Fact]
    public void Fix_Focus_Breathing_Only_Removes_The_Pump_On_The_Zoom_Fixture()
    {
        // End-to-end focus-breathing verification: decode the ±3 % pumping-zoom clip, then solve it with the
        // shipped "Fix Focus Breathing Only" preset. The corrected scale path must be flat (the pump is gone).
        MotionTrack track = Analyze(StabFixtures.ZoomPath);

        EffectPreset preset = StabilizationPresets.All.Single(p => p.Name == "Fix Focus Breathing Only");
        StabilizationSettings settings = StabilizationSettings.FromParameters(
            (name, fallback) => preset.Values.TryGetValue(name, out double v) ? v : fallback);

        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, 320, 240);

        double rawPump = PeakToPeak(Detrend(sol.RawPath.Select(p => p.LogScale).ToArray()));
        double correctedPump = PeakToPeak(sol.SmoothedPath.Select(p => p.LogScale).ToArray());

        Assert.True(rawPump > 0.03, $"expected the raw scale path to pump (>0.03), was {rawPump:F4}");
        Assert.True(correctedPump < 1e-9, $"scale pump not removed: corrected peak-to-peak {correctedPump:E2}");
    }

    [Fact]
    public void Camera_Lock_On_The_Centred_Zoom_Fixture_Adds_No_Pan()
    {
        // zoompan zooms about the frame centre, so the only true camera motion in this clip is scale. The estimator
        // reports each similarity about the top-left origin (a centred zoom arrives as t = (1 − s)·c, ±4.8 px here),
        // and the solver must re-centre it: the integrated pan/tilt stays flat, so Camera Lock corrects the scale
        // without shifting the frame in step with the zoom — the phantom pan that made a stabilized focus-breathing
        // shot look like it was still breathing.
        MotionTrack track = Analyze(StabFixtures.ZoomPath);
        var settings = StabilizationSettings.Default with { Mode = StabilizationMode.CameraLock, Zoom = false };
        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, StabFixtures.Width, StabFixtures.Height);

        double rawPump = PeakToPeak(Detrend(sol.RawPath.Select(p => p.LogScale).ToArray()));
        double panPx = PeakToPeak(Detrend(sol.RawPath.Select(p => p.Tx * AnalysisWidth).ToArray()));
        double tiltPx = PeakToPeak(Detrend(sol.RawPath.Select(p => p.Ty * AnalysisWidth).ToArray()));

        // What the uncentred convention would have integrated as "pan": (1 − s)·c for the recovered scale path.
        double[] scale = CumulativeScale(track);
        double phantomPanPx = PeakToPeak(scale.Select(s => (1 - s) * 0.5 * AnalysisWidth).ToArray());
        double phantomTiltPx = phantomPanPx * StabFixtures.Height / StabFixtures.Width;

        Assert.True(rawPump > 0.03, $"expected the raw scale path to pump (>0.03), was {rawPump:F4}");
        Assert.True(phantomPanPx > 6, $"fixture zoom too small to be diagnostic ({phantomPanPx:F1}px phantom)");
        // zoompan rounds its window origin to whole pixels, so ~1–2 px of apparent pan is fixture quantisation, not
        // the origin leak; the leak itself would be the full phantom amplitude.
        Assert.True(panPx < phantomPanPx / 3, $"pan of {panPx:F2}px leaked from the centred zoom (uncentred it would be ~{phantomPanPx:F1}px)");
        Assert.True(tiltPx < phantomTiltPx / 3, $"tilt of {tiltPx:F2}px leaked from the centred zoom (uncentred it would be ~{phantomTiltPx:F1}px)");
    }

    [Fact]
    public void Cancellation_Stops_Within_One_Frame()
    {
        using var cts = new CancellationTokenSource();
        int reports = 0;
        IProgress<double> cancelOnFirst = new SyncProgress(_ =>
        {
            reports++;
            cts.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => MotionTrackAnalyzer.Analyze(
            MediaOpenRequest.ForPath(StabFixtures.StaticPath),
            sourceIdentity: "fixture",
            from: Timecode.Zero,
            to: Seconds(StabFixtures.DurationSeconds + 1),
            StabilizationSettings.Default,
            cancelOnFirst,
            cts.Token));

        // Cancelled after the first per-frame progress report; it must not have run the whole clip.
        Assert.InRange(reports, 1, 3);
    }

    [Fact]
    public void Analyzed_Track_Survives_Write_Read_Roundtrip()
    {
        MotionTrack track = Analyze(StabFixtures.StaticPath);

        using var stream = new MemoryStream();
        track.Write(stream);
        stream.Position = 0;
        MotionTrack read = MotionTrack.Read(stream);

        Assert.Equal(track.FrameCount, read.FrameCount);
        Assert.Equal(track.SourceIdentity, read.SourceIdentity);
        Assert.Equal(track.RangeStart, read.RangeStart);
        Assert.Equal(track.RangeEnd, read.RangeEnd);
        Assert.Equal(track.FrameRate, read.FrameRate);
        for (int i = 0; i < track.FrameCount; i++)
        {
            Assert.Equal(track.FramePts[i], read.FramePts[i]);
            Assert.Equal(track.Motions[i], read.Motions[i]);
        }
    }

    /// <summary>The integrated camera path of one similarity channel, in analysis pixels.</summary>
    private static double[] CumulativePixels(MotionTrack track, Func<FrameMotion, double> channel)
    {
        var path = new double[track.FrameCount];
        double cum = 0;
        for (int i = 0; i < track.FrameCount; i++)
        {
            cum += channel(track.Motions[i]);
            path[i] = cum * AnalysisWidth;
        }
        return path;
    }

    /// <summary>The integrated scale path as a linear factor (<c>exp(Σ logScale)</c>).</summary>
    private static double[] CumulativeScale(MotionTrack track)
    {
        var path = new double[track.FrameCount];
        double cumLog = 0;
        for (int i = 0; i < track.FrameCount; i++)
        {
            cumLog += track.Motions[i].LogScale;
            path[i] = Math.Exp(cumLog);
        }
        return path;
    }

    private static double PeakToPeak(double[] values)
    {
        if (values.Length == 0)
            return 0;
        double min = values[0], max = values[0];
        foreach (double v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return max - min;
    }

    /// <summary>Subtracts the least-squares line so a slow drift doesn't inflate the peak-to-peak of an
    /// oscillation.</summary>
    private static double[] Detrend(double[] y)
    {
        int n = y.Length;
        if (n < 2)
            return y;

        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            sx += i;
            sy += y[i];
            sxx += (double)i * i;
            sxy += (double)i * y[i];
        }
        double denom = n * sxx - sx * sx;
        double slope = denom == 0 ? 0 : (n * sxy - sx * sy) / denom;
        double intercept = (sy - slope * sx) / n;

        var result = new double[n];
        for (int i = 0; i < n; i++)
            result[i] = y[i] - (slope * i + intercept);
        return result;
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
