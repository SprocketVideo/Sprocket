using System.Diagnostics;
using System.Runtime.InteropServices;
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

    [Fact]
    public void Exported_Mp4_CarriesSprocketCreationMetadata()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-meta-{System.Guid.NewGuid():N}.mp4");
        try
        {
            // Encode a couple of solid RGBA frames — enough to produce a valid, probe-able MP4.
            const int w = 64, h = 48, rowBytes = w * 4;
            var rgba = new byte[rowBytes * h];
            using (MediaEncoder encoder = MediaEncoder.Create(path, new VideoEncoderSettings(w, h, Fps)))
            {
                GCHandle pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                try
                {
                    nint pixels = pin.AddrOfPinnedObject();
                    for (long i = 0; i < 3; i++)
                        encoder.WriteVideoFrame(pixels, rowBytes, i);
                }
                finally { pin.Free(); }
                encoder.Finish();
            }

            string tags = ProbeFormatTags(path);
            Assert.Contains("Created with Sprocket", tags);
            Assert.Contains("Sprocket", tags); // the `encoder` tag, e.g. "Sprocket 0.1.27"
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ConverterPath_EncodesByteIdentically_ToRawPixelPath()
    {
        // Export-speed phase 3 moves the RGBA → yuv conversion onto the render workers via EncoderVideoConverter /
        // EncoderVideoFrame. It must produce exactly what WriteVideoFrame(nint, …) does, including a source row
        // stride wider than the frame and a frame ring reused across writes.
        const int w = 96, h = 64, rowBytes = w * 4 + 32;
        byte[][] frames = new byte[6][];
        for (int f = 0; f < frames.Length; f++)
        {
            frames[f] = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = y * rowBytes + x * 4;
                    frames[f][o] = (byte)(x * 2 + f * 20);
                    frames[f][o + 1] = (byte)(y * 3 + f * 7);
                    frames[f][o + 2] = (byte)((x ^ y) + f);
                    frames[f][o + 3] = 255;
                }
        }

        string raw = Encode(useConverter: false);
        string converted = Encode(useConverter: true);
        try
        {
            Assert.Equal(File.ReadAllBytes(raw), File.ReadAllBytes(converted));
        }
        finally
        {
            try { File.Delete(raw); File.Delete(converted); } catch { /* best-effort */ }
        }

        string Encode(bool useConverter)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-conv-{System.Guid.NewGuid():N}.mp4");
            using MediaEncoder encoder = MediaEncoder.Create(path, new VideoEncoderSettings(w, h, Fps));
            using EncoderVideoConverter converter = encoder.CreateVideoConverter();
            using EncoderVideoFrame a = encoder.CreateVideoFrame();
            using EncoderVideoFrame b = encoder.CreateVideoFrame();
            for (int f = 0; f < frames.Length; f++)
            {
                GCHandle pin = GCHandle.Alloc(frames[f], GCHandleType.Pinned);
                try
                {
                    nint pixels = pin.AddrOfPinnedObject();
                    if (useConverter)
                    {
                        EncoderVideoFrame target = f % 2 == 0 ? a : b;
                        converter.Convert(pixels, rowBytes, target);
                        encoder.WriteVideoFrame(target, f);
                    }
                    else
                    {
                        encoder.WriteVideoFrame(pixels, rowBytes, f);
                    }
                }
                finally { pin.Free(); }
            }
            encoder.Finish();
            return path;
        }
    }

    [Fact]
    public void ConverterPath_RejectsAFrameFromAnotherEncoder()
    {
        string p1 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-own-{System.Guid.NewGuid():N}.mp4");
        string p2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-own-{System.Guid.NewGuid():N}.mp4");
        try
        {
            using MediaEncoder first = MediaEncoder.Create(p1, new VideoEncoderSettings(64, 48, Fps));
            using MediaEncoder second = MediaEncoder.Create(p2, new VideoEncoderSettings(64, 48, Fps));
            using EncoderVideoFrame foreign = second.CreateVideoFrame();
            using EncoderVideoConverter converter = first.CreateVideoConverter();

            Assert.Throws<ArgumentException>(() => first.WriteVideoFrame(foreign, 0));
            Assert.Throws<ArgumentException>(() => converter.Convert(1, 64 * 4, foreign));
        }
        finally
        {
            try { File.Delete(p1); File.Delete(p2); } catch { /* best-effort */ }
        }
    }

    /// <summary>Reads the container-level metadata tags from <paramref name="path"/> with the <c>ffprobe</c> CLI
    /// (alongside <c>ffmpeg</c> on PATH, per the test prerequisites).</summary>
    private static string ProbeFormatTags(string path)
    {
        var psi = new ProcessStartInfo("ffprobe",
            $"-v error -show_entries format_tags -of default=noprint_wrappers=1 \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffprobe CLI not found on PATH.");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }
}
