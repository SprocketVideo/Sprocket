using System;
using System.Collections.Generic;
using System.IO;
using Sprocket.App.Stabilization;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The per-user motion-analysis cache (plan/features/stabilization.md phase 5): a content-hash-keyed store of
/// recovered <see cref="MotionTrack"/>s, the analysis analogue of <see cref="Sprocket.App.Proxy.ProxyCache"/>.
/// Pins the round-trip, the deterministic path from an <see cref="AnalysisKey"/>, the bucketing re-use (two trims
/// within a bucket resolve to the same file), and best-effort clearing.
/// </summary>
[Collection("Stabilization analysis cache")]
public sealed class AnalysisCacheTests : IDisposable
{
    private readonly string _root;

    public AnalysisCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sprocket-analysis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("SPROCKET_ANALYSIS_DIR", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SPROCKET_ANALYSIS_DIR", null);
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public void Write_then_read_round_trips_the_track()
    {
        var key = new AnalysisKey("id|100|200", Detailed: false, new Timecode(0), new Timecode(Timecode.TicksPerSecond));
        MotionTrack track = SampleTrack("id|100|200");

        AnalysisCache.Write(key, track);
        MotionTrack? read = AnalysisCache.TryRead(key);

        Assert.NotNull(read);
        Assert.Equal(track.FrameCount, read!.FrameCount);
        Assert.Equal(track.SourceIdentity, read.SourceIdentity);
        Assert.Equal(track.FramePts, read.FramePts);
    }

    [Fact]
    public void A_missing_entry_reads_as_null()
    {
        var key = new AnalysisKey("nope", Detailed: true, new Timecode(0), new Timecode(1));
        Assert.Null(AnalysisCache.TryRead(key));
    }

    [Fact]
    public void PathFor_is_deterministic_and_lands_in_the_cache_dir()
    {
        var key = new AnalysisKey("id|1|2", Detailed: false, new Timecode(0), new Timecode(5 * Timecode.TicksPerSecond));

        string a = AnalysisCache.PathFor(key);
        string b = AnalysisCache.PathFor(key);

        Assert.Equal(a, b);
        Assert.Equal(_root, Path.GetDirectoryName(a));
        Assert.EndsWith(".spmt", a);
    }

    [Fact]
    public void Two_trims_within_the_same_bucket_resolve_to_the_same_cache_file()
    {
        // AnalysisKey pads by ±2 s handles and rounds out to 5 s buckets, so nearby trims share one analysis.
        Timecode a1 = Timecode.FromSeconds(3), a2 = Timecode.FromSeconds(4);
        Timecode b1 = Timecode.FromSeconds(3.2), b2 = Timecode.FromSeconds(4.1);

        AnalysisKey k1 = AnalysisKey.ForClipRange("src", detailed: false, a1, a2);
        AnalysisKey k2 = AnalysisKey.ForClipRange("src", detailed: false, b1, b2);

        Assert.Equal(AnalysisCache.PathFor(k1), AnalysisCache.PathFor(k2));
    }

    [Fact]
    public void The_detailed_flag_forks_the_cache_file()
    {
        Timecode in0 = Timecode.FromSeconds(3), out0 = Timecode.FromSeconds(4);
        AnalysisKey standard = AnalysisKey.ForClipRange("src", detailed: false, in0, out0);
        AnalysisKey detailed = AnalysisKey.ForClipRange("src", detailed: true, in0, out0);

        Assert.NotEqual(AnalysisCache.PathFor(standard), AnalysisCache.PathFor(detailed));
    }

    [Fact]
    public void Delete_all_empties_the_cache()
    {
        AnalysisCache.Write(
            new AnalysisKey("a", false, new Timecode(0), new Timecode(1)), SampleTrack("a"));
        AnalysisCache.Write(
            new AnalysisKey("b", false, new Timecode(0), new Timecode(1)), SampleTrack("b"));

        int deleted = AnalysisCache.DeleteAll();

        Assert.Equal(2, deleted);
        Assert.Equal(0, AnalysisCache.SizeBytes());
    }

    private static MotionTrack SampleTrack(string identity)
    {
        var pts = new List<long> { 0, 1000, 2000 };
        var motions = new List<FrameMotion>
        {
            FrameMotion.Identity,
            new(1, 2, 0, 0, Homography.Identity, 1.0, 40),
            new(2, 4, 0, 0, Homography.Identity, 1.0, 40),
        };
        return new MotionTrack(
            identity, detailedAnalysis: false, new Timecode(0), new Timecode(2000),
            new Rational(30, 1), pts, motions);
    }
}
