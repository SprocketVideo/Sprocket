using System.Linq;
using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Tests the built-in effect registry (PLAN.md step 15): the Effects browser and (later) the Inspector and
/// plugin host enumerate effects through <see cref="EffectCatalog"/>, so its descriptors and factories must be
/// correct.
/// </summary>
public class EffectCatalogTests
{
    [Fact]
    public void BuiltIns_Contains_The_Slice_Effects()
    {
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Brightness);
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Fade);
    }

    [Fact]
    public void Find_Returns_Descriptor_By_Id()
    {
        EffectDescriptor? brightness = EffectCatalog.Find(EffectTypeIds.Brightness);
        Assert.NotNull(brightness);
        Assert.Equal("Brightness", brightness!.DisplayName);
        Assert.Equal(EffectCategory.Color, brightness.Category);
    }

    [Fact]
    public void Find_Returns_Null_For_Unknown_Id()
    {
        Assert.Null(EffectCatalog.Find("plugin.unknown.effect"));
        // DisplayName falls back to the id itself so an unregistered (plugin) effect still labels in the UI.
        Assert.Equal("plugin.unknown.effect", EffectCatalog.DisplayName("plugin.unknown.effect"));
    }

    [Fact]
    public void CreateInstance_Builds_An_Instance_With_Default_Params()
    {
        EffectInstance brightness = EffectCatalog.Find(EffectTypeIds.Brightness)!.CreateInstance();
        Assert.Equal(EffectTypeIds.Brightness, brightness.EffectTypeId);
        Assert.True(brightness.Parameters.ContainsKey(EffectParamNames.Amount));

        EffectInstance fade = EffectCatalog.Find(EffectTypeIds.Fade)!.CreateInstance();
        Assert.Equal(EffectTypeIds.Fade, fade.EffectTypeId);
        Assert.True(fade.Parameters.ContainsKey(EffectParamNames.Opacity));

        // Each call yields a fresh instance (adding to a clip's stack must not share state).
        Assert.NotSame(brightness, EffectCatalog.Find(EffectTypeIds.Brightness)!.CreateInstance());
    }

    [Fact]
    public void InCategory_Filters_By_Category()
    {
        Assert.All(EffectCatalog.InCategory(EffectCategory.Color), d => Assert.Equal(EffectCategory.Color, d.Category));
        Assert.Contains(EffectCatalog.InCategory(EffectCategory.Color), d => d.Id == EffectTypeIds.Brightness);
    }

    // ── Step 16: Transform & Color effects, type-driven parameter descriptors ──────────────────────────

    [Fact]
    public void BuiltIns_Contains_The_Step16_Effects()
    {
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Transform);
        Assert.Contains(EffectCatalog.BuiltIns, d => d.Id == EffectTypeIds.Color);

        Assert.Equal(EffectCategory.Video, EffectCatalog.Find(EffectTypeIds.Transform)!.Category);
        Assert.Equal(EffectCategory.Color, EffectCatalog.Find(EffectTypeIds.Color)!.Category);
    }

    [Fact]
    public void Transform_Exposes_Its_Geometric_Parameters()
    {
        EffectDescriptor transform = EffectCatalog.Find(EffectTypeIds.Transform)!;
        string[] names = transform.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.Scale, EffectParamNames.PositionX, EffectParamNames.PositionY,
                EffectParamNames.Rotation, EffectParamNames.AnchorX, EffectParamNames.AnchorY,
                EffectParamNames.Opacity,
            },
            names);
    }

    [Fact]
    public void Color_Exposes_Exposure_Contrast_Saturation_Vibrance()
    {
        EffectDescriptor color = EffectCatalog.Find(EffectTypeIds.Color)!;
        string[] names = color.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[] { EffectParamNames.Exposure, EffectParamNames.Contrast, EffectParamNames.Saturation, EffectParamNames.Vibrance },
            names);
    }

    [Fact]
    public void CreateInstance_Sets_Every_Descriptor_Parameter_To_Its_Default()
    {
        EffectDescriptor transform = EffectCatalog.Find(EffectTypeIds.Transform)!;
        EffectInstance instance = transform.CreateInstance();

        // Every declared parameter is present, and at its declared default value.
        foreach (EffectParameterDescriptor p in transform.Parameters)
        {
            Assert.True(instance.Parameters.ContainsKey(p.Name));
            Assert.Equal(p.Default, instance.Parameters[p.Name].Evaluate(Sprocket.Core.Timing.Timecode.Zero), 5);
        }

        // Scale/opacity default to identity; anchor to centre.
        Assert.Equal(1.0, instance.Parameters[EffectParamNames.Scale].Evaluate(Sprocket.Core.Timing.Timecode.Zero), 5);
        Assert.Equal(0.5, instance.Parameters[EffectParamNames.AnchorX].Evaluate(Sprocket.Core.Timing.Timecode.Zero), 5);
    }

    [Fact]
    public void Parameter_Defaults_Are_Within_Their_Declared_Range()
    {
        foreach (EffectDescriptor d in EffectCatalog.BuiltIns)
            foreach (EffectParameterDescriptor p in d.Parameters)
            {
                Assert.True(p.Min <= p.Max, $"{d.Id}.{p.Name} has Min > Max");
                Assert.InRange(p.Default, p.Min, p.Max);
            }
    }

    // ── Step 41: Studio Reverb descriptor + factory presets ────────────────────────────────────────────

    [Fact]
    public void StudioReverb_Is_Registered_As_An_Audio_Effect_With_The_Step41_Parameters()
    {
        EffectDescriptor reverb = EffectCatalog.Find(EffectTypeIds.AudioStudioReverb)!;
        Assert.Equal("Studio Reverb", reverb.DisplayName);
        Assert.Equal(EffectCategory.Audio, reverb.Category);
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioStudioReverb)); // routes to the mixer, not the shaders

        string[] names = reverb.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.PreDelayMs, EffectParamNames.Decay, EffectParamNames.Size,
                EffectParamNames.Diffusion, EffectParamNames.ModDepth, EffectParamNames.ModRateHz,
                EffectParamNames.EarlyLate, EffectParamNames.Width, EffectParamNames.LowDamp,
                EffectParamNames.HighDamp, EffectParamNames.Mix,
            },
            names);
    }

    [Fact]
    public void Freeverb_Reverb_Is_Now_Labelled_As_The_Lite_Tier()
    {
        Assert.Equal("Reverb (Lite)", EffectCatalog.Find(EffectTypeIds.AudioReverb)!.DisplayName);
    }

    [Fact]
    public void StudioReverb_Ships_The_Step41_Preset_Families()
    {
        EffectDescriptor reverb = EffectCatalog.Find(EffectTypeIds.AudioStudioReverb)!;
        string[] presets = reverb.Presets.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Room", "Chamber", "Plate", "Hall", "Cathedral", "Ambient Bloom" }, presets);
    }

    [Fact]
    public void Every_Preset_Value_Names_A_Declared_Parameter_And_Stays_In_Its_Range()
    {
        foreach (EffectDescriptor d in EffectCatalog.BuiltIns)
            foreach (EffectPreset preset in d.Presets)
                foreach ((string name, double value) in preset.Values)
                {
                    EffectParameterDescriptor? p = d.Parameters.FirstOrDefault(x => x.Name == name);
                    Assert.True(p is not null, $"{d.Id} preset '{preset.Name}' sets unknown parameter '{name}'");
                    Assert.InRange(value, p!.Min, p.Max);
                }
    }

    [Fact]
    public void Presets_Leave_Mix_Untouched_So_A_Preset_Keeps_The_Users_Blend()
    {
        EffectDescriptor reverb = EffectCatalog.Find(EffectTypeIds.AudioStudioReverb)!;
        Assert.All(reverb.Presets, p => Assert.False(p.Values.ContainsKey(EffectParamNames.Mix)));
    }

    [Fact]
    public void Effects_Without_Presets_Report_An_Empty_List()
    {
        Assert.Empty(EffectCatalog.Find(EffectTypeIds.Brightness)!.Presets);
    }

    // ── Step 46: the delay family (Digital / Tape / Multi-Tap / Stereo) ────────────────────────────────

    [Fact]
    public void Delay_Family_Is_Registered_As_Audio_Effects()
    {
        foreach (string id in new[]
        {
            EffectTypeIds.AudioDelayDigital, EffectTypeIds.AudioDelayTape,
            EffectTypeIds.AudioDelayMultiTap, EffectTypeIds.AudioDelayStereo,
        })
        {
            EffectDescriptor? d = EffectCatalog.Find(id);
            Assert.NotNull(d);
            Assert.Equal(EffectCategory.Audio, d!.Category);
            Assert.True(EffectTypeIds.IsAudio(id)); // routes to the mixer, not the shaders
        }
        Assert.Equal("Digital Delay", EffectCatalog.DisplayName(EffectTypeIds.AudioDelayDigital));
        Assert.Equal("Tape Delay", EffectCatalog.DisplayName(EffectTypeIds.AudioDelayTape));
        Assert.Equal("Multi-Tap Delay", EffectCatalog.DisplayName(EffectTypeIds.AudioDelayMultiTap));
        Assert.Equal("Stereo Delay", EffectCatalog.DisplayName(EffectTypeIds.AudioDelayStereo));
    }

    [Fact]
    public void Digital_Delay_Exposes_Time_Feedback_HighCut_Mix()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.AudioDelayDigital)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[] { EffectParamNames.DelayMs, EffectParamNames.Feedback, EffectParamNames.HighCutHz, EffectParamNames.Mix },
            names);
    }

    [Fact]
    public void Tape_Delay_Exposes_The_Step46_Parameters()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.AudioDelayTape)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.DelayMs, EffectParamNames.Feedback, EffectParamNames.WowFlutterDepth,
                EffectParamNames.WowFlutterRateHz, EffectParamNames.Drive, EffectParamNames.Mix,
            },
            names);
    }

    [Fact]
    public void MultiTap_Delay_Exposes_Eight_Taps_Of_Enable_Time_Level_Pan_Plus_Mix()
    {
        EffectDescriptor multiTap = EffectCatalog.Find(EffectTypeIds.AudioDelayMultiTap)!;
        Assert.Equal(EffectParamNames.MultiTapCount * 4 + 1, multiTap.Parameters.Count);
        for (int i = 0; i < EffectParamNames.MultiTapCount; i++)
        {
            Assert.Equal(EffectParamNames.TapEnable[i], multiTap.Parameters[i * 4 + 0].Name);
            Assert.Equal(EffectParamNames.TapTimeMs[i], multiTap.Parameters[i * 4 + 1].Name);
            Assert.Equal(EffectParamNames.TapLevel[i], multiTap.Parameters[i * 4 + 2].Name);
            Assert.Equal(EffectParamNames.TapPan[i], multiTap.Parameters[i * 4 + 3].Name);
        }
        Assert.Equal(EffectParamNames.Mix, multiTap.Parameters[^1].Name);

        // Default pattern: the first two taps audible, the rest staged but disabled.
        EffectInstance instance = multiTap.CreateInstance();
        Assert.Equal(1.0, instance.Parameters[EffectParamNames.TapEnable[0]].Evaluate(Sprocket.Core.Timing.Timecode.Zero));
        Assert.Equal(1.0, instance.Parameters[EffectParamNames.TapEnable[1]].Evaluate(Sprocket.Core.Timing.Timecode.Zero));
        Assert.Equal(0.0, instance.Parameters[EffectParamNames.TapEnable[2]].Evaluate(Sprocket.Core.Timing.Timecode.Zero));
    }

    [Fact]
    public void Stereo_Delay_Exposes_The_Step46_Parameters()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.AudioDelayStereo)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.LeftTimeMs, EffectParamNames.RightTimeMs, EffectParamNames.Feedback,
                EffectParamNames.PingPong, EffectParamNames.CrossFeed, EffectParamNames.Mix,
            },
            names);
    }

    // ── Step 47: the Noise Gate ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Noise_Gate_Is_Registered_As_An_Audio_Effect()
    {
        EffectDescriptor? gate = EffectCatalog.Find(EffectTypeIds.AudioNoiseGate);
        Assert.NotNull(gate);
        Assert.Equal(EffectCategory.Audio, gate!.Category);
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioNoiseGate)); // routes to the mixer, not the shaders
        Assert.Equal("Noise Gate", EffectCatalog.DisplayName(EffectTypeIds.AudioNoiseGate));
    }

    [Fact]
    public void Noise_Gate_Exposes_The_Step47_Parameters()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.AudioNoiseGate)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.ThresholdDb, EffectParamNames.AttackMs, EffectParamNames.HoldMs,
                EffectParamNames.ReleaseMs, EffectParamNames.RangeDb, EffectParamNames.HysteresisDb,
            },
            names);
    }

    // ── Step 48: the Shelving EQ ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shelving_Eq_Is_Registered_As_An_Audio_Effect()
    {
        EffectDescriptor? eq = EffectCatalog.Find(EffectTypeIds.AudioShelvingEq);
        Assert.NotNull(eq);
        Assert.Equal(EffectCategory.Audio, eq!.Category);
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioShelvingEq)); // routes to the mixer, not the shaders
        Assert.Equal("Shelving EQ", EffectCatalog.DisplayName(EffectTypeIds.AudioShelvingEq));
    }

    [Fact]
    public void Shelving_Eq_Exposes_The_Step48_Parameters()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.AudioShelvingEq)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.LowFreq, EffectParamNames.LowGainDb, EffectParamNames.LowSlope,
                EffectParamNames.LowEnable, EffectParamNames.HighFreq, EffectParamNames.HighGainDb,
                EffectParamNames.HighSlope, EffectParamNames.HighEnable,
            },
            names);
    }

    // ── Step 41: heavy-chain traits (freeze hints) ─────────────────────────────────────────────────────

    [Fact]
    public void AudioEffectTraits_Flags_The_Studio_Reverb_As_Heavy()
    {
        Assert.True(Sprocket.Core.Audio.AudioEffectTraits.IsHeavy(EffectTypeIds.AudioStudioReverb));
        Assert.False(Sprocket.Core.Audio.AudioEffectTraits.IsHeavy(EffectTypeIds.AudioReverb));
        Assert.False(Sprocket.Core.Audio.AudioEffectTraits.IsHeavy(EffectTypeIds.AudioGain));
    }

    [Fact]
    public void HasHeavyEffect_Sees_Only_Enabled_Heavy_Stages()
    {
        var heavy = new EffectInstance(EffectTypeIds.AudioStudioReverb);
        var light = new EffectInstance(EffectTypeIds.AudioGain);
        Assert.True(Sprocket.Core.Audio.AudioEffectTraits.HasHeavyEffect([light, heavy]));
        Assert.False(Sprocket.Core.Audio.AudioEffectTraits.HasHeavyEffect([light]));

        heavy.Enabled = false; // a disabled stage doesn't run, so it isn't a freeze candidate
        Assert.False(Sprocket.Core.Audio.AudioEffectTraits.HasHeavyEffect([light, heavy]));
    }
}
