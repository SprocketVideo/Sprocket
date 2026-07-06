using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Real encode→decode test for the step-37 export toggle: a bright log-gray solid with the input color
/// transform prepended exports near display white when the transform is <b>baked</b> (the default), and
/// stays at the untouched source gray when the export <b>passes through the log encoding</b>
/// (<see cref="ExportOptions.BakeColorTransform"/> = false).
/// </summary>
public sealed class ColorTransformExportTests
{
    [Fact]
    public void Bake_Toggle_Controls_Whether_The_Log_Transform_Lands_In_The_File()
    {
        // A 0.75-gray solid: D-Log code 0.75 decodes to super-white, so the baked output is near 255
        // while the pass-through output keeps the source's ≈191 gray — far apart under codec tolerance.
        var timeline = new Timeline(new Rational(ExportFixture.Fps, 1),
            new Resolution(ExportFixture.Width, ExportFixture.Height), ExportFixture.SampleRate);
        var track = new VideoTrack { Name = "V1" };
        GeneratorSpec solid = GeneratorCatalog.BuiltIns.Single(d => d.Id == GeneratorTypeIds.SolidColor)
            .CreateSpec().SetString(GeneratorParamNames.Color, "#BFBFBF");
        Clip clip = Clip.CreateGenerator(solid, Timecode.FromSeconds(0.5), Timecode.Zero);
        clip.Effects.Insert(0, new EffectInstance(EffectTypeIds.ColorTransform)
            .Set(EffectParamNames.SourceProfile, ColorProfiles.IndexOf(ColorProfiles.DjiDLog)));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        var project = new Project(timeline);

        using var baked = new TempFile();
        using var passthrough = new TempFile();
        VideoExporter.Export(project, baked.Path, new ExportOptions(VideoOnly: true));
        VideoExporter.Export(project, passthrough.Path, new ExportOptions(VideoOnly: true, BakeColorTransform: false));

        double bakedMean = ExportProbe.FirstFrameMeanRgb(baked.Path);
        double passthroughMean = ExportProbe.FirstFrameMeanRgb(passthrough.Path);

        Assert.True(bakedMean > 215, $"baked D-Log 0.75 should be near white, got {bakedMean:0.0}");
        Assert.InRange(passthroughMean, 181, 201); // the untouched #BFBFBF source gray (±codec noise)
    }

    [Fact]
    public void Bake_Toggle_Also_Controls_A_Math_Based_Non_Dji_Profile()
    {
        // Same shape as the DJI test above, for a PLAN.md step 52 curve-based profile (ARRI LogC3): the
        // StripEffects pass-through plan surgery is generic over the effect type id, not the profile, so one
        // math profile is enough to confirm it works for the whole family, not all eight.
        var timeline = new Timeline(new Rational(ExportFixture.Fps, 1),
            new Resolution(ExportFixture.Width, ExportFixture.Height), ExportFixture.SampleRate);
        var track = new VideoTrack { Name = "V1" };
        GeneratorSpec solid = GeneratorCatalog.BuiltIns.Single(d => d.Id == GeneratorTypeIds.SolidColor)
            .CreateSpec().SetString(GeneratorParamNames.Color, "#BFBFBF");
        Clip clip = Clip.CreateGenerator(solid, Timecode.FromSeconds(0.5), Timecode.Zero);
        clip.Effects.Insert(0, new EffectInstance(EffectTypeIds.ColorTransform)
            .Set(EffectParamNames.SourceProfile, ColorProfiles.IndexOf(ColorProfiles.ArriLogC3)));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        var project = new Project(timeline);

        using var baked = new TempFile();
        using var passthrough = new TempFile();
        VideoExporter.Export(project, baked.Path, new ExportOptions(VideoOnly: true));
        VideoExporter.Export(project, passthrough.Path, new ExportOptions(VideoOnly: true, BakeColorTransform: false));

        double bakedMean = ExportProbe.FirstFrameMeanRgb(baked.Path);
        double passthroughMean = ExportProbe.FirstFrameMeanRgb(passthrough.Path);

        // LogC3 code 0.75 decodes to scene-linear ~5.36 (well above white) — baked lands at display white.
        Assert.True(bakedMean > 215, $"baked LogC3 0.75 should be near white, got {bakedMean:0.0}");
        Assert.InRange(passthroughMean, 181, 201); // the untouched #BFBFBF source gray (±codec noise)
    }
}
