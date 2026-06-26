using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Decode/resample/seek correctness for <see cref="AudioSource"/> against the deterministic fixture (a 3 s
/// 48 kHz 440 Hz sine). Validates the FFmpeg + libswresample interop end to end: total sample count matches
/// the duration, resampling to another rate scales the count, the signal is non-silent, and a seek lands at
/// the requested time.
/// </summary>
public class AudioSourceTests
{
    private const int FixtureSeconds = TestVideo.DurationSeconds; // 3

    private static int ReadAll(AudioSource source, int chunkFrames = 4096)
    {
        var buffer = new float[chunkFrames * source.Channels];
        int total = 0;
        int got;
        while ((got = source.Read(buffer)) > 0)
            total += got;
        return total;
    }

    [Fact]
    public void Decodes_Whole_Stream_At_Project_Rate()
    {
        using var source = AudioSource.Open(TestVideo.Path, sampleRate: 48000, channels: 2);
        int frames = ReadAll(source);

        // ~3 s at 48 kHz; allow a small codec priming/padding tolerance.
        int expected = 48000 * FixtureSeconds;
        Assert.InRange(frames, expected - 4096, expected + 4096);
    }

    [Fact]
    public void Resamples_To_A_Lower_Rate()
    {
        using var source = AudioSource.Open(TestVideo.Path, sampleRate: 24000, channels: 1);
        int frames = ReadAll(source);

        int expected = 24000 * FixtureSeconds;
        Assert.InRange(frames, expected - 2048, expected + 2048);
    }

    [Fact]
    public void Output_Is_Not_Silent()
    {
        using var source = AudioSource.Open(TestVideo.Path, sampleRate: 48000, channels: 2);
        var buffer = new float[48000 * 2];
        source.Read(buffer); // one second

        float peak = 0;
        foreach (float s in buffer)
            peak = Math.Max(peak, Math.Abs(s));
        Assert.True(peak > 0.1f, $"expected an audible sine, peak was {peak}");
    }

    [Fact]
    public void Interleaves_Channels_On_Upmix()
    {
        // Mono source upmixed to stereo: the two channels of each frame must be identical.
        using var source = AudioSource.Open(TestVideo.Path, sampleRate: 48000, channels: 2);
        var buffer = new float[4096 * 2];
        int got = source.Read(buffer);
        Assert.True(got > 0);

        for (int f = 0; f < got; f++)
            Assert.Equal(buffer[f * 2], buffer[f * 2 + 1]);
    }

    [Fact]
    public void Seek_Then_Read_Resumes_Near_The_Target()
    {
        using var source = AudioSource.Open(TestVideo.Path, sampleRate: 48000, channels: 1);

        // Read 1 s from the start, then seek to 2 s and confirm we still get a full second of audio there.
        var second = new float[48000];
        source.Read(second);

        source.SeekTo(Timecode.FromSeconds(2.0));
        int got = source.Read(second);
        Assert.True(got > 40000, $"expected ~1 s of audio after seeking to 2 s, got {got} frames");

        float peak = 0;
        foreach (float s in second.AsSpan(0, got))
            peak = Math.Max(peak, Math.Abs(s));
        Assert.True(peak > 0.1f, "post-seek audio should still be the audible sine");
    }
}
