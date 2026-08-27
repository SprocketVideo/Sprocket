using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Reverse playback and speed ramps in the mixer (PLAN.md step 21 remainder): a reversed layer plays the source
/// PCM backwards from its cursor (block-carried across buffers), a ramped layer resamples the exact source span
/// the plan mapped over each buffer (so the audio cursor follows the video map with no drift), and the forward 1×
/// fast path is untouched.
/// </summary>
public class ReverseRetimeMixerTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const float RampScale = 1e-6f; // keeps a 10 s (480 k-frame) ramp inside [-1, 1] so the master hard-limit never clips it

    private static readonly MediaRefId A = MediaRefId.New();

    private static Project ProjectWith(AudioTrack track)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(timeline);
        timeline.Tracks.Add(track);
        return project;
    }

    private static AudioTrack TrackWith(Clip clip)
    {
        var track = new AudioTrack { Name = "A" };
        track.Clips.Add(clip);
        return track;
    }

    private static float[] Mix(AudioMixer mixer, Project project, int frames, Timecode at)
    {
        var buffer = new float[frames * Channels];
        mixer.MixInto(buffer, at, project);
        return buffer;
    }

    [Fact]
    public void Reversed_Layer_Plays_The_Source_Backwards()
    {
        var reader = new RampPcmReader(Rate, Channels, RampScale);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        // Source [0, 10 s) reversed at 1×: timeline 0 shows source frame 480000, walking down.
        var clip = new Clip(A, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero) { Reverse = true };
        Project project = ProjectWith(TrackWith(clip));

        const int frames = 256;
        float[] b1 = Mix(mixer, project, frames, Timecode.Zero);

        long top = 10 * Rate; // exclusive out-point in source frames
        // Output frame f is source frame (top − 1 − f): the descending ramp.
        foreach (int f in new[] { 0, 1, 100, 255 })
            Assert.Equal((top - 1 - f) * RampScale, b1[f * Channels], 1e-6);
        Assert.True(b1[0] > b1[(frames - 1) * Channels], "a reversed ramp must fall across the buffer");
    }

    [Fact]
    public void Reversed_Layer_Streams_Across_Buffers_Continuously()
    {
        var reader = new RampPcmReader(Rate, Channels, RampScale);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        var clip = new Clip(A, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero) { Reverse = true };
        Project project = ProjectWith(TrackWith(clip));

        const int frames = 256;
        float[] b1 = Mix(mixer, project, frames, Timecode.Zero);
        float[] b2 = Mix(mixer, project, frames, Timecode.FromSamples(frames, Rate));

        long top = 10 * Rate;
        // The second buffer continues exactly where the first stopped — the carried reverse block served both.
        Assert.Equal((top - 1 - frames) * RampScale, b2[0], 1e-6);
        Assert.Equal(b1[(frames - 1) * Channels] - RampScale, b2[0], 1e-6);
        // One block pull (a single reader seek) covered both buffers.
        Assert.Single(reader.Seeks);
    }

    [Fact]
    public void Reversed_Layer_Crosses_Block_Boundaries_Seamlessly()
    {
        var reader = new RampPcmReader(Rate, Channels, RampScale);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        var clip = new Clip(A, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero) { Reverse = true };
        Project project = ProjectWith(TrackWith(clip));

        // Walk through more than one 8192-frame reverse block in 1024-frame buffers and check every sample.
        const int frames = 1024;
        long top = 10 * Rate;
        long served = 0;
        for (int i = 0; i < 12; i++)
        {
            float[] b = Mix(mixer, project, frames, Timecode.FromSamples(served, Rate));
            for (int f = 0; f < frames; f += 97)
                Assert.Equal((top - 1 - served - f) * RampScale, b[f * Channels], 1e-6);
            served += frames;
        }
        Assert.True(reader.Seeks.Count >= 2, "a 12 k-frame walk spans at least two reverse blocks");
    }

    [Fact]
    public void Reversed_Layer_Reads_Silence_Before_Source_Time_Zero()
    {
        var reader = new RampPcmReader(Rate, Channels, RampScale);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        // Reversed 1× over source [0, 100 frames): after 100 output frames the walk has hit source time zero.
        var clip = new Clip(A, Timecode.Zero, Timecode.FromSamples(100, Rate), Timecode.Zero) { Reverse = true };
        Project project = ProjectWith(TrackWith(clip));

        float[] b = Mix(mixer, project, 64, Timecode.Zero);
        Assert.Equal(99 * RampScale, b[0], 1e-6);
        Assert.Equal(36 * RampScale, b[63 * Channels], 1e-6);
        float[] b2 = Mix(mixer, project, 64, Timecode.FromSamples(64, Rate));
        Assert.Equal(35 * RampScale, b2[0], 1e-6);
        Assert.Equal(0 * RampScale, b2[35 * Channels], 1e-6);
        // Beyond the clip (frames 36+ of this buffer are past its end) the plan contributes nothing → silence.
        Assert.Equal(0f, b2[40 * Channels]);
    }

    [Fact]
    public void Reversed_Retimed_Layer_Resamples_The_Backward_Stream()
    {
        var reader = new RampPcmReader(Rate, Channels, RampScale);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        // 2× reversed over [0, 10 s): output frame f samples source frame top − 2f.
        var clip = new Clip(A, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero)
        {
            SpeedRatio = new Rational(2, 1),
            Reverse = true,
        };
        Project project = ProjectWith(TrackWith(clip));

        float[] b = Mix(mixer, project, 256, Timecode.Zero);
        long top = 10 * Rate;
        foreach (int f in new[] { 0, 10, 100, 200 })
            Assert.Equal((top - 1 - 2 * f) * RampScale, b[f * Channels], 2e-6);
    }

    [Fact]
    public void Ramped_Layer_Consumes_Exactly_The_Mapped_Span_Per_Buffer()
    {
        var reader = new RampPcmReader(Rate, Channels, RampScale);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        // A ramp from 1× to 3× over the first 2 s of the clip. The mixer must follow the *integrated* map — after
        // N buffers its source cursor equals the clip's MapToSource at that timeline time (no drift), so the
        // sample at each buffer start is the source frame the video map names.
        var clip = new Clip(A, Timecode.Zero, Timecode.FromSeconds(30), Timecode.Zero);
        clip.SpeedCurve = AnimatableValue.Animated(
        [
            new Keyframe(Timecode.Zero, 1.0),
            new Keyframe(Timecode.FromSeconds(2), 3.0),
        ]);
        Project project = ProjectWith(TrackWith(clip));

        const int frames = 480; // 10 ms buffers
        for (int i = 0; i < 150; i++) // 1.5 s into the ramp
        {
            Timecode at = Timecode.FromSamples((long)i * frames, Rate);
            float[] b = Mix(mixer, project, frames, at);
            long expectedFrame = clip.MapToSource(at).Ticks * Rate / Timecode.TicksPerSecond;
            // Linear interpolation between neighbouring source frames of a linear ramp reproduces the ramp
            // exactly, so the first output sample is the (fractional) mapped source position.
            double actualFrame = b[0] / RampScale;
            Assert.InRange(actualFrame, expectedFrame - 1.5, expectedFrame + 1.5);
        }
        // Steady playback never re-seeks: the carried window keeps the reader sequential.
        Assert.Single(reader.Seeks);
    }

    [Fact]
    public void Forward_Unity_Layer_Keeps_The_Untouched_Fast_Path()
    {
        var reader = new RampPcmReader(Rate, Channels, RampScale);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        var clip = new Clip(A, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero);
        Project project = ProjectWith(TrackWith(clip));

        float[] b = Mix(mixer, project, 256, Timecode.FromSamples(1000, Rate));
        // Bit-exact pass-through of the source frames (no resampler in the path).
        for (int f = 0; f < 256; f++)
            Assert.Equal((1000 + f) * RampScale, b[f * Channels]);
    }
}
