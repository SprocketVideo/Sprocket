using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// The mixer executing audio effect chains (PLAN.md step 31): clip / track / bus / master scopes, the
/// pre-fader insert order, unknown-id pass-through, DSP state carrying across buffers, and the untouched
/// chain-less fast path.
/// </summary>
public class AudioMixerChainTests
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

    private static AudioTrack TrackOver(MediaRefId id)
    {
        var track = new AudioTrack { Name = "A" };
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

    private static EffectInstance Gain(double db) =>
        new EffectInstance(EffectTypeIds.AudioGain).Set(EffectParamNames.GainDb, db);

    [Fact]
    public void Clip_Chain_Gain_Applies()
    {
        var track = TrackOver(A);
        track.Clips[0].Effects.Add(Gain(-6.0206));
        using AudioMixer mixer = MixerFor((A, 0.8f));
        float[] buffer = Mix(mixer, ProjectWith(track), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.4f, s, 0.001));
    }

    [Fact]
    public void Track_Chain_Gain_Applies()
    {
        var track = TrackOver(A);
        track.Effects.Add(Gain(-6.0206));
        using AudioMixer mixer = MixerFor((A, 0.8f));
        float[] buffer = Mix(mixer, ProjectWith(track), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.4f, s, 0.001));
    }

    [Fact]
    public void Track_Inserts_Run_Before_The_Track_Fader()
    {
        // A compressor on the track's insert chain must see the signal BEFORE the -20 dB track fader
        // (pre-fader inserts, the DAW/NLE convention). Signal 1.0 vs threshold -12 dB / ratio 4 → ~9 dB of
        // reduction, then the fader: ≈ 0.0355. Were the fader applied first, the compressor would see
        // -20 dB (below threshold) and pass 0.1 through untouched.
        var track = TrackOver(A);
        track.GainDb = -20;
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioCompressor)
            .Set(EffectParamNames.ThresholdDb, -12.0)
            .Set(EffectParamNames.Ratio, 4.0)
            .Set(EffectParamNames.AttackMs, 1.0)
            .Set(EffectParamNames.ReleaseMs, 50.0));
        using AudioMixer mixer = MixerFor((A, 1.0f));

        float[] buffer = Mix(mixer, ProjectWith(track), Rate, Timecode.Zero); // 1 s so the envelope settles
        float steady = buffer[^1];
        Assert.Equal(0.1 * Math.Pow(10, -9.0 / 20), steady, 0.005);
    }

    [Fact]
    public void Clip_Fade_Feeds_The_Track_Chain()
    {
        // The clip-level gain (fade) applies before the track inserts: a whole-clip fade at 0.25 puts the
        // signal below the compressor threshold, so the track compressor must not engage.
        var track = TrackOver(A);
        track.Clips[0].Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity, 0.25));
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioCompressor)
            .Set(EffectParamNames.ThresholdDb, -6.0)
            .Set(EffectParamNames.Ratio, 20.0)
            .Set(EffectParamNames.AttackMs, 1.0));
        using AudioMixer mixer = MixerFor((A, 1.0f));

        float[] buffer = Mix(mixer, ProjectWith(track), Rate, Timecode.Zero);
        Assert.Equal(0.25f, buffer[^1], 0.005); // 0.25 is -12 dB, below the -6 dB threshold → untouched
    }

    [Fact]
    public void Bus_Chain_Processes_The_Summed_Mix()
    {
        Project project = ProjectWith(TrackOver(A), TrackOver(B));
        project.Timeline.AudioEffects.Add(Gain(-6.0206));
        using AudioMixer mixer = MixerFor((A, 0.25f), (B, 0.25f));
        float[] buffer = Mix(mixer, project, 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.25f, s, 0.001)); // (0.25 + 0.25) × 0.5
    }

    [Fact]
    public void Master_Chain_Processes_The_Final_Mix()
    {
        Project project = ProjectWith(TrackOver(A));
        project.Settings.MasterAudioEffects.Add(Gain(-6.0206));
        using AudioMixer mixer = MixerFor((A, 0.5f));
        float[] buffer = Mix(mixer, project, 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.25f, s, 0.001));
    }

    [Fact]
    public void Unknown_Chain_Ids_Pass_Through()
    {
        var track = TrackOver(A);
        track.Clips[0].Effects.Add(new EffectInstance("builtin.audio.notyetinvented"));
        using AudioMixer mixer = MixerFor((A, 0.5f));
        float[] buffer = Mix(mixer, ProjectWith(track), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 0.0001));
    }

    [Fact]
    public void Chain_State_Carries_Across_Contiguous_Buffers()
    {
        // A reverb on the sequence bus keeps ringing into the NEXT sequential buffer even when the source has
        // gone silent (the clip ends mid-way through the first buffer) — the chain's DSP state persists across
        // contiguous MixInto calls.
        var track = new AudioTrack { Name = "A" };
        track.Clips.Add(new Clip(A, Timecode.Zero, Timecode.FromSamples(2400, Rate), Timecode.Zero));
        Project project = ProjectWith(track);
        project.Timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioReverb)
            .Set(EffectParamNames.RoomSize, 0.8)
            .Set(EffectParamNames.Damping, 0.2)
            .Set(EffectParamNames.Mix, 1.0));
        using AudioMixer mixer = MixerFor((A, 0.9f));

        Mix(mixer, project, 4800, Timecode.Zero); // excite the reverb with the 0–0.05 s clip content
        // The clip has ended, but the next buffer is contiguous: the bus chain's tail rings on.
        float[] tail = Mix(mixer, project, 4800, Timecode.FromSamples(4800, Rate));
        Assert.Contains(tail, s => Math.Abs(s) > 0.001f);
    }

    [Fact]
    public void Transport_Jump_Resets_Chain_State()
    {
        // Seeking must NOT bleed the old position's reverb tail into the new one: after a non-contiguous
        // MixInto (a jump from 0.1 s to 20 s) the chain starts clean, so a fully-wet reverb over silence is
        // exactly silent.
        Project project = ProjectWith(TrackOver(A)); // clip 0–10 s
        project.Timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioReverb)
            .Set(EffectParamNames.RoomSize, 1.0)
            .Set(EffectParamNames.Damping, 0.2)
            .Set(EffectParamNames.Mix, 1.0));
        using AudioMixer mixer = MixerFor((A, 0.9f));

        Mix(mixer, project, 4800, Timecode.Zero); // excite the reverb with clip content
        float[] after = Mix(mixer, project, 4800, Timecode.FromSeconds(20)); // jump past the clip
        Assert.All(after, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Delay_Echo_Rings_Into_The_Next_Contiguous_Buffer()
    {
        // A digital delay on the sequence bus (PLAN.md step 46): a 0.05 s clip's content echoes back 75 ms
        // later — inside the NEXT sequential buffer, after the source has gone silent — because the chain's
        // delay-line state persists across contiguous MixInto calls, like the reverb tail above.
        var track = new AudioTrack { Name = "A" };
        track.Clips.Add(new Clip(A, Timecode.Zero, Timecode.FromSamples(2400, Rate), Timecode.Zero));
        Project project = ProjectWith(track);
        project.Timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioDelayDigital)
            .Set(EffectParamNames.DelayMs, 75.0)
            .Set(EffectParamNames.Feedback, 0.5)
            .Set(EffectParamNames.Mix, 1.0));
        using AudioMixer mixer = MixerFor((A, 0.9f));

        float[] first = Mix(mixer, project, 2400, Timecode.Zero); // fully wet: the echo hasn't arrived yet
        Assert.All(first, s => Assert.Equal(0f, s, 0.0001));
        float[] second = Mix(mixer, project, 2400, Timecode.FromSamples(2400, Rate));
        // The echo of the clip's 0–0.05 s content lands at 0.075–0.125 s: the second half of this buffer.
        Assert.All(second.AsSpan(0, 1200 * Channels).ToArray(), s => Assert.Equal(0f, s, 0.0001));
        Assert.Contains(second.AsSpan(1200 * Channels).ToArray(), s => Math.Abs(s) > 0.5f);
    }

    [Fact]
    public void Delay_Orders_With_Existing_Chain_Stages()
    {
        // Gain (-6 dB) BEFORE the delay: the echo carries the attenuated level — chain order is the list
        // order, delays composing with the step-31 built-ins like any other stage.
        var track = new AudioTrack { Name = "A" };
        track.Clips.Add(new Clip(A, Timecode.Zero, Timecode.FromSamples(2400, Rate), Timecode.Zero));
        Project project = ProjectWith(track);
        project.Timeline.AudioEffects.Add(Gain(-6.0206));
        project.Timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioDelayDigital)
            .Set(EffectParamNames.DelayMs, 25.0)
            .Set(EffectParamNames.Feedback, 0.0)
            .Set(EffectParamNames.Mix, 1.0));
        using AudioMixer mixer = MixerFor((A, 0.8f));

        float[] buffer = Mix(mixer, project, 4800, Timecode.Zero);
        // Echo at 25 ms (sample 1200) of the halved 0.8 source: 0.4.
        Assert.Equal(0.4f, buffer[1200 * Channels], 0.005);
    }

    [Fact]
    public void Fast_Path_Without_Chains_Is_Unchanged()
    {
        // No chains anywhere → the combined clip × track gain ramp sums exactly as before step 31.
        var track = TrackOver(A);
        track.GainDb = -6.0206;
        track.Clips[0].GainDb = -6.0206;
        using AudioMixer mixer = MixerFor((A, 1.0f));
        float[] buffer = Mix(mixer, ProjectWith(track), 256, Timecode.Zero);
        Assert.All(buffer, s => Assert.Equal(0.25f, s, 0.002));
    }
}
