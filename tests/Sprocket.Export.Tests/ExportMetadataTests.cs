using System.Diagnostics;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Container metadata tags on export (PLAN.md step 38): the option-mapping logic plus a real encode
/// verified with the <c>ffmpeg</c> CLI (already a test prerequisite), whose <c>-i</c> banner prints the
/// container's metadata dictionary.
/// </summary>
public sealed class ExportMetadataTests
{
    [Fact]
    public void BuildMetadata_Maps_Set_Fields_And_Skips_Blanks()
    {
        Assert.Null(VideoExporter.BuildMetadata(new ExportOptions()));
        Assert.Null(VideoExporter.BuildMetadata(new ExportOptions(MetaTitle: "  ", MetaComment: "")));

        var tags = VideoExporter.BuildMetadata(new ExportOptions(
            MetaTitle: " My Film ", MetaAuthor: "Jane", MetaCopyright: "© 2026", MetaComment: "cut 3"));
        Assert.NotNull(tags);
        Assert.Equal(
            [new("title", "My Film"), new("artist", "Jane"), new("copyright", "© 2026"), new("comment", "cut 3")],
            tags);

        var partial = VideoExporter.BuildMetadata(new ExportOptions(MetaAuthor: "Jane"));
        Assert.Equal([new KeyValuePair<string, string>("artist", "Jane")], partial);
    }

    [Fact]
    public void Exported_File_Carries_The_Metadata_Tags()
    {
        var timeline = new Timeline(new Rational(ExportFixture.Fps, 1),
            new Resolution(ExportFixture.Width, ExportFixture.Height), ExportFixture.SampleRate);
        var track = new VideoTrack { Name = "V1" };
        GeneratorSpec solid = GeneratorCatalog.BuiltIns.Single(d => d.Id == GeneratorTypeIds.SolidColor).CreateSpec();
        track.Clips.Add(Clip.CreateGenerator(solid, Timecode.FromSeconds(0.5), Timecode.Zero));
        timeline.Tracks.Add(track);
        var project = new Project(timeline);

        using var output = new TempFile();
        VideoExporter.Export(project, output.Path, new ExportOptions(
            VideoOnly: true,
            MetaTitle: "Metadata Test Film",
            MetaAuthor: "Test Author",
            MetaCopyright: "(c) 2026 Sprocket Tests",
            MetaComment: "A user comment overriding the default"));

        // `ffmpeg -i <file>` prints the container metadata dictionary to stderr (and exits nonzero for
        // having no output — expected; we only read the banner).
        string banner = FfmpegBanner(output.Path);
        Assert.Contains("Metadata Test Film", banner);
        Assert.Contains("Test Author", banner);
        Assert.Contains("(c) 2026 Sprocket Tests", banner);
        Assert.Contains("A user comment overriding the default", banner);
        Assert.DoesNotContain("Created with Sprocket", banner); // the user comment replaced the default
    }

    private static string FfmpegBanner(string path)
    {
        var psi = new ProcessStartInfo("ffmpeg", $"-hide_banner -i \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg CLI not found on PATH.");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stderr;
    }
}
