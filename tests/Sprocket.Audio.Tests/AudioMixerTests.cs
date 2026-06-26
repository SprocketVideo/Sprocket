using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Deterministic tests for <see cref="AudioMixer"/> using synthetic <see cref="FakePcmReader"/>s (no FFmpeg):
/// summing, per-track gain (dB), mute/solo, master gain, hard-limit, fade gain ramps, and seek-on-jump.
/// </summary>
public class AudioMixerTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    private static readonly MediaRefId A = MediaRefId.New();
    private static readonly MediaRefId B = MediaRefId.New();

    private static Project ProjectWith(params AudioTrack[] tracks)
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(timeline);
        foreach (AudioTrack t in tracks)
            timeline.Tracks.Add(t);
        return project;
    }

    private static AudioTrack TrackOver(MediaRefId id, double gainDb = 0, bool muted = false, bool solo = false)
    {
        var track = new AudioTrack { Name = "A", GainDb = gainDb, Muted = muted, Solo = solo };
        track.Clips.Add(new Clip(id, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        return track;
    }

    private static AudioMixer MixerFor(params (MediaRefId id, float value)[] sources)
    {
        var readers = sources.ToDictionary(s => s.id, s => new FakePcmReader(Rate, Channels, s.value));
        return new AudioMixer(Rate, Channels, id => readers.TryGetValue(id, out FakePcmReader? r) ? r : null);
    }

    private static float[] Mix(AudioMixer mixer, Project project, int frames, Timecode at)
    {
        var buffer = new float[frames * Channels];
        mixer.MixInto(buffer, at, project);
        return buffer;
    }

    [Fact]
    public void Mixes_Single_Source_At_Unity_Gain()
    {
        using AudioMixer mixer = MixerFor((A, 0.5f));
        float[] buffer = Mix(mixer, ProjectWith(TrackOver(A)), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 0.0001));
    }

    [Fact]
    public void Sums_Two_Tracks()
    {
        using AudioMixer mixer = MixerFor((A, 0.25f), (B, 0.25f));
        float[] buffer = Mix(mixer, ProjectWith(TrackOver(A), TrackOver(B)), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 0.0001));
    }

    [Fact]
    public void Applies_Track_Gain_In_Db()
    {
        using AudioMixer mixer = MixerFor((A, 1.0f));
        // -6.0206 dB ≈ 0.5 linear.
        float[] buffer = Mix(mixer, ProjectWith(TrackOver(A, gainDb: -6.0206)), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 0.01));
    }

    [Fact]
    public void Mute_Silences_The_Track()
    {
        using AudioMixer mixer = MixerFor((A, 0.8f));
        float[] buffer = Mix(mixer, ProjectWith(TrackOver(A, muted: true)), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Solo_Excludes_Non_Soloed_Tracks()
    {
        using AudioMixer mixer = MixerFor((A, 0.3f), (B, 0.7f));
        // B is soloed → only B is audible.
        float[] buffer = Mix(mixer, ProjectWith(TrackOver(A), TrackOver(B, solo: true)), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.7f, s, 0.0001));
    }

    [Fact]
    public void Applies_Master_Gain()
    {
        using AudioMixer mixer = MixerFor((A, 1.0f));
        Project project = ProjectWith(TrackOver(A));
        project.Settings.MasterGainDb = -6.0206; // ≈ 0.5
        float[] buffer = Mix(mixer, project, 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 0.01));
    }

    [Fact]
    public void Hard_Limits_To_Unit_Range()
    {
        using AudioMixer mixer = MixerFor((A, 2.0f)); // would overflow [-1, 1]
        float[] buffer = Mix(mixer, ProjectWith(TrackOver(A)), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(1.0f, s, 0.0001));
    }

    [Fact]
    public void Fade_Produces_A_Gain_Ramp_Across_The_Buffer()
    {
        // A fade-out: opacity 1 → 0 over [0, 1s]; the gain ramps down across a 1 s buffer.
        var track = TrackOver(A);
        track.Clips[0].Effects.Add(new EffectInstance(EffectTypeIds.Fade)
            .Set(EffectParamNames.Opacity, AnimatableValue.Animated(
            [
                new Keyframe(Timecode.Zero, 1.0),
                new Keyframe(Timecode.FromSeconds(1.0), 0.0),
            ])));

        using AudioMixer mixer = MixerFor((A, 1.0f));
        int frames = Rate; // exactly 1 s
        float[] buffer = Mix(mixer, ProjectWith(track), frames, Timecode.Zero);

        float first = buffer[0];
        float mid = buffer[(frames / 2) * Channels];
        float last = buffer[(frames - 1) * Channels];

        Assert.Equal(1.0f, first, 0.01);
        Assert.Equal(0.5f, mid, 0.01);
        Assert.True(last < 0.02f, $"end of fade should be ~0, was {last}");
    }

    [Fact]
    public void Seeks_Reader_Only_When_The_Source_Position_Jumps()
    {
        var reader = new FakePcmReader(Rate, Channels, 0.5f);
        using var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        Project project = ProjectWith(TrackOver(A));

        int frames = 4800; // 0.1 s
        var buffer = new float[frames * Channels];

        mixer.MixInto(buffer, Timecode.Zero, project);                       // initial positioning → seek to 0
        mixer.MixInto(buffer, Timecode.FromSamples(frames, Rate), project);  // contiguous → no seek
        mixer.MixInto(buffer, Timecode.FromSeconds(5.0), project);           // jump → seek to 5 s

        Assert.Equal(2, reader.Seeks.Count);
        Assert.Equal(Timecode.Zero.Ticks, reader.Seeks[0].Ticks);
        Assert.Equal(Timecode.FromSeconds(5.0).Ticks, reader.Seeks[1].Ticks);
    }

    [Fact]
    public void Silence_Where_No_Clip_Is_Active()
    {
        using AudioMixer mixer = MixerFor((A, 0.9f));
        // The clip ends at 10 s; mixing at 20 s yields silence.
        float[] buffer = Mix(mixer, ProjectWith(TrackOver(A)), 256, Timecode.FromSeconds(20.0));
        Assert.All(buffer, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Disposes_Resolved_Readers()
    {
        var reader = new FakePcmReader(Rate, Channels, 0.5f);
        var mixer = new AudioMixer(Rate, Channels, id => id == A ? reader : null);
        Mix(mixer, ProjectWith(TrackOver(A)), 64, Timecode.Zero); // resolve + cache the reader
        mixer.Dispose();
        Assert.True(reader.Disposed);
    }
}
