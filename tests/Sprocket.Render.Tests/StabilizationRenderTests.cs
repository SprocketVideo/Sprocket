using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Exercises the stabilization render seam (plan/features/stabilization.md, phase 4) headlessly on the CPU
/// backend: a fake <see cref="IMotionTrackProvider"/> hands the pipeline a hand-built <see cref="MotionTrack"/>,
/// the real <see cref="StabilizationSolver"/> solves it, and the <c>StabilizeSksl</c> shader applies the
/// resulting warp. The tests assert that the pipeline plumbs provider → solve → shader correctly (a translating
/// track moves content, a scale-lock track scales it about the centre), that a missing provider/track passes
/// through, and that an unchanged (track, settings, size) is solved only once.
/// </summary>
public sealed class StabilizationRenderTests
{
    private const int Size = 40;
    private static readonly MediaRefId Media = new(Guid.NewGuid());

    /// <summary>A provider that always returns one track (or null, to model "not analysed yet").</summary>
    private sealed class FakeProvider(MotionTrack? track) : IMotionTrackProvider
    {
        public int Calls { get; private set; }

        public MotionTrack? TryGetTrack(MediaRefId mediaRefId, Timecode sourceTime, bool detailed)
        {
            Calls++;
            return track;
        }
    }

    [Fact]
    public void NullProvider_PassesThrough()
    {
        // No provider set on the pipeline → the stabilization effect renders as a plain image draw.
        using SKBitmap result = Render(provider: null, track: null, sourceFrame: 0, settings: Default());
        Assert.True(IsMarker(result, Size / 2, Size / 2), "Centre marker should be untouched with no provider.");
    }

    [Fact]
    public void NoTrackYet_PassesThrough()
    {
        // Provider present but returns null (source not analysed) → pass-through, marker stays centred.
        var provider = new FakeProvider(null);
        using SKBitmap result = Render(provider, track: null, sourceFrame: 0, settings: Default());
        Assert.True(provider.Calls > 0, "The pipeline should have queried the provider.");
        Assert.True(IsMarker(result, Size / 2, Size / 2), "Centre marker should be untouched when no track is available.");
    }

    [Fact]
    public void IdentityTrack_PassesThrough()
    {
        // A track with no motion solves to an identity warp → the centre marker is unmoved.
        MotionTrack track = BuildTrack(5, _ => FrameMotion.Identity);
        using SKBitmap result = Render(new FakeProvider(track), track, sourceFrame: 2, settings: Default());
        Assert.True(IsMarker(result, Size / 2, Size / 2), "Identity track should leave the centre marker in place.");
    }

    [Fact]
    public void TranslatingTrack_CameraLock_MovesMarker()
    {
        // A steady pan right; Camera Lock removes it, so the frame-0 correction re-centres the path — the centre
        // marker is warped off-centre. Zoom off keeps the correction undamped so the shift is the raw residual.
        const double perFrame = 0.08; // fraction of width per frame
        MotionTrack track = BuildTrack(5, i => Motion(tx: i == 0 ? 0 : perFrame));
        StabilizationSettings settings = Default() with { Mode = StabilizationMode.CameraLock, Zoom = false };

        using SKBitmap result = Render(new FakeProvider(track), track, sourceFrame: 0, settings: settings);

        // Predict where the source-centre marker lands from the real solve's matrix (proves the render applies it).
        (int px, int py) = PredictOutputPixel(track, settings, frame: 0, srcNx: 0.0, srcNy: 0.0);
        Assert.False(IsMarker(result, Size / 2, Size / 2), "Centre should no longer hold the marker after a pan removal.");
        Assert.True(px != Size / 2, "The solve should have produced a horizontal shift.");
        Assert.True(IsMarker(result, px, py), $"Marker should have moved to ({px},{py}).");
    }

    [Fact]
    public void ZoomingTrack_ScaleLock_ScalesAboutCentre()
    {
        // A source that pumps larger over time about its centre; Scale Lock (ref = first frame) removes the scale
        // change. The correction is a pure zoom about the centre: the centre marker stays put, an off-centre marker
        // moves. The track carries the motion the way the estimator reports it — about the top-left origin, so a
        // centred zoom has t = (1 − s)·c, not t = 0 (see CentredZoom).
        MotionTrack track = BuildTrack(3, i => i == 0 ? Motion() : CentredZoom(0.12));
        StabilizationSettings settings = Default() with
        {
            ScaleMode = ScaleMode.Lock,
            ScaleLockRef = ScaleLockReference.FirstFrame,
            Zoom = false,
        };

        // Off-centre marker at source-normalised (0.25, 0) → canvas pixel it occupies in the source.
        int offX = Size / 2 + (int)Math.Round(0.25 * Size);
        int offY = Size / 2;
        using SKBitmap result = Render(new FakeProvider(track), track, sourceFrame: 2, settings: settings,
            markerX: offX, markerY: offY, alsoCentre: true);

        // Centre marker stays; off-centre marker moves inward (predicted from the solve).
        Assert.True(IsMarker(result, Size / 2, Size / 2), "Scale-about-centre must leave the centre marker fixed.");
        (int px, int py) = PredictOutputPixel(track, settings, frame: 2, srcNx: 0.25, srcNy: 0.0);
        Assert.True(px < offX, "A scale-out correction should pull the off-centre marker toward the centre.");
        Assert.True(IsMarker(result, px, py), $"Off-centre marker should map to ({px},{py}).");
    }

    [Fact]
    public void UnchangedParams_SolvesOnce()
    {
        MotionTrack track = BuildTrack(5, i => Motion(tx: i == 0 ? 0 : 0.03));
        StabilizationSettings settings = Default();
        var provider = new FakeProvider(track);

        using var pipeline = new SkiaEffectPipeline { MotionTracks = provider };
        for (int frame = 0; frame < 5; frame++)
            RenderWith(pipeline, track, settings, frame).Dispose();

        Assert.Equal(1, pipeline.StabilizationSolveCount); // one solve reused across all five frames
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static StabilizationSettings Default() => StabilizationSettings.Default;

    private static FrameMotion Motion(double tx = 0, double ty = 0, double logScale = 0, double angle = 0) =>
        new(tx, ty, logScale, angle, Homography.Identity, 1.0, 100);

    /// <summary>A zoom about the frame centre as the estimator emits it: the similarity is fit about the top-left
    /// origin, so the centred zoom carries t = (1 − s)·c (c = (0.5, 0.5·aspY); aspY = 1 for the square test frame).</summary>
    private static FrameMotion CentredZoom(double logScale)
    {
        double s = Math.Exp(logScale);
        return Motion(tx: (1 - s) * 0.5, ty: (1 - s) * 0.5, logScale: logScale);
    }

    private static MotionTrack BuildTrack(int frames, Func<int, FrameMotion> motion)
    {
        var pts = new long[frames];
        var motions = new FrameMotion[frames];
        for (int i = 0; i < frames; i++)
        {
            pts[i] = i * Timecode.TicksPerSecond / 30; // 30 fps sample spacing
            motions[i] = motion(i);
        }
        return new MotionTrack("fake-source", detailedAnalysis: false,
            new Timecode(pts[0]), new Timecode(pts[^1]), new Rational(30, 1), pts, motions);
    }

    /// <summary>The output canvas pixel the solve maps a source-normalised point to at the given frame.</summary>
    private static (int px, int py) PredictOutputPixel(
        MotionTrack track, StabilizationSettings settings, int frame, double srcNx, double srcNy)
    {
        StabilizationSolution sol = StabilizationSolver.Solve(track, settings, Size, Size);
        double[] m = sol.OutputToSource[frame]; // output→source; invert to find output for a known source point
        // Affine inverse of the 2×2 linear part (v1 is a similarity; the third row is [0 0 1]).
        double det = m[0] * m[4] - m[1] * m[3];
        double ox = (m[4] * (srcNx - m[2]) - m[1] * (srcNy - m[5])) / det;
        double oy = (-m[3] * (srcNx - m[2]) + m[0] * (srcNy - m[5])) / det;
        int px = (int)Math.Round(Size / 2.0 + ox * Size);
        int py = (int)Math.Round(Size / 2.0 + oy * Size);
        return (px, py);
    }

    private static bool IsMarker(SKBitmap bmp, int x, int y)
    {
        if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height)
            return false;
        return bmp.GetPixel(x, y).Red > 180;
    }

    private static SKBitmap MakeSource(int markerX, int markerY, bool alsoCentre)
    {
        var bmp = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bmp.Erase(SKColors.Black);
        DrawBlock(bmp, markerX, markerY);
        if (alsoCentre)
            DrawBlock(bmp, Size / 2, Size / 2);
        return bmp;
    }

    private static void DrawBlock(SKBitmap bmp, int cx, int cy)
    {
        for (int y = cy - 2; y <= cy + 2; y++)
            for (int x = cx - 2; x <= cx + 2; x++)
                if (x >= 0 && y >= 0 && x < bmp.Width && y < bmp.Height)
                    bmp.SetPixel(x, y, SKColors.White);
    }

    private static ResolvedEffect StabEffect(StabilizationSettings s, int sourceFrame)
    {
        var p = new Dictionary<string, double>
        {
            [EffectParamNames.StabMode] = (double)(int)s.Mode,
            [EffectParamNames.Smoothness] = s.Smoothness,
            [EffectParamNames.Strength] = s.Strength,
            [EffectParamNames.StabMethod] = (double)(int)s.Method,
            [EffectParamNames.PositionSmooth] = s.PositionSmooth,
            [EffectParamNames.RotationSmooth] = s.RotationSmooth,
            [EffectParamNames.ScaleMode] = (double)(int)s.ScaleMode,
            [EffectParamNames.ScaleSmooth] = s.ScaleSmooth,
            [EffectParamNames.ScaleLockRef] = (double)(int)s.ScaleLockRef,
            [EffectParamNames.LockRotation] = s.LockRotation ? 1 : 0,
            [EffectParamNames.Zoom] = s.Zoom ? 1 : 0,
            [EffectParamNames.CroppingRatio] = s.CroppingRatio,
            [EffectParamNames.DetailedAnalysis] = s.DetailedAnalysis ? 1 : 0,
        };
        long ticks = sourceFrame * Timecode.TicksPerSecond / 30;
        return new ResolvedEffect(EffectTypeIds.Stabilization, p, null, ticks, new Timecode(ticks), Media);
    }

    private static SKBitmap Render(
        IMotionTrackProvider? provider, MotionTrack? track, int sourceFrame, StabilizationSettings settings,
        int markerX = Size / 2, int markerY = Size / 2, bool alsoCentre = false)
    {
        using var pipeline = new SkiaEffectPipeline { MotionTracks = provider };
        return RenderWith(pipeline, track, settings, sourceFrame, markerX, markerY, alsoCentre);
    }

    private static SKBitmap RenderWith(
        SkiaEffectPipeline pipeline, MotionTrack? track, StabilizationSettings settings, int sourceFrame,
        int markerX = Size / 2, int markerY = Size / 2, bool alsoCentre = false)
    {
        using SKBitmap src = MakeSource(markerX, markerY, alsoCentre);
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        pipeline.Present(
            surface.Canvas, SKRect.Create(Size, Size), src.GetPixels(), src.RowBytes, Size, Size,
            [StabEffect(settings, sourceFrame)], SKColors.Black);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }
}
