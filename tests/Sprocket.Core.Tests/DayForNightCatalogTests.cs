using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// The Day for Night toolkit's catalog entry and looks (plan/features/special-effects.md, phase 4). The shared
/// catalog invariants (ranges, tooltips, percent scaling, unique short codes, preset values in range) are
/// asserted over every built-in by <see cref="EffectCatalogTests"/>; this file pins the things specific to the
/// toolkit — the control set the shader binds, that every look is complete and keeps the user's Night
/// Strength, and the preset-aware instance factory the one-click entry points use.
/// </summary>
public sealed class DayForNightCatalogTests
{
    private static EffectDescriptor Descriptor => EffectCatalog.Find(EffectTypeIds.DayForNight)!;

    /// <summary>Every parameter a look sets: all of them except the master blend.</summary>
    private static IEnumerable<string> GradeParameters =>
        Descriptor.Parameters.Select(p => p.Name).Where(n => n != EffectParamNames.NightStrength);

    [Fact]
    public void Is_Registered_As_A_Color_Effect()
    {
        EffectDescriptor d = Descriptor;
        Assert.NotNull(d);
        Assert.Equal("Day for Night", d.DisplayName);
        Assert.Equal(EffectCategory.Color, d.Category);
        Assert.Equal("DN", d.ShortCode);
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.DayForNight));
    }

    [Fact]
    public void Declares_The_Guided_Controls_In_Display_Order()
    {
        Assert.Equal(
            new[]
            {
                EffectParamNames.NightStrength, EffectParamNames.Exposure, EffectParamNames.SkyDarken,
                EffectParamNames.HighlightRolloff, EffectParamNames.ShadowFloor, EffectParamNames.Saturation,
                EffectParamNames.MoonlightTint, EffectParamNames.MoonlightHue, EffectParamNames.PracticalLights,
                EffectParamNames.ProtectSkin, EffectParamNames.VignetteAmount,
            },
            Descriptor.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Defaults_Are_A_Full_Strength_Night_That_Only_Ever_Darkens()
    {
        EffectDescriptor d = Descriptor;
        Assert.Equal(1.0, d.Parameters.Single(p => p.Name == EffectParamNames.NightStrength).Default);
        EffectParameterDescriptor exposure = d.Parameters.Single(p => p.Name == EffectParamNames.Exposure);
        Assert.True(exposure.Default < 0);
        Assert.Equal(0.0, exposure.Max); // day for night never brightens the plate overall
    }

    [Fact]
    public void Ships_The_Roadmap_Looks()
    {
        Assert.Equal(
            new[] { "Standard", "Exterior Wide", "Street Scene", "Blue Moon" },
            Descriptor.Presets.Select(p => p.Name));
        Assert.All(Descriptor.Presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void Every_Look_Is_Complete_And_Leaves_Night_Strength_Alone()
    {
        foreach (EffectPreset preset in Descriptor.Presets)
        {
            Assert.False(preset.Values.ContainsKey(EffectParamNames.NightStrength),
                $"'{preset.Name}' must keep the user's Night Strength");
            Assert.Equal(GradeParameters.OrderBy(n => n), preset.Values.Keys.OrderBy(n => n));
        }
    }

    [Fact]
    public void The_Standard_Look_Is_The_Descriptor_Defaults()
    {
        foreach (EffectParameterDescriptor p in Descriptor.Parameters.Where(p => p.Name != EffectParamNames.NightStrength))
            Assert.Equal(p.Default, DayForNightPresets.Standard.Values[p.Name], 6);
    }

    [Fact]
    public void The_Looks_Differ_Where_Their_Names_Say_They_Do()
    {
        // Exterior Wide leans on the sky; Street Scene keeps practicals and faces; Blue Moon is the strongest cast.
        Assert.True(DayForNightPresets.ExteriorWide.Values[EffectParamNames.SkyDarken]
                    > DayForNightPresets.StreetScene.Values[EffectParamNames.SkyDarken]);
        Assert.True(DayForNightPresets.StreetScene.Values[EffectParamNames.PracticalLights]
                    > DayForNightPresets.ExteriorWide.Values[EffectParamNames.PracticalLights]);
        Assert.True(DayForNightPresets.StreetScene.Values[EffectParamNames.ProtectSkin]
                    > DayForNightPresets.BlueMoon.Values[EffectParamNames.ProtectSkin]);
        Assert.All(DayForNightPresets.All.Where(p => p != DayForNightPresets.BlueMoon), p =>
            Assert.True(DayForNightPresets.BlueMoon.Values[EffectParamNames.MoonlightTint] > p.Values[EffectParamNames.MoonlightTint]));
    }

    [Fact]
    public void CreateInstance_With_A_Preset_Applies_It_Over_The_Defaults()
    {
        EffectInstance instance = Descriptor.CreateInstance(DayForNightPresets.BlueMoon);

        Assert.Equal(EffectTypeIds.DayForNight, instance.EffectTypeId);
        Assert.Equal(Descriptor.Parameters.Count, instance.Parameters.Count);
        foreach ((string name, double value) in DayForNightPresets.BlueMoon.Values)
            Assert.Equal(value, instance.Parameters[name].Evaluate(Timecode.Zero), 6);
        Assert.Equal(1.0, instance.Parameters[EffectParamNames.NightStrength].Evaluate(Timecode.Zero), 6);
    }

    [Fact]
    public void CreateInstance_Rejects_A_Preset_For_Another_Effect()
    {
        EffectPreset foreign = EffectCatalog.Find(EffectTypeIds.AudioStudioReverb)!.Presets[0];
        Assert.Throws<ArgumentException>(() => Descriptor.CreateInstance(foreign));
    }

    [Fact]
    public void FindPreset_Is_Case_Insensitive_And_Null_For_Unknown_Names()
    {
        Assert.Same(DayForNightPresets.StreetScene, Descriptor.FindPreset("street SCENE"));
        Assert.Null(Descriptor.FindPreset("High Noon"));
        Assert.Null(EffectCatalog.Find(EffectTypeIds.Brightness)!.FindPreset("Standard"));
    }
}
