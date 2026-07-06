using System.Text.Json.Nodes;
using ModelContextProtocol;
using Sprocket.Core.Model;
using Sprocket.Mcp;
using Xunit;

namespace Sprocket.Mcp.Tests;

/// <summary>
/// The MCP edit tools over a fake session wrapping a real model + <c>EditHistory</c> (PLAN.md step 38):
/// every mutation lands in the model, pushes a labelled undo entry, and resolves ids robustly; failures
/// throw <see cref="McpException"/> and leave the history untouched.
/// </summary>
public class SprocketToolsTests
{
    private static async Task<(FakeEditorSession Session, SprocketTools Tools, int ClipId, Clip Clip)> PlacedClip()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        JsonNode imported = JsonNode.Parse(await tools.ImportMedia(@"C:\media\shot.mp4"))!;
        JsonNode placed = JsonNode.Parse(await tools.AddClipToTimeline(
            (string)imported["media_id"]!, startTicks: 240000))!;
        int clipId = (int)placed["clip_id"]!;
        Clip clip = RuntimeIds.FindClip(session.Project, clipId, out _)!;
        return (session, tools, clipId, clip);
    }

    [Fact]
    public void BuildTools_Exposes_The_Full_Surface()
    {
        var tools = new SprocketTools(new FakeEditorSession());
        var names = tools.BuildTools().Select(t => t.ProtocolTool.Name).ToHashSet();
        string[] expected =
        [
            "get_project_state", "list_media", "list_clips", "get_playhead", "list_effect_types",
            "seek", "play", "pause", "undo", "redo", "save_project",
            "import_media", "add_clip_to_timeline", "trim_clip", "move_clip", "split_clip", "delete_clip",
            "add_effect", "set_effect_parameter", "remove_effect", "move_effect", "add_marker", "remove_marker",
            // Clip tools (step 38 follow-on)
            "get_clip", "duplicate_clip", "unlink_clip", "link_clips", "set_clip_fade",
            "set_effect_parameter_keyframes", "copy_effects", "set_effect_enabled",
            "set_clip_speed", "set_clip_gain", "ripple_trim", "roll_edit", "slide_clip", "ripple_delete",
            // Structure tools
            "add_track", "remove_track", "list_transition_types", "add_transition", "remove_transition",
            "set_transition", "update_marker", "list_generator_types", "add_generator_clip",
            "set_generator_text", "set_generator_parameter", "list_audio_chain", "add_chain_effect",
            "remove_chain_effect", "set_chain_effect_parameter",
            "begin_edit_group", "end_edit_group", "cancel_edit_group",
            // Session tools
            "open_project", "close_project", "new_project", "save_project_as",
            "export_video", "export_audio", "get_export_status", "cancel_export",
            "stop", "go_to_start", "go_to_end", "step_frames",
        ];
        Assert.Equal(expected.Length, names.Count);
        foreach (string name in expected)
            Assert.Contains(name, names);
    }

    [Fact]
    public async Task Import_And_Place_Create_Model_State_With_Undo_Entries()
    {
        (FakeEditorSession session, SprocketTools _, int _, Clip clip) = await PlacedClip();
        Assert.Single(session.Project.MediaPool.Items);
        Assert.Same(clip, session.Project.Timeline.Tracks[0].Clips.Single());
        Assert.Equal(240000, clip.TimelineStart.Ticks);
        Assert.True(session.History.CanUndo);
        Assert.True(session.RefreshCount > 0);
    }

    [Fact]
    public async Task TrimClip_Moves_The_Requested_Edge_And_Is_Undoable()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId, Clip clip) = await PlacedClip();
        long originalEnd = clip.TimelineEnd.Ticks;

        await tools.TrimClip(clipId, "in", clip.TimelineStart.Ticks + 120000);
        Assert.Equal(240000 + 120000, clip.TimelineStart.Ticks);
        Assert.Equal(120000, clip.SourceIn.Ticks); // the source in moved with the edge
        Assert.Equal(originalEnd, clip.TimelineEnd.Ticks); // out edge untouched
        Assert.Equal("Trim linked clips", session.History.UndoLabel); // the placed pair trims together

        session.History.Undo();
        Assert.Equal(240000, clip.TimelineStart.Ticks);
        Assert.Equal(0, clip.SourceIn.Ticks);

        await tools.TrimClip(clipId, "out", originalEnd - 120000);
        Assert.Equal(originalEnd - 120000, clip.TimelineEnd.Ticks);
    }

    [Fact]
    public async Task TrimClip_Rejects_Bad_Edges_And_Empty_Results()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId, Clip clip) = await PlacedClip();
        int entries = session.History.UndoCount;

        await Assert.ThrowsAsync<McpException>(() => tools.TrimClip(clipId, "middle", 0));
        await Assert.ThrowsAsync<McpException>(() => tools.TrimClip(clipId, "in", clip.TimelineEnd.Ticks));
        await Assert.ThrowsAsync<McpException>(() => tools.TrimClip(clipId, "out", clip.TimelineStart.Ticks));
        // Trimming in before the source has media (source in would go negative).
        await Assert.ThrowsAsync<McpException>(() => tools.TrimClip(clipId, "in", clip.TimelineStart.Ticks - 120000));

        Assert.Equal(entries, session.History.UndoCount); // nothing landed
    }

    [Fact]
    public async Task MoveClip_Repositions_And_Changes_Tracks_With_Kind_Checking()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId, Clip clip) = await PlacedClip();
        var secondVideo = new VideoTrack { Name = "V2" };
        session.Project.Timeline.Tracks.Add(secondVideo);
        Track audio = session.Project.Timeline.Tracks.First(t => t is AudioTrack);

        await tools.MoveClip(clipId, 480000);
        Assert.Equal(480000, clip.TimelineStart.Ticks);

        await tools.MoveClip(clipId, 0, RuntimeIds.IdOf(secondVideo));
        Assert.Same(clip, secondVideo.Clips.Single());
        Assert.Empty(session.Project.Timeline.Tracks[0].Clips);

        await Assert.ThrowsAsync<McpException>(() => tools.MoveClip(clipId, 0, RuntimeIds.IdOf(audio)));
        await Assert.ThrowsAsync<McpException>(() => tools.MoveClip(clipId, 0, int.MaxValue));
    }

    [Fact]
    public async Task SplitClip_Yields_Two_Addressable_Clips()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId, Clip clip) = await PlacedClip();
        long cut = clip.TimelineStart.Ticks + 120000;

        JsonNode result = JsonNode.Parse(await tools.SplitClip(clipId, cut))!;
        int leftId = (int)result["left_clip_id"]!;
        int rightId = (int)result["right_clip_id"]!;
        Assert.Equal(clipId, leftId);
        Assert.NotEqual(leftId, rightId);

        Clip right = RuntimeIds.FindClip(session.Project, rightId, out _)!;
        Assert.Equal(cut, clip.TimelineEnd.Ticks);
        Assert.Equal(cut, right.TimelineStart.Ticks);

        await Assert.ThrowsAsync<McpException>(() => tools.SplitClip(clipId, clip.TimelineStart.Ticks));
    }

    [Fact]
    public async Task DeleteClip_Removes_And_Undo_Restores_The_Same_Instance()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId, Clip clip) = await PlacedClip();

        await tools.DeleteClip(clipId);
        Assert.Empty(session.Project.Timeline.Tracks[0].Clips);
        await Assert.ThrowsAsync<McpException>(() => tools.DeleteClip(clipId)); // already gone

        JsonNode undone = JsonNode.Parse(await tools.Undo())!;
        Assert.Same(clip, session.Project.Timeline.Tracks[0].Clips.Single());
        Assert.NotNull(undone["performed"]);
    }

    [Fact]
    public async Task Effect_Tools_Add_Set_And_Remove_Through_The_Stack()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId, Clip clip) = await PlacedClip();

        JsonNode added = JsonNode.Parse(await tools.AddEffect(clipId, EffectTypeIds.Brightness))!;
        int index = (int)added["effect_index"]!;
        Assert.Equal(EffectTypeIds.Brightness, clip.Effects[index].EffectTypeId);

        await tools.SetEffectParameter(clipId, index, EffectParamNames.Amount, 0.5);
        Assert.Equal(0.5, clip.Effects[index].Parameters[EffectParamNames.Amount].Evaluate(default));

        // The input color transform inserts at the head of the stack.
        await tools.AddEffect(clipId, EffectTypeIds.ColorTransform);
        Assert.Equal(EffectTypeIds.ColorTransform, clip.Effects[0].EffectTypeId);

        await tools.RemoveEffect(clipId, 0);
        Assert.DoesNotContain(clip.Effects, e => e.EffectTypeId == EffectTypeIds.ColorTransform);

        await Assert.ThrowsAsync<McpException>(() => tools.AddEffect(clipId, "no.such.effect"));
        await Assert.ThrowsAsync<McpException>(() => tools.RemoveEffect(clipId, 99));
        Assert.True(session.History.CanUndo);
    }

    [Fact]
    public async Task MoveEffect_Reorders_The_Stack_As_One_Undoable_Step()
    {
        (FakeEditorSession session, SprocketTools tools, int clipId, Clip clip) = await PlacedClip();
        await tools.AddEffect(clipId, EffectTypeIds.Brightness);
        await tools.AddEffect(clipId, EffectTypeIds.Fade);
        int entries = session.History.UndoCount;

        JsonNode moved = JsonNode.Parse(await tools.MoveEffect(clipId, 0, 1))!;
        Assert.Equal(1, (int)moved["effect_index"]!);
        Assert.Equal(EffectTypeIds.Fade, clip.Effects[0].EffectTypeId);
        Assert.Equal(EffectTypeIds.Brightness, clip.Effects[1].EffectTypeId);
        Assert.Equal(entries + 1, session.History.UndoCount); // one atomic entry, not remove+re-add

        session.History.Undo();
        Assert.Equal(EffectTypeIds.Brightness, clip.Effects[0].EffectTypeId);

        // Out-of-range targets clamp; a same-index move is a no-op that adds no history entry.
        await tools.MoveEffect(clipId, 0, 99);
        Assert.Equal(EffectTypeIds.Brightness, clip.Effects[^1].EffectTypeId);
        session.History.Undo();
        int before = session.History.UndoCount;
        await tools.MoveEffect(clipId, 1, 1);
        Assert.Equal(before, session.History.UndoCount);

        await Assert.ThrowsAsync<McpException>(() => tools.MoveEffect(clipId, 99, 0));
    }

    [Fact]
    public async Task Marker_Tools_Add_And_Remove_On_The_Timeline()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);

        await tools.AddMarker(120000, "beat", "Green");
        Marker marker = session.Project.Timeline.Markers.Single();
        Assert.Equal("beat", marker.Name);
        Assert.Equal(MarkerColor.Green, marker.Color);

        await Assert.ThrowsAsync<McpException>(() => tools.AddMarker(0, "x", "Chartreuse"));
        await Assert.ThrowsAsync<McpException>(() => tools.RemoveMarker(999));

        await tools.RemoveMarker(120000);
        Assert.Empty(session.Project.Timeline.Markers);
    }

    [Fact]
    public async Task Transport_And_Save_Tools_Drive_The_Session()
    {
        (FakeEditorSession session, SprocketTools tools, int _, Clip _) = await PlacedClip();

        await tools.Seek(120000);
        Assert.Equal(120000, session.PlayheadTicks);
        await tools.Play();
        Assert.True(session.IsPlaying);
        await tools.Pause();
        Assert.False(session.IsPlaying);

        await Assert.ThrowsAsync<McpException>(() => tools.SaveProject()); // untitled
        session.ProjectPath = @"C:\projects\demo.sprocket.json";
        await tools.SaveProject();
        Assert.Equal(1, session.SaveCount);
    }

    [Fact]
    public async Task Unknown_Clip_Ids_Fail_With_Actionable_Errors()
    {
        var tools = new SprocketTools(new FakeEditorSession());
        McpException ex = await Assert.ThrowsAsync<McpException>(() => tools.DeleteClip(12345));
        Assert.Contains("list_clips", ex.Message);
    }
}
