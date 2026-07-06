using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Effect instance reference tags (<see cref="EffectTags"/>): the Inspector shows them and MCP/AI clients
/// address effects by them, so they must be unique across the whole project, stable for already-tagged
/// instances, and never renumber on the sweep.
/// </summary>
public class EffectTagsTests
{
    private static (Project Project, Clip Clip) ProjectWithClip()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(5), Timecode.Zero);
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return (project, clip);
    }

    [Fact]
    public void BuiltIn_ShortCodes_Are_Present_And_Unique()
    {
        List<string?> codes = EffectCatalog.BuiltIns.Select(d => d.ShortCode).ToList();
        Assert.All(codes, c => Assert.False(string.IsNullOrEmpty(c)));
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void EnsureAssigned_Tags_Every_Effect_With_A_Project_Unique_Number()
    {
        (Project project, Clip clip) = ProjectWithClip();
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioReverb));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioGain));
        project.Settings.MasterAudioEffects.Add(new EffectInstance(EffectTypeIds.AudioEq));

        Assert.True(EffectTags.EnsureAssigned(project));
        Assert.Equal("RV-1", clip.Effects[0].Tag);
        Assert.Equal("GP-2", clip.Effects[1].Tag);
        Assert.Equal("EQ-3", project.Settings.MasterAudioEffects[0].Tag);

        // Idempotent: a second sweep changes nothing.
        Assert.False(EffectTags.EnsureAssigned(project));
        Assert.Equal("RV-1", clip.Effects[0].Tag);
    }

    [Fact]
    public void EnsureAssigned_Numbers_Continue_After_A_Remove()
    {
        (Project project, Clip clip) = ProjectWithClip();
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioReverb));
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioGain));
        EffectTags.EnsureAssigned(project);

        clip.Effects.RemoveAt(0); // RV-1 gone
        clip.Effects.Add(new EffectInstance(EffectTypeIds.AudioEq));
        EffectTags.EnsureAssigned(project);

        // The freed number is not reused — a stale "RV-1" reference must fail, not point elsewhere.
        Assert.Equal("EQ-3", clip.Effects[1].Tag);
    }

    [Fact]
    public void EnsureAssigned_Retags_A_Duplicate_Keeping_The_Earlier_Instance()
    {
        (Project project, Clip clip) = ProjectWithClip();
        var first = new EffectInstance(EffectTypeIds.AudioReverb) { Tag = "RV-1" };
        var duplicate = new EffectInstance(EffectTypeIds.AudioReverb) { Tag = "RV-1" };
        clip.Effects.Add(first);
        clip.Effects.Add(duplicate);

        EffectTags.EnsureAssigned(project);
        Assert.Equal("RV-1", first.Tag);
        Assert.Equal("RV-2", duplicate.Tag);
    }

    [Fact]
    public void Clones_Start_Untagged_So_They_Get_Fresh_Tags()
    {
        var effect = new EffectInstance(EffectTypeIds.Brightness) { Tag = "BR-1" };
        Assert.Null(effect.Clone().Tag);
        Assert.Null(effect.CloneShifted(Timecode.FromSeconds(1)).Tag);
    }

    [Fact]
    public void DeriveShortCode_Falls_Back_For_Plugin_Names()
    {
        Assert.Equal("WZ", EffectTags.DeriveShortCode("Warp Zoom"));
        Assert.Equal("GL", EffectTags.DeriveShortCode("Glitch"));
        Assert.Equal("FX", EffectTags.DeriveShortCode("!!!"));
        // Unregistered type ids derive from the id text rather than failing.
        Assert.False(string.IsNullOrEmpty(EffectTags.ShortCode("plugin.unknown.effect")));
    }
}
