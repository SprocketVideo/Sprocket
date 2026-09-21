using Sprocket.Analysis.Features;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Analysis.Motion;

/// <summary>
/// Drives a sequential software decode of one source range and estimates the per-frame camera motion,
/// producing the <see cref="MotionTrack"/> the stabilization solver consumes
/// (plan/features/stabilization.md phase 3). This is the bridge between <c>Sprocket.Media</c> (the GRAY8
/// decode driver, <see cref="MediaSource.TryDecodeNextGray"/>) and the pure CV primitives in
/// <see cref="MotionEstimator"/> — both Media-free.
/// </summary>
/// <remarks>
/// <para>Decode is forced to software (<see cref="HardwareAccelMode.Disabled"/>) so the recovered track is
/// bit-identical run to run — the whole feature's determinism (and thus golden-frame export equality) rests
/// on it (ARCHITECTURE.md §5). Frames are downscaled to a fixed analysis width (480 px, or 960 px with
/// Detailed Analysis) into pooled native GRAY8 buffers, so the pass allocates ≈0 managed pixels per frame
/// (§1).</para>
/// <para>The analysis is cancellable at frame granularity and reports progress as the fraction of the
/// requested range decoded. Frames whose fit was unreliable (fewer than <see cref="MinReliableFeatures"/>
/// tracked inliers) are linearly interpolated from their nearest reliable neighbours and left flagged
/// (confidence 0) so downstream code can surface them.</para>
/// </remarks>
public static class MotionTrackAnalyzer
{
    /// <summary>Analysis frame width for a standard pass, in pixels (height follows the source aspect).</summary>
    public const int StandardWidth = 480;

    /// <summary>Analysis frame width for a Detailed Analysis pass, in pixels.</summary>
    public const int DetailedWidth = 960;

    /// <summary>Per-bucket corner cap for a standard pass (8×6 grid ⇒ ~300 features); doubled when Detailed.</summary>
    private const int StandardCapPerBucket = 7;

    /// <summary>A fit backed by fewer inliers than this is treated as unreliable and interpolated.</summary>
    public const int MinReliableFeatures = 8;

    /// <summary>
    /// Analyses the source range [<paramref name="from"/>, <paramref name="to"/>] of
    /// <paramref name="request"/> and returns its motion track. <paramref name="sourceIdentity"/> is the
    /// stable source identity the App keys the analysis cache by (path + size + mtime), stored verbatim on
    /// the track. Throws <see cref="OperationCanceledException"/> if cancelled.
    /// </summary>
    public static MotionTrack Analyze(
        MediaOpenRequest request,
        string sourceIdentity,
        Timecode from,
        Timecode to,
        StabilizationSettings settings,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(settings);

        bool detailed = settings.DetailedAnalysis;

        using MediaSource source = MediaSource.Open(request, HardwareAccelMode.Disabled);
        if (!source.HasVideo)
            throw new InvalidOperationException($"Cannot analyse '{request.Path}': it has no video stream.");

        (int width, int height) = AnalysisSize(source.Info.Width, source.Info.Height, detailed);
        using var pool = new GrayFramePool(width, height);
        var estimator = new MotionEstimator(capPerBucket: detailed ? StandardCapPerBucket * 2 : StandardCapPerBucket);

        long fromTicks = from.Ticks;
        long toTicks = to.Ticks;
        double rangeTicks = Math.Max(1, toTicks - fromTicks);

        var framePts = new List<long>();
        var motions = new List<FrameMotion>();

        source.SeekTo(from);
        GrayFrame? previous = null;
        try
        {
            while (source.TryDecodeNextGray(pool, out GrayFrame? current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (current.Pts.Ticks > toTicks)
                {
                    current.Dispose();
                    break;
                }

                FrameMotion motion = previous is null ? FrameMotion.Identity : Estimate(estimator, previous, current);
                framePts.Add(current.Pts.Ticks);
                motions.Add(motion);

                previous?.Dispose();
                previous = current;

                if (progress is not null)
                {
                    double fraction = (current.Pts.Ticks - fromTicks) / rangeTicks;
                    progress.Report(Math.Clamp(fraction, 0.0, 1.0));
                }
            }
        }
        finally
        {
            previous?.Dispose();
        }

        InterpolateLowConfidence(motions);
        progress?.Report(1.0);

        return new MotionTrack(
            sourceIdentity, detailed, from, to, source.Info.FrameRate, framePts, motions);
    }

    /// <summary>Estimates motion between two decoded gray frames, wrapping each native buffer as a
    /// <see cref="GrayImage"/> span without copying to the managed heap (§1).</summary>
    private static unsafe FrameMotion Estimate(MotionEstimator estimator, GrayFrame previous, GrayFrame current)
    {
        var prevImg = new GrayImage(
            new ReadOnlySpan<byte>((void*)previous.Pixels, previous.RowBytes * previous.Height),
            previous.Width, previous.Height, previous.RowBytes);
        var curImg = new GrayImage(
            new ReadOnlySpan<byte>((void*)current.Pixels, current.RowBytes * current.Height),
            current.Width, current.Height, current.RowBytes);
        return estimator.Estimate(prevImg, curImg);
    }

    /// <summary>The analysis frame size for a source: the target width (capped at the source width so we
    /// never upscale), with height following the source aspect ratio; both rounded to even values.</summary>
    private static (int Width, int Height) AnalysisSize(int sourceWidth, int sourceHeight, bool detailed)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidOperationException("Source has no usable frame size.");

        int target = detailed ? DetailedWidth : StandardWidth;
        int width = Math.Min(target, sourceWidth);
        int height = (int)Math.Round((double)width * sourceHeight / sourceWidth);
        return (MakeEven(width), MakeEven(height));
    }

    private static int MakeEven(int v) => Math.Max(2, v - (v & 1));

    /// <summary>
    /// Replaces the similarity channels of unreliably-tracked frames (fewer than
    /// <see cref="MinReliableFeatures"/> inliers) with a linear interpolation of their nearest reliable
    /// neighbours, leaving <see cref="FrameMotion.Confidence"/> at 0 so they stay flagged. Frame 0 is the
    /// identity anchor and is never interpolated.
    /// </summary>
    private static void InterpolateLowConfidence(List<FrameMotion> motions)
    {
        int n = motions.Count;
        for (int i = 1; i < n; i++)
        {
            if (motions[i].FeatureCount >= MinReliableFeatures)
                continue;

            int prev = i - 1;
            while (prev >= 1 && motions[prev].FeatureCount < MinReliableFeatures)
                prev--;
            int next = i + 1;
            while (next < n && motions[next].FeatureCount < MinReliableFeatures)
                next++;

            bool hasPrev = prev >= 0 && (prev == 0 || motions[prev].FeatureCount >= MinReliableFeatures);
            bool hasNext = next < n && motions[next].FeatureCount >= MinReliableFeatures;

            FrameMotion filled;
            if (hasPrev && hasNext)
            {
                double t = (double)(i - prev) / (next - prev);
                filled = Lerp(motions[prev], motions[next], t);
            }
            else if (hasPrev)
            {
                filled = motions[prev];
            }
            else if (hasNext)
            {
                filled = motions[next];
            }
            else
            {
                continue; // nothing reliable to interpolate from — leave the raw estimate
            }

            // Keep the flag: confidence 0, the original (low) feature count, similarity-only.
            motions[i] = filled with
            {
                Homography = Homography.Identity,
                Confidence = 0,
                FeatureCount = motions[i].FeatureCount,
            };
        }
    }

    /// <summary>Linearly interpolates the similarity channels of two motions (translation / log-scale / angle).</summary>
    private static FrameMotion Lerp(FrameMotion a, FrameMotion b, double t) => new(
        Tx: a.Tx + (b.Tx - a.Tx) * t,
        Ty: a.Ty + (b.Ty - a.Ty) * t,
        LogScale: a.LogScale + (b.LogScale - a.LogScale) * t,
        Angle: a.Angle + (b.Angle - a.Angle) * t,
        Homography: Homography.Identity,
        Confidence: 0,
        FeatureCount: 0);
}
