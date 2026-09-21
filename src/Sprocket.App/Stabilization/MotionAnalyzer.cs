using System;
using System.Threading;
using Sprocket.Analysis.Motion;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.App.Stabilization;

/// <summary>
/// The seam the <see cref="StabilizationService"/> runs one motion analysis through
/// (plan/features/stabilization.md phase 5). Production is <see cref="MediaMotionAnalyzer"/>, a thin adapter over
/// <see cref="MotionTrackAnalyzer.Analyze"/> (which drives the FFmpeg gray decode). The seam is what makes the
/// service's queue / cancel / generation-fencing state machine testable without <c>ffmpeg</c>: a fake analyzer
/// can block mid-run, report progress, and observe cancellation.
/// </summary>
public interface IMotionAnalyzer
{
    /// <summary>
    /// Analyses the source range [<paramref name="from"/>, <paramref name="to"/>] of <paramref name="request"/>
    /// and returns its motion track, stamping <paramref name="sourceIdentity"/> onto it. Must honour
    /// <paramref name="cancellationToken"/> promptly (throwing <see cref="OperationCanceledException"/> is fine —
    /// the service treats it as a cancel, not a failure).
    /// </summary>
    MotionTrack Analyze(
        MediaOpenRequest request, string sourceIdentity, Timecode from, Timecode to,
        StabilizationSettings settings, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>The production <see cref="IMotionAnalyzer"/>: a thin adapter over
/// <see cref="MotionTrackAnalyzer.Analyze"/> in <c>Sprocket.Analysis</c> (software gray decode + CV tracking).</summary>
public sealed class MediaMotionAnalyzer : IMotionAnalyzer
{
    /// <inheritdoc />
    public MotionTrack Analyze(
        MediaOpenRequest request, string sourceIdentity, Timecode from, Timecode to,
        StabilizationSettings settings, IProgress<double>? progress, CancellationToken cancellationToken) =>
        MotionTrackAnalyzer.Analyze(request, sourceIdentity, from, to, settings, progress, cancellationToken);
}
