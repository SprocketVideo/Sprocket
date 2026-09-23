namespace Sprocket.Export.Tests;

/// <summary>Deterministic <see cref="ExportRunSummary"/> values for fake <see cref="ExportJobRunner"/>s, so queue
/// tests exercise orchestration without encoding (export-speed phase 1).</summary>
internal static class TestSummaries
{
    /// <summary>A small fixed software-encode summary.</summary>
    public static ExportRunSummary Fake { get; } = new(
        ExportAcceleration.Software, "libx264", "libx264", HardwareVideoEngaged: false,
        VideoFrames: 30, AudioSampleFrames: 48000,
        new ExportStageTimings(
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100)));

    /// <summary>Adapts a void fake runner body into an <see cref="ExportJobRunner"/> that returns <see cref="Fake"/>.</summary>
    public static ExportJobRunner Runner(Action<ExportJob, IProgress<double>, CancellationToken> body) =>
        (job, progress, ct) =>
        {
            body(job, progress, ct);
            return Fake;
        };
}
