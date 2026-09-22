using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The action-VFX primitives' catalog entries (plan/features/special-effects.md, phase 1): the eight
/// reusable optical/geometric effects the later action, atmospheric and day-for-night presets are assembled
/// from. The shared catalog invariants (ranges, tooltips, percent scaling, unique short codes) are asserted
/// over every built-in by <see cref="EffectCatalogTests"/> and <see cref="ParameterKindTests"/>; this file
/// pins the things specific to these eight — that they are all offered under Video, that their parameter
/// sets are the ones the shaders bind, and that the keyframe-driven ones default to doing nothing.
/// </summary>
public sealed class VfxPrimitiveCatalogTests
{
    private static readonly string[] Ids =
    [
        EffectTypeIds.Glow,
        EffectTypeIds.DirectionalBlur,
        EffectTypeIds.ZoomBlur,
        EffectTypeIds.HeatDistortion,
        EffectTypeIds.Shockwave,
        EffectTypeIds.ChromaticAberration,
        EffectTypeIds.ImpactShake,
        EffectTypeIds.Flicker,
    ];

    [Fact]
    public void All_Eight_Primitives_Are_Registered_Under_Video()
    {
        foreach (string id in Ids)
        {
            EffectDescriptor? d = EffectCatalog.Find(id);
            Assert.True(d is not null, $"{id} is missing from EffectCatalog.BuiltIns");
            Assert.Equal(EffectCategory.Video, d!.Category);
            Assert.False(EffectTypeIds.IsAudio(id)); // they render, they do not route to the mixer
            Assert.NotNull(d.ShortCode);
        }
    }

    [Fact]
    public void Each_Primitive_Declares_The_Parameters_Its_Shader_Binds()
    {
        Assert.Equal(
            new[] { EffectParamNames.Threshold, EffectParamNames.Radius, EffectParamNames.Intensity },
            Names(EffectTypeIds.Glow));
        Assert.Equal(
            new[] { EffectParamNames.Angle, EffectParamNames.BlurLength },
            Names(EffectTypeIds.DirectionalBlur));
        Assert.Equal(
            new[] { EffectParamNames.Amount, EffectParamNames.CenterX, EffectParamNames.CenterY },
            Names(EffectTypeIds.ZoomBlur));
        Assert.Equal(
            new[] { EffectParamNames.Amount, EffectParamNames.NoiseScale, EffectParamNames.Speed },
            Names(EffectTypeIds.HeatDistortion));
        Assert.Equal(
            new[]
            {
                EffectParamNames.Radius, EffectParamNames.RingWidth, EffectParamNames.Amplitude,
                EffectParamNames.CenterX, EffectParamNames.CenterY,
            },
            Names(EffectTypeIds.Shockwave));
        Assert.Equal(
            new[] { EffectParamNames.Amount, EffectParamNames.CenterX, EffectParamNames.CenterY },
            Names(EffectTypeIds.ChromaticAberration));
        Assert.Equal(
            new[]
            {
                EffectParamNames.Amount, EffectParamNames.Frequency, EffectParamNames.Rotation,
                EffectParamNames.Overscan,
            },
            Names(EffectTypeIds.ImpactShake));
        Assert.Equal(
            new[] { EffectParamNames.Amount, EffectParamNames.Frequency, EffectParamNames.Randomness },
            Names(EffectTypeIds.Flicker));
    }

    [Fact]
    public void The_Keyframe_Driven_Primitives_Default_To_No_Visible_Change()
    {
        // Zoom Blur, Chromatic Aberration and Shockwave are animated by hand, so dropping one on a clip must
        // do nothing until the user keyframes it — a visible jump on apply would be a surprise.
        Assert.Equal(0.0, Default(EffectTypeIds.ZoomBlur, EffectParamNames.Amount));
        Assert.Equal(0.0, Default(EffectTypeIds.ChromaticAberration, EffectParamNames.Amount));
        Assert.Equal(0.0, Default(EffectTypeIds.Shockwave, EffectParamNames.Radius));
    }

    [Fact]
    public void Every_Spatial_Parameter_Is_A_Fraction_Of_The_Frame_Not_A_Pixel_Count()
    {
        // The whole set is resolution-independent by construction (a preview at one size and an export at
        // another must match), so no spatial parameter may range beyond a small multiple of the frame.
        string[] spatial =
        [
            EffectParamNames.Radius, EffectParamNames.BlurLength, EffectParamNames.RingWidth,
            EffectParamNames.Amplitude,
        ];
        foreach (string id in Ids)
            foreach (EffectParameterDescriptor p in EffectCatalog.Find(id)!.Parameters.Where(p => spatial.Contains(p.Name)))
                Assert.InRange(p.Max, 0.0, 1.5);
    }

    [Fact]
    public void The_Primitives_Ship_No_Presets_Yet()
    {
        // The action / atmospheric preset stacks are phase 3 — the primitives themselves are raw building
        // blocks, so an empty preset list here is the expected state, not an oversight.
        foreach (string id in Ids)
            Assert.Empty(EffectCatalog.Find(id)!.Presets);
    }

    private static string[] Names(string id) =>
        [.. EffectCatalog.Find(id)!.Parameters.Select(p => p.Name)];

    private static double Default(string id, string parameter) =>
        EffectCatalog.Find(id)!.Parameters.Single(p => p.Name == parameter).Default;
}
