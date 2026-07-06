using System.Linq;
using Sprocket.App.MediaBrowser;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>Badges for image-sequence and still media (PLAN.md step 42): a sequence shows
/// "SEQ · N frames @ fps" and a still shows "STILL"; ordinary files are unchanged.</summary>
public class MediaBadgesSequenceTests
{
    private static ProbedMediaInfo VideoInfo(Rational fps, double durationSeconds) =>
        new(Timecode.FromSeconds(durationSeconds), HasVideo: true, fps, 1920, 1080,
            HasAudio: false, SampleRate: 0, Channels: 0);

    [Fact]
    public void SequenceBadgeShowsFramesAndRate()
    {
        var media = new MediaRef(MediaRefId.New(), @"C:\shot\frame_0001.png", VideoInfo(new Rational(12, 1), 20))
        {
            Kind = MediaKind.ImageSequence,
            SequencePattern = @"C:\shot\frame_%04d.png",
            SequenceStartNumber = 1,
            SequenceFrameCount = 240,
        };

        var badges = MediaBadges.Describe(media);

        Assert.Equal("SEQ · 240 frames @ 12", badges[0]); // sequence tag leads
        Assert.Contains("1080p", badges);
    }

    [Fact]
    public void StillBadgeIsTagged()
    {
        var media = new MediaRef(MediaRefId.New(), @"C:\art\logo.png", VideoInfo(new Rational(25, 1), 5))
        {
            Kind = MediaKind.Still,
        };

        var badges = MediaBadges.Describe(media);

        Assert.Equal("STILL", badges[0]);
    }

    [Fact]
    public void OrdinaryFileHasNoKindTag()
    {
        var media = new MediaRef(MediaRefId.New(), @"C:\v\clip.mp4", VideoInfo(new Rational(30, 1), 10));
        var badges = MediaBadges.Describe(media);
        Assert.DoesNotContain(badges, b => b == "STILL" || b.StartsWith("SEQ"));
    }
}
