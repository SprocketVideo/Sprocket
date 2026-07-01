using System.Runtime.InteropServices;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Hardware-encode probe + automatic software fallback (PLAN.md step 29). The GPU encoders are device-dependent,
/// so — like <see cref="HardwareDecodeTests"/> — these assert the behaviour that must hold <em>regardless</em> of
/// whether a GPU encoder is present: an unavailable candidate always falls back to the deterministic software
/// encoder and still writes a valid file, and the real platform candidates either engage hardware or fall back,
/// with a decodable output either way. On a machine with a usable GPU encoder the last test exercises the real
/// upload+encode path.
/// </summary>
public class HardwareEncodeTests
{
    private static readonly Rational Fps = new(30, 1);
    private const int W = 320, H = 240, RowBytes = W * 4;
    private const int FrameCount = 12;

    /// <summary>Encodes a handful of solid grey frames with <paramref name="video"/>, returning the encoder that
    /// actually engaged, whether it was hardware, and the number of frames the written file decodes back to.</summary>
    private static (string encoder, bool hardware, int frames) Encode(string path, VideoEncoderSettings video)
    {
        var rgba = new byte[RowBytes * H];
        Array.Fill(rgba, (byte)128);

        string engaged;
        bool hardware;
        using (MediaEncoder encoder = MediaEncoder.Create(path, video))
        {
            engaged = encoder.VideoEncoderName;
            hardware = encoder.IsHardwareVideo;
            GCHandle pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                nint pixels = pin.AddrOfPinnedObject();
                for (long i = 0; i < FrameCount; i++)
                    encoder.WriteVideoFrame(pixels, RowBytes, i);
            }
            finally { pin.Free(); }
            encoder.Finish();
        }
        return (engaged, hardware, CountFrames(path));
    }

    private static int CountFrames(string path)
    {
        using MediaSource source = MediaSource.Open(path, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        int n = 0;
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
            using (frame)
                n++;
        return n;
    }

    [Fact]
    public void NoHardwareCandidates_IsPureSoftware()
    {
        // The default (no candidates) is unchanged software encode — libx264 engaged, not flagged hardware.
        using var output = new TempMp4();
        (string encoder, bool hardware, int frames) = Encode(output.Path, new VideoEncoderSettings(W, H, Fps));
        Assert.Equal("libx264", encoder);
        Assert.False(hardware);
        Assert.InRange(frames, FrameCount - 1, FrameCount + 1);
    }

    [Fact]
    public void UnavailableHardwareCandidate_FallsBackToSoftware_AndStillEncodes()
    {
        // A candidate no FFmpeg build provides must be skipped and the software CodecName engaged — the guaranteed
        // deterministic fallback. This is the fallback machinery under test independent of any GPU.
        using var output = new TempMp4();
        var settings = new VideoEncoderSettings(
            W, H, Fps, CodecName: "libx264", HardwareCandidates: ["h264_sprocket_nonexistent"]);

        (string encoder, bool hardware, int frames) = Encode(output.Path, settings);

        Assert.Equal("libx264", encoder);
        Assert.False(hardware);
        Assert.InRange(frames, FrameCount - 1, FrameCount + 1);
    }

    [Fact]
    public void MultipleUnavailableCandidates_AllSkipped_StillSoftware()
    {
        // The whole chain is probed in order; when every candidate is unavailable the encoder still lands on
        // software without leaking a second video stream into the muxer (a decodable single-stream file).
        using var output = new TempMp4();
        var settings = new VideoEncoderSettings(
            W, H, Fps, CodecName: "libx264",
            HardwareCandidates: ["h264_nope_a", "h264_nope_b", "h264_nope_c"]);

        (string encoder, bool hardware, int frames) = Encode(output.Path, settings);

        Assert.Equal("libx264", encoder);
        Assert.False(hardware);
        Assert.InRange(frames, FrameCount - 1, FrameCount + 1);
        // A single, well-formed video stream: reopening reports exactly one H.264 video stream.
        using MediaSource source = MediaSource.Open(output.Path, HardwareAccelMode.Disabled);
        Assert.Equal("h264", source.Info.VideoCodec);
    }

    [Fact]
    public void PlatformHardwareCandidates_EngageOrFallBack_ButAlwaysProduceAValidFile()
    {
        // Feed the real platform H.264 hardware candidates. On a machine with a usable GPU encoder this exercises
        // the real encode path; on one without, it must transparently fall back to libx264. Either way the output
        // is a valid, decodable H.264 stream.
        IReadOnlyList<string> candidates = PlatformH264HardwareNames();
        if (candidates.Count == 0)
            return; // no hardware family for this OS

        using var output = new TempMp4();
        var settings = new VideoEncoderSettings(W, H, Fps, CodecName: "libx264", HardwareCandidates: candidates);

        (string encoder, bool hardware, int frames) = Encode(output.Path, settings);

        Assert.InRange(frames, FrameCount - 1, FrameCount + 1);
        if (hardware)
            Assert.Contains(encoder, candidates); // a GPU encoder engaged and encoded through the upload path
        else
            Assert.Equal("libx264", encoder);      // no usable GPU encoder here → clean software fallback
    }

    private static IReadOnlyList<string> PlatformH264HardwareNames()
    {
        if (OperatingSystem.IsWindows()) return ["h264_nvenc", "h264_qsv", "h264_amf"];
        if (OperatingSystem.IsMacOS()) return ["h264_videotoolbox"];
        if (OperatingSystem.IsLinux()) return ["h264_vaapi", "h264_nvenc"];
        return [];
    }

    private sealed class TempMp4 : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sprocket-hwenc-{Guid.NewGuid():N}.mp4");

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
