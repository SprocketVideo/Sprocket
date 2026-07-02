using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Mcp;
using Xunit;

namespace Sprocket.Mcp.Tests;

/// <summary>The session-lifetime clip/track id registry (PLAN.md step 38).</summary>
public class RuntimeIdsTests
{
    private static (Project Project, VideoTrack Track, Clip Clip) SmallProject()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(64, 64), 48000);
        var track = new VideoTrack { Name = "V1" };
        var clip = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(1), Timecode.Zero);
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return (new Project(timeline), track, clip);
    }

    [Fact]
    public void Ids_Are_Stable_Per_Instance_And_Distinct_Across_Instances()
    {
        (_, VideoTrack track, Clip clip) = SmallProject();
        Assert.Equal(RuntimeIds.IdOf(clip), RuntimeIds.IdOf(clip));
        Assert.Equal(RuntimeIds.IdOf(track), RuntimeIds.IdOf(track));
        Assert.NotEqual(RuntimeIds.IdOf(clip), RuntimeIds.IdOf(track));
    }

    [Fact]
    public void FindClip_Resolves_By_Id_And_Reports_The_Track()
    {
        (Project project, VideoTrack track, Clip clip) = SmallProject();
        int id = RuntimeIds.IdOf(clip);
        Clip? found = RuntimeIds.FindClip(project, id, out Track? foundTrack);
        Assert.Same(clip, found);
        Assert.Same(track, foundTrack);
    }

    [Fact]
    public void FindClip_Misses_Cleanly_For_Unknown_Or_Unassigned_Ids()
    {
        (Project project, _, Clip clip) = SmallProject();
        // An id that was never handed out must not match — and probing must not assign one to `clip`.
        Assert.Null(RuntimeIds.FindClip(project, int.MaxValue, out Track? track));
        Assert.Null(track);
        // The clip still gets a fresh id on first legitimate sight.
        Assert.True(RuntimeIds.IdOf(clip) > 0);
    }

    [Fact]
    public void FindTrack_Resolves_By_Id()
    {
        (Project project, VideoTrack track, _) = SmallProject();
        int id = RuntimeIds.IdOf(track);
        Assert.Same(track, RuntimeIds.FindTrack(project, id));
        Assert.Null(RuntimeIds.FindTrack(project, int.MaxValue));
    }

    [Fact]
    public void Id_Survives_Remove_Then_Undo()
    {
        (Project project, VideoTrack track, Clip clip) = SmallProject();
        int id = RuntimeIds.IdOf(clip);

        var history = new EditHistory();
        history.Execute(new RemoveClipCommand(track, clip));
        Assert.Null(RuntimeIds.FindClip(project, id, out _));

        history.Undo(); // the command re-inserts the SAME instance, so the id resolves again
        Assert.Same(clip, RuntimeIds.FindClip(project, id, out _));
        Assert.Equal(id, RuntimeIds.IdOf(clip));
    }
}
