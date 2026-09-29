using System.Linq;
using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The Source monitor's throwaway one-clip project (PLAN.md step 61 phase 5): a track per stream the source has, each
/// spanning the whole source — the video track for the picture feed, the audio track for the source's audio clock.
/// </summary>
public class SourceMonitorProjectTests
{
    private static MediaRef Media(bool video, bool audio) =>
        new(MediaRefId.New(), "/tmp/src.mp4", new ProbedMediaInfo(
            Timecode.FromSeconds(5), HasVideo: video, new Rational(25, 1), 1280, 720, HasAudio: audio, 44100, 2));

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Has_A_Full_Length_Track_Per_Stream(bool video, bool audio)
    {
        MediaRef media = Media(video, audio);

        Project project = SourceMonitor.BuildSourceProject(media);

        Assert.Equal(video ? 1 : 0, project.Timeline.VideoTracks.Count());
        Assert.Equal(audio ? 1 : 0, project.Timeline.AudioTracks.Count());
        Assert.All(project.Timeline.Tracks, t =>
        {
            Clip clip = Assert.Single(t.Clips);
            Assert.Equal(media.Id, clip.MediaRefId);
            Assert.Equal(Timecode.Zero, clip.TimelineStart);
            Assert.Equal(media.Info.Duration, clip.TimelineEnd);
        });
        Assert.Equal(44100, project.Timeline.SampleRate);
        Assert.Same(media, project.MediaPool.Get(media.Id));
    }
}
