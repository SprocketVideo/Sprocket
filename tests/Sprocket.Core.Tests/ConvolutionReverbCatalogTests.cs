using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Audio;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Core-side contract of the Convolution Reverb (PLAN.md step 49): the catalog entry with its asset-kind IR
/// descriptor, the heavy-effect trait, and the new asset plumbing on <see cref="EffectInstance"/> —
/// <see cref="EffectInstance.Assets"/>, cloning, the undoable <see cref="SetEffectAssetCommand"/>, and
/// <see cref="ResolvedEffect.GetAsset"/>.
/// </summary>
public class ConvolutionReverbCatalogTests
{
    [Fact]
    public void Convolution_Reverb_Is_Registered_As_An_Audio_Effect()
    {
        EffectDescriptor? conv = EffectCatalog.Find(EffectTypeIds.AudioConvolutionReverb);
        Assert.NotNull(conv);
        Assert.Equal(EffectCategory.Audio, conv!.Category);
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioConvolutionReverb));
        Assert.Equal("Convolution Reverb", EffectCatalog.DisplayName(EffectTypeIds.AudioConvolutionReverb));
        Assert.Equal("IR", conv.ShortCode);
        Assert.Empty(conv.Presets); // with user IRs the impulse response IS the preset
    }

    [Fact]
    public void Convolution_Reverb_Exposes_The_Step49_Parameters()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.AudioConvolutionReverb)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.ImpulseResponse, EffectParamNames.PreDelayMs, EffectParamNames.IrLength,
                EffectParamNames.LowDamp, EffectParamNames.HighDamp, EffectParamNames.Width, EffectParamNames.Mix,
            },
            names);
    }

    [Fact]
    public void The_Impulse_Response_Is_An_Asset_Descriptor_And_CreateInstance_Leaves_It_Unset()
    {
        EffectDescriptor conv = EffectCatalog.Find(EffectTypeIds.AudioConvolutionReverb)!;
        EffectParameterDescriptor ir = conv.Parameters.Single(p => p.Name == EffectParamNames.ImpulseResponse);
        Assert.Equal(ParameterKind.Asset, ir.Kind);

        EffectInstance instance = conv.CreateInstance();
        Assert.DoesNotContain(EffectParamNames.ImpulseResponse, instance.Parameters.Keys); // not a number
        Assert.Empty(instance.Assets);                                                    // no bundled IR
        Assert.Equal(1.0, instance.Parameters[EffectParamNames.IrLength].Evaluate(Timecode.Zero));
        Assert.Equal(0.3, instance.Parameters[EffectParamNames.Mix].Evaluate(Timecode.Zero));
    }

    [Fact]
    public void Convolution_Reverb_Is_A_Heavy_Effect()
    {
        Assert.True(AudioEffectTraits.IsHeavy(EffectTypeIds.AudioConvolutionReverb));
        Assert.True(AudioEffectTraits.HasHeavyEffect([new EffectInstance(EffectTypeIds.AudioConvolutionReverb)]));
    }

    [Fact]
    public void SetAsset_Sets_And_Clears()
    {
        var effect = new EffectInstance(EffectTypeIds.AudioConvolutionReverb)
            .SetAsset(EffectParamNames.ImpulseResponse, @"C:\ir\hall.wav");
        Assert.Equal(@"C:\ir\hall.wav", effect.Assets[EffectParamNames.ImpulseResponse]);
        effect.SetAsset(EffectParamNames.ImpulseResponse, null);
        Assert.Empty(effect.Assets);
        effect.SetAsset(EffectParamNames.ImpulseResponse, "x").SetAsset(EffectParamNames.ImpulseResponse, "");
        Assert.Empty(effect.Assets);
    }

    [Fact]
    public void Clone_And_CloneShifted_Copy_Assets_Independently()
    {
        var effect = new EffectInstance(EffectTypeIds.AudioConvolutionReverb)
            .Set(EffectParamNames.Mix, 0.5)
            .SetAsset(EffectParamNames.ImpulseResponse, "hall.wav");
        EffectInstance clone = effect.Clone();
        EffectInstance shifted = effect.CloneShifted(Timecode.FromSeconds(1));
        Assert.Equal("hall.wav", clone.Assets[EffectParamNames.ImpulseResponse]);
        Assert.Equal("hall.wav", shifted.Assets[EffectParamNames.ImpulseResponse]);
        clone.SetAsset(EffectParamNames.ImpulseResponse, "plate.wav");
        Assert.Equal("hall.wav", effect.Assets[EffectParamNames.ImpulseResponse]); // the map is fresh, not shared
    }

    [Fact]
    public void SetEffectAssetCommand_Is_Undoable_In_Both_Directions()
    {
        var effect = new EffectInstance(EffectTypeIds.AudioConvolutionReverb);
        var history = new EditHistory();

        history.Execute(new SetEffectAssetCommand(effect, EffectParamNames.ImpulseResponse, "hall.wav"));
        Assert.Equal("hall.wav", effect.Assets[EffectParamNames.ImpulseResponse]);
        history.Execute(new SetEffectAssetCommand(effect, EffectParamNames.ImpulseResponse, "plate.wav"));
        Assert.Equal("plate.wav", effect.Assets[EffectParamNames.ImpulseResponse]);
        history.Execute(new SetEffectAssetCommand(effect, EffectParamNames.ImpulseResponse, null)); // clear
        Assert.Empty(effect.Assets);

        Assert.True(history.Undo());
        Assert.Equal("plate.wav", effect.Assets[EffectParamNames.ImpulseResponse]);
        Assert.True(history.Undo());
        Assert.Equal("hall.wav", effect.Assets[EffectParamNames.ImpulseResponse]);
        Assert.True(history.Undo());
        Assert.Empty(effect.Assets); // back to the fresh, asset-less instance
        Assert.True(history.Redo());
        Assert.Equal("hall.wav", effect.Assets[EffectParamNames.ImpulseResponse]);
    }

    [Fact]
    public void ResolvedEffect_GetAsset_Reads_The_Reference_And_Falls_Back()
    {
        var withAsset = new ResolvedEffect(EffectTypeIds.AudioConvolutionReverb, new Dictionary<string, double>(),
            new Dictionary<string, string> { [EffectParamNames.ImpulseResponse] = "hall.wav" });
        var without = new ResolvedEffect(EffectTypeIds.AudioConvolutionReverb, new Dictionary<string, double>());
        Assert.Equal("hall.wav", withAsset.GetAsset(EffectParamNames.ImpulseResponse));
        Assert.Equal("", without.GetAsset(EffectParamNames.ImpulseResponse));
        Assert.Equal("none", without.GetAsset(EffectParamNames.ImpulseResponse, "none"));
    }

    [Fact]
    public void The_Audio_Plan_Carries_The_Asset_Into_The_Resolved_Chain()
    {
        // RenderGraph.ResolveAudioChain hands the asset map through so the mixer's DSP can resolve the IR.
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new AudioTrack { Name = "A" };
        track.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero));
        track.Effects.Add(new EffectInstance(EffectTypeIds.AudioConvolutionReverb)
            .Set(EffectParamNames.Mix, 1.0)
            .SetAsset(EffectParamNames.ImpulseResponse, "hall.wav"));
        timeline.Tracks.Add(track);

        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, Timecode.Zero, Timecode.FromSamples(512, 48000));
        AudioLayer layer = Assert.Single(plan.Layers);
        ResolvedEffect conv = Assert.Single(layer.TrackChain!.Effects);
        Assert.Equal("hall.wav", conv.GetAsset(EffectParamNames.ImpulseResponse));
    }
}
