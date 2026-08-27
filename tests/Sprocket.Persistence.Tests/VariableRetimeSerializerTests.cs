using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Persistence of reverse playback and speed ramps (PLAN.md step 21 remainder): both fields are additive +
/// nullable — a forward, constant-speed clip writes neither (byte-identical to earlier files) and a reversed /
/// ramped clip round-trips its direction, curve (clip-local keyframes) and derived duration.
/// </summary>
public class VariableRetimeSerializerTests
{
    private static readonly MediaRefId VideoId = MediaRefId.New();

    private static Project ProjectWithClip(Clip clip)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        project.MediaPool.Add(new MediaRef(VideoId, @"C:\media\clip.mp4",
            new ProbedMediaInfo(Timecode.FromSeconds(12.5), true, new Rational(30, 1), 1920, 1080, false, 0, 0)));
        var video = new VideoTrack { Name = "V1" };
        video.Clips.Add(clip);
        timeline.Tracks.Add(video);
        return project;
    }

    private static Clip RoundTripClip(Project project) =>
        ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).Timeline.VideoTracks.First().Clips.Single();

    [Fact]
    public void Reverse_And_Speed_Curve_Round_Trip()
    {
        var clip = new Clip(VideoId, Timecode.FromSeconds(1), Timecode.FromSeconds(7), Timecode.FromSeconds(3))
        {
            SpeedRatio = new Rational(3, 2),
            Reverse = true,
            SpeedCurve = AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 2.0, Interpolation.Hold),
                new Keyframe(Timecode.FromSeconds(1.5), 0.5, Interpolation.EaseInOut),
            ]),
        };
        Project project = ProjectWithClip(clip);

        Clip loaded = RoundTripClip(project);
        Assert.True(loaded.Reverse);
        Assert.True(loaded.HasSpeedRamp);
        Assert.Equal(2, loaded.SpeedCurve!.Keyframes.Count);
        Assert.Equal(Timecode.FromSeconds(1.5), loaded.SpeedCurve.Keyframes[1].Time); // clip-local time preserved
        Assert.Equal(0.5, loaded.SpeedCurve.Keyframes[1].Value);
        Assert.Equal(Interpolation.EaseInOut, loaded.SpeedCurve.Keyframes[1].Interpolation);
        Assert.Equal(new Rational(3, 2), loaded.SpeedRatio);            // the constant is retained beneath the ramp
        Assert.Equal(clip.Duration, loaded.Duration);
        Assert.Equal(clip.MapToSource(Timecode.FromSeconds(4)), loaded.MapToSource(Timecode.FromSeconds(4)));
    }

    [Fact]
    public void Forward_Constant_Speed_Clip_Omits_Both_Fields_And_Loads_As_Before()
    {
        Project project = ProjectWithClip(new Clip(VideoId, Timecode.Zero, Timecode.FromSeconds(6), Timecode.Zero));
        string json = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("\"reverse\"", json);
        Assert.DoesNotContain("\"speedCurve\"", json);

        Clip loaded = RoundTripClip(project);
        Assert.False(loaded.Reverse);
        Assert.Null(loaded.SpeedCurve);
    }

    [Fact]
    public void Reverse_Alone_Round_Trips_Without_A_Curve()
    {
        Project project = ProjectWithClip(new Clip(VideoId, Timecode.Zero, Timecode.FromSeconds(6), Timecode.Zero) { Reverse = true });
        string json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"reverse\"", json);
        Assert.DoesNotContain("\"speedCurve\"", json);

        Clip loaded = RoundTripClip(project);
        Assert.True(loaded.Reverse);
        Assert.Null(loaded.SpeedCurve);
        Assert.Equal(Timecode.FromSeconds(6), loaded.MapToSource(Timecode.Zero));
    }
}
