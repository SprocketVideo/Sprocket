using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Tests for image-sequence duration math and the <see cref="ReinterpretFootageCommand"/> (PLAN.md step 42):
/// reinterpreting a source's frame rate must keep the same frames selected (rescaling clip source spans by
/// oldFps/newFps) and undo byte-exactly.
/// </summary>
public class ReinterpretFootageTests
{
    private static ProbedMediaInfo SeqInfo(Rational fps, int frameCount) =>
        new(Timecode.FromFrames(frameCount, fps), HasVideo: true, fps, 1920, 1080,
            HasAudio: false, SampleRate: 0, Channels: 0);

    [Fact]
    public void SequenceDurationIsFrameCountOverFps()
    {
        // 240 frames @ 12 fps = 20 s exactly.
        ProbedMediaInfo info = SeqInfo(new Rational(12, 1), 240);
        Assert.Equal(Timecode.FromSeconds(20).Ticks, info.Duration.Ticks);

        // NTSC 30000/1001: 300 frames = 300 * 1001 / 30000 s, exact in ticks.
        ProbedMediaInfo ntsc = SeqInfo(new Rational(30000, 1001), 300);
        Assert.Equal(Timecode.FromFrames(300, new Rational(30000, 1001)).Ticks, ntsc.Duration.Ticks);
    }

    [Fact]
    public void ReinterpretKeepsFrameIndicesAndRescalesClips()
    {
        var project = new Project(new Timeline(new Rational(24, 1), new Resolution(1920, 1080), 48000));
        project.Timeline.Tracks.Add(new VideoTrack());
        var media = new MediaRef(MediaRefId.New(), @"C:\shot\f_%04d.png", SeqInfo(new Rational(12, 1), 240))
        {
            Kind = MediaKind.ImageSequence,
            SequencePattern = @"C:\shot\f_%04d.png",
            SequenceStartNumber = 1,
            SequenceFrameCount = 240,
        };
        project.MediaPool.Add(media);

        var fps12 = new Rational(12, 1);
        // A clip selecting source frames 24..48 at 12 fps.
        Timecode inAt = Timecode.FromFrames(24, fps12);
        Timecode outAt = Timecode.FromFrames(48, fps12);
        var clip = new Clip(media.Id, inAt, outAt, Timecode.Zero);
        project.Timeline.VideoTracks.First().Clips.Add(clip);

        var history = new EditHistory();
        history.Execute(ReinterpretFootageCommand.ForMedia(project, media, new Rational(24, 1)));

        // Media now reports 24 fps and half the duration (same 240 frames at double the rate).
        Assert.Equal(new Rational(24, 1), media.Info.FrameRate);
        Assert.Equal(Timecode.FromFrames(240, new Rational(24, 1)).Ticks, media.Info.Duration.Ticks);

        // The same frame indices (24..48) stay selected — now measured at 24 fps.
        Assert.Equal(24, clip.SourceIn.ToFrameIndex(new Rational(24, 1)));
        Assert.Equal(48, clip.SourceOut.ToFrameIndex(new Rational(24, 1)));

        // Undo restores the exact original ticks.
        long origIn = inAt.Ticks, origOut = outAt.Ticks;
        history.Undo();
        Assert.Equal(new Rational(12, 1), media.Info.FrameRate);
        Assert.Equal(origIn, clip.SourceIn.Ticks);
        Assert.Equal(origOut, clip.SourceOut.Ticks);
    }

    [Fact]
    public void ReinterpretIgnoresClipsOfOtherMedia()
    {
        var project = new Project();
        project.Timeline.Tracks.Add(new VideoTrack());
        var a = new MediaRef(MediaRefId.New(), "a", SeqInfo(new Rational(12, 1), 10));
        var b = new MediaRef(MediaRefId.New(), "b", SeqInfo(new Rational(12, 1), 10));
        project.MediaPool.Add(a);
        project.MediaPool.Add(b);
        var clipB = new Clip(b.Id, Timecode.Zero, Timecode.FromFrames(5, new Rational(12, 1)), Timecode.Zero);
        project.Timeline.VideoTracks.First().Clips.Add(clipB);

        long before = clipB.SourceOut.Ticks;
        new EditHistory().Execute(ReinterpretFootageCommand.ForMedia(project, a, new Rational(24, 1)));

        Assert.Equal(before, clipB.SourceOut.Ticks); // clip referencing b is untouched
    }

    [Fact]
    public void UnboundedDurationOnlyForStills()
    {
        Assert.True(new MediaRef(MediaRefId.New(), "s", SeqInfo(new Rational(1, 1), 1)) { Kind = MediaKind.Still }.HasUnboundedDuration);
        Assert.False(new MediaRef(MediaRefId.New(), "q", SeqInfo(new Rational(12, 1), 10)) { Kind = MediaKind.ImageSequence }.HasUnboundedDuration);
        Assert.False(new MediaRef(MediaRefId.New(), "f", SeqInfo(new Rational(30, 1), 10)).HasUnboundedDuration);
    }
}
