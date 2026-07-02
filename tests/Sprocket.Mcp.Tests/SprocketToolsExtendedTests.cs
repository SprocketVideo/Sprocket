using System.Text.Json.Nodes;
using ModelContextProtocol;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Mcp;
using Xunit;

namespace Sprocket.Mcp.Tests;

/// <summary>
/// The step-38 follow-on tool surface: linked-aware editing, duplicate, fades/keyframes, professional trims,
/// structure (tracks/transitions/generators/audio chains), edit groups, and the session tools
/// (lifecycle / export / transport). Same harness as <see cref="SprocketToolsTests"/> — a fake session over a
/// real model + <c>EditHistory</c>.
/// </summary>
public class SprocketToolsExtendedTests
{
    /// <summary>Imports A/V media and places a linked pair at 1 s; returns the video clip + its audio partner.</summary>
    private static async Task<(FakeEditorSession Session, SprocketTools Tools, Clip Video, Clip Audio)> LinkedPair()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        JsonNode imported = JsonNode.Parse(await tools.ImportMedia(@"C:\media\shot.mp4"))!;
        JsonNode placed = JsonNode.Parse(await tools.AddClipToTimeline(
            (string)imported["media_id"]!, startTicks: 240000))!;
        Clip video = RuntimeIds.FindClip(session.Project, (int)placed["clip_id"]!, out _)!;
        Clip audio = session.Project.Timeline.ClipsLinkedTo(video).Single().Clip;
        return (session, tools, video, audio);
    }

    // ── Linked placement / linked editing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AddClipToTimeline_Video_Only_Stream_Creates_No_Audio_Clip()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        JsonNode imported = JsonNode.Parse(await tools.ImportMedia(@"C:\media\shot.mp4"))!;

        JsonNode placed = JsonNode.Parse(await tools.AddClipToTimeline(
            (string)imported["media_id"]!, 0, stream: "video"))!;
        Clip clip = RuntimeIds.FindClip(session.Project, (int)placed["clip_id"]!, out Track? track)!;

        Assert.IsType<VideoTrack>(track);
        Assert.Null(clip.LinkGroupId);
        Assert.Empty(session.Project.Timeline.AudioTracks.Single().Clips);
    }

    [Fact]
    public async Task AddClipToTimeline_Unlinked_Places_Both_Streams_Without_A_Link_Group()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        JsonNode imported = JsonNode.Parse(await tools.ImportMedia(@"C:\media\shot.mp4"))!;

        JsonNode placed = JsonNode.Parse(await tools.AddClipToTimeline(
            (string)imported["media_id"]!, 0, linked: false))!;
        Clip clip = RuntimeIds.FindClip(session.Project, (int)placed["clip_id"]!, out _)!;

        Assert.Null(clip.LinkGroupId);
        Assert.Single(session.Project.Timeline.AudioTracks.Single().Clips);
    }

    [Fact]
    public async Task DeleteClip_Removes_Linked_Partner_As_One_Undo_Entry()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();
        int entries = session.History.UndoCount;

        await tools.DeleteClip(RuntimeIds.IdOf(video));
        Assert.Empty(session.Project.Timeline.VideoTracks.Single().Clips);
        Assert.Empty(session.Project.Timeline.AudioTracks.Single().Clips);
        Assert.Equal(entries + 1, session.History.UndoCount);

        session.History.Undo(); // one undo restores both
        Assert.Single(session.Project.Timeline.VideoTracks.Single().Clips);
        Assert.Single(session.Project.Timeline.AudioTracks.Single().Clips);
    }

    [Fact]
    public async Task MoveClip_Shifts_The_Linked_Partner_By_The_Same_Delta()
    {
        (FakeEditorSession _, SprocketTools tools, Clip video, Clip audio) = await LinkedPair();

        await tools.MoveClip(RuntimeIds.IdOf(video), 480000);
        Assert.Equal(480000, video.TimelineStart.Ticks);
        Assert.Equal(480000, audio.TimelineStart.Ticks);

        await tools.MoveClip(RuntimeIds.IdOf(video), 0, includeLinked: false);
        Assert.Equal(0, video.TimelineStart.Ticks);
        Assert.Equal(480000, audio.TimelineStart.Ticks); // partner untouched
    }

    [Fact]
    public async Task SplitClip_Splits_The_Linked_Partner_And_Relinks_The_Right_Halves()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip audio) = await LinkedPair();
        long cut = video.TimelineStart.Ticks + 120000;

        JsonNode result = JsonNode.Parse(await tools.SplitClip(RuntimeIds.IdOf(video), cut))!;
        Clip right = RuntimeIds.FindClip(session.Project, (int)result["right_clip_id"]!, out _)!;
        Clip audioRight = session.Project.Timeline.AudioTracks.Single().Clips.Single(c => c.TimelineStart.Ticks == cut);

        Assert.Equal(cut, video.TimelineEnd.Ticks);
        Assert.Equal(cut, audio.TimelineEnd.Ticks);
        Assert.NotNull(right.LinkGroupId);
        Assert.Equal(right.LinkGroupId, audioRight.LinkGroupId);   // right halves share a fresh group
        Assert.NotEqual(video.LinkGroupId, right.LinkGroupId);     // distinct from the left pair's group
        Assert.Single(result["linked_splits"]!.AsArray());
    }

    [Fact]
    public async Task TrimClip_Trims_The_Linked_Partner_Too()
    {
        (FakeEditorSession _, SprocketTools tools, Clip video, Clip audio) = await LinkedPair();
        long newEnd = video.TimelineEnd.Ticks - 120000;

        await tools.TrimClip(RuntimeIds.IdOf(video), "out", newEnd);
        Assert.Equal(newEnd, video.TimelineEnd.Ticks);
        Assert.Equal(newEnd, audio.TimelineEnd.Ticks);
    }

    // ── Duplicate ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DuplicateClip_Copies_Effects_With_Rebased_Keyframes_And_The_Linked_Partner()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();
        int videoId = RuntimeIds.IdOf(video);
        await tools.AddEffect(videoId, EffectTypeIds.Color);
        await tools.SetClipFade(videoId, fadeOutTicks: 120000); // keyframed opacity to verify the rebase

        int entries = session.History.UndoCount;
        JsonNode result = JsonNode.Parse(await tools.DuplicateClip(videoId))!;
        Clip copy = RuntimeIds.FindClip(session.Project, (int)result["clip_id"]!, out Track? copyTrack)!;

        Assert.IsType<VideoTrack>(copyTrack);
        Assert.Equal(video.TimelineEnd.Ticks, copy.TimelineStart.Ticks); // butted after the original
        Assert.Equal(video.Effects.Count, copy.Effects.Count);           // Color + Fade both copied
        Assert.Contains(copy.Effects, e => e.EffectTypeId == EffectTypeIds.Color);

        // The fade-out envelope moved with the copy: opacity is 0 at the copy's own end, 1 mid-clip.
        AnimatableValue opacity = FadeOps.FadeOpacity(copy)!;
        Assert.Equal(0, opacity.Evaluate(copy.TimelineEnd), 3);
        Assert.Equal(1, opacity.Evaluate(copy.TimelineStart), 3);

        // The linked audio partner duplicated too, sharing a fresh group.
        Clip audioCopy = session.Project.Timeline.ClipsLinkedTo(copy).Single().Clip;
        Assert.NotEqual(video.LinkGroupId, copy.LinkGroupId);
        Assert.Equal(copy.LinkGroupId, audioCopy.LinkGroupId);

        // One undo removes the whole duplicate group.
        Assert.Equal(entries + 1, session.History.UndoCount);
        session.History.Undo();
        Assert.Single(session.Project.Timeline.VideoTracks.Single().Clips);
        Assert.Single(session.Project.Timeline.AudioTracks.Single().Clips);
    }

    [Fact]
    public async Task DuplicateClip_Video_Only_When_IncludeLinked_Is_Off()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();

        JsonNode result = JsonNode.Parse(await tools.DuplicateClip(
            RuntimeIds.IdOf(video), includeLinked: false))!;
        Clip copy = RuntimeIds.FindClip(session.Project, (int)result["clip_id"]!, out _)!;

        Assert.Null(copy.LinkGroupId);
        Assert.Single(session.Project.Timeline.AudioTracks.Single().Clips); // no audio copy
    }

    // ── Link management ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unlink_And_Relink_Round_Trip()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip audio) = await LinkedPair();

        await tools.UnlinkClip(RuntimeIds.IdOf(video));
        Assert.Null(video.LinkGroupId);
        Assert.Null(audio.LinkGroupId);

        await tools.LinkClips([RuntimeIds.IdOf(video), RuntimeIds.IdOf(audio)]);
        Assert.NotNull(video.LinkGroupId);
        Assert.Equal(video.LinkGroupId, audio.LinkGroupId);

        session.History.Undo(); // relink is one entry
        Assert.Null(video.LinkGroupId);
    }

    // ── Fades & keyframes ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetClipFade_Authors_The_Handle_Envelope()
    {
        (FakeEditorSession _, SprocketTools tools, Clip video, Clip _) = await LinkedPair();

        JsonNode result = JsonNode.Parse(await tools.SetClipFade(
            RuntimeIds.IdOf(video), fadeInTicks: 60000, fadeOutTicks: 120000))!;
        Assert.Equal(60000, (long)result["fade_in_ticks"]!);

        // Identical to the UI fade handles' envelope for the same lengths.
        AnimatableValue expected = FadeOps.BuildOpacity(
            null, video.TimelineStart.Ticks, video.TimelineEnd.Ticks, 60000, 120000);
        AnimatableValue actual = FadeOps.FadeOpacity(video)!;
        Assert.Equal(expected.Keyframes.Count, actual.Keyframes.Count);
        for (int i = 0; i < expected.Keyframes.Count; i++)
        {
            Assert.Equal(expected.Keyframes[i].Time.Ticks, actual.Keyframes[i].Time.Ticks);
            Assert.Equal(expected.Keyframes[i].Value, actual.Keyframes[i].Value, 6);
        }
        Assert.Equal((60000, 120000), FadeOps.ReadFades(video));

        // Overlapping fades are rejected.
        await Assert.ThrowsAsync<McpException>(() => tools.SetClipFade(
            RuntimeIds.IdOf(video), fadeInTicks: video.Duration.Ticks, fadeOutTicks: 1));
    }

    [Fact]
    public async Task SetEffectParameterKeyframes_Writes_A_Clip_Relative_Envelope()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();
        int clipId = RuntimeIds.IdOf(video);
        JsonNode added = JsonNode.Parse(await tools.AddEffect(clipId, EffectTypeIds.Brightness))!;
        int index = (int)added["effect_index"]!;

        await tools.SetEffectParameterKeyframes(clipId, index, EffectParamNames.Amount,
        [
            new KeyframeInput(0, 1.0),
            new KeyframeInput(120000, 2.0, "EaseInOut"),
        ]);

        AnimatableValue amount = video.Effects[index].Parameters[EffectParamNames.Amount];
        Assert.True(amount.IsAnimated);
        Assert.Equal(1.0, amount.Evaluate(video.TimelineStart), 6);
        Assert.Equal(2.0, amount.Evaluate(video.TimelineStart + new Timecode(120000)), 6);

        await Assert.ThrowsAsync<McpException>(() => tools.SetEffectParameterKeyframes(
            clipId, index, EffectParamNames.Amount, [new KeyframeInput(0, 1, "Bouncy")]));
        await Assert.ThrowsAsync<McpException>(() => tools.SetEffectParameterKeyframes(
            clipId, index, EffectParamNames.Amount, []));

        // get_clip reports the keyframes with clip-relative offsets.
        JsonNode detail = JsonNode.Parse(await tools.GetClip(clipId))!;
        JsonNode parameter = detail["effects"]!.AsArray()
            .Single(e => (string)e!["type_id"]! == EffectTypeIds.Brightness)!["parameters"]![EffectParamNames.Amount]!;
        JsonArray keyframes = parameter["keyframes"]!.AsArray();
        Assert.Equal(2, keyframes.Count);
        Assert.Equal(0, (long)keyframes[0]!["clip_offset_ticks"]!);
        Assert.Equal(120000, (long)keyframes[1]!["clip_offset_ticks"]!);
        Assert.NotNull(session.Project); // silence unused warning path
    }

    [Fact]
    public async Task GetClip_Reports_Placement_Links_Fades_And_Media()
    {
        (FakeEditorSession _, SprocketTools tools, Clip video, Clip audio) = await LinkedPair();
        await tools.SetClipFade(RuntimeIds.IdOf(video), fadeOutTicks: 120000);

        JsonNode detail = JsonNode.Parse(await tools.GetClip(RuntimeIds.IdOf(video)))!;
        Assert.Equal("video", (string)detail["track_kind"]!);
        Assert.Equal(120000, (long)detail["fade_out_ticks"]!);
        Assert.Equal("shot.mp4", (string)detail["media"]!["name"]!);
        Assert.Equal(RuntimeIds.IdOf(audio), (int)detail["linked_clips"]!.AsArray().Single()!["clip_id"]!);
    }

    // ── Effect utilities / retime / gain ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyEffects_Clones_The_Stack_Onto_The_Target()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();
        int videoId = RuntimeIds.IdOf(video);
        await tools.AddEffect(videoId, EffectTypeIds.Color);
        await tools.SetEffectParameter(videoId, 0, EffectParamNames.Exposure, 1.5);

        JsonNode dup = JsonNode.Parse(await tools.DuplicateClip(videoId, includeLinked: false))!;
        Clip copy = RuntimeIds.FindClip(session.Project, (int)dup["clip_id"]!, out _)!;
        copy.Effects.Clear(); // strip so copy_effects is what restores them

        await tools.CopyEffects(videoId, (int)dup["clip_id"]!);
        Assert.Single(copy.Effects);
        Assert.Equal(1.5, copy.Effects[0].Parameters[EffectParamNames.Exposure].Evaluate(default), 6);
        Assert.NotSame(video.Effects[0], copy.Effects[0]); // cloned, not shared

        await Assert.ThrowsAsync<McpException>(() => tools.CopyEffects(videoId, videoId));
    }

    [Fact]
    public async Task SetEffectEnabled_Toggles_Without_Removing()
    {
        (FakeEditorSession _, SprocketTools tools, Clip video, Clip _) = await LinkedPair();
        int clipId = RuntimeIds.IdOf(video);
        await tools.AddEffect(clipId, EffectTypeIds.Brightness);

        await tools.SetEffectEnabled(clipId, 0, false);
        Assert.False(video.Effects[0].Enabled);
        await tools.SetEffectEnabled(clipId, 0, true);
        Assert.True(video.Effects[0].Enabled);
    }

    [Fact]
    public async Task SetClipSpeed_Retimes_The_Linked_Pair()
    {
        (FakeEditorSession _, SprocketTools tools, Clip video, Clip audio) = await LinkedPair();
        long originalDuration = video.Duration.Ticks;

        await tools.SetClipSpeed(RuntimeIds.IdOf(video), 2);
        Assert.Equal(new Rational(2, 1), video.SpeedRatio);
        Assert.Equal(new Rational(2, 1), audio.SpeedRatio);
        Assert.Equal(originalDuration / 2, video.Duration.Ticks);

        await Assert.ThrowsAsync<McpException>(() => tools.SetClipSpeed(RuntimeIds.IdOf(video), 0));
    }

    [Fact]
    public async Task SetClipGain_Sets_And_Undoes()
    {
        (FakeEditorSession session, SprocketTools tools, Clip _, Clip audio) = await LinkedPair();
        await tools.SetClipGain(RuntimeIds.IdOf(audio), -6);
        Assert.Equal(-6, audio.GainDb);
        session.History.Undo();
        Assert.Equal(0, audio.GainDb);
    }

    // ── Ripple / roll / slide / ripple delete ───────────────────────────────────────────────────────

    /// <summary>Two butted video-only clips (0..2 s, 2..4 s) trimmed to half their 2 s source (0.5..1.5 s).</summary>
    private static async Task<(FakeEditorSession Session, SprocketTools Tools, Clip First, Clip Second)> ButtedClips()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        JsonNode imported = JsonNode.Parse(await tools.ImportMedia(@"C:\media\shot.mp4"))!;
        string mediaId = (string)imported["media_id"]!;

        JsonNode first = JsonNode.Parse(await tools.AddClipToTimeline(mediaId, 0, stream: "video"))!;
        Clip a = RuntimeIds.FindClip(session.Project, (int)first["clip_id"]!, out _)!;
        a.SourceIn = new Timecode(120000);
        a.SourceOut = new Timecode(360000); // 1 s long with 0.5 s handles each side

        JsonNode second = JsonNode.Parse(await tools.AddClipToTimeline(mediaId, a.Duration.Ticks, stream: "video"))!;
        Clip b = RuntimeIds.FindClip(session.Project, (int)second["clip_id"]!, out _)!;
        b.SourceIn = new Timecode(120000);
        b.SourceOut = new Timecode(360000);
        b.TimelineStart = a.TimelineEnd;
        return (session, tools, a, b);
    }

    [Fact]
    public async Task RippleTrim_Shifts_Downstream_Clips_To_Close_The_Gap()
    {
        (FakeEditorSession _, SprocketTools tools, Clip first, Clip second) = await ButtedClips();
        long secondStart = second.TimelineStart.Ticks;

        // Shorten the first clip's out edge by 0.25 s — the second clip slides left to stay butted.
        await tools.RippleTrim(RuntimeIds.IdOf(first), "out", first.TimelineEnd.Ticks - 60000);
        Assert.Equal(secondStart - 60000, second.TimelineStart.Ticks);
        Assert.Equal(first.TimelineEnd.Ticks, second.TimelineStart.Ticks);

        // Trimming past the media's end is rejected.
        await Assert.ThrowsAsync<McpException>(() => tools.RippleTrim(
            RuntimeIds.IdOf(first), "out", first.TimelineEnd.Ticks + 480000));
    }

    [Fact]
    public async Task RollEdit_Moves_The_Cut_Keeping_The_Combined_Span()
    {
        (FakeEditorSession _, SprocketTools tools, Clip first, Clip second) = await ButtedClips();
        long cut = first.TimelineEnd.Ticks;
        long combinedEnd = second.TimelineEnd.Ticks;

        await tools.RollEdit(RuntimeIds.IdOf(first), "end", cut + 60000);
        Assert.Equal(cut + 60000, first.TimelineEnd.Ticks);
        Assert.Equal(cut + 60000, second.TimelineStart.Ticks);
        Assert.Equal(combinedEnd, second.TimelineEnd.Ticks); // downstream end unchanged

        await Assert.ThrowsAsync<McpException>(() => tools.RollEdit(
            RuntimeIds.IdOf(first), "start", 1)); // no clip before the first
    }

    [Fact]
    public async Task SlideClip_Moves_The_Clip_While_Neighbours_Absorb()
    {
        (FakeEditorSession session, SprocketTools tools, Clip first, Clip second) = await ButtedClips();
        // Add a third butted clip so the middle one has neighbours on both sides.
        JsonNode imported = JsonNode.Parse(await tools.ListMedia())!;
        string mediaId = (string)imported["media"]!.AsArray().Single()!["media_id"]!;
        JsonNode third = JsonNode.Parse(await tools.AddClipToTimeline(mediaId, second.TimelineEnd.Ticks, stream: "video"))!;
        Clip c = RuntimeIds.FindClip(session.Project, (int)third["clip_id"]!, out _)!;
        c.SourceIn = new Timecode(120000);
        c.SourceOut = new Timecode(360000);
        c.TimelineStart = second.TimelineEnd;

        long duration = second.Duration.Ticks;
        await tools.SlideClip(RuntimeIds.IdOf(second), 60000);
        Assert.Equal(duration, second.Duration.Ticks);                    // slid clip unchanged in length
        Assert.Equal(first.TimelineEnd, second.TimelineStart);             // still butted left
        Assert.Equal(second.TimelineEnd, c.TimelineStart);                 // still butted right
    }

    [Fact]
    public async Task RippleDelete_Closes_The_Gap()
    {
        (FakeEditorSession session, SprocketTools tools, Clip first, Clip second) = await ButtedClips();
        long duration = first.Duration.Ticks;
        long secondStart = second.TimelineStart.Ticks;

        await tools.RippleDelete(RuntimeIds.IdOf(first));
        Assert.DoesNotContain(first, session.Project.Timeline.VideoTracks.Single().Clips);
        Assert.Equal(secondStart - duration, second.TimelineStart.Ticks);
    }

    // ── Tracks / transitions / markers / generators / chains ───────────────────────────────────────

    [Fact]
    public async Task AddTrack_And_RemoveTrack_With_Force_Guard()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();

        JsonNode added = JsonNode.Parse(await tools.AddTrack("video"))!;
        int trackId = (int)added["track_id"]!;
        Assert.Equal(2, session.Project.Timeline.VideoTracks.Count());

        await tools.RemoveTrack(trackId);
        Assert.Single(session.Project.Timeline.VideoTracks);

        int occupied = RuntimeIds.IdOf(session.Project.Timeline.VideoTracks.Single());
        await Assert.ThrowsAsync<McpException>(() => tools.RemoveTrack(occupied));
        await tools.RemoveTrack(occupied, force: true);
        Assert.Empty(session.Project.Timeline.VideoTracks);
        Assert.NotNull(video); // keep the placed clip rooted for the assertion above
    }

    [Fact]
    public async Task Transition_Add_Set_Remove_Round_Trip()
    {
        (FakeEditorSession session, SprocketTools tools, Clip first, Clip _) = await ButtedClips();

        JsonNode added = JsonNode.Parse(await tools.AddTransition(RuntimeIds.IdOf(first), "end"))!;
        int transitionId = (int)added["transition_id"]!;
        Track track = session.Project.Timeline.VideoTracks.Single();
        Transition transition = track.Transitions.Single();
        Assert.Equal(TransitionTypeIds.CrossDissolve, transition.TransitionTypeId);
        Assert.Equal(first.TimelineEnd.Ticks, transition.CutPoint.Ticks);
        Assert.True(transition.Duration.Ticks <= first.Duration.Ticks); // clamped into the clips

        await tools.SetTransition(transitionId, durationTicks: 120000, alignment: "EndAtCut");
        Assert.Equal(120000, transition.Duration.Ticks);
        Assert.Equal(TransitionAlignment.EndAtCut, transition.Alignment);

        await tools.RemoveTransition(transitionId);
        Assert.Empty(track.Transitions);

        // No adjacent clip at the second clip's end — rejected.
        await Assert.ThrowsAsync<McpException>(() => tools.AddTransition(RuntimeIds.IdOf(first), "start"));
    }

    [Fact]
    public async Task UpdateMarker_Moves_And_Relabels_As_One_Entry()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        await tools.AddMarker(120000, "beat", "Green");
        int entries = session.History.UndoCount;

        await tools.UpdateMarker(120000, newTimeTicks: 240000, name: "chorus", color: "Red", comment: "drop");
        Marker marker = session.Project.Timeline.Markers.Single();
        Assert.Equal(240000, marker.Time.Ticks);
        Assert.Equal("chorus", marker.Name);
        Assert.Equal(MarkerColor.Red, marker.Color);
        Assert.Equal("drop", marker.Comment);
        Assert.Equal(entries + 1, session.History.UndoCount);

        session.History.Undo();
        Assert.Equal(120000, marker.Time.Ticks);
        Assert.Equal("beat", marker.Name);
    }

    [Fact]
    public async Task Generator_Tools_Create_And_Edit_A_Title()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);

        JsonNode types = JsonNode.Parse(await tools.ListGeneratorTypes())!;
        Assert.Contains(types["generator_types"]!.AsArray(),
            t => (string)t!["type_id"]! == GeneratorTypeIds.Title);

        JsonNode added = JsonNode.Parse(await tools.AddGeneratorClip(GeneratorTypeIds.Title, 0))!;
        Clip clip = RuntimeIds.FindClip(session.Project, (int)added["clip_id"]!, out Track? track)!;
        Assert.IsType<VideoTrack>(track);
        Assert.Equal(ClipKind.Generator, clip.Kind);

        await tools.SetGeneratorText((int)added["clip_id"]!, GeneratorParamNames.Text, "Hello");
        await tools.SetGeneratorParameter((int)added["clip_id"]!, GeneratorParamNames.FontSize, 0.2);
        Assert.Equal("Hello", clip.Generator!.GetString(GeneratorParamNames.Text));
        Assert.Equal(0.2, clip.Generator.Parameters[GeneratorParamNames.FontSize].Evaluate(default), 6);

        // A generator over occupied topmost track stacks onto a fresh track (one undo entry).
        int before = session.Project.Timeline.VideoTracks.Count();
        await tools.AddGeneratorClip(GeneratorTypeIds.SolidColor, 0);
        Assert.Equal(before + 1, session.Project.Timeline.VideoTracks.Count());
    }

    [Fact]
    public async Task Audio_Chain_Tools_Manage_All_Three_Scopes()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        int trackId = RuntimeIds.IdOf(session.Project.Timeline.AudioTracks.Single());

        await tools.AddChainEffect("track", EffectTypeIds.AudioEq, trackId);
        await tools.AddChainEffect("sequence", EffectTypeIds.AudioCompressor);
        await tools.AddChainEffect("master", EffectTypeIds.AudioGain);
        Assert.Single(session.Project.Timeline.AudioTracks.Single().Effects);
        Assert.Single(session.Project.Timeline.AudioEffects);
        Assert.Single(session.Project.Settings.MasterAudioEffects);

        await tools.SetChainEffectParameter("master", 0, EffectParamNames.GainDb, -3);
        Assert.Equal(-3, session.Project.Settings.MasterAudioEffects[0]
            .Parameters[EffectParamNames.GainDb].Evaluate(default), 6);

        JsonNode chain = JsonNode.Parse(await tools.ListAudioChain("track", trackId))!;
        Assert.Single(chain["effects"]!.AsArray());

        await tools.RemoveChainEffect("sequence", 0);
        Assert.Empty(session.Project.Timeline.AudioEffects);

        // Guards: non-audio effect, video track, missing trackId.
        await Assert.ThrowsAsync<McpException>(() => tools.AddChainEffect("master", EffectTypeIds.Brightness));
        int videoTrackId = RuntimeIds.IdOf(session.Project.Timeline.VideoTracks.Single());
        await Assert.ThrowsAsync<McpException>(() => tools.ListAudioChain("track", videoTrackId));
        await Assert.ThrowsAsync<McpException>(() => tools.ListAudioChain("track"));
    }

    // ── Edit groups & undo steps ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EditGroup_Collapses_Multiple_Edits_Into_One_Undo_Entry()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();
        int clipId = RuntimeIds.IdOf(video);
        int entries = session.History.UndoCount;

        await tools.BeginEditGroup("Grade and fade");
        await tools.AddEffect(clipId, EffectTypeIds.Color);
        await tools.SetEffectParameter(clipId, 0, EffectParamNames.Exposure, 1.0);
        await tools.SetClipFade(clipId, fadeOutTicks: 120000);
        await tools.EndEditGroup();

        Assert.Equal(entries + 1, session.History.UndoCount);
        Assert.Equal("Grade and fade", session.History.UndoLabel);

        session.History.Undo(); // the whole group reverts
        Assert.Empty(video.Effects);

        await Assert.ThrowsAsync<McpException>(() => tools.EndEditGroup()); // nothing open
    }

    [Fact]
    public async Task CancelEditGroup_Reverts_The_Collected_Edits()
    {
        (FakeEditorSession session, SprocketTools tools, Clip video, Clip _) = await LinkedPair();
        int clipId = RuntimeIds.IdOf(video);
        int entries = session.History.UndoCount;

        await tools.BeginEditGroup("Abandoned work");
        await tools.AddEffect(clipId, EffectTypeIds.Brightness);
        await tools.CancelEditGroup();

        Assert.Empty(video.Effects);
        Assert.Equal(entries, session.History.UndoCount); // no entry landed
    }

    [Fact]
    public async Task Undo_And_Redo_Take_A_Step_Count()
    {
        var session = new FakeEditorSession();
        var tools = new SprocketTools(session);
        await tools.AddMarker(0, "a");
        await tools.AddMarker(240000, "b");

        await tools.Undo(2);
        Assert.Empty(session.Project.Timeline.Markers);
        await tools.Redo(2);
        Assert.Equal(2, session.Project.Timeline.Markers.Count);
        await Assert.ThrowsAsync<McpException>(() => tools.Undo(0));
    }

    // ── Session: lifecycle / export / transport ─────────────────────────────────────────────────────

    [Fact]
    public async Task OpenProject_Gates_On_Unsaved_Changes()
    {
        (FakeEditorSession session, SprocketTools tools, Clip _, Clip _) = await LinkedPair();
        Assert.True(session.IsDirty);

        await Assert.ThrowsAsync<McpException>(() => tools.OpenProject(@"C:\projects\other.sprocket.json"));
        Assert.Null(session.OpenedPath);

        await tools.OpenProject(@"C:\projects\other.sprocket.json", discardChanges: true);
        Assert.Equal(@"C:\projects\other.sprocket.json", session.OpenedPath);
    }

    [Fact]
    public async Task CloseProject_And_NewProject_Swap_To_A_Fresh_Session()
    {
        (FakeEditorSession session, SprocketTools tools, Clip _, Clip _) = await LinkedPair();

        await Assert.ThrowsAsync<McpException>(() => tools.CloseProject());
        await tools.SaveProjectAs(@"C:\projects\demo.sprocket.json"); // now clean
        await tools.CloseProject();
        await tools.NewProject();
        Assert.Equal(2, session.NewProjectCount);
    }

    [Fact]
    public async Task SaveProjectAs_Adopts_The_Path_And_Clears_Dirty()
    {
        (FakeEditorSession session, SprocketTools tools, Clip _, Clip _) = await LinkedPair();
        Assert.True(session.IsDirty);

        await tools.SaveProjectAs(@"C:\projects\demo.sprocket.json");
        Assert.Equal(@"C:\projects\demo.sprocket.json", session.ProjectPath);
        Assert.False(session.IsDirty);

        await Assert.ThrowsAsync<McpException>(() => tools.SaveProjectAs("relative.json"));
    }

    [Fact]
    public async Task ExportVideo_Starts_And_Reports_Status()
    {
        (FakeEditorSession session, SprocketTools tools, Clip _, Clip _) = await LinkedPair();

        JsonNode status = JsonNode.Parse(await tools.ExportVideo(
            @"C:\out\final.mp4", videoOnly: true, rangeInTicks: 0, rangeOutTicks: 240000))!;
        Assert.Equal(@"C:\out\final.mp4", session.ExportedPath);
        Assert.True(session.ExportVideoOnly);
        Assert.Equal((0L, 240000L), (session.ExportRange.In!.Value, session.ExportRange.Out!.Value));
        Assert.True((bool)status["completed"]!);

        JsonNode polled = JsonNode.Parse(await tools.GetExportStatus())!;
        Assert.Equal(1.0, (double)polled["progress"]!, 3);

        await tools.CancelExport();
        Assert.True(session.ExportCancelRequested);

        await Assert.ThrowsAsync<McpException>(() => tools.ExportVideo("relative.mp4"));
        await Assert.ThrowsAsync<McpException>(() => tools.ExportVideo(@"C:\out\x.mp4", rangeInTicks: 5, rangeOutTicks: 5));
    }

    [Fact]
    public async Task Transport_Stop_Rewind_End_And_Frame_Step()
    {
        (FakeEditorSession session, SprocketTools tools, Clip _, Clip _) = await LinkedPair();

        await tools.Play();
        await tools.Stop();
        Assert.False(session.IsPlaying);

        await tools.GoToEnd();
        Assert.Equal(session.DurationTicks, session.PlayheadTicks);
        await tools.GoToStart();
        Assert.Equal(0, session.PlayheadTicks);

        await tools.StepFrames(3); // 30 fps → 8000 ticks per frame
        Assert.Equal(3 * 8000, session.PlayheadTicks);
        await tools.StepFrames(-1);
        Assert.Equal(2 * 8000, session.PlayheadTicks);
        await Assert.ThrowsAsync<McpException>(() => tools.StepFrames(0));
    }

    // ── Read-side ergonomics ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectState_Sections_Filter_Trims_The_Payload()
    {
        (FakeEditorSession _, SprocketTools tools, Clip _, Clip _) = await LinkedPair();

        JsonNode full = JsonNode.Parse(await tools.GetProjectState())!;
        Assert.NotNull(full["media"]);
        Assert.NotNull(full["tracks"]);
        Assert.NotNull(full["dirty"]);

        JsonNode slim = JsonNode.Parse(await tools.GetProjectState("tracks"))!;
        Assert.Null(slim["media"]);
        Assert.NotNull(slim["tracks"]);
    }

    [Fact]
    public async Task ListEffectTypes_Filters_By_Category_And_Name()
    {
        var tools = new SprocketTools(new FakeEditorSession());

        JsonNode audio = JsonNode.Parse(await tools.ListEffectTypes("Audio"))!;
        Assert.All(audio["effect_types"]!.AsArray(),
            e => Assert.Equal("Audio", (string)e!["category"]!));

        JsonNode fade = JsonNode.Parse(await tools.ListEffectTypes(nameQuery: "fade"))!;
        Assert.Single(fade["effect_types"]!.AsArray());

        // Step/unit descriptor fields are emitted now.
        JsonNode color = JsonNode.Parse(await tools.ListEffectTypes(nameQuery: "white balance"))!;
        JsonNode temperature = color["effect_types"]!.AsArray().Single()!["parameters"]!.AsArray()
            .Single(p => (string)p!["name"]! == EffectParamNames.Temperature)!;
        Assert.Equal(1.0, (double)temperature["step"]!, 6);

        await Assert.ThrowsAsync<McpException>(() => tools.ListEffectTypes("Chartreuse"));
    }
}
