using Sprocket.Audio.Effects;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// The Convolution Reverb (PLAN.md step 49) running inside the mixer's effect chains end to end: the render
/// graph carries the IR asset reference into the resolved chain, the production factory + cache supply the
/// DSP, and the tail rings into the next contiguous buffer like the other reverbs.
/// </summary>
public class ConvolutionReverbChainTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private static readonly MediaRefId A = MediaRefId.New();

    private static Project ProjectWith(AudioTrack track)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(timeline);
        timeline.Tracks.Add(track);
        return project;
    }

    private static float[] Mix(AudioMixer mixer, Project project, int frames, Timecode at)
    {
        var buffer = new float[frames * Channels];
        mixer.MixInto(buffer, at, project);
        return buffer;
    }

    [Fact]
    public void ConvolutionReverb_On_The_Bus_Chain_Echoes_Into_The_Next_Contiguous_Buffer()
    {
        // IR: a direct spike plus a half-level echo 0.1 s later. Mixed in 0.05 s buffers aligned to a 0.05 s
        // clip of constant 0.9: buffer 0 carries the clip (0.9), buffer 1 is the silent gap, buffer 2 is the
        // echo (0.45) — the convolver's state persists across the contiguous MixInto calls — and buffer 3 is
        // silent again. (The mixer resolves clip participation per buffer, hence the aligned sizes.)
        var taps = new float[Rate / 10 + 1];
        taps[0] = 1f;
        taps[Rate / 10] = 0.5f;
        string path = WavFixture.TempPath("echo-ir.wav");
        WavFixture.Write(path, taps, 1, Rate, asFloat: true);
        ImpulseResponseCache.Load(path, Rate); // preload so the very first buffer convolves (no async wait)

        var track = new AudioTrack { Name = "A" };
        track.Clips.Add(new Clip(A, Timecode.Zero, Timecode.FromSamples(2400, Rate), Timecode.Zero));
        Project project = ProjectWith(track);
        project.Timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioConvolutionReverb)
            .Set(EffectParamNames.Mix, 1.0)
            .SetAsset(EffectParamNames.ImpulseResponse, path));
        var reader = new FakePcmReader(Rate, Channels, 0.9f);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);

        const int Frames = 2400;
        float[] b0 = Mix(mixer, project, Frames, Timecode.Zero);
        float[] b1 = Mix(mixer, project, Frames, Timecode.FromSamples(Frames, Rate));
        float[] b2 = Mix(mixer, project, Frames, Timecode.FromSamples(2 * Frames, Rate));
        float[] b3 = Mix(mixer, project, Frames, Timecode.FromSamples(3 * Frames, Rate));
        Assert.Equal(0.9f, b0[100 * Channels], 1e-3f);  // direct
        Assert.Equal(0f, b1[100 * Channels], 1e-3f);    // past the clip, before the echo
        Assert.Equal(0.45f, b2[100 * Channels], 1e-3f); // the echo of the clip, one IR delay later
        Assert.Equal(0.45f, b2[2000 * Channels], 1e-3f);
        Assert.Equal(0f, b3[100 * Channels], 1e-3f);    // echo over, nothing else rings

        ImpulseResponseCache.Invalidate(path);
    }

    [Fact]
    public void Missing_IR_On_A_Chain_Is_A_Pass_Through()
    {
        var track = new AudioTrack { Name = "A" };
        track.Clips.Add(new Clip(A, Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero));
        Project project = ProjectWith(track);
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioConvolutionReverb)
            .Set(EffectParamNames.Mix, 1.0)
            .SetAsset(EffectParamNames.ImpulseResponse, WavFixture.TempPath("nope.wav")));
        var reader = new FakePcmReader(Rate, Channels, 0.5f);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);

        float[] buffer = Mix(mixer, project, 1024, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 1e-6f));
    }
}
