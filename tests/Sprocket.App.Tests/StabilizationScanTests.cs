using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sprocket.App.Stabilization;
using Sprocket.Core.Model;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Phase-6 tests for the project-wide stabilization enumeration (<see cref="StabilizationScan"/>), the export
/// pre-check (<see cref="StabilizationExportPrecheck"/>), and the auto-analyze-on-apply / stale-on-trim / export
/// worker-pause behaviors those feed. The <see cref="IMotionAnalyzer"/> seam keeps it ffmpeg-free.
/// </summary>
[Collection("Stabilization analysis cache")]
public sealed class StabilizationScanTests : IDisposable
{
    private const int Timeout = 10_000;
    private readonly string _root;

    public StabilizationScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sprocket-stabscan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "cache"));
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        Environment.SetEnvironmentVariable("SPROCKET_ANALYSIS_DIR", Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SPROCKET_ANALYSIS_DIR", null);
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    // ── Enumeration ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StabilizedClips_finds_media_clips_with_an_enabled_stabilization_effect()
    {
        (Project project, Clip stabilized, _) = BuildProject();

        List<StabilizationScan.Item> items = StabilizationScan.StabilizedClips(project).ToList();

        StabilizationScan.Item item = Assert.Single(items);
        Assert.Same(stabilized, item.Clip);
        Assert.False(item.Detailed);
    }

    [Fact]
    public void StabilizedClips_skips_a_disabled_stabilization_effect()
    {
        (Project project, Clip stabilized, _) = BuildProject();
        stabilized.Effects.Single(e => e.EffectTypeId == EffectTypeIds.Stabilization).Enabled = false;

        Assert.Empty(StabilizationScan.StabilizedClips(project));
    }

    [Fact]
    public void StabilizedClips_ignores_a_clip_whose_source_left_the_pool()
    {
        (Project project, Clip stabilized, _) = BuildProject();
        project.MediaPool.Remove(stabilized.MediaRefId);

        Assert.Empty(StabilizationScan.StabilizedClips(project));
    }

    // ── Export pre-check ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unanalyzed_lists_a_stabilized_clip_whose_analysis_is_not_ready()
    {
        (Project project, _, _) = BuildProject();

        var notReady = StabilizationExportPrecheck.Unanalyzed(project, (_, _) => AnalysisState.NotAnalyzed);
        Assert.Single(notReady);

        var allReady = StabilizationExportPrecheck.Unanalyzed(project, (_, _) => AnalysisState.Ready);
        Assert.Empty(allReady);
    }

    [Fact]
    public void Unanalyzed_dedupes_by_source_and_detail()
    {
        (Project project, Clip stabilized, VideoTrack track) = BuildProject();
        // A second clip of the SAME source, also stabilized, in the same detail — one pending entry, not two.
        Clip second = new(stabilized.MediaRefId, Timecode.FromSeconds(3), Timecode.FromSeconds(5), Timecode.FromSeconds(10));
        second.Effects.Add(EffectCatalog.Find(EffectTypeIds.Stabilization)!.CreateInstance());
        track.Clips.Add(second);

        var notReady = StabilizationExportPrecheck.Unanalyzed(project, (_, _) => AnalysisState.NotAnalyzed);
        Assert.Single(notReady);
    }

    // ── Auto-analyze on apply / stale on trim ────────────────────────────────────────────────────────

    [Fact]
    public void Auto_analyze_enqueues_each_stabilized_clip_then_a_re_trim_beyond_the_bucket_re_analyzes()
    {
        (Project project, Clip stabilized, _) = BuildProject();
        using var fake = new InstantAnalyzer();
        using var service = new StabilizationService(fake);

        // Mimic MainWindow.AutoAnalyzeStabilizations' dedup: one enqueue per (source, detail, bucketed range).
        var seen = new HashSet<(MediaRefId, bool, long, long)>();
        void Sweep()
        {
            foreach (StabilizationScan.Item item in StabilizationScan.StabilizedClips(project))
            {
                (Timecode s, Timecode e) = AnalysisKey.BucketRange(item.Clip.SourceIn, item.Clip.SourceOut);
                if (seen.Add((item.Media.Id, item.Detailed, s.Ticks, e.Ticks)))
                    service.Analyze(item.Media, item.Clip.SourceIn, item.Clip.SourceOut, item.Detailed);
            }
        }

        Sweep();
        WaitFor(() => service.StatusOf(stabilized.MediaRefId, false).State == AnalysisState.Ready);
        Assert.Equal(1, fake.CallCount);

        Sweep(); // an unrelated re-sweep with the same range must not re-enqueue
        Assert.Equal(1, fake.CallCount);

        // Trim well beyond the analysed bucket → a new AnalysisKey → re-analyze.
        stabilized.SourceIn = Timecode.FromSeconds(40);
        stabilized.SourceOut = Timecode.FromSeconds(43);
        Sweep();
        WaitFor(() => fake.CallCount == 2);
        Assert.Equal(2, fake.CallCount);
    }

    // ── Export worker pause ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetPaused_parks_new_work_and_resume_runs_it()
    {
        MediaRef media = NewMedia("clip.mp4");
        using var fake = new InstantAnalyzer();
        using var service = new StabilizationService(fake);

        service.SetPaused(true);
        service.Analyze(media, Timecode.Zero, Timecode.FromSeconds(2), detailed: false);

        // Paused: the item sits queued; the worker never starts it.
        Assert.True(service.Paused);
        Thread.Sleep(100);
        Assert.Equal(AnalysisState.Queued, service.StatusOf(media.Id, false).State);
        Assert.Equal(0, fake.CallCount);

        service.SetPaused(false);
        WaitFor(() => service.StatusOf(media.Id, false).State == AnalysisState.Ready);
        Assert.Equal(1, fake.CallCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private (Project Project, Clip Stabilized, VideoTrack Track) BuildProject()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var track = new VideoTrack { Name = "V1" };
        timeline.Tracks.Add(track);
        var project = new Project(timeline);

        MediaRef media = NewMedia("clip.mp4");
        project.MediaPool.Add(media);

        var clip = new Clip(media.Id, Timecode.Zero, Timecode.FromSeconds(2), Timecode.Zero);
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Stabilization)!.CreateInstance());
        track.Clips.Add(clip);

        return (project, clip, track);
    }

    private MediaRef NewMedia(string fileName)
    {
        string path = Path.Combine(_root, "media", fileName);
        File.WriteAllBytes(path, new byte[128]);
        return new MediaRef(MediaRefId.New(), path,
            new ProbedMediaInfo(Timecode.FromSeconds(60), true, new Rational(30, 1), 1920, 1080, false, 0, 0));
    }

    private void WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < Timeout)
        {
            if (condition())
                return;
            Thread.Sleep(5);
        }
        Assert.True(condition(), "condition not met within the timeout");
    }

    /// <summary>A non-blocking analyzer that returns a small valid track immediately.</summary>
    private sealed class InstantAnalyzer : IMotionAnalyzer, IDisposable
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public MotionTrack Analyze(
            MediaOpenRequest request, string sourceIdentity, Timecode from, Timecode to,
            StabilizationSettings settings, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var pts = new List<long> { from.Ticks, from.Ticks + 1000, from.Ticks + 2000 };
            var motions = new List<FrameMotion>
            {
                FrameMotion.Identity,
                new(1, 1, 0, 0, Homography.Identity, 1.0, 40),
                new(2, 2, 0, 0, Homography.Identity, 1.0, 40),
            };
            return new MotionTrack(sourceIdentity, settings.DetailedAnalysis, from, to, new Rational(30, 1), pts, motions);
        }

        public void Dispose() { }
    }
}
