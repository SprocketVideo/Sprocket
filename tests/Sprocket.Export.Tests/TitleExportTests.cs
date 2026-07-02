using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Real encode→decode test for the rich titles of PLAN.md step 40: a Credits Roll renders on the deterministic
/// raster export path, its position a pure function of the clip's local progress — off-screen at the clip's
/// first frame, visible mid-clip, off-screen again at the end. Asserted by whole-frame brightness (white text
/// over black), so it is independent of exact glyph shapes.
/// </summary>
public sealed class TitleExportTests
{
    [Fact]
    public void Roll_Title_Scrolls_Through_The_Frame_On_Export()
    {
        var timeline = new Timeline(new Rational(ExportFixture.Fps, 1),
            new Resolution(ExportFixture.Width, ExportFixture.Height), ExportFixture.SampleRate);
        var track = new VideoTrack { Name = "V1" };
        GeneratorSpec spec = GeneratorCatalog.BuiltIns.Single(d => d.Id == GeneratorTypeIds.Roll).CreateSpec()
            .SetString(GeneratorParamNames.Text, "CREDITS\nLINE TWO\nLINE THREE")
            .Set(GeneratorParamNames.FontSize, 0.2);
        track.Clips.Add(Clip.CreateGenerator(spec, Timecode.FromSeconds(1), Timecode.Zero));
        timeline.Tracks.Add(track);
        var project = new Project(timeline);

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path);

        int frames = ExportProbe.CountVideoFrames(output.Path);
        double first = ExportProbe.FrameMeanRgbAt(output.Path, 0);          // progress 0 → off-screen below
        double mid = ExportProbe.FrameMeanRgbAt(output.Path, frames / 2);   // progress ~0.5 → crossing the frame
        double last = ExportProbe.FrameMeanRgbAt(output.Path, frames - 1);  // progress ~1 → scrolled off the top

        Assert.True(mid > first + 2.0,
            $"the roll should be visible mid-clip and off-screen at the start: first={first:0.00}, mid={mid:0.00}");
        Assert.True(mid > last + 2.0,
            $"the roll should have left the frame by the clip's end: mid={mid:0.00}, last={last:0.00}");
    }
}
