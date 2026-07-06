using Sprocket.App.Mixer;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The mixer insert-chain targets (PLAN.md step 31's track / bus / master chain UI): what the mixer rows and
/// the Inspector's chain view agree on — which model list a scope edits, the DSP state key live metering
/// looks effects up under (it must match the render graph's chain keying), display titles, staleness
/// (<see cref="AudioChainTarget.IsAlive"/>), and the change-detection signature the mixer rebuilds on.
/// The controls' wiring rests on these plus manual verification, like <see cref="EffectReorderTests"/>.
/// </summary>
public sealed class AudioChainTargetTests
{
    private static Project MakeProject(out AudioTrack track)
    {
        var project = new Project();
        track = new AudioTrack { Name = "Music" };
        project.Timeline.Tracks.Add(track);
        return project;
    }

    [Fact]
    public void ForTrack_EditsTheTrackChain_AndKeysStateByTheTrack()
    {
        Project project = MakeProject(out AudioTrack track);
        AudioChainTarget target = AudioChainTarget.ForTrack(track);
        Assert.Same(track.Effects, target.Chain);
        Assert.Same(track, target.StateKey); // RenderGraph keys the track chain by the track object
        Assert.Equal("Track Inserts — Music", target.Title);
        Assert.True(target.IsAlive(project));
    }

    [Fact]
    public void ForTrack_UnnamedTrack_FallsBackLikeTheMixerStrip()
    {
        var track = new AudioTrack();
        Assert.Equal("Track Inserts — Audio", AudioChainTarget.ForTrack(track).Title);
    }

    [Fact]
    public void ForSequenceBus_EditsTheBusChain_AndKeysStateByTheTimeline()
    {
        Project project = MakeProject(out _);
        AudioChainTarget target = AudioChainTarget.ForSequenceBus(project.Timeline, project.ActiveSequence.Name);
        Assert.Same(project.Timeline.AudioEffects, target.Chain);
        Assert.Same(project.Timeline, target.StateKey); // the root bus is keyed by the timeline
        Assert.Equal("Bus Inserts — Sequence 1", target.Title);
        Assert.True(target.IsAlive(project));
    }

    [Fact]
    public void ForMaster_EditsTheMasterChain_AndKeysStateByTheSettings()
    {
        Project project = MakeProject(out _);
        AudioChainTarget target = AudioChainTarget.ForMaster(project.Settings);
        Assert.Same(project.Settings.MasterAudioEffects, target.Chain);
        Assert.Same(project.Settings, target.StateKey); // the master chain is keyed by the settings object
        Assert.Equal("Master Inserts", target.Title);
        Assert.True(target.IsAlive(project));
    }

    [Fact]
    public void IsAlive_False_AfterTheTrackIsRemoved()
    {
        Project project = MakeProject(out AudioTrack track);
        AudioChainTarget target = AudioChainTarget.ForTrack(track);
        project.Timeline.Tracks.Remove(track);
        Assert.False(target.IsAlive(project));
    }

    [Fact]
    public void IsAlive_False_AfterTheActiveSequenceChanges()
    {
        Project project = MakeProject(out _);
        AudioChainTarget bus = AudioChainTarget.ForSequenceBus(project.Timeline, project.ActiveSequence.Name);

        var other = new Sequence(SequenceId.New(), "Sequence 2",
            new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        project.Sequences.Add(other);
        project.ActiveSequence = other;

        Assert.False(bus.IsAlive(project)); // the open sequence's bus is a different chain now
        Assert.True(AudioChainTarget.ForMaster(project.Settings).IsAlive(project)); // master survives navigation
    }

    [Fact]
    public void IsAlive_False_AgainstADifferentProject()
    {
        Project project = MakeProject(out AudioTrack track);
        var other = new Project();
        Assert.False(AudioChainTarget.ForTrack(track).IsAlive(other));
        Assert.False(AudioChainTarget.ForMaster(project.Settings).IsAlive(other));
    }

    // ── change-detection signature (what makes the mixer rebuild its insert rows) ─────────────────────

    private static bool SignaturesEqual(List<object> a, List<object> b) => a.SequenceEqual(b);

    [Fact]
    public void Signature_StableAcrossReads_WhenNothingChanged()
    {
        Project project = MakeProject(out AudioTrack track);
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioGain));
        Assert.True(SignaturesEqual(AudioChainTarget.Signature(project), AudioChainTarget.Signature(project)));
    }

    [Theory]
    [InlineData("track")]
    [InlineData("bus")]
    [InlineData("master")]
    public void Signature_Changes_WhenAnEffectIsAdded(string scope)
    {
        Project project = MakeProject(out AudioTrack track);
        List<object> before = AudioChainTarget.Signature(project);
        List<EffectInstance> chain = scope switch
        {
            "track" => track.Effects,
            "bus" => project.Timeline.AudioEffects,
            _ => project.Settings.MasterAudioEffects,
        };
        chain.Add(new EffectInstance(EffectTypeIds.AudioReverb));
        Assert.False(SignaturesEqual(before, AudioChainTarget.Signature(project)));
    }

    [Fact]
    public void Signature_Changes_OnRemoveReorderAndToggle()
    {
        Project project = MakeProject(out AudioTrack track);
        var eq = new EffectInstance(EffectTypeIds.AudioEq);
        var comp = new EffectInstance(EffectTypeIds.AudioCompressor);
        track.Effects.Add(eq);
        track.Effects.Add(comp);

        List<object> built = AudioChainTarget.Signature(project);

        eq.Enabled = false; // bypass toggle re-renders the LED → rebuild
        Assert.False(SignaturesEqual(built, AudioChainTarget.Signature(project)));
        eq.Enabled = true;
        Assert.True(SignaturesEqual(built, AudioChainTarget.Signature(project)));

        (track.Effects[0], track.Effects[1]) = (track.Effects[1], track.Effects[0]); // reorder
        Assert.False(SignaturesEqual(built, AudioChainTarget.Signature(project)));
        (track.Effects[0], track.Effects[1]) = (track.Effects[1], track.Effects[0]);

        track.Effects.Remove(comp); // remove
        Assert.False(SignaturesEqual(built, AudioChainTarget.Signature(project)));
    }

    [Fact]
    public void Signature_Unchanged_ByGainPanAndClipEdits()
    {
        Project project = MakeProject(out AudioTrack track);
        List<object> before = AudioChainTarget.Signature(project);

        // The edits a fader drag / timeline edit issues must NOT force an insert-row rebuild.
        track.GainDb = -6;
        track.Pan = 0.4;
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(3), Timecode.Zero);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioReverb)); // clip-scope chain is not mixer-visible
        track.Clips.Add(clip);

        Assert.True(SignaturesEqual(before, AudioChainTarget.Signature(project)));
    }
}
