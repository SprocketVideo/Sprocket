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
            $"{EffectTypeIds.AudioDelayStereo}.{EffectParamNames.PingPong}",
            $"{EffectTypeIds.AudioShelvingEq}.{EffectParamNames.LowEnable}",
            $"{EffectTypeIds.AudioShelvingEq}.{EffectParamNames.HighEnable}",
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
    public void ShimmerInterval_Is_The_Only_Integer()
    {
        (EffectDescriptor effect, EffectParameterDescriptor p) = Assert.Single(
            AllBuiltInParams().Where(x => x.Param.Kind == ParameterKind.Integer));
        Assert.Equal(EffectTypeIds.AudioShimmerReverb, effect.Id);
        Assert.Equal(EffectParamNames.ShimmerInterval, p.Name);
    }

    [Fact]
    public void Everything_Else_Is_Continuous_Including_Step_One_Scalars()
    {
        // Kind is never inferred: genuinely continuous step-1 parameters stay Continuous.
        EffectParameterDescriptor rotation = EffectCatalog.Find(EffectTypeIds.Transform)!
            .Parameters.Single(p => p.Name == EffectParamNames.Rotation);
        Assert.Equal(ParameterKind.Continuous, rotation.Kind);

        int discrete = AllBuiltInParams().Count(x => x.Param.Kind != ParameterKind.Continuous);
        // 12 toggles (ShowMask, PingPong, Low/HighEnable, 8 tap enables) + 1 dropdown + 1 integer.
        Assert.Equal(14, discrete);
    }
}
