using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Reverse and ramped retime on the export path (PLAN.md step 21 remainder): a reversed clip's exported frame k is
/// the source's frame N−1−k (served through the export provider's GOP window), and a speed-ramped clip exports the
/// duration its integrated map derives. Frames are compared by mean absolute pixel difference against the source
/// decoded directly, so the check survives the lossy encode.
/// </summary>
public sealed class ReverseExportTests
{
    private static unsafe List<byte[]> DecodeAllFrames(string path)
    {
        using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        var frames = new List<byte[]>();
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            using (frame)
            {
                var pixels = new byte[frame.Width * frame.Height * 3];
                var p = (byte*)frame.Pixels;
                int i = 0;
                for (int y = 0; y < frame.Height; y++)
                {
                    byte* row = p + (long)y * frame.RowBytes;
                    for (int x = 0; x < frame.Width; x++)
                    {
                        pixels[i++] = row[x * 4];
                        pixels[i++] = row[x * 4 + 1];
                        pixels[i++] = row[x * 4 + 2];
                    }
                }
                frames.Add(pixels);
            }
        }
        return frames;
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            sum += Math.Abs(a[i] - b[i]);
        return n == 0 ? 0 : (double)sum / n;
    }

    [Fact]
    public void Reversed_Clip_Exports_The_Source_Frames_In_Descending_Order()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);
        Clip clip = project.Timeline.VideoTracks.First().Clips[0];
        clip.Reverse = true;

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path);

        List<byte[]> source = DecodeAllFrames(ExportFixture.SourcePath);
        List<byte[]> exported = DecodeAllFrames(output.Path);
        int n = source.Count;
        Assert.InRange(exported.Count, n - 2, n + 2);

        // Exported frame k must resemble source frame n−1−k far more than source frame k (the forward order).
        // testsrc2's moving elements make distinct frames clearly distinguishable even after the lossy encode.
        foreach (int k in new[] { 0, 3, 7, 12, 20, Math.Min(exported.Count, n) - 3 })
        {
            int mirrored = n - 1 - k;
            if (mirrored == k || k >= exported.Count)
                continue;
            double reversed = MeanAbsDiff(exported[k], source[mirrored]);
            double forward = MeanAbsDiff(exported[k], source[k]);
            Assert.True(reversed < forward * 0.5,
                $"frame {k}: expected source frame {mirrored} (diff {reversed:0.00}) rather than {k} (diff {forward:0.00})");
        }
    }

    [Fact]
    public void Ramped_Clip_Exports_Its_Integrated_Duration()
    {
        Project project = ExportFixture.BuildProject(withAudio: false);
        Clip clip = project.Timeline.VideoTracks.First().Clips[0];
        // 2× for the first 0.25 s of the clip, then 0.5×: a 1 s source spans 0.25 + (1 − 0.5)/0.5 = 1.25 s.
        clip.SpeedCurve = AnimatableValue.Animated(
        [
            new Keyframe(Timecode.Zero, 2.0, Interpolation.Hold),
            new Keyframe(Timecode.FromSeconds(0.25), 0.5, Interpolation.Hold),
        ]);
        Assert.Equal(Timecode.FromSeconds(1.25), clip.Duration);

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path);

        int frames = ExportProbe.CountVideoFrames(output.Path);
        // 1.25 s at 30 fps ≈ 37–38 frames; the container/encoder may pad or trim an edge frame.
        Assert.InRange(frames, 35, 40);
    }
}
