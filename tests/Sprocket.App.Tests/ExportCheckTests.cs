using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The pure half of the <c>--export-check</c> release smoke (code-signing ticket 04): the expectation derived from
/// the sample project, and the verdict on the re-probed output. The export + probe themselves need the FFmpeg
/// natives and run in the CI smoke legs against the packaged build.
/// </summary>
public class ExportCheckTests
{
    private static readonly ExportCheck.Expectation Expected = new(
        Duration: Timecode.FromSeconds(10), Width: 1920, Height: 1080, VideoCodec: "h264", AudioCodec: "aac");

    private static ProbedMediaInfo Probed(
        double seconds = 10, bool hasVideo = true, int width = 1920, int height = 1080, bool hasAudio = true,
        string videoCodec = "h264", string audioCodec = "aac") =>
        new(Timecode.FromSeconds(seconds), hasVideo, new Rational(30, 1), width, height, hasAudio, 48000, 2,
            VideoCodec: videoCodec, AudioCodec: audioCodec);

    [Fact]
    public void Matching_Output_Passes() => Assert.Empty(ExportCheck.Verify(Expected, Probed()));

    [Theory]
    [InlineData(9.85)]
    [InlineData(10.15)]
    public void Duration_Within_Tolerance_Passes(double seconds) =>
        Assert.Empty(ExportCheck.Verify(Expected, Probed(seconds)));

    [Theory]
    [InlineData(9.5)]
    [InlineData(10.5)]
    [InlineData(0)]
    public void Duration_Outside_Tolerance_Fails(double seconds) =>
        Assert.Contains(ExportCheck.Verify(Expected, Probed(seconds)), f => f.Contains("duration"));

    [Fact]
    public void Missing_Video_Stream_Fails() =>
        Assert.Contains(ExportCheck.Verify(Expected, Probed(hasVideo: false)), f => f.Contains("video stream"));

    [Fact]
    public void Missing_Audio_Stream_Fails() =>
        Assert.Contains(ExportCheck.Verify(Expected, Probed(hasAudio: false)), f => f.Contains("audio stream"));

    [Fact]
    public void Wrong_Resolution_Fails() =>
        Assert.Contains(ExportCheck.Verify(Expected, Probed(width: 1280, height: 720)), f => f.Contains("1280x720"));

    [Fact]
    public void Wrong_Codecs_Fail()
    {
        IReadOnlyList<string> failures = ExportCheck.Verify(Expected, Probed(videoCodec: "hevc", audioCodec: "mp3"));
        Assert.Contains(failures, f => f.Contains("hevc"));
        Assert.Contains(failures, f => f.Contains("mp3"));
    }

    [Fact]
    public void Expectation_Comes_From_The_Timeline_And_Default_Export_Format()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var media = new MediaRef(MediaRefId.New(), "clip.mp4",
            new ProbedMediaInfo(Timecode.FromSeconds(4), true, new Rational(30, 1), 1920, 1080, true, 48000, 2));
        var track = new VideoTrack();
        track.Clips.Add(new Clip(media.Id, Timecode.Zero, Timecode.FromSeconds(4), Timecode.FromSeconds(1)));
        timeline.Tracks.Add(track);

        ExportCheck.Expectation e = ExportCheck.ExpectFor(timeline);

        Assert.Equal(Timecode.FromSeconds(5), e.Duration);
        Assert.Equal((1920, 1080), (e.Width, e.Height));
        Assert.Equal(("h264", "aac"), (e.VideoCodec, e.AudioCodec));
    }
}
