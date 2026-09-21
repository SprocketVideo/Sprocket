using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Sprocket.App.Stabilization;
using Sprocket.Core.Model;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the stabilization analysis service's queue / cancel / generation-fencing state machine
/// (plan/features/stabilization.md phase 5), the load-bearing half behind the Inspector's Analyze row. The
/// <see cref="IMotionAnalyzer"/> seam is what makes these testable without <c>ffmpeg</c>: the fake below can block
/// mid-analysis, report progress, observe cancellation, and be released late to stage a stale completion landing
/// after the entry moved on.
/// </summary>
[Collection("Stabilization analysis cache")]
public sealed class StabilizationServiceTests : IDisposable
{
    private const int Timeout = 10_000;

    private readonly string _root;

    public StabilizationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sprocket-stab-tests", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Analyze_queues_then_completes_and_the_track_becomes_available()
    {
        MediaRef media = NewMedia("clip.mp4");
        using var fake = new FakeAnalyzer();
        using var service = new StabilizationService(fake);
        var changed = new ConcurrentBag<MediaRefId>();
        service.TrackChanged += id => changed.Add(id);

        Assert.Null(service.TryGetTrack(media.Id, Timecode.Zero, detailed: false)); // nothing before analysis

        service.Analyze(media, Timecode.Zero, Timecode.FromSeconds(2), detailed: false);

        WaitFor(() => service.StatusOf(media.Id, false).State == AnalysisState.Ready);
        WaitFor(() => changed.Contains(media.Id)); // TrackChanged fires just after the state flips
        Assert.NotNull(service.TryGetTrack(media.Id, Timecode.Zero, detailed: false));
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public void A_cached_track_is_adopted_without_re_analysing()
    {
        MediaRef media = NewMedia("clip.mp4");
        Timecode in0 = Timecode.Zero, out0 = Timecode.FromSeconds(2);

        // Pre-seed the cache with the exact key the service will compute.
        AnalysisKey key = AnalysisKey.ForClipRange(SourceIdentity.For(media), detailed: false, in0, out0);
        AnalysisCache.Write(key, FakeAnalyzer.BuildTrack(SourceIdentity.For(media), detailed: false, in0, out0));

        using var fake = new FakeAnalyzer();
        using var service = new StabilizationService(fake);

        service.Analyze(media, in0, out0, detailed: false);

        // Adopted synchronously on the calling thread — Ready at once, analyzer never invoked.
        Assert.Equal(AnalysisState.Ready, service.StatusOf(media.Id, false).State);
        Assert.NotNull(service.TryGetTrack(media.Id, Timecode.Zero, detailed: false));
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public void Analyze_is_idempotent_for_an_already_running_analysis_of_the_same_range()
    {
        MediaRef media = NewMedia("clip.mp4");
        using var fake = new FakeAnalyzer { BlockUntilReleased = true };
        using var service = new StabilizationService(fake);

        service.Analyze(media, Timecode.Zero, Timecode.FromSeconds(2), detailed: false);
        Assert.True(fake.WaitForStart(), "the analysis never started");

        service.Analyze(media, Timecode.Zero, Timecode.FromSeconds(2), detailed: false); // same range → no-op

        fake.BlockUntilReleased = false;
        fake.Release();
        WaitFor(() => service.StatusOf(media.Id, false).State == AnalysisState.Ready);
        Assert.Equal(1, fake.CallCount); // not re-queued
    }

    [Fact]
    public void Cancel_stops_an_in_flight_analysis_and_reverts_to_not_analyzed()
    {
        MediaRef media = NewMedia("clip.mp4");
        using var fake = new FakeAnalyzer { BlockUntilReleased = true };
        using var service = new StabilizationService(fake);

        service.Analyze(media, Timecode.Zero, Timecode.FromSeconds(2), detailed: false);
        Assert.True(fake.WaitForStart(), "the analysis never started");
        Assert.Equal(AnalysisState.Analyzing, service.StatusOf(media.Id, false).State);

        service.Cancel(media.Id, detailed: false);

        WaitFor(() => service.StatusOf(media.Id, false).State == AnalysisState.NotAnalyzed);
        Assert.Null(service.TryGetTrack(media.Id, Timecode.Zero, detailed: false));
    }

    [Fact]
    public void A_stale_completion_after_a_re_analyse_is_discarded_and_the_new_one_lands()
    {
        // Generation fencing: re-analysing a different range while the first is still running must not let the
        // first (now stale) result overwrite the new state; IgnoreCancellation lets the first run to completion.
        MediaRef media = NewMedia("clip.mp4");
        using var fake = new FakeAnalyzer { BlockUntilReleased = true, IgnoreCancellation = true };
        using var service = new StabilizationService(fake);

        service.Analyze(media, Timecode.Zero, Timecode.FromSeconds(2), detailed: false);
        Assert.True(fake.WaitForStart(), "the first analysis never started");

        // A range in a different bucket → a different AnalysisKey, so this supersedes rather than being a no-op.
        service.Analyze(media, Timecode.FromSeconds(20), Timecode.FromSeconds(22), detailed: false);

        fake.Release();                                  // the stale first analysis completes — must be discarded
        Assert.True(fake.WaitForStart(), "the re-queued analysis never started");
        fake.BlockUntilReleased = false;
        fake.Release();

        WaitFor(() => service.StatusOf(media.Id, false).State == AnalysisState.Ready);
        Assert.Equal(2, fake.CallCount);
        Assert.NotNull(service.TryGetTrack(media.Id, Timecode.Zero, detailed: false));
    }

    [Fact]
    public void The_detailed_pass_is_tracked_independently_of_the_standard_pass()
    {
        MediaRef media = NewMedia("clip.mp4");
        using var fake = new FakeAnalyzer();
        using var service = new StabilizationService(fake);

        service.Analyze(media, Timecode.Zero, Timecode.FromSeconds(2), detailed: false);
        WaitFor(() => service.StatusOf(media.Id, false).State == AnalysisState.Ready);

        Assert.Equal(AnalysisState.NotAnalyzed, service.StatusOf(media.Id, detailed: true).State);
        Assert.Null(service.TryGetTrack(media.Id, Timecode.Zero, detailed: true));
    }

    [Fact]
    public void ShouldPostProgress_rate_limits_ticks_to_the_throttle_interval()
    {
        Assert.False(StabilizationService.ShouldPostProgress(1_000, 1_000));
        Assert.False(StabilizationService.ShouldPostProgress(1_000 + StabilizationService.ProgressThrottleMs - 1, 1_000));
        Assert.True(StabilizationService.ShouldPostProgress(1_000 + StabilizationService.ProgressThrottleMs, 1_000));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private MediaRef NewMedia(string fileName)
    {
        string path = Path.Combine(_root, "media", fileName);
        File.WriteAllBytes(path, new byte[128]);
        return new MediaRef(MediaRefId.New(), path,
            new ProbedMediaInfo(Timecode.FromSeconds(30), true, new Rational(30, 1), 1920, 1080, false, 0, 0));
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

    /// <summary>A scriptable stand-in for the real motion analyzer: it can block until released (to catch an
    /// analysis mid-flight), report progress, honour or deliberately ignore cancellation (the latter stages a
    /// stale completion), and returns a small valid track.</summary>
    private sealed class FakeAnalyzer : IMotionAnalyzer, IDisposable
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _release = new(0);
        private int _calls;

        public volatile bool BlockUntilReleased;
        public bool IgnoreCancellation { get; init; }
        public int ProgressTicks { get; init; } = 2;

        public int CallCount => Volatile.Read(ref _calls);

        public bool WaitForStart(int timeoutMs = Timeout) => _started.Wait(timeoutMs);
        public void Release() => _release.Release();

        public MotionTrack Analyze(
            MediaOpenRequest request, string sourceIdentity, Timecode from, Timecode to,
            StabilizationSettings settings, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _started.Release();

            for (int i = 1; i <= ProgressTicks; i++)
                progress?.Report((double)i / (ProgressTicks + 1));

            if (BlockUntilReleased)
            {
                while (!_release.Wait(5))
                    if (cancellationToken.IsCancellationRequested && !IgnoreCancellation)
                        cancellationToken.ThrowIfCancellationRequested();
            }

            if (cancellationToken.IsCancellationRequested && !IgnoreCancellation)
                cancellationToken.ThrowIfCancellationRequested();

            return BuildTrack(sourceIdentity, settings.DetailedAnalysis, from, to);
        }

        public static MotionTrack BuildTrack(string identity, bool detailed, Timecode from, Timecode to)
        {
            var pts = new List<long> { from.Ticks, from.Ticks + 1000, from.Ticks + 2000 };
            var motions = new List<FrameMotion>
            {
                FrameMotion.Identity,
                new(1, 1, 0, 0, Homography.Identity, 1.0, 40),
                new(2, 2, 0, 0, Homography.Identity, 1.0, 40),
            };
            return new MotionTrack(identity, detailed, from, to, new Rational(30, 1), pts, motions);
        }

        public void Dispose()
        {
            _started.Dispose();
            _release.Dispose();
        }
    }
}
