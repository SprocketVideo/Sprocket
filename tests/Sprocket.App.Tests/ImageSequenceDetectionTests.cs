using System.IO;
using System.Linq;
using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the pure image-sequence detection helper (PLAN.md step 42): given a picked file and its
/// sibling names, find the contiguous numbered run (pattern + start + count), refusing gaps and mixed padding.
/// The IO-reading overload and the import dialog rest on these + manual verification.
/// </summary>
public class ImageSequenceDetectionTests
{
    private static readonly string Dir = Path.Combine("C:", "shot"); // any absolute-ish dir; only its shape matters

    private static string[] Padded(string prefix, int start, int count, int width, string ext)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = $"{prefix}{(start + i).ToString(new string('0', width))}{ext}";
        return names;
    }

    [Fact]
    public void DetectsContiguousPaddedRun()
    {
        string[] names = Padded("frame_", 1, 240, 4, ".png");
        string picked = Path.Combine(Dir, names[0]);

        ImageSequenceInfo? result = ImageSequenceDetection.Detect(picked, names);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Value.StartNumber);
        Assert.Equal(240, result.Value.FrameCount);
        Assert.Equal("frame_%04d.png", Path.GetFileName(result.Value.Pattern));
        Assert.Equal(Path.Combine(Dir, "frame_0001.png"), result.Value.FirstFilePath);
    }

    [Fact]
    public void StopsAtAGapAroundThePickedFrame()
    {
        // 0001..0005, gap at 0006, 0007..0009. Picking 0003 yields the 1..5 run only.
        string[] present = Padded("f", 1, 5, 4, ".png").Concat(Padded("f", 7, 3, 4, ".png")).ToArray();
        ImageSequenceInfo? result = ImageSequenceDetection.Detect(Path.Combine(Dir, "f0003.png"), present);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Value.StartNumber);
        Assert.Equal(5, result.Value.FrameCount);
    }

    [Fact]
    public void MixedPaddingIsRefused()
    {
        // Different digit widths for the same prefix/suffix aren't one run — only the picked file's width counts.
        string[] names = ["img1.png", "img2.png", "img10.png", "img11.png"];
        ImageSequenceInfo? result = ImageSequenceDetection.Detect(Path.Combine(Dir, "img10.png"), names);

        // img10/img11 are width 2 and contiguous → a 2-frame run; the width-1 files are excluded.
        Assert.NotNull(result);
        Assert.Equal(10, result!.Value.StartNumber);
        Assert.Equal(2, result.Value.FrameCount);
    }

    [Fact]
    public void SingleNumberedFileIsNotASequence()
    {
        ImageSequenceInfo? result = ImageSequenceDetection.Detect(Path.Combine(Dir, "frame_0001.png"), ["frame_0001.png"]);
        Assert.Null(result);
    }

    [Fact]
    public void UnnumberedFileIsNotASequence()
    {
        ImageSequenceInfo? result = ImageSequenceDetection.Detect(Path.Combine(Dir, "logo.png"), ["logo.png", "other.png"]);
        Assert.Null(result);
    }

    [Fact]
    public void StartNumberNeedNotBeZeroOrOne()
    {
        string[] names = Padded("clip_", 100, 12, 5, ".jpg");
        ImageSequenceInfo? result = ImageSequenceDetection.Detect(Path.Combine(Dir, "clip_00100.jpg"), names);

        Assert.NotNull(result);
        Assert.Equal(100, result!.Value.StartNumber);
        Assert.Equal(12, result.Value.FrameCount);
        Assert.Equal("clip_%05d.jpg", Path.GetFileName(result.Value.Pattern));
    }

    [Fact]
    public void UsesTheLastDigitRunAsTheFrameNumber()
    {
        // A prefix that itself contains digits: the trailing run is the frame index.
        string[] names = Padded("shot2_frame_", 5, 3, 4, ".tif");
        ImageSequenceInfo? result = ImageSequenceDetection.Detect(Path.Combine(Dir, "shot2_frame_0005.tif"), names);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Value.StartNumber);
        Assert.Equal(3, result.Value.FrameCount);
        Assert.Equal("shot2_frame_%04d.tif", Path.GetFileName(result.Value.Pattern));
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.JPG", true)]
    [InlineData("a.dpx", true)]
    [InlineData("a.exr", false)] // deliberately excluded
    [InlineData("a.mp4", false)]
    [InlineData("a", false)]
    public void IsImagePathMatchesRecognisedExtensions(string name, bool expected) =>
        Assert.Equal(expected, ImageSequenceDetection.IsImagePath(name));
}
