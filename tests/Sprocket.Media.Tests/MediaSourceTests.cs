using System.Runtime.InteropServices;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Decode + seek correctness for <see cref="MediaSource"/> against the generated fixture
/// (ARCHITECTURE.md §11, PLAN.md build-order step 3).
/// </summary>
public class MediaSourceTests
{
    // 30 fps → one frame is 240000/30 = 8000 ticks.
    private static readonly Rational FrameRate = new(TestVideo.Fps, 1);
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps;

    private static MediaSource Open() => MediaSource.Open(TestVideo.Path);
    private static VideoFramePool Pool() => new(TestVideo.Width, TestVideo.Height);

    /// <summary>Sequentially decodes the whole clip, returning each frame's PTS in ticks.</summary>
    private static List<long> DecodeAllPtsTicks(MediaSource source, VideoFramePool pool)
    {
        var pts = new List<long>();
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            using (frame)
                pts.Add(frame.Pts.Ticks);
        }
        return pts;
    }

    [Fact]
    public void Open_Probes_Video_And_Audio_Streams()
    {
        using MediaSource source = Open();
        var info = source.Info;

        Assert.True(info.HasVideo);
        Assert.Equal(TestVideo.Width, info.Width);
        Assert.Equal(TestVideo.Height, info.Height);
        Assert.Equal(FrameRate, info.FrameRate);

        // ~3 s, allowing a little slack for container rounding.
        Assert.InRange(info.Duration.ToSeconds(), 2.8, 3.2);

        Assert.True(info.HasAudio);
        Assert.Equal(TestVideo.SampleRate, info.SampleRate);
        Assert.True(info.Channels >= 1);

        // The fixture is yuv420p (no alpha channel), so alpha detection must report false (PLAN.md step 26).
        Assert.False(info.HasAlpha);
    }

    [Fact]
    public void Open_Probes_Source_Format_Details()
    {
        // PLAN.md step 27 import-coverage probe: the fixture is 8-bit 4:2:0 H.264 + AAC, constant-frame-rate, SDR.
        using MediaSource source = Open();
        var info = source.Info;

        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal("yuv420p", info.PixelFormatName);
        Assert.Equal(8, info.BitDepth);
        Assert.False(info.IsHdr);
        Assert.False(info.IsVariableFrameRate); // testsrc2 → x264 is CFR: avg and base frame rates agree
    }

    [Fact]
    public void ProbeInfo_AudioOnlySource_Probes_Without_Video()
    {
        // .m4a import path (PLAN.md step 16b): ProbeInfo must succeed where MediaSource.Open (video-led) throws.
        var info = MediaSource.ProbeInfo(TestVideo.AudioOnlyPath);

        Assert.False(info.HasVideo);
        Assert.Equal(Rational.Zero, info.FrameRate);
        Assert.Equal(0, info.Width);
        Assert.Equal(0, info.Height);
        Assert.Equal("", info.VideoCodec);

        Assert.True(info.HasAudio);
        Assert.Equal(TestVideo.SampleRate, info.SampleRate);
        Assert.True(info.Channels >= 1);
        Assert.Equal("aac", info.AudioCodec);
        Assert.InRange(info.Duration.ToSeconds(), 2.8, 3.2);
    }

    [Fact]
    public void ProbeInfo_VideoSource_Matches_Open()
    {
        // For video-bearing files ProbeInfo delegates to the normal Open path — identical facts.
        using MediaSource source = Open();
        Assert.Equal(source.Info, MediaSource.ProbeInfo(TestVideo.Path));
    }

    [Fact]
    public void Open_AlphaSource_Reports_HasAlpha_On_Info_And_Frames()
    {
        using MediaSource source = MediaSource.Open(TestVideo.AlphaPath);
        Assert.True(source.Info.HasVideo);
        Assert.True(source.Info.HasAlpha); // qtrle stores argb — an alpha pixel format

        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? frame));
        using (frame)
        {
            Assert.True(frame!.HasAlpha); // the flag is carried onto every decoded frame

            // End-to-end: the 50%-alpha source's alpha survives swscale into the pooled RGBA buffer (not forced to
            // opaque 255). Read the centre pixel's alpha byte (RGBA8888 → byte 3 of the pixel).
            int cx = frame.Width / 2, cy = frame.Height / 2;
            int alpha = Marshal.ReadByte(frame.Pixels, cy * frame.RowBytes + cx * 4 + 3);
            Assert.InRange(alpha, TestVideo.AlphaFixtureAlpha - 8, TestVideo.AlphaFixtureAlpha + 8);
        }
    }

    [Fact]
    public void Open_NonExistent_File_Throws()
    {
        Assert.ThrowsAny<Exception>(() => MediaSource.Open("does-not-exist-12345.mp4"));
    }

    [Fact]
    public void DecodeAll_Yields_Expected_Frame_Count_With_Monotonic_Pts()
    {
        using MediaSource source = Open();
        using VideoFramePool pool = Pool();

        List<long> pts = DecodeAllPtsTicks(source, pool);

        // Exactly the generated frame count (CFR clip), first frame at t=0.
        Assert.Equal(TestVideo.FrameCount, pts.Count);
        Assert.Equal(0, pts[0]);

        // Strictly increasing, one frame interval apart.
        for (int i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] > pts[i - 1], $"PTS not increasing at frame {i}");
            Assert.Equal(FrameTicks, pts[i] - pts[i - 1]);
        }
    }

    [Fact]
    public void DecodedFrame_Exposes_A_Native_Rgba_Buffer()
    {
        using MediaSource source = Open();
        using VideoFramePool pool = Pool();

        Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? frame));
        using (frame)
        {
            Assert.NotEqual(IntPtr.Zero, frame!.Pixels);
            Assert.True(frame.RowBytes >= frame.Width * 4, "stride must hold a full RGBA row");
            Assert.Equal(TestVideo.Width, frame.Width);
            Assert.Equal(TestVideo.Height, frame.Height);
        }
    }

    [Fact]
    public void Seek_Lands_Frame_Accurately_On_The_Target_Frame()
    {
        // Reference PTS list from a clean sequential decode.
        List<long> reference;
        using (MediaSource src = Open())
        using (VideoFramePool pool = Pool())
            reference = DecodeAllPtsTicks(src, pool);

        const int targetFrame = 40; // mid-GOP (GOP = 12), so this requires decode-to-target, not just a keyframe
        var target = Timecode.FromFrames(targetFrame, FrameRate);

        using MediaSource source = Open();
        using VideoFramePool pool2 = Pool();
        source.SeekTo(target);

        Assert.True(source.TryDecodeNextFrame(pool2, out VideoFrame? frame));
        using (frame)
            Assert.Equal(reference[targetFrame], frame!.Pts.Ticks);
    }

    [Fact]
    public void Seek_To_Time_Between_Frames_Returns_The_Frame_At_Or_After_Target()
    {
        var target = Timecode.FromSeconds(1.55); // between frame 46 (1.533s) and 47 (1.567s)

        using MediaSource source = Open();
        using VideoFramePool pool = Pool();
        source.SeekTo(target);

        Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? frame));
        using (frame)
        {
            // Decode-to-target: first frame is at or after the target, within one frame interval.
            Assert.True(frame!.Pts >= target, "first frame after seek must be at/after the target");
            Assert.True(frame.Pts < target + new Timecode(FrameTicks), "should be the very next frame, not further");
        }
    }

    [Fact]
    public void Seek_Then_Decode_Continues_Sequentially()
    {
        const int targetFrame = 50;
        var target = Timecode.FromFrames(targetFrame, FrameRate);

        using MediaSource source = Open();
        using VideoFramePool pool = Pool();
        source.SeekTo(target);

        long expected = Timecode.FromFrames(targetFrame, FrameRate).Ticks;
        for (int i = 0; i < 5; i++)
        {
            Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? frame));
            using (frame)
                Assert.Equal(expected, frame!.Pts.Ticks);
            expected += FrameTicks;
        }
    }

    [Fact]
    public void Rewind_Returns_To_The_First_Frame()
    {
        using MediaSource source = Open();
        using VideoFramePool pool = Pool();

        // Advance a few frames.
        for (int i = 0; i < 10; i++)
        {
            Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? f));
            f!.Dispose();
        }

        source.Rewind();

        Assert.True(source.TryDecodeNextFrame(pool, out VideoFrame? frame));
        using (frame)
            Assert.Equal(0, frame!.Pts.Ticks);
    }
}
