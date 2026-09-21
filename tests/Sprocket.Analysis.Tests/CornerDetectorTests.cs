using System.Numerics;
using Sprocket.Analysis.Features;
using Xunit;

namespace Sprocket.Analysis.Tests;

public class CornerDetectorTests
{
    [Fact]
    public void Detect_FindsCornersSpreadAcrossBuckets()
    {
        GrayBuffer img = TestImages.Reference(320, 240);
        var detector = new CornerDetector();
        const int bx = 8, by = 6, cap = 7;
        var corners = new Vector2[bx * by * cap];

        int count = detector.Detect(img.View, bx, by, cap, corners);

        Assert.True(count > 60, $"expected a healthy corner count, got {count}");

        // Corners should occupy most buckets, not clump in one region.
        var occupied = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int cx = Math.Min(bx - 1, (int)corners[i].X * bx / img.Width);
            int cy = Math.Min(by - 1, (int)corners[i].Y * by / img.Height);
            occupied.Add(cy * bx + cx);
        }
        Assert.True(occupied.Count >= bx * by / 2, $"corners only covered {occupied.Count} of {bx * by} buckets");
    }

    [Fact]
    public void Detect_RespectsPerBucketCap()
    {
        GrayBuffer img = TestImages.Reference(320, 240);
        var detector = new CornerDetector();
        const int bx = 4, by = 3, cap = 3;
        var corners = new Vector2[bx * by * cap];

        int count = detector.Detect(img.View, bx, by, cap, corners);

        var perBucket = new int[bx * by];
        for (int i = 0; i < count; i++)
        {
            int cx = Math.Min(bx - 1, (int)corners[i].X * bx / img.Width);
            int cy = Math.Min(by - 1, (int)corners[i].Y * by / img.Height);
            perBucket[cy * bx + cx]++;
        }
        Assert.All(perBucket, c => Assert.True(c <= cap));
    }

    [Fact]
    public void Detect_FlatImage_ReturnsNothing()
    {
        var flat = new GrayBuffer(new byte[320 * 240], 320, 240);   // all zero ⇒ no gradient
        var detector = new CornerDetector();
        var corners = new Vector2[8 * 6 * 7];

        int count = detector.Detect(flat.View, 8, 6, 7, corners);

        Assert.Equal(0, count);
    }
}
