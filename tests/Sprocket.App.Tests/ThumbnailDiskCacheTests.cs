using System;
using System.IO;
using System.Linq;
using Sprocket.App.MediaBrowser;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the bin-thumbnail disk cache (PLAN.md step 15, UI.md §3.3) and the streaming waveform
/// reduction behind it: the key is a stable function of source identity + output shape (reused only when nothing
/// relevant changed), the store round-trips bytes through an injected temp root, the prune trims oldest-first, and
/// <see cref="WaveformPeakAccumulator"/> matches <see cref="WaveformBuilder.BuildPeaks"/> exactly. No FFmpeg.
/// </summary>
public sealed class ThumbnailDiskCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sprocket-thumbs-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ── Key ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Key_Is_Stable_For_Identical_Inputs_And_Path_Normalised()
    {
        string a = ThumbnailDiskCache.Key("poster", @"C:\Media\Clip.mp4", 1000, 12345, 104, 58, 0);
        string b = ThumbnailDiskCache.Key("poster", "c:/media/clip.mp4", 1000, 12345, 104, 58, 0);
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA-256 hex
        Assert.True(a.All(Uri.IsHexDigit));
    }

    [Theory]
    [InlineData("wave", @"C:\media\clip.mp4", 1000, 12345, 104, 58, 0)]  // different kind
    [InlineData("poster", @"C:\media\other.mp4", 1000, 12345, 104, 58, 0)] // different path
    [InlineData("poster", @"C:\media\clip.mp4", 2000, 12345, 104, 58, 0)]  // different size
    [InlineData("poster", @"C:\media\clip.mp4", 1000, 99999, 104, 58, 0)]  // different modified time
    [InlineData("poster", @"C:\media\clip.mp4", 1000, 12345, 208, 58, 0)]  // different width
    [InlineData("poster", @"C:\media\clip.mp4", 1000, 12345, 104, 116, 0)] // different height
    [InlineData("poster", @"C:\media\clip.mp4", 1000, 12345, 104, 58, 12)] // different frame count
    public void Key_Changes_When_Any_Component_Changes(string kind, string path, long length, long ticks, int w, int h, int frames)
    {
        string baseline = ThumbnailDiskCache.Key("poster", @"C:\media\clip.mp4", 1000, 12345, 104, 58, 0);
        Assert.NotEqual(baseline, ThumbnailDiskCache.Key(kind, path, length, ticks, w, h, frames));
    }

    [Fact]
    public void KeyFor_Tracks_The_Source_Files_Size_And_Mtime()
    {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "clip.mp4");
        File.WriteAllBytes(file, new byte[10]);
        var media = new MediaRef(new MediaRefId(Guid.NewGuid()), file,
            new ProbedMediaInfo(Timecode.FromSeconds(1), HasVideo: true, new Rational(30, 1), 640, 480, false, 0, 0));

        string? first = ThumbnailDiskCache.KeyFor(media, "poster", 104, 58);
        Assert.NotNull(first);
        Assert.Equal(first, ThumbnailDiskCache.KeyFor(media, "poster", 104, 58));

        File.WriteAllBytes(file, new byte[20]); // re-encoded source: new size
        Assert.NotEqual(first, ThumbnailDiskCache.KeyFor(media, "poster", 104, 58));

        string? sized = ThumbnailDiskCache.KeyFor(media, "poster", 104, 58);
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // touched source
        Assert.NotEqual(sized, ThumbnailDiskCache.KeyFor(media, "poster", 104, 58));
    }

    [Fact]
    public void KeyFor_Is_Null_For_An_Offline_Source()
    {
        var media = new MediaRef(new MediaRefId(Guid.NewGuid()), Path.Combine(_root, "missing.mp4"),
            new ProbedMediaInfo(Timecode.FromSeconds(1), HasVideo: true, new Rational(30, 1), 640, 480, false, 0, 0));
        Assert.Null(ThumbnailDiskCache.KeyFor(media, "poster", 104, 58));
    }

    // ── Store ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_Then_Read_Round_Trips_And_Miss_Returns_Null()
    {
        var cache = new ThumbnailDiskCache(_root);
        string key = ThumbnailDiskCache.Key("poster", "a.mp4", 1, 2, 3, 4, 0);
        Assert.Null(cache.TryRead(key));

        byte[] png = [1, 2, 3, 4, 5];
        cache.TryWrite(key, png);

        Assert.True(File.Exists(cache.PathFor(key)));
        Assert.Equal(png, cache.TryRead(key));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp")); // atomic write leaves no temp behind
    }

    [Fact]
    public void Prune_Deletes_Oldest_First_Down_To_The_Low_Water_Mark()
    {
        var cache = new ThumbnailDiskCache(_root);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++)
        {
            string key = ThumbnailDiskCache.Key("poster", $"{i}.mp4", 1, 2, 3, 4, 0);
            cache.TryWrite(key, new byte[100]);
            File.SetLastWriteTimeUtc(cache.PathFor(key), start.AddMinutes(i));
        }

        Assert.Equal(0, cache.Prune(highWaterBytes: 500, lowWaterBytes: 250)); // at the limit: untouched
        Assert.Equal(3, cache.Prune(highWaterBytes: 450, lowWaterBytes: 250)); // 500 → 200 bytes

        string newest = cache.PathFor(ThumbnailDiskCache.Key("poster", "4.mp4", 1, 2, 3, 4, 0));
        string oldest = cache.PathFor(ThumbnailDiskCache.Key("poster", "0.mp4", 1, 2, 3, 4, 0));
        Assert.True(File.Exists(newest));
        Assert.False(File.Exists(oldest));
    }

    // ── Streaming waveform reduction ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10_000, 104, 8192)]
    [InlineData(1_000, 104, 7)]
    [InlineData(50, 104, 3)] // fewer samples than buckets
    public void PeakAccumulator_Matches_BuildPeaks_For_The_Expected_Count(int count, int buckets, int chunkSize)
    {
        var rng = new Random(42);
        float[] samples = Enumerable.Range(0, count).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();

        var acc = new WaveformPeakAccumulator(buckets, count);
        for (int i = 0; i < count; i += chunkSize)
            acc.Add(samples.AsSpan(i, Math.Min(chunkSize, count - i)));

        Assert.True(acc.IsFull);
        Assert.Equal(WaveformBuilder.BuildPeaks(samples, channels: 1, bucketCount: buckets), acc.Peaks);
    }

    [Fact]
    public void PeakAccumulator_Ignores_Samples_Past_The_Expected_Count()
    {
        var acc = new WaveformPeakAccumulator(bucketCount: 2, expectedFrames: 4);
        acc.Add([0.1f, 0.2f, 0.3f, 0.4f, 1f, 1f]);
        Assert.Equal(new[] { 0.2f, 0.4f }, acc.Peaks);
    }

    [Fact]
    public void ExpectedWaveformSamples_Uses_Duration_Capped_And_Zero_When_Unknown()
    {
        Assert.Equal(96_000, ThumbnailService.ExpectedWaveformSamples(Timecode.FromSeconds(2), 48000));
        Assert.Equal(4_000_000, ThumbnailService.ExpectedWaveformSamples(Timecode.FromSeconds(3600), 48000));
        Assert.Equal(0, ThumbnailService.ExpectedWaveformSamples(Timecode.Zero, 48000));
    }
}
