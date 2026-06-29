using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Persistence.Tests;

/// <summary>
/// Tests for autosave + crash recovery (PLAN.md step 20): the atomic sidecar write round-trips a project, and the
/// pure <see cref="AutosaveRecovery"/> decision. The write test touches the temp dir; the recovery test is pure.
/// </summary>
public class AutosaveTests
{
    private static Project SmallProject()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1280, 720), 48000);
        var project = new Project(timeline);
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(3), Timecode.Zero));
        timeline.Tracks.Add(track);
        timeline.Markers.Add(new Marker(Timecode.FromSeconds(1), "marker"));
        return project;
    }

    [Fact]
    public void SidecarPath_Appends_The_Suffix()
    {
        Assert.Equal(@"C:\proj\a.sprocket.json" + Autosave.Suffix, Autosave.SidecarPath(@"C:\proj\a.sprocket.json"));
    }

    [Fact]
    public void Write_Is_Atomic_And_Loadable_And_Overwrites()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-autosave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string autosavePath = Path.Combine(dir, "project.sprocket.json" + Autosave.Suffix);

            Autosave.Write(SmallProject(), autosavePath);
            Assert.True(File.Exists(autosavePath));
            Assert.False(File.Exists(autosavePath + ".tmp")); // the temp file was promoted, not left behind

            Project recovered = ProjectSerializer.Load(autosavePath);
            Assert.Single(recovered.Timeline.Tracks);
            Assert.Single(recovered.Timeline.Markers);

            // A second write overwrites cleanly (atomic move with overwrite).
            Project bigger = SmallProject();
            bigger.Timeline.Tracks.Add(new AudioTrack { Name = "A1" });
            Autosave.Write(bigger, autosavePath);
            Assert.Equal(2, ProjectSerializer.Load(autosavePath).Timeline.Tracks.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Delete_Removes_The_Sidecar_And_Tolerates_Absence()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-autosave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string autosavePath = Path.Combine(dir, "x" + Autosave.Suffix);
            Autosave.Write(SmallProject(), autosavePath);
            Autosave.Delete(autosavePath);
            Assert.False(File.Exists(autosavePath));
            Autosave.Delete(autosavePath); // no throw on a missing file
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    // autosave newer than the clean save → offer recovery.
    [InlineData(true, 200, true, 100, true)]
    // autosave older than the clean save (a normal save after the autosave) → don't.
    [InlineData(true, 100, true, 200, false)]
    // autosave but no clean save at all (crash before the first manual save) → offer.
    [InlineData(true, 100, false, 0, true)]
    // no autosave → never.
    [InlineData(false, 0, true, 100, false)]
    public void ShouldOffer_Compares_Autosave_To_Clean_Save(
        bool autoExists, long autoMin, bool savedExists, long savedMin, bool expected)
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = new AutosaveRecovery.State(
            autoExists, baseTime.AddMinutes(autoMin), savedExists, baseTime.AddMinutes(savedMin));
        Assert.Equal(expected, AutosaveRecovery.ShouldOffer(state));
    }
}
