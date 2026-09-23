using System;
using System.IO;
using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The per-project, per-user layout store: round-trip fidelity, tolerance of missing/garbage/partial files,
/// clamping, path keying, and the on-disk save/load/prune cycle (against a temp directory, never the real profile).
/// </summary>
public class ProjectLayoutStoreTests
{
    [Fact]
    public void Round_Trips_All_Fields()
    {
        var layout = new ProjectLayout(
            ProjectWidth: 310, InspectorWidth: 420, TimelineHeight: 380, ProjectVisible: false, InspectorVisible: true,
            TimelinePxPerSecond: 155.5, SourceMonitorActive: true, SourceMediaPath: @"C:\media\a.mp4",
            ProjectPath: @"C:\projects\p.sprocket.json");
        Assert.Equal(layout, ProjectLayoutStore.Deserialize(ProjectLayoutStore.Serialize(layout)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json {")]
    public void Missing_Or_Garbage_Input_Yields_Null(string? json) =>
        Assert.Null(ProjectLayoutStore.Deserialize(json));

    [Fact]
    public void Partial_File_Defaults_Missing_Fields()
    {
        ProjectLayout layout = ProjectLayoutStore.Deserialize("""{"ProjectWidth": 333}""")!;
        Assert.Equal(333, layout.ProjectWidth);
        Assert.Equal(ProjectLayoutStore.DefaultInspectorWidth, layout.InspectorWidth);
        Assert.Equal(ProjectLayoutStore.DefaultTimelineHeight, layout.TimelineHeight);
        Assert.Equal(ProjectLayoutStore.DefaultPxPerSecond, layout.TimelinePxPerSecond);
        Assert.True(layout.ProjectVisible);
        Assert.True(layout.InspectorVisible);
        Assert.False(layout.SourceMonitorActive);
        Assert.Null(layout.SourceMediaPath);
    }

    [Fact]
    public void Clamp_Bounds_Sizes_And_Zoom()
    {
        ProjectLayout low = ProjectLayoutStore.Clamp(new ProjectLayout(
            ProjectWidth: 1, InspectorWidth: 1, TimelineHeight: -5, TimelinePxPerSecond: 0.01));
        Assert.Equal(ProjectLayoutStore.MinPaneSize, low.ProjectWidth);
        Assert.Equal(ProjectLayoutStore.MinInspectorWidth, low.InspectorWidth);
        Assert.Equal(ProjectLayoutStore.MinPaneSize, low.TimelineHeight);
        Assert.Equal(TimelineControl.MinPxPerSecond, low.TimelinePxPerSecond);

        ProjectLayout high = ProjectLayoutStore.Clamp(new ProjectLayout(
            ProjectWidth: 1e6, InspectorWidth: 1e6, TimelineHeight: 1e6, TimelinePxPerSecond: 1e6));
        Assert.Equal(ProjectLayoutStore.MaxPaneSize, high.ProjectWidth);
        Assert.Equal(ProjectLayoutStore.MaxPaneSize, high.InspectorWidth);
        Assert.Equal(ProjectLayoutStore.MaxPaneSize, high.TimelineHeight);
        Assert.Equal(TimelineControl.MaxPxPerSecond, high.TimelinePxPerSecond);
    }

    [Fact]
    public void Clamp_Replaces_Non_Finite_Values_With_Defaults()
    {
        ProjectLayout layout = ProjectLayoutStore.Clamp(new ProjectLayout(
            ProjectWidth: double.NaN, TimelinePxPerSecond: double.PositiveInfinity, SourceMediaPath: "  "));
        Assert.Equal(ProjectLayoutStore.DefaultProjectWidth, layout.ProjectWidth);
        Assert.Equal(ProjectLayoutStore.DefaultPxPerSecond, layout.TimelinePxPerSecond);
        Assert.Null(layout.SourceMediaPath);
    }

    [Fact]
    public void Key_Is_Stable_And_Distinguishes_Projects()
    {
        string a = Path.Combine(Path.GetTempPath(), "a.sprocket.json");
        string b = Path.Combine(Path.GetTempPath(), "b.sprocket.json");
        Assert.Equal(ProjectLayoutStore.KeyFor(a), ProjectLayoutStore.KeyFor(a));
        Assert.NotEqual(ProjectLayoutStore.KeyFor(a), ProjectLayoutStore.KeyFor(b));
        Assert.Equal(ProjectLayoutStore.KeyFor(a), ProjectLayoutStore.KeyFor(a.ToUpperInvariant()));
        Assert.Matches("^[0-9a-f]{64}$", ProjectLayoutStore.KeyFor(a));
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_And_Records_Project_Path()
    {
        string dir = NewTempDir();
        try
        {
            string project = Path.Combine(dir, "film.sprocket.json");
            Assert.Null(ProjectLayoutStore.Load(project, dir));

            var layout = new ProjectLayout(ProjectWidth: 300, TimelinePxPerSecond: 200, SourceMonitorActive: true,
                SourceMediaPath: Path.Combine(dir, "clip.mp4"));
            ProjectLayoutStore.Save(project, layout, dir);

            ProjectLayout loaded = ProjectLayoutStore.Load(project, dir)!;
            Assert.Equal(layout with { ProjectPath = Path.GetFullPath(project) }, loaded);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_Prunes_Oldest_Layouts_Beyond_The_Cap()
    {
        string dir = NewTempDir();
        try
        {
            for (int i = 0; i < ProjectLayoutStore.MaxLayouts + 5; i++)
            {
                string project = Path.Combine(dir, $"p{i}.sprocket.json");
                ProjectLayoutStore.Save(project, new ProjectLayout(), dir);
                File.SetLastWriteTimeUtc(
                    Path.Combine(dir, ProjectLayoutStore.KeyFor(project) + ".json"),
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
            }
            // One more save triggers the prune with distinct, known timestamps in place.
            string newest = Path.Combine(dir, "newest.sprocket.json");
            ProjectLayoutStore.Save(newest, new ProjectLayout(), dir);

            Assert.Equal(ProjectLayoutStore.MaxLayouts, Directory.GetFiles(dir, "*.json").Length);
            Assert.NotNull(ProjectLayoutStore.Load(newest, dir));
            Assert.Null(ProjectLayoutStore.Load(Path.Combine(dir, "p0.sprocket.json"), dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-layouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
