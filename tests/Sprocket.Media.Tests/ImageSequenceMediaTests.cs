using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Decode/probe of image sequences and stills through the <c>image2</c> demuxer open path (PLAN.md step 42):
/// an image sequence decodes like any video once opened at a chosen frame rate; a single still probes with its
/// alpha intact and is served as one held frame by <see cref="StillFrameSource"/>.
/// </summary>
public class ImageSequenceMediaTests
{
    private static MediaOpenRequest SequenceRequest(Rational fps)
    {
        var media = new MediaRef(MediaRefId.New(), System.IO.Path.Combine(TestVideo.SequenceDir, "frame_0001.png"),
            new ProbedMediaInfo(Timecode.Zero, HasVideo: true, fps, 0, 0, false, 0, 0))
        {
            Kind = MediaKind.ImageSequence,
            SequencePattern = TestVideo.SequencePattern,
            SequenceStartNumber = 1,
            SequenceFrameCount = TestVideo.SequenceFrameCount,
        };
        return MediaOpenRequest.FromMediaRef(media);
    }

    [Fact]
    public void ImageSequenceOpensAndReportsDimensions()
    {
        using MediaSource source = MediaSource.Open(SequenceRequest(new Rational(12, 1)));
        Assert.True(source.Info.HasVideo);
        Assert.Equal(TestVideo.ImageWidth, source.Info.Width);
        Assert.Equal(TestVideo.ImageHeight, source.Info.Height);
    }

    [Fact]
    public void ImageSequenceDecodesEveryFrame()
    {
        using MediaSource source = MediaSource.Open(SequenceRequest(new Rational(12, 1)));
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);

        int frames = 0;
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            using (frame)
                frames++;
        }
        Assert.Equal(TestVideo.SequenceFrameCount, frames);
    }

    [Fact]
    public void SingleStillProbesWithAlpha()
    {
        ProbedMediaInfo info = MediaSource.ProbeInfo(MediaOpenRequest.ForPath(TestVideo.StillPath));
        Assert.True(info.HasVideo);
        Assert.Equal(TestVideo.ImageWidth, info.Width);
        Assert.Equal(TestVideo.ImageHeight, info.Height);
        Assert.True(info.HasAlpha); // authored as rgba
    }

    [Fact]
    public void StillFrameSourceServesTheHeldFrameForEveryTime()
    {
        using var still = StillFrameSource.Decode(MediaOpenRequest.ForPath(TestVideo.StillPath));
        Assert.Equal(TestVideo.ImageWidth, still.Width);
        Assert.Equal(TestVideo.ImageHeight, still.Height);

        // Each RentCopy is an independent owned frame with the same content; disposing one must not disturb another.
        using VideoFrame a = still.RentCopy();
        using VideoFrame b = still.RentCopy();
        Assert.Equal(still.Width, a.Width);
        Assert.Equal(still.Height, b.Height);
        Assert.True(a.HasAlpha);
        Assert.NotEqual(a.Pixels, b.Pixels); // distinct native buffers
    }
}
