using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Audio effect chain resolution in the render graph (PLAN.md step 31, ARCHITECTURE.md §19): the four chain
/// scopes (clip → track → sequence bus → project master), audio/video id filtering, per-buffer parameter
/// evaluation, split clip/track gains, state keys, and the chain edit commands.
/// </summary>
public class AudioEffectChainTests
{
    private static Project ProjectWithAudio(out AudioTrack track, out Clip clip)
    {
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        track = new AudioTrack();
        clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero);
        track.Clips.Add(clip);
        project.Timeline.Tracks.Add(track);
        return project;
    }

    private static AudioBufferPlan Plan(Project project, double atSeconds = 0) =>
        RenderGraph.PlanAudioBuffer(project, Timecode.FromSeconds(atSeconds), Timecode.FromSeconds(0.1));

    [Fact]
    public void Audio_Ids_Are_Recognised()
    {
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioGain));
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioEq));
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioCompressor));
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioReverb));
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.Brightness));
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.Fade));
    }

    [Fact]
    public void Audio_Effects_Are_Registered_In_The_Catalog()
    {
        string[] audio = EffectCatalog.InCategory(EffectCategory.Audio).Select(d => d.Id).ToArray();
        Assert.Contains(EffectTypeIds.AudioGain, audio);
        Assert.Contains(EffectTypeIds.AudioEq, audio);
        Assert.Contains(EffectTypeIds.AudioCompressor, audio);
        Assert.Contains(EffectTypeIds.AudioReverb, audio);
    }

    [Fact]
    public void Clip_Audio_Effect_Resolves_Into_The_Layer_Clip_Chain()
    {
        Project project = ProjectWithAudio(out _, out Clip clip);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioGain).Set(EffectParamNames.GainDb, -6.0));

        AudioLayer layer = Assert.Single(Plan(project).Layers);
        Assert.NotNull(layer.ClipChain);
        ResolvedEffect effect = Assert.Single(layer.ClipChain!.Effects);
        Assert.Equal(EffectTypeIds.AudioGain, effect.EffectTypeId);
        Assert.Equal(-6.0, effect.Get(EffectParamNames.GainDb), 6);
        Assert.Same(clip, layer.ClipChain.StateKey);
    }

    [Fact]
    public void Video_Effects_And_Fades_Stay_Out_Of_The_Audio_Chain()
    {
        Project project = ProjectWithAudio(out _, out Clip clip);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Brightness).Set(EffectParamNames.Amount, 1.5));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.Fade).Set(EffectParamNames.Opacity, 0.5));

        AudioLayer layer = Assert.Single(Plan(project).Layers);
        // No audio ids → no chain; the fade still lands where it always has, in the gain envelope.
        Assert.Null(layer.ClipChain);
        Assert.Equal(0.5, layer.GainStartLinear, 6);
    }

    [Fact]
    public void Chainless_Project_Plans_With_No_Chains()
    {
        Project project = ProjectWithAudio(out _, out _);
        AudioBufferPlan plan = Plan(project);
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.Null(layer.ClipChain);
        Assert.Null(layer.TrackChain);
        Assert.Null(plan.OutputChains);
    }

    [Fact]
    public void Track_Chain_Resolves_With_The_Track_As_State_Key()
    {
        Project project = ProjectWithAudio(out AudioTrack track, out _);
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioCompressor).Set(EffectParamNames.Ratio, 8.0));

        AudioLayer layer = Assert.Single(Plan(project).Layers);
        Assert.NotNull(layer.TrackChain);
        Assert.Same(track, layer.TrackChain!.StateKey);
        Assert.Equal(8.0, Assert.Single(layer.TrackChain.Effects).Get(EffectParamNames.Ratio), 6);
    }

    [Fact]
    public void Bus_And_Master_Chains_Land_In_Output_Chains_In_Order()
    {
        Project project = ProjectWithAudio(out _, out _);
        project.Timeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioEq));
        project.Settings.MasterAudioEffects.Add(new EffectInstance(EffectTypeIds.AudioReverb));

        AudioBufferPlan plan = Plan(project);
        Assert.NotNull(plan.OutputChains);
        Assert.Equal(2, plan.OutputChains!.Count);
        // Bus first (keyed by the timeline), then the project master (keyed by the settings).
        Assert.Equal(EffectTypeIds.AudioEq, Assert.Single(plan.OutputChains[0].Effects).EffectTypeId);
        Assert.Same(project.Timeline, plan.OutputChains[0].StateKey);
        Assert.Equal(EffectTypeIds.AudioReverb, Assert.Single(plan.OutputChains[1].Effects).EffectTypeId);
        Assert.Same(project.Settings, plan.OutputChains[1].StateKey);
    }

    [Fact]
    public void Split_Gains_Compose_To_The_Combined_Gain()
    {
        Project project = ProjectWithAudio(out AudioTrack track, out Clip clip);
        track.GainDb = -6.0206; // ≈ 0.5
        clip.GainDb = -6.0206;  // ≈ 0.5

        AudioLayer layer = Assert.Single(Plan(project).Layers);
        Assert.Equal(0.5, layer.TrackGainLinear, 3);
        Assert.Equal(0.5, layer.ClipGainStartLinear, 3);
        Assert.Equal(0.25, layer.GainStartLinear, 3);
    }

    [Fact]
    public void Keyframed_Chain_Parameters_Evaluate_At_The_Buffer_Start()
    {
        Project project = ProjectWithAudio(out _, out Clip clip);
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioGain).Set(
            EffectParamNames.GainDb,
            AnimatableValue.Animated(new[]
            {
                new Keyframe(Timecode.FromSeconds(0), 0.0),
                new Keyframe(Timecode.FromSeconds(10), -10.0),
            })));

        AudioLayer layer = Assert.Single(Plan(project, atSeconds: 5).Layers);
        Assert.Equal(-5.0, Assert.Single(layer.ClipChain!.Effects).Get(EffectParamNames.GainDb), 3);
    }

    [Fact]
    public void Unity_Master_Gain_Scope_Bypasses_The_Master_Chain()
    {
        Project project = ProjectWithAudio(out AudioTrack track, out _);
        project.Settings.MasterAudioEffects.Add(new EffectInstance(EffectTypeIds.AudioGain));

        var scope = new AudioPlanScope(OnlyTrack: track, UnityTrackGain: true, UnityMasterGain: true);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(
            project, project.ActiveSequence, Timecode.Zero, Timecode.FromSeconds(0.1), scope);
        Assert.Null(plan.OutputChains);
    }

    [Fact]
    public void Nested_Sequence_Bus_Chain_Is_Keyed_By_The_Nesting_Clip()
    {
        // A child sequence with an audio clip and a bus chain, nested in the parent's audio track.
        var project = new Project(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000));
        var childTimeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var childTrack = new AudioTrack();
        childTrack.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        childTimeline.Tracks.Add(childTrack);
        childTimeline.AudioEffects.Add(new EffectInstance(EffectTypeIds.AudioEq));
        var child = new Sequence(SequenceId.New(), "Child", childTimeline);
        project.Sequences.Add(child);

        var parentTrack = new AudioTrack();
        Clip nesting = Clip.CreateSequenceClip(child.Id, Timecode.FromSeconds(10), Timecode.Zero);
        parentTrack.Clips.Add(nesting);
        project.Timeline.Tracks.Add(parentTrack);

        AudioBufferPlan plan = Plan(project);
        AudioLayer layer = Assert.Single(plan.Layers);
        Assert.NotNull(layer.NestedPlan);
        ResolvedAudioChain bus = Assert.Single(layer.NestedPlan!.OutputChains!);
        Assert.Same(nesting, bus.StateKey); // per nesting clip, so two nests of the same child stay independent
    }

    [Fact]
    public void Chain_Commands_Apply_And_Revert()
    {
        Project project = ProjectWithAudio(out AudioTrack track, out _);
        var history = new EditHistory();
        var effect = new EffectInstance(EffectTypeIds.AudioEq);

        history.Execute(new AddChainEffectCommand(track.Effects, effect));
        Assert.Same(effect, Assert.Single(track.Effects));
        history.Undo();
        Assert.Empty(track.Effects);
        history.Redo();

        var second = new EffectInstance(EffectTypeIds.AudioReverb);
        history.Execute(new AddChainEffectCommand(track.Effects, second));
        history.Execute(new RemoveChainEffectCommand(track.Effects, effect));
        Assert.Same(second, Assert.Single(track.Effects));
        history.Undo();
        // Undo re-inserts at position 0 — chain order is processing order.
        Assert.Equal(2, track.Effects.Count);
        Assert.Same(effect, track.Effects[0]);
    }

    [Fact]
    public void Move_Command_Reorders_A_Track_Chain_And_Undoes()
    {
        // The same MoveChainEffectCommand serves every chain scope (PLAN.md step 51) — exercise the
        // track insert chain here; the clip stack is covered in CommandTests.
        Project project = ProjectWithAudio(out AudioTrack track, out _);
        var history = new EditHistory();
        var eq = new EffectInstance(EffectTypeIds.AudioEq);
        var reverb = new EffectInstance(EffectTypeIds.AudioReverb);
        track.Effects.AddRange([eq, reverb]);

        history.Execute(new MoveChainEffectCommand(track.Effects, reverb, 0));
        Assert.Equal([reverb, eq], track.Effects);
        history.Undo();
        Assert.Equal([eq, reverb], track.Effects);
    }
}
