using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Unit coverage for <see cref="MediaEncoder"/> input validation. The 4:2:0 H.264 encoder requires even output
/// dimensions; libx264 rejects odd sizes at open and (via Sdcb) a failed open crashed the process during cleanup,
/// so <see cref="MediaEncoder.Create"/> rejects odd dimensions up front with a clear managed exception instead.
/// </summary>
public class MediaEncoderTests
{
    private static readonly Rational Fps = new(30, 1);

    [Theory]
    [InlineData(1921, 1080)] // odd width
    [InlineData(1920, 1081)] // odd height
    [InlineData(1281, 721)]  // both odd
    public void Create_RejectsOddDimensions_WithoutCrashing(int width, int height)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-enc-{System.Guid.NewGuid():N}.mp4");
        try
        {
            Assert.Throws<System.ArgumentException>(() =>
                MediaEncoder.Create(path, new VideoEncoderSettings(width, height, Fps)));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
