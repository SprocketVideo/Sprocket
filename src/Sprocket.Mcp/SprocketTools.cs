using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Mcp;

/// <summary>
/// The MCP tool surface (PLAN.md step 38). Read tools return the <see cref="StateFormatter"/> JSON;
/// every edit tool resolves ids, builds an <see cref="IEditCommand"/>, and runs it through
/// <see cref="IEditorApi.History"/> — so AI edits are undoable by construction and share the model's
/// validation — all inside one <see cref="IEditorSession.OnModelThreadAsync{T}"/> callback (atomic with
/// respect to user edits, on the thread that owns the model). Failures throw <see cref="McpException"/>
/// with actionable text; the SDK surfaces them as tool errors, never as transport faults.
/// </summary>
public sealed partial class SprocketTools(IEditorSession session)
{
    private readonly IEditorSession _session = session;

    /// <summary>Builds the tool list for an <see cref="McpServerOptions.ToolCollection"/> — every public
    /// instance method carrying <see cref="McpServerToolAttribute"/>.</summary>
    public IReadOnlyList<McpServerTool> BuildTools() =>
        [.. typeof(SprocketTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, this))];

    // ── Read tools ──────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_project_state", ReadOnly = true, Idempotent = true)]
    [Description("The full state of the open project: media pool, sequences, tracks and clips (with the " +
                 "track_id / clip_id handles the edit tools take), markers, playhead, and undo/redo state. " +
                 "All times are in ticks (see ticks_per_second) with human-readable companions.")]
    public Task<string> GetProjectState(
        [Description("Restrict the heavyweight sections, comma-separated from: media, tracks, markers, " +
                     "sequences, playhead, history. Omit for everything.")] string? sections = null) =>
        _session.OnModelThreadAsync(api =>
            StateFormatter.ProjectState(api.Project, api.History, api.ProjectPath, api.IsDirty,
                api.PlayheadTicks, api.DurationTicks, api.IsPlaying, sections));

    [McpServerTool(Name = "list_media", ReadOnly = true, Idempotent = true)]
    [Description("The project's media pool: media_id, file name/path, duration, and stream kinds.")]
    public Task<string> ListMedia() => _session.OnModelThreadAsync(api => StateFormatter.MediaList(api.Project));

    [McpServerTool(Name = "list_clips", ReadOnly = true, Idempotent = true)]
    [Description("The clips on the active sequence's tracks, with their clip_id handles and timing.")]
    public Task<string> ListClips(
        [Description("Restrict to one track by its track_id; omit for all tracks.")] int? trackId = null) =>
        _session.OnModelThreadAsync(api =>
            StateFormatter.ClipList(api.Project, trackId)
            ?? throw new McpException($"track {trackId} not found — call get_project_state for current track ids."));

    [McpServerTool(Name = "get_playhead", ReadOnly = true, Idempotent = true)]
    [Description("The playhead position, sequence duration, and whether playback is running.")]
    public Task<string> GetPlayhead() => _session.OnModelThreadAsync(api =>
        StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying));

    [McpServerTool(Name = "list_effect_types", ReadOnly = true, Idempotent = true)]
    [Description("The catalog of effect types that add_effect accepts, with each type's parameters and ranges. " +
                 "Filter by category and/or a name substring to keep the payload small.")]
    public Task<string> ListEffectTypes(
        [Description("Restrict to one category: Video, Color, or Audio.")] string? category = null,
        [Description("Restrict to types whose id or display name contains this text.")] string? nameQuery = null) =>
        _session.OnModelThreadAsync(_ =>
            StateFormatter.EffectTypes(category, nameQuery)
            ?? throw new McpException($"unknown effect category '{category}' — use Video, Color, or Audio."));

    // ── Transport tools ─────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "seek", Idempotent = true)]
    [Description("Moves the playhead to the given tick position (clamped to the sequence).")]
    public Task<string> Seek([Description("Target position in ticks (240000 per second).")] long positionTicks) =>
        _session.OnModelThreadAsync(api =>
        {
            api.Seek(positionTicks);
            return StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying);
        });

    [McpServerTool(Name = "play", Idempotent = true)]
    [Description("Starts playback from the current playhead position.")]
    public Task<string> Play() => _session.OnModelThreadAsync(api =>
    {
        api.Play();
        return StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying);
    });

    [McpServerTool(Name = "pause", Idempotent = true)]
    [Description("Pauses playback.")]
    public Task<string> Pause() => _session.OnModelThreadAsync(api =>
    {
        api.Pause();
        return StateFormatter.PlayheadState(api.PlayheadTicks, api.DurationTicks, api.IsPlaying);
    });

    // ── History / persistence tools ─────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "undo")]
    [Description("Undoes the most recent edit(s) — the user's or an AI edit, one shared history. " +
                 "Pass steps to undo several edits in one call.")]
    public Task<string> Undo(
        [Description("How many edits to undo (default 1).")] int steps = 1) =>
        _session.OnModelThreadAsync(api =>
        {
            if (steps < 1)
                throw new McpException("steps must be at least 1.");
            var labels = new List<string?>();
            for (int i = 0; i < steps; i++)
            {
                string? label = api.History.CanUndo ? api.History.UndoLabel : null;
                if (!api.History.Undo())
                {
                    if (labels.Count == 0)
                        throw new McpException("nothing to undo.");
                    break;
                }
                labels.Add(label);
            }
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"undid: {string.Join("; ", labels)}");
        });

    [McpServerTool(Name = "redo")]
    [Description("Redoes the most recently undone edit(s). Pass steps to redo several in one call.")]
    public Task<string> Redo(
        [Description("How many undone edits to redo (default 1).")] int steps = 1) =>
        _session.OnModelThreadAsync(api =>
        {
            if (steps < 1)
                throw new McpException("steps must be at least 1.");
            var labels = new List<string?>();
            for (int i = 0; i < steps; i++)
            {
                string? label = api.History.CanRedo ? api.History.RedoLabel : null;
                if (!api.History.Redo())
                {
                    if (labels.Count == 0)
                        throw new McpException("nothing to redo.");
                    break;
                }
                labels.Add(label);
            }
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"redid: {string.Join("; ", labels)}");
        });

    [McpServerTool(Name = "save_project", Idempotent = true)]
    [Description("Saves the project to its existing file. Fails while the project is untitled (a save-as " +
                 "dialog cannot be driven remotely — ask the user to save once first).")]
    public Task<string> SaveProject() => _session.OnModelThreadAsync(api =>
        api.SaveProject()
            ? StateFormatter.HistoryState(api.History, $"saved: {api.ProjectPath}")
            : throw new McpException("the project is untitled — the user must File > Save As once before remote saves."));

    // ── Edit tools ──────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "import_media")]
    [Description("Imports a media file (by absolute path) into the project's media pool. Returns its media_id " +
                 "(the existing one if the file was already imported). Does not place it on the timeline — " +
                 "follow with add_clip_to_timeline.")]
    public Task<string> ImportMedia([Description("Absolute path of the media file on this machine.")] string path) =>
        _session.OnModelThreadAsync(api =>
        {
            McpResult<MediaRef> result = api.ImportMedia(path);
            if (!result.Ok)
                throw new McpException(result.Error ?? "import failed.");
            MediaRef media = result.Value!;
            return new JsonObject
            {
                ["media_id"] = media.Id.Value.ToString("D"),
                ["name"] = Path.GetFileName(media.AbsolutePath),
                ["duration_ticks"] = media.Info.Duration.Ticks,
                ["has_video"] = media.Info.HasVideo,
                ["has_audio"] = media.Info.HasAudio,
            }.ToJsonString();
        });

    [McpServerTool(Name = "add_clip_to_timeline")]
    [Description("Places a media-pool item on the timeline at the given start (by default linked audio+video, " +
                 "like dropping from the bin). Returns the new clip's clip_id, plus its linked partner's id " +
                 "when one was created. Use stream=\"video\" or \"audio\" for a single-stream placement.")]
    public Task<string> AddClipToTimeline(
        [Description("media_id from list_media / import_media.")] string mediaId,
        [Description("Timeline start position in ticks.")] long startTicks,
        [Description("Video track index (into the video tracks, bottom-up); omit for the first compatible.")] int? videoTrackIndex = null,
        [Description("Audio track index (into the audio tracks); omit for the first compatible.")] int? audioTrackIndex = null,
        [Description("Whether the source's audio+video clips share a link group and edit together (default true).")] bool linked = true,
        [Description("Which of the source's streams to place: \"both\" (default), \"video\", or \"audio\".")] string stream = "both") =>
        _session.OnModelThreadAsync(api =>
        {
            if (!Guid.TryParse(mediaId, out Guid guid))
                throw new McpException($"'{mediaId}' is not a media_id — call list_media.");
            (bool includeVideo, bool includeAudio) = stream.ToLowerInvariant() switch
            {
                "both" => (true, true),
                "video" => (true, false),
                "audio" => (false, true),
                _ => throw new McpException("stream must be \"both\", \"video\", or \"audio\"."),
            };
            McpResult<Clip> result = api.PlaceClip(
                guid, Math.Max(0, startTicks), videoTrackIndex, audioTrackIndex, linked, includeVideo, includeAudio);
            if (!result.Ok)
                throw new McpException(result.Error ?? "placement failed.");
            api.RefreshPreview();
            Clip clip = result.Value!;
            var payload = new JsonObject
            {
                ["clip_id"] = RuntimeIds.IdOf(clip),
                ["start_ticks"] = clip.TimelineStart.Ticks,
                ["end_ticks"] = clip.TimelineEnd.Ticks,
                ["history"] = StateFormatter.HistoryObject(api.History),
            };
            if (clip.LinkGroupId is { } link)
            {
                payload["link_group"] = link.ToString("D");
                payload["linked_clip_ids"] = new JsonArray(api.Project.Timeline.ClipsLinkedTo(clip)
                    .Select(l => (JsonNode)RuntimeIds.IdOf(l.Clip)).ToArray());
            }
            return payload.ToJsonString();
        });

    [McpServerTool(Name = "trim_clip")]
    [Description("Trims a clip's in or out edge to a new timeline position (the source in/out moves with it, " +
                 "like an edge trim in the timeline). The clip does not move otherwise. By default a linked " +
                 "A/V partner's matching edge trims with it, as one undo entry.")]
    public Task<string> TrimClip(
        [Description("clip_id from list_clips / get_project_state.")] int clipId,
        [Description("Which edge to trim: \"in\" or \"out\".")] string edge,
        [Description("The edge's new timeline position in ticks.")] long newTimelineTicks,
        [Description("Whether linked partner clips trim together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            bool trimIn = edge.Equals("in", StringComparison.OrdinalIgnoreCase);
            if (!trimIn && !edge.Equals("out", StringComparison.OrdinalIgnoreCase))
                throw new McpException("edge must be \"in\" or \"out\".");

            var at = new Timecode(newTimelineTicks);
            var commands = new List<IEditCommand> { BuildTrimCommand(clip, trimIn, at) };
            if (includeLinked)
                foreach ((Track _, Clip partner) in api.Project.Timeline.ClipsLinkedTo(clip))
                    commands.Add(BuildTrimCommand(partner, trimIn, at));

            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Trim linked clips", commands));
            api.RefreshPreview();
            return ClipResult(api, clip, "trimmed clip");
        });

    private static IEditCommand BuildTrimCommand(Clip clip, bool trimIn, Timecode at)
    {
        if (trimIn)
        {
            if (at >= clip.TimelineEnd)
                throw new McpException($"the in edge must stay before clip {RuntimeIds.IdOf(clip)}'s end.");
            Timecode newSourceIn = clip.MapToSource(at);
            if (newSourceIn < Timecode.Zero || newSourceIn >= clip.SourceOut)
                throw new McpException(
                    $"that trim runs clip {RuntimeIds.IdOf(clip)} out of source media (pass includeLinked=false " +
                    "to trim only the addressed clip).");
            return new SetClipPlacementCommand(clip, newSourceIn, clip.SourceOut, at, "Trim clip");
        }
        if (at <= clip.TimelineStart)
            throw new McpException($"the out edge must stay after clip {RuntimeIds.IdOf(clip)}'s start.");
        Timecode newSourceOut = clip.MapToSource(at);
        if (newSourceOut <= clip.SourceIn)
            throw new McpException($"that trim collapses clip {RuntimeIds.IdOf(clip)} to nothing.");
        return new SetClipPlacementCommand(clip, clip.SourceIn, newSourceOut, clip.TimelineStart, "Trim clip");
    }

    [McpServerTool(Name = "move_clip")]
    [Description("Moves a clip to a new timeline start, optionally onto another track of the same kind. " +
                 "Keyframed effects move with the clip. By default linked partner clips shift by the same " +
                 "delta on their own tracks, as one undo entry.")]
    public Task<string> MoveClip(
        [Description("clip_id of the clip to move.")] int clipId,
        [Description("New timeline start in ticks.")] long newStartTicks,
        [Description("track_id of the destination track (same kind); omit to stay on the current track.")] int? targetTrackId = null,
        [Description("Whether linked partner clips move together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            var newStart = new Timecode(Math.Max(0, newStartTicks));
            long delta = newStart.Ticks - clip.TimelineStart.Ticks;

            IEditCommand primary;
            if (targetTrackId is { } destId && RuntimeIds.FindTrack(api.Project, destId) is { } dest && !ReferenceEquals(dest, track))
            {
                if (dest.GetType() != track.GetType())
                    throw new McpException($"track {destId} is a {Kind(dest)} track — a {Kind(track)} clip can't move there.");
                primary = new MoveClipToTrackCommand(track, dest, clip, newStart);
            }
            else if (targetTrackId is { } missing && RuntimeIds.FindTrack(api.Project, missing) is null)
            {
                throw new McpException($"track {missing} not found — call get_project_state for current track ids.");
            }
            else
            {
                primary = new SetClipPlacementCommand(clip, clip.SourceIn, clip.SourceOut, newStart);
            }

            var commands = new List<IEditCommand> { primary };
            if (includeLinked && delta != 0)
            {
                foreach ((Track _, Clip partner) in api.Project.Timeline.ClipsLinkedTo(clip))
                {
                    long partnerStart = partner.TimelineStart.Ticks + delta;
                    if (partnerStart < 0)
                        throw new McpException(
                            $"the move would push linked clip {RuntimeIds.IdOf(partner)} before the timeline " +
                            "origin (pass includeLinked=false to move only the addressed clip).");
                    commands.Add(new SetClipPlacementCommand(
                        partner, partner.SourceIn, partner.SourceOut, new Timecode(partnerStart)));
                }
            }

            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Move linked clips", commands));
            api.RefreshPreview();
            return ClipResult(api, clip, "moved clip");
        });

    [McpServerTool(Name = "split_clip")]
    [Description("Splits (blades) a clip at the given timeline position into two clips. Returns both clip ids. " +
                 "By default a linked partner spanning the cut splits too (the right halves stay linked to " +
                 "each other), as one undo entry.")]
    public Task<string> SplitClip(
        [Description("clip_id of the clip to split.")] int clipId,
        [Description("Timeline position of the cut, in ticks (must fall inside the clip).")] long positionTicks,
        [Description("Whether linked partner clips split together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            var at = new Timecode(positionTicks);
            if (at <= clip.TimelineStart || at >= clip.TimelineEnd)
                throw new McpException(
                    $"position {positionTicks} is outside the clip ({clip.TimelineStart.Ticks}..{clip.TimelineEnd.Ticks}).");

            List<(Track Track, Clip Clip)> companions = includeLinked
                ? api.Project.Timeline.ClipsLinkedTo(clip).Where(l => l.Clip.Contains(at)).ToList()
                : [];
            // Each side of a linked blade stays an independently linked A/V pair (the App's convention).
            Guid? rightGroup = (clip.LinkGroupId is not null && companions.Count > 0) ? Guid.NewGuid() : null;

            var primary = new SplitClipCommand(track, clip, at, rightGroup);
            var commands = new List<IEditCommand> { primary };
            var companionSplits = new List<SplitClipCommand>();
            foreach ((Track ctrack, Clip cclip) in companions)
            {
                var split = new SplitClipCommand(ctrack, cclip, at, rightGroup);
                companionSplits.Add(split);
                commands.Add(split);
            }
            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Blade linked clips", commands));
            api.RefreshPreview();

            var payload = new JsonObject
            {
                ["left_clip_id"] = RuntimeIds.IdOf(clip),
                ["right_clip_id"] = RuntimeIds.IdOf(primary.RightClip),
                ["history"] = StateFormatter.HistoryObject(api.History, "split clip"),
            };
            if (companionSplits.Count > 0)
                payload["linked_splits"] = new JsonArray(companionSplits
                    .Select(s => (JsonNode)new JsonObject
                    {
                        ["right_clip_id"] = RuntimeIds.IdOf(s.RightClip),
                    })
                    .ToArray());
            return payload.ToJsonString();
        });

    [McpServerTool(Name = "delete_clip", Destructive = true)]
    [Description("Removes a clip from the timeline (undoable). By default linked partner clips are removed " +
                 "too, as one undo entry.")]
    public Task<string> DeleteClip(
        [Description("clip_id of the clip to remove.")] int clipId,
        [Description("Whether linked partner clips are removed together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            var commands = new List<IEditCommand> { new RemoveClipCommand(track, clip) };
            if (includeLinked)
                foreach ((Track ctrack, Clip cclip) in api.Project.Timeline.ClipsLinkedTo(clip))
                    commands.Add(new RemoveClipCommand(ctrack, cclip));
            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Delete clips", commands));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History,
                commands.Count == 1 ? $"deleted clip {clipId}" : $"deleted clip {clipId} and {commands.Count - 1} linked clip(s)");
        });

    [McpServerTool(Name = "add_effect")]
    [Description("Adds an effect to a clip (see list_effect_types). The input color transform is inserted " +
                 "first in the stack (it converts the source before other effects); everything else appends.")]
    public Task<string> AddEffect(
        [Description("clip_id of the target clip.")] int clipId,
        [Description("Effect type id, e.g. \"builtin.brightness\".")] string effectTypeId) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            EffectDescriptor descriptor = EffectCatalog.Find(effectTypeId)
                ?? throw new McpException($"unknown effect type '{effectTypeId}' — call list_effect_types.");
            EffectInstance effect = descriptor.CreateInstance();
            IEditCommand command = effectTypeId == EffectTypeIds.ColorTransform
                ? new InsertEffectAtCommand(clip, effect, 0)
                : new AddEffectCommand(clip, effect);
            api.History.Execute(command);
            EffectTags.EnsureAssigned(api.Project); // settle the new instance's reference tag before reporting it
            api.RefreshPreview();
            return new JsonObject
            {
                ["clip_id"] = clipId,
                ["effect_index"] = clip.Effects.IndexOf(effect),
                ["effect_tag"] = effect.Tag,
                ["type_id"] = effectTypeId,
                ["history"] = StateFormatter.HistoryObject(api.History, $"added {descriptor.DisplayName}"),
            }.ToJsonString();
        });

    [McpServerTool(Name = "set_effect_parameter")]
    [Description("Sets one parameter of a clip's effect to a constant value (see the clip's effects list for " +
                 "tags/indexes, list_effect_types for parameter names and ranges). Identify the effect by " +
                 "effect_tag (preferred — stable across reorders) or effect_index.")]
    public Task<string> SetEffectParameter(
        [Description("clip_id of the clip carrying the effect.")] int clipId,
        [Description("Parameter name, e.g. \"amount\".")] string parameter,
        [Description("New constant value.")] double value,
        [Description("The effect's reference tag, e.g. \"RV-1\" (see the clip's effects list).")] string? effectTag = null,
        [Description("Index of the effect in the clip's effect stack (alternative to effect_tag).")] int effectIndex = -1) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            EffectInstance effect = ResolveEffect(clip, effectIndex, effectTag);
            double coerced = CoerceParameterValue(FindParameter(effect, parameter), value);
            api.History.Execute(new SetEffectParameterCommand(effect, parameter, AnimatableValue.Constant(coerced)));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History,
                $"set {effect.EffectTypeId}.{parameter} = {coerced}");
        });

    [McpServerTool(Name = "remove_effect", Destructive = true)]
    [Description("Removes an effect from a clip's stack (undoable). Identify the effect by effect_tag " +
                 "(preferred — stable across reorders) or effect_index.")]
    public Task<string> RemoveEffect(
        [Description("clip_id of the clip carrying the effect.")] int clipId,
        [Description("The effect's reference tag, e.g. \"RV-1\" (see the clip's effects list).")] string? effectTag = null,
        [Description("Index of the effect in the clip's effect stack (alternative to effect_tag).")] int effectIndex = -1) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            EffectInstance effect = ResolveEffect(clip, effectIndex, effectTag);
            api.History.Execute(new RemoveEffectCommand(clip, effect));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"removed {effect.EffectTypeId}");
        });

    [McpServerTool(Name = "move_effect")]
    [Description("Moves an effect to a new position in a clip's effect stack (stack order is the processing " +
                 "order, so this changes the result; undoable as one step). The new index is clamped to the " +
                 "stack.")]
    public Task<string> MoveEffect(
        [Description("clip_id of the clip carrying the effect.")] int clipId,
        [Description("Index the effect should end up at (clamped to the stack).")] int newIndex,
        [Description("The effect's reference tag, e.g. \"RV-1\" (see the clip's effects list).")] string? effectTag = null,
        [Description("Current index of the effect in the clip's effect stack (alternative to effect_tag).")] int effectIndex = -1) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            EffectInstance effect = ResolveEffect(clip, effectIndex, effectTag);
            int current = clip.Effects.IndexOf(effect);
            int target = Math.Clamp(newIndex, 0, clip.Effects.Count - 1);
            if (target != current) // skip a no-op so it doesn't pollute the undo history
            {
                api.History.Execute(new MoveChainEffectCommand(clip.Effects, effect, target));
                api.RefreshPreview();
            }
            return new JsonObject
            {
                ["clip_id"] = clipId,
                ["effect_index"] = clip.Effects.IndexOf(effect),
                ["effect_tag"] = effect.Tag,
                ["type_id"] = effect.EffectTypeId,
                ["history"] = StateFormatter.HistoryObject(api.History,
                    target == current
                        ? $"{effect.EffectTypeId} already at index {target} (no-op)"
                        : $"moved {effect.EffectTypeId} to index {target}"),
            }.ToJsonString();
        });

    [McpServerTool(Name = "add_marker")]
    [Description("Adds a timeline marker at the given position.")]
    public Task<string> AddMarker(
        [Description("Marker position in ticks.")] long positionTicks,
        [Description("Marker name/label.")] string name = "",
        [Description("Marker color: Red, Orange, Yellow, Green, Cyan, Blue, Purple, Magenta.")] string? color = null) =>
        _session.OnModelThreadAsync(api =>
        {
            MarkerColor markerColor = default;
            if (!string.IsNullOrEmpty(color) && !Enum.TryParse(color, ignoreCase: true, out markerColor))
                throw new McpException($"unknown marker color '{color}'.");
            var marker = new Marker(new Timecode(Math.Max(0, positionTicks)), name, color: markerColor);
            api.History.Execute(new AddMarkerCommand(api.Project.Timeline.Markers, marker));
            return StateFormatter.HistoryState(api.History,
                $"added marker at {StateFormatter.TimeString(marker.Time.Ticks)}");
        });

    [McpServerTool(Name = "remove_marker", Destructive = true)]
    [Description("Removes the timeline marker at exactly the given tick position (as reported by " +
                 "get_project_state).")]
    public Task<string> RemoveMarker([Description("The marker's time_ticks value.")] long timeTicks) =>
        _session.OnModelThreadAsync(api =>
        {
            Marker marker = api.Project.Timeline.Markers.FirstOrDefault(m => m.Time.Ticks == timeTicks)
                ?? throw new McpException($"no marker at {timeTicks} ticks — call get_project_state.");
            api.History.Execute(new RemoveMarkerCommand(api.Project.Timeline.Markers, marker));
            return StateFormatter.HistoryState(api.History, "removed marker");
        });

    // ── Shared resolution helpers ───────────────────────────────────────────────────────────────────

    private static (Clip Clip, Track Track) ResolveClip(IEditorApi api, int clipId) =>
        RuntimeIds.FindClip(api.Project, clipId, out Track? track) is { } clip
            ? (clip, track!)
            : throw new McpException($"clip {clipId} not found — call list_clips for current clip ids.");

    /// <summary>Resolves an effect on the clip by reference tag (preferred — stable across stack reorders)
    /// or by stack index. Exactly the effect-addressing rule every effect tool shares.</summary>
    internal static EffectInstance ResolveEffect(Clip clip, int index, string? tag)
    {
        if (tag is { Length: > 0 })
            return clip.Effects.FirstOrDefault(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException($"the clip has no effect tagged '{tag}' (it has: " +
                    $"{string.Join(", ", clip.Effects.Select(e => e.Tag ?? "untagged"))}).");
        if (index < 0)
            throw new McpException("identify the effect with effect_tag (see the clip's effects list) or effect_index.");
        return index < clip.Effects.Count
            ? clip.Effects[index]
            : throw new McpException($"the clip has no effect at index {index} (it has {clip.Effects.Count}).");
    }

    /// <summary>The catalog descriptor for one of the effect's parameters, or <see langword="null"/> for an
    /// unregistered effect / unknown parameter name (those stay unvalidated — the tools are lenient).</summary>
    internal static EffectParameterDescriptor? FindParameter(EffectInstance effect, string parameter) =>
        EffectCatalog.Find(effect.EffectTypeId)?.Parameters.FirstOrDefault(p => p.Name == parameter);

    /// <summary>
    /// Snaps a value for a discrete parameter kind: toggle / integer / dropdown values are rounded and
    /// clamped into the descriptor's range (an AI passing showMask = 0.3 means "off", not a 30% mask).
    /// Continuous parameters — and parameters with no descriptor — pass through untouched.
    /// </summary>
    internal static double CoerceParameterValue(EffectParameterDescriptor? p, double value) =>
        p is null || p.Kind == ParameterKind.Continuous
            ? value
            : Math.Clamp(Math.Round(value), p.Min, p.Max);

    private static string Kind(Track track) => track is VideoTrack ? "video" : "audio";

    private static string ClipResult(IEditorApi api, Clip clip, string performed) => new JsonObject
    {
        ["clip_id"] = RuntimeIds.IdOf(clip),
        ["start_ticks"] = clip.TimelineStart.Ticks,
        ["end_ticks"] = clip.TimelineEnd.Ticks,
        ["source_in_ticks"] = clip.SourceIn.Ticks,
        ["source_out_ticks"] = clip.SourceOut.Ticks,
        ["history"] = StateFormatter.HistoryObject(api.History, performed),
    }.ToJsonString();
}
