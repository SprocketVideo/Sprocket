using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Persistence of the audio effect chains (PLAN.md step 31): the track insert chain, the sequence bus chain,
/// and the project master chain round-trip (the clip-scope chain is the ordinary clip effect stack, covered
/// by the existing effect tests), and chain-less projects keep the pre-31 wire format byte-identically.
/// </summary>
public class AudioEffectChainPersistenceTests
{
    private static Project BuildProjectWithChains()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);

        var track = new AudioTrack { Name = "A1" };
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioEq)
            .Set(EffectParamNames.LowGainDb, 3.0)
            .Set(EffectParamNames.LowFreq, 120.0));
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioCompressor)
            .Set(EffectParamNames.ThresholdDb, -14.0)
            .Set(EffectParamNames.Ratio, 3.0));
        timeline.Tracks.Add(track);

        timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioReverb)
            .Set(EffectParamNames.Mix, 0.2));

        project.Settings.MasterAudioEffects.Add(new EffectInstance(EffectTypeIds.AudioGain)
            .Set(EffectParamNames.GainDb,
                AnimatableValue.Animated(
                [
                    new Keyframe(Timecode.Zero, 0.0),
                    new Keyframe(Timecode.FromSeconds(5), -6.0),
                ])));
        return project;
    }

    private static Project RoundTrip(Project project)
        => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

    [Fact]
    public void Track_Insert_Chain_Round_Trips_In_Order()
    {
        Project loaded = RoundTrip(BuildProjectWithChains());
        AudioTrack track = Assert.IsType<AudioTrack>(loaded.Timeline.Tracks[0]);
        Assert.Equal(2, track.Effects.Count);
        Assert.Equal(EffectTypeIds.AudioEq, track.Effects[0].EffectTypeId);
        Assert.Equal(3.0, track.Effects[0].Parameters[EffectParamNames.LowGainDb].Evaluate(Timecode.Zero));
        Assert.Equal(120.0, track.Effects[0].Parameters[EffectParamNames.LowFreq].Evaluate(Timecode.Zero));
        Assert.Equal(EffectTypeIds.AudioCompressor, track.Effects[1].EffectTypeId);
        Assert.Equal(-14.0, track.Effects[1].Parameters[EffectParamNames.ThresholdDb].Evaluate(Timecode.Zero));
    }

    [Fact]
    public void Sequence_Bus_Chain_Round_Trips()
    {
        Project loaded = RoundTrip(BuildProjectWithChains());
        EffectInstance bus = Assert.Single(loaded.Timeline.AudioEffects);
        Assert.Equal(EffectTypeIds.AudioReverb, bus.EffectTypeId);
        Assert.Equal(0.2, bus.Parameters[EffectParamNames.Mix].Evaluate(Timecode.Zero));
    }

    [Fact]
    public void Master_Chain_Round_Trips_With_Keyframed_Parameters()
    {
        Project loaded = RoundTrip(BuildProjectWithChains());
        EffectInstance master = Assert.Single(loaded.Settings.MasterAudioEffects);
        Assert.Equal(EffectTypeIds.AudioGain, master.EffectTypeId);
        AnimatableValue gain = master.Parameters[EffectParamNames.GainDb];
        Assert.True(gain.IsAnimated);
        Assert.Equal(-3.0, gain.Evaluate(Timecode.FromSeconds(2.5)), 6);
    }

    [Fact]
    public void Step46_Delay_Effects_Round_Trip()
    {
        // Each new delay id serializes via the existing EffectInstance JSON — additive, no schema bump.
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new AudioTrack { Name = "A1" };
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioDelayDigital)
            .Set(EffectParamNames.DelayMs, 250.0)
            .Set(EffectParamNames.Feedback, 0.45)
            .Set(EffectParamNames.HighCutHz, 6000.0));
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioDelayTape)
            .Set(EffectParamNames.WowFlutterDepth, 0.6)
            .Set(EffectParamNames.Drive, 0.8));
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioDelayMultiTap)
            .Set(EffectParamNames.TapEnable[2], 1.0)
            .Set(EffectParamNames.TapTimeMs[2], 333.0)
            .Set(EffectParamNames.TapPan[2], -0.5));
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioDelayStereo)
            .Set(EffectParamNames.LeftTimeMs, 300.0)
            .Set(EffectParamNames.PingPong, 1.0));
        timeline.Tracks.Add(track);

        AudioTrack loaded = Assert.IsType<AudioTrack>(RoundTrip(project).Timeline.Tracks[0]);
        Assert.Equal(4, loaded.Effects.Count);
        Assert.Equal(EffectTypeIds.AudioDelayDigital, loaded.Effects[0].EffectTypeId);
        Assert.Equal(250.0, loaded.Effects[0].Parameters[EffectParamNames.DelayMs].Evaluate(Timecode.Zero));
        Assert.Equal(0.45, loaded.Effects[0].Parameters[EffectParamNames.Feedback].Evaluate(Timecode.Zero));
        Assert.Equal(EffectTypeIds.AudioDelayTape, loaded.Effects[1].EffectTypeId);
        Assert.Equal(0.6, loaded.Effects[1].Parameters[EffectParamNames.WowFlutterDepth].Evaluate(Timecode.Zero));
        Assert.Equal(EffectTypeIds.AudioDelayMultiTap, loaded.Effects[2].EffectTypeId);
        Assert.Equal(333.0, loaded.Effects[2].Parameters[EffectParamNames.TapTimeMs[2]].Evaluate(Timecode.Zero));
        Assert.Equal(-0.5, loaded.Effects[2].Parameters[EffectParamNames.TapPan[2]].Evaluate(Timecode.Zero));
        Assert.Equal(EffectTypeIds.AudioDelayStereo, loaded.Effects[3].EffectTypeId);
        Assert.Equal(1.0, loaded.Effects[3].Parameters[EffectParamNames.PingPong].Evaluate(Timecode.Zero));
    }

    [Fact]
    public void Step47_Noise_Gate_Round_Trips()
    {
        // The gate id serializes via the existing EffectInstance JSON — additive, no schema bump.
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new AudioTrack { Name = "A1" };
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioNoiseGate)
            .Set(EffectParamNames.ThresholdDb, -45.0)
            .Set(EffectParamNames.AttackMs, 2.5)
            .Set(EffectParamNames.HoldMs, 120.0)
            .Set(EffectParamNames.ReleaseMs, 250.0)
            .Set(EffectParamNames.RangeDb, -30.0)
            .Set(EffectParamNames.HysteresisDb, 6.0));
        timeline.Tracks.Add(track);

        AudioTrack loaded = Assert.IsType<AudioTrack>(RoundTrip(project).Timeline.Tracks[0]);
        EffectInstance gate = Assert.Single(loaded.Effects);
        Assert.Equal(EffectTypeIds.AudioNoiseGate, gate.EffectTypeId);
        Assert.Equal(-45.0, gate.Parameters[EffectParamNames.ThresholdDb].Evaluate(Timecode.Zero));
        Assert.Equal(2.5, gate.Parameters[EffectParamNames.AttackMs].Evaluate(Timecode.Zero));
        Assert.Equal(120.0, gate.Parameters[EffectParamNames.HoldMs].Evaluate(Timecode.Zero));
        Assert.Equal(250.0, gate.Parameters[EffectParamNames.ReleaseMs].Evaluate(Timecode.Zero));
        Assert.Equal(-30.0, gate.Parameters[EffectParamNames.RangeDb].Evaluate(Timecode.Zero));
        Assert.Equal(6.0, gate.Parameters[EffectParamNames.HysteresisDb].Evaluate(Timecode.Zero));
    }

    [Fact]
    public void Chainless_Project_Omits_The_Chain_Fields_In_Json()
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        project.Timeline.Tracks.Add(new AudioTrack { Name = "A1" });

        string json = ProjectSerializer.Serialize(project);
        // Additive format discipline: empty chains write nothing, so pre-31 files and chain-less projects
        // keep the same wire shape (WhenWritingNull).
        Assert.DoesNotContain("audioEffects", json);
        Assert.DoesNotContain("masterAudioEffects", json);
    }
}
