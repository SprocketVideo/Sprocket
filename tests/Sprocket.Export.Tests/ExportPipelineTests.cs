using System.Security.Cryptography;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// The staged export pipeline (export-speed phase 2): render / mux overlap and background decode prefetch must be
/// output-neutral. Each scenario exports the same project with the pipeline on and with the one-frame-at-a-time
/// schedule, and requires <b>byte-identical files</b> — the same decoded pixels, the same audio, the same mux
/// interleave. Scenarios cover the provider paths prefetch interacts with: forward decode, a cut back to an earlier
/// in-point (prefetch discarded ahead of a seek), a reversed clip (GOP window), held frames from a frame-rate
/// override, and a same-source transition — each with one render worker and with several.
/// </summary>
public sealed class ExportPipelineTests
{
    public static TheoryData<string> Scenarios => ["effect+audio", "backward-cut", "reverse", "held-frames", "same-source-transition"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void PipelinedExport_IsByteIdentical_ToSequentialExport(string scenario)
    {
        (Project project, ExportOptions options) = Build(scenario);

        using var sequential = new TempFile();
        ExportRunSummary reference = VideoExporter.ExportCore(
            project, sequential.Path, options, null, null, null, default, null, pipelined: false);
        string expected = Hash(sequential.Path);

        // One worker (pure stage overlap) and three (round-robin across independent pipelines + decoders; three so
        // the frame count doesn't divide evenly) — both must reproduce the sequential file exactly.
        foreach (int workers in new[] { 1, 3 })
        {
            using var pipelined = new TempFile();
            ExportRunSummary run = VideoExporter.ExportCore(
                project, pipelined.Path, options, null, null, null, default, null, pipelined: true, renderWorkers: workers);

            Assert.Equal(reference.VideoFrames, run.VideoFrames);
            Assert.Equal(reference.AudioSampleFrames, run.AudioSampleFrames);
            Assert.True(expected == Hash(pipelined.Path), $"{scenario}: {workers}-worker pipelined export differs from sequential");
        }
    }

    [Fact]
    public void PipelinedExport_CancelledMidRun_ThrowsAndLeavesNoPartialFile()
    {
        Project project = ExportFixture.BuildProject(withAudio: true, brightness: 0.2);
        using var output = new TempFile();
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p > 0.3) cts.Cancel(); });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            VideoExporter.ExportWithSummary(project, output.Path, default, null, null, progress, cts.Token));
        Assert.False(File.Exists(output.Path), "a cancelled pipelined export must not leave a partial file behind");
    }

    [Fact]
    public void PipelinedExport_MuxStageFault_SurfacesTheOriginalException()
    {
        // The progress callback runs on the mux thread: a fault there must reach the caller as itself (not as the
        // cancellation it induces in the render stage), and the partial output must still be deleted.
        Project project = ExportFixture.BuildProject(withAudio: true);
        using var output = new TempFile();
        var progress = new InlineProgress(p => { if (p > 0.3) throw new InvalidOperationException("mux boom"); });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            VideoExporter.ExportWithSummary(project, output.Path, default, null, null, progress));
        Assert.Equal("mux boom", ex.Message);
        Assert.False(File.Exists(output.Path));
    }

    [Fact]
    public void Provider_WithPrefetch_ServesTheSameFramesAsWithout()
    {
        // Forward walk, a backward jump (prefetch discarded before the seek), a switch into reverse, and back to
        // forward: the prefetching provider must hand out exactly the frames the synchronous one does.
        ProbedMediaInfo info = ExportFixture.Probe();
        var requests = new List<(Timecode Time, bool Reverse)>();
        for (int i = 0; i < 20; i++) requests.Add((Timecode.FromFrames(i, info.FrameRate), false));
        for (int i = 5; i < 12; i++) requests.Add((Timecode.FromFrames(i, info.FrameRate), false));
        for (int i = 25; i > 14; i--) requests.Add((Timecode.FromFrames(i, info.FrameRate), true));
        for (int i = 3; i < 9; i++) requests.Add((Timecode.FromFrames(i, info.FrameRate), false));

        List<long> expected = Walk(prefetch: false);
        List<long> actual = Walk(prefetch: true);
        Assert.Equal(expected, actual);

        List<long> Walk(bool prefetch)
        {
            using var provider = new ExportFrameProvider(
                MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled), prefetch: prefetch);
            var pts = new List<long>();
            foreach ((Timecode time, bool reverse) in requests)
                pts.Add(provider.GetFrame(time, reverse)?.Pts.Ticks ?? -1);
            if (!prefetch) // synchronous: every decode blocks the caller (with prefetch, a wait also covers task scheduling)
                Assert.Equal(provider.DecodeElapsed, provider.BlockingDecodeElapsed);
            return pts;
        }
    }

    [Fact]
    public void Provider_FirstRequestBetweenSourceFrames_ServesTheFrameAtOrBefore()
    {
        // A render worker's first request lands mid-stream, usually between two source frames (e.g. a 60 fps export
        // of 30 fps media). The seek must serve the latest frame at/before the request — what a walk from zero
        // serves — not the next frame the decoder's seek lands on.
        ProbedMediaInfo info = ExportFixture.Probe();
        Timecode frame = Timecode.FromFrames(1, info.FrameRate);
        Timecode between = Timecode.FromFrames(7, info.FrameRate) + Timecode.FromTicks(frame.Ticks / 2);

        using var walked = new ExportFrameProvider(MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled));
        for (int i = 0; i < 7; i++)
            walked.GetFrame(Timecode.FromFrames(i, info.FrameRate));
        long expected = walked.GetFrame(between)!.Pts.Ticks;

        using var fresh = new ExportFrameProvider(MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled));
        Assert.Equal(expected, fresh.GetFrame(between)!.Pts.Ticks);
        Assert.True(expected <= between.Ticks);
    }

    private static (Project, ExportOptions) Build(string scenario)
    {
        switch (scenario)
        {
            case "effect+audio":
                return (ExportFixture.BuildProject(withAudio: true, brightness: 0.25), default);

            case "backward-cut":
            {
                // Two clips of one source: the second starts earlier in the source than the first ended, so the
                // shared provider must seek backwards at the cut.
                Project project = ExportFixture.BuildProject(withAudio: false);
                VideoTrack track = project.Timeline.VideoTracks.First();
                Clip whole = track.Clips[0];
                Timecode half = Timecode.FromSeconds(0.5);
                track.Clips.Clear();
                track.Clips.Add(new Clip(whole.MediaRefId, Timecode.FromSeconds(0.4), Timecode.FromSeconds(0.9), Timecode.Zero));
                track.Clips.Add(new Clip(whole.MediaRefId, Timecode.FromSeconds(0.1), Timecode.FromSeconds(0.6), half));
                return (project, default);
            }

            case "reverse":
            {
                Project project = ExportFixture.BuildProject(withAudio: false);
                project.Timeline.VideoTracks.First().Clips[0].Reverse = true;
                return (project, default);
            }

            case "held-frames":
                return (ExportFixture.BuildProject(withAudio: true), new ExportOptions(FrameRate: new Rational(60, 1)));

            case "same-source-transition":
            {
                Project project = ExportFixture.BuildProject(withAudio: false);
                VideoTrack track = project.Timeline.VideoTracks.First();
                Clip whole = track.Clips[0];
                Timecode half = Timecode.FromSeconds(0.5);
                track.Clips.Clear();
                track.Clips.Add(new Clip(whole.MediaRefId, Timecode.Zero, half, Timecode.Zero));
                track.Clips.Add(new Clip(whole.MediaRefId, Timecode.FromSeconds(0.2), Timecode.FromSeconds(0.7), half));
                track.Transitions.Add(new Transition(TransitionTypeIds.CrossDissolve, half, Timecode.FromSeconds(0.2)));
                return (project, default);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>An <see cref="IProgress{T}"/> that runs its callback synchronously on the reporting thread
    /// (<see cref="Progress{T}"/> would post it to the thread pool).</summary>
    private sealed class InlineProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
