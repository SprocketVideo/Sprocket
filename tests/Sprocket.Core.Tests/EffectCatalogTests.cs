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

    [Fact]
    public void DisplayScale_And_A_Percent_Unit_Are_Declared_Together()
    {
        // The two halves are what make a percent parameter work: the unit renders the "%", DisplayScale does
        // the ×100. Declaring one without the other silently shows a 0–1 ratio labelled "%", or a 0–100
        // number with no sign of what it means.
        foreach (EffectDescriptor d in EffectCatalog.BuiltIns)
            foreach (EffectParameterDescriptor p in d.Parameters)
            {
                if (p.Unit == "%")
                    Assert.True(p.DisplayScale == 100, $"{d.Id}.{p.Name} is a percent but does not scale by 100");
                else
                    Assert.True(p.DisplayScale == 1.0, $"{d.Id}.{p.Name} scales its display but has no unit");
            }
    }

    [Fact]
    public void Percent_Parameters_Are_Normalized_Ratios()
    {
        // A percent parameter's model value is the 0–1 (or 0–N) ratio, never an already-scaled 0–100 — the
        // slider, the clamp and every command work in model units.
        foreach (EffectDescriptor d in EffectCatalog.BuiltIns)
            foreach (EffectParameterDescriptor p in d.Parameters.Where(p => p.Unit == "%"))
            {
                Assert.InRange(p.Min, -1.0, 1.0);
                Assert.InRange(p.Max, 0.0, 4.0);
                Assert.InRange(p.Default * p.DisplayScale, -100.0, 400.0);
            }
    }

    [Fact]
    public void Every_BuiltIn_Parameter_Has_A_Tooltip_Description()
    {
        // The Inspector shows Description as the parameter label's tooltip; every built-in must carry one.
        foreach (EffectDescriptor d in EffectCatalog.BuiltIns)
            foreach (EffectParameterDescriptor p in d.Parameters)
                Assert.False(string.IsNullOrWhiteSpace(p.Description), $"{d.Id}.{p.Name} has no Description");
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

    // ── Step 50: the Shimmer Reverb ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shimmer_Reverb_Is_Registered_As_An_Audio_Effect()
    {
        EffectDescriptor? shimmer = EffectCatalog.Find(EffectTypeIds.AudioShimmerReverb);
        Assert.NotNull(shimmer);
        Assert.Equal(EffectCategory.Audio, shimmer!.Category);
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioShimmerReverb)); // routes to the mixer, not the shaders
        Assert.Equal("Shimmer Reverb", EffectCatalog.DisplayName(EffectTypeIds.AudioShimmerReverb));
    }

    [Fact]
    public void Shimmer_Reverb_Exposes_The_Step50_Parameters()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.AudioShimmerReverb)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                EffectParamNames.ShimmerAmount, EffectParamNames.ShimmerInterval, EffectParamNames.Size,
                EffectParamNames.Decay, EffectParamNames.Damping, EffectParamNames.Mix,
            },
            names);
    }

    [Fact]
    public void Shimmer_Reverb_Ships_The_Step50_Presets_Leaving_Mix_Untouched()
    {
        EffectDescriptor shimmer = EffectCatalog.Find(EffectTypeIds.AudioShimmerReverb)!;
        Assert.Equal(
            new[] { "Classic Shimmer", "Dark Shimmer", "Fifth Shimmer", "Drone / Infinite" },
            shimmer.Presets.Select(p => p.Name).ToArray());
        Assert.All(shimmer.Presets, p => Assert.DoesNotContain(EffectParamNames.Mix, p.Values.Keys));
        // The fifth preset selects the +7 st interval; the drone preset is the deliberate near-sustain wash.
        Assert.Equal(7, shimmer.Presets.Single(p => p.Name == "Fifth Shimmer").Values[EffectParamNames.ShimmerInterval]);
        Assert.Equal(1.0, shimmer.Presets.Single(p => p.Name == "Drone / Infinite").Values[EffectParamNames.ShimmerAmount]);
    }

    // ── Black & White, phase 1 (plan/features/black-and-white.md) ───────────────────────────────────────

    [Fact]
    public void BlackWhite_Is_Registered_As_A_Color_Effect_With_A_Unique_Short_Code()
    {
        EffectDescriptor? bw = EffectCatalog.Find(EffectTypeIds.BlackWhite);
        Assert.NotNull(bw);
        Assert.Equal("Black & White", bw!.DisplayName);
        Assert.Equal(EffectCategory.Color, bw.Category);
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.BlackWhite)); // a video shader stage, not the mixer
        Assert.Equal("BW", bw.ShortCode);

        // Short codes are the tag prefix (EffectTags) and must stay unique across the built-ins.
        List<string> codes = EffectCatalog.BuiltIns.Where(d => d.ShortCode is not null).Select(d => d.ShortCode!).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void Mosaic_Is_A_Video_Effect_With_After_Effects_Naming_And_Defaults()
    {
        // plan/features/toy-cassette-camera.md phase 1: AE's Mosaic controls and 10×10 default grid, plus our
        // Edge Softness (defaulting off, so a fresh Mosaic reads exactly like AE's).
        EffectDescriptor? mosaic = EffectCatalog.Find(EffectTypeIds.Mosaic);
        Assert.NotNull(mosaic);
        Assert.Equal("Mosaic", mosaic!.DisplayName);
        Assert.Equal(EffectCategory.Video, mosaic.Category);
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.Mosaic));
        Assert.Equal("MO", mosaic.ShortCode);
        Assert.Empty(mosaic.Presets);

        Assert.Equal(
            new[]
            {
                (EffectParamNames.HorizontalBlocks, "Horizontal Blocks", 10.0, ParameterKind.Integer),
                (EffectParamNames.VerticalBlocks, "Vertical Blocks", 10.0, ParameterKind.Integer),
                (EffectParamNames.SharpColors, "Sharp Colors", 0.0, ParameterKind.Toggle),
                (EffectParamNames.EdgeSoftness, "Edge Softness", 0.0, ParameterKind.Continuous),
            },
            mosaic.Parameters.Select(p => (p.Name, p.DisplayName, p.Default, p.Kind)));
        Assert.Equal((1.0, 1920.0), Range(EffectParamNames.HorizontalBlocks));
        Assert.Equal((1.0, 1080.0), Range(EffectParamNames.VerticalBlocks));

        (double, double) Range(string name)
        {
            EffectParameterDescriptor p = mosaic.Parameters.Single(x => x.Name == name);
            return (p.Min, p.Max);
        }
    }

    [Fact]
    public void Posterize_Time_Is_A_Video_Time_Modifier_With_After_Effects_Naming_And_Defaults()
    {
        // plan/features/toy-cassette-camera.md phase 2: AE's single Frame Rate control, 12 fps default, fine
        // enough a step for 23.976; a time modifier (read by the clip's video map, not a shader, not keyframeable).
        EffectDescriptor? posterize = EffectCatalog.Find(EffectTypeIds.PosterizeTime);
        Assert.NotNull(posterize);
        Assert.Equal("Posterize Time", posterize!.DisplayName);
        Assert.Equal(EffectCategory.Video, posterize.Category);
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.PosterizeTime));
        Assert.Equal("PT", posterize.ShortCode);
        Assert.True(posterize.IsTimeModifier);
        Assert.Empty(posterize.Presets);

        EffectParameterDescriptor rate = Assert.Single(posterize.Parameters);
        Assert.Equal(
            (EffectParamNames.PosterizeFrameRate, "Frame Rate", 12.0, 1.0, 60.0, 0.001, "fps", ParameterKind.Continuous),
            (rate.Name, rate.DisplayName, rate.Default, rate.Min, rate.Max, rate.Step, rate.Unit, rate.Kind));
    }

    [Fact]
    public void Toy_Cassette_Camera_Is_A_Video_Effect_With_Its_Parameters_And_Presets()
    {
        // plan/features/toy-cassette-camera.md phase 3: one ordered stage, a generic name (the brand lives only in
        // the description), 120×90 picture pixels by default, and the Clean / Worn Tape / Low Light looks.
        EffectDescriptor? toy = EffectCatalog.Find(EffectTypeIds.ToyCam);
        Assert.NotNull(toy);
        Assert.Equal("Toy Cassette Camera", toy!.DisplayName);
        Assert.Equal(EffectCategory.Video, toy.Category);
        Assert.False(EffectTypeIds.IsAudio(EffectTypeIds.ToyCam));
        Assert.False(toy.IsTimeModifier);
        Assert.Equal("TC", toy.ShortCode);
        Assert.StartsWith("Inspired by the Fisher-Price PXL 2000", toy.Description);

        Assert.Equal(
            new[]
            {
                (EffectParamNames.HorizontalPixels, "Horizontal Pixels", 120.0, ParameterKind.Integer),
                (EffectParamNames.VerticalPixels, "Vertical Pixels", 90.0, ParameterKind.Integer),
                (EffectParamNames.PixelSoftness, "Pixel Softness", 0.15, ParameterKind.Continuous),
                (EffectParamNames.Contrast, "Contrast", 1.15, ParameterKind.Continuous),
                (EffectParamNames.BlackCrush, "Black Crush", 0.08, ParameterKind.Continuous),
                (EffectParamNames.HighlightBloom, "Highlight Bloom", 0.5, ParameterKind.Continuous),
                (EffectParamNames.SmearLength, "Smear Length", 0.35, ParameterKind.Continuous),
                (EffectParamNames.SmearThreshold, "Smear Threshold", 0.78, ParameterKind.Continuous),
                (EffectParamNames.NoiseLines, "Noise Lines", 0.2, ParameterKind.Continuous),
                (EffectParamNames.Dropouts, "Dropouts", 0.15, ParameterKind.Continuous),
                (EffectParamNames.GrainAmount, "Grain", 0.3, ParameterKind.Continuous),
                (EffectParamNames.BorderSize, "Border Size", 0.25, ParameterKind.Continuous),
                (EffectParamNames.BorderSoftness, "Border Softness", 0.2, ParameterKind.Continuous),
                (EffectParamNames.Seed, "Seed", 0.0, ParameterKind.Integer),
            },
            toy.Parameters.Select(p => (p.Name, p.DisplayName, p.Default, p.Kind)));
        EffectParameterDescriptor seed = toy.Parameters.Single(p => p.Name == EffectParamNames.Seed);
        Assert.Equal((0.0, 999.0), (seed.Min, seed.Max));

        Assert.Equal(new[] { "Clean", "Worn Tape", "Low Light" }, toy.Presets.Select(p => p.Name));
        Assert.Same(ToyCamPresets.WornTape, toy.FindPreset("Worn Tape"));
        // Complete looks: every picture parameter is set, and the user's Seed survives a change of look.
        string[] look = [.. toy.Parameters.Select(p => p.Name).Where(n => n != EffectParamNames.Seed)];
        foreach (EffectPreset preset in toy.Presets)
        {
            Assert.Equal(look.Order(), preset.Values.Keys.Order());
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
        }
    }

    [Fact]
    public void Cassette_Is_An_Audio_Effect_With_Its_Parameters_And_Presets()
    {
        // plan/features/toy-cassette-camera.md phase 4: the audio half of the toy-camera look, reusing the existing
        // tape parameter names, with presets named after the video looks so the phase-5 stacks pair them.
        EffectDescriptor? cassette = EffectCatalog.Find(EffectTypeIds.AudioCassette);
        Assert.NotNull(cassette);
        Assert.Equal("Cassette", cassette!.DisplayName);
        Assert.Equal(EffectCategory.Audio, cassette.Category);
        Assert.True(EffectTypeIds.IsAudio(EffectTypeIds.AudioCassette)); // routes to the mixer, not the shaders
        Assert.Equal("CS", cassette.ShortCode);

        Assert.Equal(
            new[]
            {
                (EffectParamNames.Mono, "Mono", 1.0, ParameterKind.Toggle),
                (EffectParamNames.AgcAmount, "AGC Amount", 0.5, ParameterKind.Continuous),
                (EffectParamNames.ReleaseMs, "AGC Release", 600.0, ParameterKind.Continuous),
                (EffectParamNames.Drive, "Saturation", 0.3, ParameterKind.Continuous),
                (EffectParamNames.LowCutHz, "Low Cut", 100.0, ParameterKind.Continuous),
                (EffectParamNames.HighCutHz, "High Cut", 5000.0, ParameterKind.Continuous),
                (EffectParamNames.HissDb, "Hiss", -42.0, ParameterKind.Continuous),
                (EffectParamNames.WowFlutterDepth, "Wow / Flutter", 0.25, ParameterKind.Continuous),
                (EffectParamNames.WowFlutterRateHz, "Wow Rate", 0.9, ParameterKind.Continuous),
                (EffectParamNames.Mix, "Mix", 1.0, ParameterKind.Continuous),
            },
            cassette.Parameters.Select(p => (p.Name, p.DisplayName, p.Default, p.Kind)));

        Assert.Equal(new[] { "Clean", "Worn Tape", "Low Light" }, cassette.Presets.Select(p => p.Name));
        Assert.Equal(ToyCamPresets.All.Select(p => p.Name), cassette.Presets.Select(p => p.Name)); // pairs by name
        Assert.Same(CassettePresets.LowLight, cassette.FindPreset("Low Light"));
        // Complete characters: every tone parameter is set, and the user's Mix survives (the Studio Reverb rule).
        string[] tone = [.. cassette.Parameters.Select(p => p.Name).Where(n => n != EffectParamNames.Mix)];
        foreach (EffectPreset preset in cassette.Presets)
        {
            Assert.Equal(tone.Order(), preset.Values.Keys.Order());
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
        }
        // Low Light is the pumping one: the strongest AGC with the fastest release.
        Assert.Equal(cassette.Presets.Max(p => p.Values[EffectParamNames.AgcAmount]),
            CassettePresets.LowLight.Values[EffectParamNames.AgcAmount]);
        Assert.Equal(cassette.Presets.Min(p => p.Values[EffectParamNames.ReleaseMs]),
            CassettePresets.LowLight.Values[EffectParamNames.ReleaseMs]);
    }

    [Fact]
    public void BlackWhite_Exposes_Its_Conversion_Film_And_Finishing_Parameters()
    {
        string[] names = EffectCatalog.Find(EffectTypeIds.BlackWhite)!.Parameters.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                // Conversion
                EffectParamNames.Mix, EffectParamNames.FilterHue, EffectParamNames.FilterStrength,
                EffectParamNames.MixReds, EffectParamNames.MixOranges, EffectParamNames.MixYellows,
                EffectParamNames.MixGreens, EffectParamNames.MixAquas, EffectParamNames.MixBlues,
                EffectParamNames.MixPurples, EffectParamNames.MixMagentas,
                // Film (tone response) — phase 2 adds grain
                EffectParamNames.Exposure, EffectParamNames.Contrast, EffectParamNames.Shadows,
                EffectParamNames.Highlights, EffectParamNames.GrainAmount, EffectParamNames.GrainSize,
                EffectParamNames.GrainSeedLock,
                // Finishing — phase 3 adds toning + split toning before the vignette
                EffectParamNames.ToneHue, EffectParamNames.ToneStrength,
                EffectParamNames.SplitShadowHue, EffectParamNames.SplitHighlightHue,
                EffectParamNames.SplitStrength, EffectParamNames.SplitBalance,
                EffectParamNames.VignetteAmount, EffectParamNames.VignetteSize, EffectParamNames.VignetteSoftness,
            },
            names);
    }

    [Fact]
    public void BlackWhite_CreateInstance_Is_Neutral_By_Default()
    {
        EffectInstance bw = EffectCatalog.Find(EffectTypeIds.BlackWhite)!.CreateInstance();
        Timing.Timecode t = Timing.Timecode.Zero;
        Assert.Equal(1.0, bw.Parameters[EffectParamNames.Mix].Evaluate(t), 5);        // full monochrome
        Assert.Equal(0.0, bw.Parameters[EffectParamNames.FilterStrength].Evaluate(t), 5); // no filter
        Assert.Equal(0.0, bw.Parameters[EffectParamNames.MixReds].Evaluate(t), 5);    // flat mixer
        Assert.Equal(1.0, bw.Parameters[EffectParamNames.Contrast].Evaluate(t), 5);
    }

    [Fact]
    public void BlackWhite_Ships_The_Phase4_NonFilm_Preset_Families()
    {
        // Phase 4: the non-film library (Neutral / Filters / Toning / Cinematic). Film stocks follow in phase 5.
        EffectDescriptor bw = EffectCatalog.Find(EffectTypeIds.BlackWhite)!;
        string[] names = bw.Presets.Select(p => p.Name).ToArray();

        Assert.Equal(BlackWhitePresets.All.Count, names.Length);
        Assert.Equal(names.Length, names.Distinct().Count()); // names must be unique
        Assert.True(names.Length >= 30, $"expected ~30 non-film presets, got {names.Length}");

        // Every family is represented and prefixes its members with "Family ▸ ".
        Assert.Contains("Neutral ▸ Neutral", names);
        Assert.Contains(names, n => n.StartsWith("Filters ▸ ", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("Toning ▸ ", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("Cinematic ▸ ", StringComparison.Ordinal));

        // The Studio Reverb rule: no preset touches Mix, so switching looks keeps the user's dry/wet.
        Assert.All(bw.Presets, p => Assert.DoesNotContain(EffectParamNames.Mix, p.Values.Keys));
    }

    [Fact]
    public void BlackWhite_Presets_Layer_By_Scope()
    {
        // Filters touch only conversion (filter + mixer); tonings touch only the finishing tint — so a filter
        // and a toning layer without one clobbering the other's group.
        string[] conversionOnly =
        {
            EffectParamNames.FilterHue, EffectParamNames.FilterStrength,
            EffectParamNames.MixReds, EffectParamNames.MixOranges, EffectParamNames.MixYellows,
            EffectParamNames.MixGreens, EffectParamNames.MixAquas, EffectParamNames.MixBlues,
            EffectParamNames.MixPurples, EffectParamNames.MixMagentas,
            EffectParamNames.Highlights, // Infrared's glow is the one film-group exception
        };
        foreach (EffectPreset p in BlackWhitePresets.Filters)
            Assert.All(p.Values.Keys, k => Assert.Contains(k, conversionOnly));

        string[] finishingOnly =
        {
            EffectParamNames.ToneHue, EffectParamNames.ToneStrength,
            EffectParamNames.SplitShadowHue, EffectParamNames.SplitHighlightHue,
            EffectParamNames.SplitStrength, EffectParamNames.SplitBalance,
        };
        foreach (EffectPreset p in BlackWhitePresets.Toning)
            Assert.All(p.Values.Keys, k => Assert.Contains(k, finishingOnly));
    }

    [Fact]
    public void EffectPreset_Description_Defaults_Null_And_Round_Trips()
    {
        // Phase 3: EffectPreset gains an optional Description (the film presets' "Inspired by …" note that the
        // Inspector shows as the picker item's tooltip). It is additive — existing presets keep null.
        var plain = new EffectPreset("X", new Dictionary<string, double>());
        Assert.Null(plain.Description);
        var described = new EffectPreset("Y", new Dictionary<string, double>(), "Inspired by a stock.");
        Assert.Equal("Inspired by a stock.", described.Description);

        // The non-film presets carry no description; only phase 5's film stocks set the "Inspired by …" note.
        foreach (EffectPreset p in EffectCatalog.Find(EffectTypeIds.BlackWhite)!.Presets)
            if (BlackWhitePresets.Film.Contains(p))
                Assert.NotNull(p.Description);
            else
                Assert.Null(p.Description);
    }

    // ── Black & White, phase 5 (film-stock presets) ────────────────────────────────────────────────────

    [Fact]
    public void BlackWhite_Ships_The_Phase5_Film_Stock_Presets()
    {
        // Phase 5 adds the 19 film-stock emulations to the picker, each carrying an "Inspired by …" description.
        Assert.Equal(19, BlackWhitePresets.Film.Count);

        EffectDescriptor bw = EffectCatalog.Find(EffectTypeIds.BlackWhite)!;
        string[] names = bw.Presets.Select(p => p.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count()); // still unique with the film family added
        Assert.All(BlackWhitePresets.Film, p => Assert.Contains(p, bw.Presets)); // wired into the descriptor

        // Every film preset carries a non-empty description (the only presets that do); the non-film ones stay
        // null.
        Assert.All(BlackWhitePresets.Film, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.All(BlackWhitePresets.Film, p => Assert.StartsWith("Inspired by ", p.Description!, StringComparison.Ordinal));
        foreach (EffectPreset p in bw.Presets.Where(p => !BlackWhitePresets.Film.Contains(p)))
            Assert.Null(p.Description);
    }

    [Fact]
    public void BlackWhite_Film_Preset_Names_Carry_No_Brand_Or_Stock_Token()
    {
        // The trademark reference lives only in Description — the generic name trades on no brand. This is the
        // whole point of the "generic name, stock in the tooltip" decision (plan/features/black-and-white.md).
        string[] tokens =
        {
            "Kodak", "Ilford", "Fujifilm", "Fuji", "Agfa", "Rollei", "Tri-X", "T-Max", "TMax", "HP5", "FP4",
            "Delta", "Pan F", "Plus-X", "XP2", "Neopan", "Acros", "APX", "Retro", "BW400",
        };
        foreach (EffectPreset p in BlackWhitePresets.Film)
            foreach (string token in tokens)
                Assert.False(p.Name.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"film preset name '{p.Name}' leaks the stock token '{token}' — it belongs only in Description");
    }

    [Fact]
    public void Toy_Cassette_Camera_Names_Carry_No_Brand_Token()
    {
        // The same rule for the toy-camera look (plan/features/toy-cassette-camera.md): the descriptor's display
        // name and its preset names are generic; only the description may name the camera.
        string[] tokens = ["Fisher-Price", "Fisher Price", "PXL", "PixelVision", "Pixel Vision"];
        EffectDescriptor toy = EffectCatalog.Find(EffectTypeIds.ToyCam)!;
        EffectDescriptor cassette = EffectCatalog.Find(EffectTypeIds.AudioCassette)!; // phase 4's audio half
        string[] names =
        [
            toy.DisplayName, .. toy.Presets.Select(p => p.Name),
            cassette.DisplayName, .. cassette.Presets.Select(p => p.Name),
            // phase 5's one-tap stacks: the variant names and their browser group
            .. ToyCassetteCameraStacks.All.SelectMany(s => new[] { s.Name, s.Group, s.Title }),
        ];
        foreach (string name in names)
            foreach (string token in tokens)
                Assert.False(name.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"toy camera name '{name}' leaks the brand token '{token}' — it belongs only in Description");
        Assert.Contains("PXL", toy.Description); // …which is where the reference does live
        Assert.All(ToyCassetteCameraStacks.All, s => Assert.StartsWith("Inspired by the Fisher-Price PXL 2000", s.Description));
    }

    [Fact]
    public void BlackWhite_Film_Presets_Set_No_Finishing_Params_So_They_Layer_Under_A_Toning()
    {
        // Film stocks set conversion (mixer) + film (curve/grain) only — no toning/vignette — so a stock layers
        // under a filter and a toning the way the other families do.
        string[] finishing =
        {
            EffectParamNames.ToneHue, EffectParamNames.ToneStrength,
            EffectParamNames.SplitShadowHue, EffectParamNames.SplitHighlightHue,
            EffectParamNames.SplitStrength, EffectParamNames.SplitBalance,
            EffectParamNames.VignetteAmount, EffectParamNames.VignetteSize, EffectParamNames.VignetteSoftness,
        };
        foreach (EffectPreset p in BlackWhitePresets.Film)
        {
            Assert.DoesNotContain(EffectParamNames.Mix, p.Values.Keys); // the Studio Reverb rule
            Assert.All(p.Values.Keys, k => Assert.DoesNotContain(k, finishing));
        }
    }

    // ── Step 41: heavy-chain traits (freeze hints) ─────────────────────────────────────────────────────

    [Fact]
    public void AudioEffectTraits_Flags_The_Studio_Reverb_As_Heavy()
    {
        Assert.True(Sprocket.Core.Audio.AudioEffectTraits.IsHeavy(EffectTypeIds.AudioStudioReverb));
        Assert.True(Sprocket.Core.Audio.AudioEffectTraits.IsHeavy(EffectTypeIds.AudioShimmerReverb)); // step 50
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
