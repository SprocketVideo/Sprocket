using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The "+ Effect" / Effects-menu relevance filter (PLAN.md step 33 follow-up): an audio-track clip is only
/// offered the audio DSP stages; a video-track clip only the video/colour shader stages — audio effects on a
/// video clip (and vice versa) would silently no-op, so they aren't offered.
/// </summary>
public sealed class EffectRelevanceTests
{
    private static (Timeline timeline, Clip videoClip, Clip audioClip) MakeTimeline()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var video = new VideoTrack { Name = "V1" };
        var audio = new AudioTrack { Name = "A1" };
        var videoClip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero);
        var audioClip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero);
        video.Clips.Add(videoClip);
        audio.Clips.Add(audioClip);
        timeline.Tracks.Add(video);
        timeline.Tracks.Add(audio);
        return (timeline, videoClip, audioClip);
    }

    [Fact]
    public void AudioTrackClip_GetsOnlyAudioEffects()
    {
        (Timeline timeline, _, Clip audioClip) = MakeTimeline();
        var offered = EffectRelevance.For(timeline, audioClip).ToList();
        Assert.NotEmpty(offered);
        Assert.All(offered, d => Assert.Equal(EffectCategory.Audio, d.Category));
        Assert.DoesNotContain(offered, d => d.Id == EffectTypeIds.Color);
        Assert.DoesNotContain(offered, d => d.Id == EffectTypeIds.Brightness);
    }

    [Fact]
    public void VideoTrackClip_GetsOnlyVideoAndColorEffects()
    {
        (Timeline timeline, Clip videoClip, _) = MakeTimeline();
        var offered = EffectRelevance.For(timeline, videoClip).ToList();
        Assert.NotEmpty(offered);
        Assert.All(offered, d => Assert.NotEqual(EffectCategory.Audio, d.Category));
        Assert.Contains(offered, d => d.Id == EffectTypeIds.Color);
        Assert.Contains(offered, d => d.Id == EffectTypeIds.AcesFilmic);
    }

    [Fact]
    public void PluginEffects_AreFilteredByCategoryToo()
    {
        (Timeline timeline, Clip videoClip, Clip audioClip) = MakeTimeline();
        var videoFx = new EffectDescriptor("plugin.relevance.video", "PV", EffectCategory.Color, "t", []);
        var audioFx = new EffectDescriptor("plugin.relevance.audio", "PA", EffectCategory.Audio, "t", []);
        Assert.True(EffectCatalog.Register(videoFx));
        Assert.True(EffectCatalog.Register(audioFx));
        try
        {
            Assert.Contains(EffectRelevance.For(timeline, videoClip), d => d.Id == videoFx.Id);
            Assert.DoesNotContain(EffectRelevance.For(timeline, videoClip), d => d.Id == audioFx.Id);
            Assert.Contains(EffectRelevance.For(timeline, audioClip), d => d.Id == audioFx.Id);
            Assert.DoesNotContain(EffectRelevance.For(timeline, audioClip), d => d.Id == videoFx.Id);
        }
        finally
        {
            EffectCatalog.Unregister(videoFx.Id);
            EffectCatalog.Unregister(audioFx.Id);
        }
    }

    [Fact]
    public void IsOnAudioTrack_DistinguishesTrackKinds()
    {
        (Timeline timeline, Clip videoClip, Clip audioClip) = MakeTimeline();
        Assert.False(EffectRelevance.IsOnAudioTrack(timeline, videoClip));
        Assert.True(EffectRelevance.IsOnAudioTrack(timeline, audioClip));
    }
}
