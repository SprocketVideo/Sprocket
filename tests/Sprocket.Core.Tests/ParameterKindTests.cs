using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The typed parameter-control kinds on <see cref="EffectParameterDescriptor"/>: the exact set of built-in
/// descriptors tagged Toggle / Integer / Dropdown (everything else stays Continuous — kind is declared, never
/// inferred from Min/Max/Step, since continuous params like Rotation also use a step of 1), plus the shape
/// invariants each kind implies (toggles are 0/1, dropdowns carry choices matching their range).
/// </summary>
public sealed class ParameterKindTests
{
    private static IEnumerable<(EffectDescriptor Effect, EffectParameterDescriptor Param)> AllBuiltInParams() =>
        EffectCatalog.BuiltIns.SelectMany(e => e.Parameters.Select(p => (e, p)));

    [Fact]
    public void Exactly_The_Expected_Descriptors_Are_Toggles()
    {
        string[] expected =
        [
            $"{EffectTypeIds.HslQualifier}.{EffectParamNames.ShowMask}",
            $"{EffectTypeIds.BlackWhite}.{EffectParamNames.GrainSeedLock}",
            $"{EffectTypeIds.Mosaic}.{EffectParamNames.SharpColors}",
            $"{EffectTypeIds.AudioDelayStereo}.{EffectParamNames.PingPong}",
            $"{EffectTypeIds.AudioShelvingEq}.{EffectParamNames.LowEnable}",
            $"{EffectTypeIds.AudioShelvingEq}.{EffectParamNames.HighEnable}",
            $"{EffectTypeIds.AudioCassette}.{EffectParamNames.Mono}",
            $"{EffectTypeIds.Stabilization}.{EffectParamNames.LockRotation}",
            $"{EffectTypeIds.Stabilization}.{EffectParamNames.Zoom}",
            $"{EffectTypeIds.Stabilization}.{EffectParamNames.DetailedAnalysis}",
            $"{EffectTypeIds.Stabilization}.{EffectParamNames.ShowTrackPoints}",
            $"{EffectTypeIds.Stabilization}.{EffectParamNames.HideBanner}",
            .. EffectParamNames.TapEnable.Select(n => $"{EffectTypeIds.AudioDelayMultiTap}.{n}"),
        ];
        string[] actual = [.. AllBuiltInParams()
            .Where(x => x.Param.Kind == ParameterKind.Toggle)
            .Select(x => $"{x.Effect.Id}.{x.Param.Name}")];
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public void Toggles_Are_Zero_One_Flags()
    {
        foreach ((EffectDescriptor _, EffectParameterDescriptor p) in
                 AllBuiltInParams().Where(x => x.Param.Kind == ParameterKind.Toggle))
        {
            Assert.Equal(0.0, p.Min);
            Assert.Equal(1.0, p.Max);
            Assert.True(p.Default is 0.0 or 1.0, $"{p.Name} default {p.Default} is not 0/1");
            Assert.Null(p.Choices);
        }
    }

    [Fact]
    public void SourceProfile_Is_A_Dropdown_Over_The_Color_Profiles()
    {
        EffectParameterDescriptor p = EffectCatalog.Find(EffectTypeIds.ColorTransform)!
            .Parameters.Single(x => x.Name == EffectParamNames.SourceProfile);
        Assert.Equal(ParameterKind.Dropdown, p.Kind);
        Assert.Same(ColorProfiles.DisplayNames, p.Choices);
    }

    [Fact]
    public void Echo_Operator_Is_A_Dropdown_Over_After_Effects_Operators()
    {
        EffectParameterDescriptor p = EffectCatalog.Find(EffectTypeIds.Echo)!
            .Parameters.Single(x => x.Name == EffectParamNames.EchoOperator);
        Assert.Equal(ParameterKind.Dropdown, p.Kind);
        Assert.Same(EchoOperators.Names, p.Choices);
        Assert.Equal(
            ["Add", "Maximum", "Minimum", "Screen", "Composite in Back", "Composite in Front", "Blend"],
            p.Choices);
    }

    [Fact]
    public void Dropdowns_Carry_Choices_Matching_Their_Range()
    {
        foreach ((EffectDescriptor _, EffectParameterDescriptor p) in
                 AllBuiltInParams().Where(x => x.Param.Kind == ParameterKind.Dropdown))
        {
            Assert.NotNull(p.Choices);
            Assert.NotEmpty(p.Choices);
            Assert.Equal(0.0, p.Min);
            Assert.Equal(p.Choices.Count - 1, p.Max);
        }
    }

    [Fact]
    public void Exactly_The_Expected_Descriptors_Are_Integers()
    {
        string[] expected =
        [
            $"{EffectTypeIds.AudioShimmerReverb}.{EffectParamNames.ShimmerInterval}",
            $"{EffectTypeIds.Mosaic}.{EffectParamNames.HorizontalBlocks}",
            $"{EffectTypeIds.Mosaic}.{EffectParamNames.VerticalBlocks}",
            $"{EffectTypeIds.ToyCam}.{EffectParamNames.HorizontalPixels}",
            $"{EffectTypeIds.ToyCam}.{EffectParamNames.VerticalPixels}",
            $"{EffectTypeIds.ToyCam}.{EffectParamNames.Seed}",
            $"{EffectTypeIds.Echo}.{EffectParamNames.EchoCount}",
        ];
        string[] actual = [.. AllBuiltInParams()
            .Where(x => x.Param.Kind == ParameterKind.Integer)
            .Select(x => $"{x.Effect.Id}.{x.Param.Name}")];
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public void Integers_Have_Whole_Number_Defaults_Ranges_And_Unit_Step()
    {
        foreach ((EffectDescriptor _, EffectParameterDescriptor p) in
                 AllBuiltInParams().Where(x => x.Param.Kind == ParameterKind.Integer))
        {
            Assert.Equal(1.0, p.Step);
            Assert.Equal(Math.Round(p.Default), p.Default);
            Assert.Equal(Math.Round(p.Min), p.Min);
            Assert.Equal(Math.Round(p.Max), p.Max);
            Assert.Null(p.Choices);
        }
    }

    [Fact]
    public void Everything_Else_Is_Continuous_Including_Step_One_Scalars()
    {
        // Kind is never inferred: genuinely continuous step-1 parameters stay Continuous.
        EffectParameterDescriptor rotation = EffectCatalog.Find(EffectTypeIds.Transform)!
            .Parameters.Single(p => p.Name == EffectParamNames.Rotation);
        Assert.Equal(ParameterKind.Continuous, rotation.Kind);

        int discrete = AllBuiltInParams().Count(x => x.Param.Kind != ParameterKind.Continuous);
        // 20 toggles (ShowMask, B&W Static Grain, Mosaic Sharp Colors, PingPong, Low/HighEnable, Cassette Mono, 8 tap enables,
        // + Stabilization's LockRotation/Zoom/DetailedAnalysis/ShowTrackPoints/HideBanner) + 6 dropdowns
        // (SourceProfile + Stabilization's StabMode/StabMethod/ScaleMode/ScaleLockRef + Echo Operator) + 7 integers
        // (Shimmer Interval, Mosaic Horizontal/Vertical Blocks, Toy Cassette Camera Horizontal/Vertical Pixels + Seed,
        // Echo's Number of Echoes) + 2 assets (IR, creative LUT).
        Assert.Equal(35, discrete);
    }

    [Fact]
    public void The_Impulse_Response_And_Creative_Lut_Are_The_Only_Assets()
    {
        // Step 49 / looks browser: file references, not numbers — they live in EffectInstance.Assets, so no
        // numeric default; each names the file kind its picker filters to.
        (EffectDescriptor Effect, EffectParameterDescriptor Param)[] assets =
            [.. AllBuiltInParams().Where(x => x.Param.Kind == ParameterKind.Asset)];
        Assert.Equal(
            [(EffectTypeIds.AudioConvolutionReverb, EffectParamNames.ImpulseResponse), (EffectTypeIds.CreativeLut, EffectParamNames.LutFile)],
            assets.Select(x => (x.Effect.Id, x.Param.Name)).OrderBy(x => x.Id == EffectTypeIds.CreativeLut));
        Assert.All(assets, x => Assert.Null(x.Param.Choices));
        Assert.All(assets, x => Assert.NotEmpty(x.Param.AssetType!.Extensions));
    }
}
