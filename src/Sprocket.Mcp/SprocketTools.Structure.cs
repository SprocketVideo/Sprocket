using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Mcp;

/// <summary>
/// Structural tools (PLAN.md step 38 follow-on): tracks, transitions, marker editing, generator/title clips,
/// the audio effect chains (track insert / sequence bus / project master), and cross-call edit groups.
/// </summary>
public sealed partial class SprocketTools
{
    private EditHistory.EditTransaction? _editGroup;

    // ── Tracks ──────────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "add_track")]
    [Description("Adds a video or audio track to the active sequence (appended on top in z-order for video). " +
                 "Returns the new track_id.")]
    public Task<string> AddTrack(
        [Description("Track kind: \"video\" or \"audio\".")] string kind,
        [Description("Display name; omit for the next V#/A# name.")] string? name = null) =>
        _session.OnModelThreadAsync(api =>
        {
            Timeline timeline = api.Project.Timeline;
            Track track = kind.ToLowerInvariant() switch
            {
                "video" => new VideoTrack { Name = name ?? $"V{timeline.VideoTracks.Count() + 1}" },
                "audio" => new AudioTrack { Name = name ?? $"A{timeline.AudioTracks.Count() + 1}" },
                _ => throw new McpException("kind must be \"video\" or \"audio\"."),
            };
            api.History.Execute(new AddTrackCommand(timeline, track));
            return new JsonObject
            {
                ["track_id"] = RuntimeIds.IdOf(track),
                ["name"] = track.Name,
                ["history"] = StateFormatter.HistoryObject(api.History, $"added track {track.Name}"),
            }.ToJsonString();
        });

    [McpServerTool(Name = "remove_track", Destructive = true)]
    [Description("Removes a track from the active sequence (undoable). Refuses a track that still carries " +
                 "clips unless force=true (the clips are removed with it).")]
    public Task<string> RemoveTrack(
        [Description("track_id of the track to remove.")] int trackId,
        [Description("Remove even when the track still carries clips (default false).")] bool force = false) =>
        _session.OnModelThreadAsync(api =>
        {
            Track track = RuntimeIds.FindTrack(api.Project, trackId)
                ?? throw new McpException($"track {trackId} not found — call get_project_state.");
            if (track.Clips.Count > 0 && !force)
                throw new McpException(
                    $"track {trackId} still carries {track.Clips.Count} clip(s) — pass force=true to remove it anyway.");
            api.History.Execute(new RemoveTrackCommand(api.Project.Timeline, track));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"removed track {track.Name}");
        });

    // ── Transitions ─────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_transition_types", ReadOnly = true, Idempotent = true)]
    [Description("The catalog of transition types that add_transition accepts, with the default type and " +
                 "duration.")]
    public Task<string> ListTransitionTypes() =>
        _session.OnModelThreadAsync(_ => StateFormatter.TransitionTypes());

    [McpServerTool(Name = "add_transition")]
    [Description("Adds a transition at the cut on one of a clip's edges — the clip must butt against another " +
                 "clip there. The duration is clamped to fit inside both clips. Returns the transition_id.")]
    public Task<string> AddTransition(
        [Description("clip_id of either clip at the cut.")] int clipId,
        [Description("Which of the clip's cuts: \"start\" (with the previous clip) or \"end\" (with the next).")] string cutEdge,
        [Description("Transition type id from list_transition_types; omit for the cross dissolve.")] string? transitionTypeId = null,
        [Description("Transition length in ticks; omit for the catalog default (1 s), clamped to the clips.")] long? durationTicks = null,
        [Description("Window alignment: CenterOnCut (default), EndAtCut, or StartAtCut.")] string? alignment = null) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            bool atEnd = cutEdge.Equals("end", StringComparison.OrdinalIgnoreCase);
            if (!atEnd && !cutEdge.Equals("start", StringComparison.OrdinalIgnoreCase))
                throw new McpException("cutEdge must be \"start\" or \"end\".");

            string typeId = transitionTypeId ?? TransitionCatalog.DefaultTransitionId;
            if (TransitionCatalog.Find(typeId) is null)
                throw new McpException($"unknown transition type '{typeId}' — call list_transition_types.");

            TransitionAlignment align = TransitionAlignment.CenterOnCut;
            if (!string.IsNullOrWhiteSpace(alignment) && !Enum.TryParse(alignment, ignoreCase: true, out align))
                throw new McpException("alignment must be CenterOnCut, EndAtCut, or StartAtCut.");

            Timecode cut = atEnd ? clip.TimelineEnd : clip.TimelineStart;
            Clip? from = track.ResolveActiveClip(cut - new Timecode(1));
            Clip? to = track.ResolveActiveClip(cut);
            if (from is null || to is null || ReferenceEquals(from, to))
                throw new McpException("a transition needs two adjacent clips that share that cut.");

            long maxByClips = Math.Min(from.Duration.Ticks, to.Duration.Ticks);
            long duration = Math.Min(durationTicks ?? TransitionCatalog.DefaultDuration.Ticks, maxByClips);
            if (duration <= 0)
                throw new McpException("the transition duration must be positive.");

            var transition = new Transition(typeId, cut, new Timecode(duration), align);
            api.History.Execute(new AddTransitionCommand(track, transition));
            api.RefreshPreview();
            var payload = StateFormatter.TransitionObject(transition);
            payload["history"] = StateFormatter.HistoryObject(api.History,
                $"added {TransitionCatalog.DisplayName(typeId)}");
            return payload.ToJsonString();
        });

    [McpServerTool(Name = "remove_transition", Destructive = true)]
    [Description("Removes a transition from its cut (undoable).")]
    public Task<string> RemoveTransition(
        [Description("transition_id from get_project_state / add_transition.")] int transitionId) =>
        _session.OnModelThreadAsync(api =>
        {
            Transition transition = RuntimeIds.FindTransition(api.Project, transitionId, out Track? track)
                ?? throw new McpException($"transition {transitionId} not found — call get_project_state.");
            api.History.Execute(new RemoveTransitionCommand(track!, transition));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, "removed transition");
        });

    [McpServerTool(Name = "set_transition")]
    [Description("Changes a transition's duration and/or alignment (the cut point is fixed).")]
    public Task<string> SetTransition(
        [Description("transition_id of the transition to change.")] int transitionId,
        [Description("New length in ticks; omit to keep the current duration.")] long? durationTicks = null,
        [Description("New alignment: CenterOnCut, EndAtCut, or StartAtCut; omit to keep the current one.")] string? alignment = null) =>
        _session.OnModelThreadAsync(api =>
        {
            Transition transition = RuntimeIds.FindTransition(api.Project, transitionId, out Track? _)
                ?? throw new McpException($"transition {transitionId} not found — call get_project_state.");
            if (durationTicks is null && alignment is null)
                throw new McpException("pass durationTicks and/or alignment.");
            if (durationTicks is <= 0)
                throw new McpException("the transition duration must be positive.");

            TransitionAlignment align = transition.Alignment;
            if (!string.IsNullOrWhiteSpace(alignment) && !Enum.TryParse(alignment, ignoreCase: true, out align))
                throw new McpException("alignment must be CenterOnCut, EndAtCut, or StartAtCut.");

            api.History.Execute(new SetTransitionWindowCommand(
                transition, new Timecode(durationTicks ?? transition.Duration.Ticks), align));
            api.RefreshPreview();
            var payload = StateFormatter.TransitionObject(transition);
            payload["history"] = StateFormatter.HistoryObject(api.History, "adjusted transition");
            return payload.ToJsonString();
        });

    // ── Markers ─────────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "update_marker")]
    [Description("Moves and/or relabels the timeline marker at exactly the given tick position: new time, " +
                 "name, color, and/or comment. One undo entry.")]
    public Task<string> UpdateMarker(
        [Description("The marker's current time_ticks value (as reported by get_project_state).")] long timeTicks,
        [Description("New time in ticks; omit to keep the position.")] long? newTimeTicks = null,
        [Description("New name; omit to keep it.")] string? name = null,
        [Description("New color (Red, Orange, Yellow, Green, Cyan, Blue, Purple, Magenta, White); omit to keep it.")] string? color = null,
        [Description("New comment; omit to keep it.")] string? comment = null) =>
        _session.OnModelThreadAsync(api =>
        {
            Marker marker = api.Project.Timeline.Markers.FirstOrDefault(m => m.Time.Ticks == timeTicks)
                ?? throw new McpException($"no marker at {timeTicks} ticks — call get_project_state.");
            var commands = new List<IEditCommand>();
            if (newTimeTicks is { } t)
                commands.Add(new MoveMarkerCommand(marker, new Timecode(Math.Max(0, t))));
            if (name is not null)
                commands.Add(SetPropertyCommand<string>.Create(
                    "Rename marker", () => marker.Name, v => marker.Name = v, name));
            if (color is not null)
            {
                if (!Enum.TryParse(color, ignoreCase: true, out MarkerColor markerColor))
                    throw new McpException($"unknown marker color '{color}'.");
                commands.Add(SetPropertyCommand<MarkerColor>.Create(
                    "Recolor marker", () => marker.Color, v => marker.Color = v, markerColor));
            }
            if (comment is not null)
                commands.Add(SetPropertyCommand<string>.Create(
                    "Marker comment", () => marker.Comment, v => marker.Comment = v, comment));
            if (commands.Count == 0)
                throw new McpException("pass newTimeTicks, name, color, and/or comment.");
            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Edit marker", commands));
            return StateFormatter.HistoryState(api.History, "updated marker");
        });

    // ── Generators / titles ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_generator_types", ReadOnly = true, Idempotent = true)]
    [Description("The catalog of generator (title / matte) types that add_generator_clip accepts, with each " +
                 "type's default parameters and strings.")]
    public Task<string> ListGeneratorTypes() =>
        _session.OnModelThreadAsync(_ => StateFormatter.GeneratorTypes());

    [McpServerTool(Name = "add_generator_clip")]
    [Description("Adds a generator clip (title, lower third, credits roll, crawl, color matte) at the given " +
                 "time. Without a videoTrackIndex it lands on the topmost video track when that span is " +
                 "free, otherwise a new track is created above so it overlays existing content — one undo " +
                 "entry either way. Customize afterwards with set_generator_text / set_generator_parameter.")]
    public Task<string> AddGeneratorClip(
        [Description("Generator type id from list_generator_types, e.g. \"builtin.gen.title\".")] string generatorTypeId,
        [Description("Timeline start position in ticks.")] long startTicks,
        [Description("Clip length in ticks; omit for the catalog default (5 s).")] long? durationTicks = null,
        [Description("Video track index (bottom-up) to place on; omit for topmost-free-or-new-track.")] int? videoTrackIndex = null) =>
        _session.OnModelThreadAsync(api =>
        {
            GeneratorDescriptor descriptor = GeneratorCatalog.Find(generatorTypeId)
                ?? throw new McpException($"unknown generator type '{generatorTypeId}' — call list_generator_types.");
            long duration = durationTicks ?? GeneratorCatalog.DefaultDuration.Ticks;
            if (duration <= 0)
                throw new McpException("durationTicks must be positive.");
            var start = new Timecode(Math.Max(0, startTicks));
            Clip clip = descriptor.CreateClip(new Timecode(duration), start);

            Timeline timeline = api.Project.Timeline;
            var videoTracks = timeline.VideoTracks.ToList();
            if (videoTrackIndex is { } index)
            {
                if (index < 0 || index >= videoTracks.Count)
                    throw new McpException($"video track index {index} is out of range (the sequence has {videoTracks.Count}).");
                api.History.Execute(new AddClipCommand(videoTracks[index], clip));
            }
            else
            {
                VideoTrack? top = videoTracks.LastOrDefault();
                if (top is not null
                    && top.ResolveActiveClip(start) is null
                    && top.ResolveActiveClip(clip.TimelineEnd - new Timecode(1)) is null)
                {
                    api.History.Execute(new AddClipCommand(top, clip));
                }
                else
                {
                    // Stack on a fresh top track so the generator overlays (not displaces) existing content.
                    var newTrack = new VideoTrack { Name = $"V{videoTracks.Count + 1}" };
                    api.History.Execute(new CompositeCommand("Add generator clip",
                    [
                        new AddTrackCommand(timeline, newTrack),
                        new AddClipCommand(newTrack, clip),
                    ]));
                }
            }
            api.RefreshPreview();
            return new JsonObject
            {
                ["clip_id"] = RuntimeIds.IdOf(clip),
                ["start_ticks"] = clip.TimelineStart.Ticks,
                ["end_ticks"] = clip.TimelineEnd.Ticks,
                ["history"] = StateFormatter.HistoryObject(api.History,
                    $"added {GeneratorCatalog.DisplayName(generatorTypeId)}"),
            }.ToJsonString();
        });

    [McpServerTool(Name = "set_generator_text")]
    [Description("Sets a string attribute of a generator clip (its text, colors as #AARRGGBB hex, font " +
                 "family, alignment, scroll mode, …). See list_generator_types / get_clip for names. An " +
                 "empty value clears the attribute back to its default.")]
    public Task<string> SetGeneratorText(
        [Description("clip_id of the generator clip.")] int clipId,
        [Description("Attribute name, e.g. \"text\", \"color\", \"align\".")] string name,
        [Description("New value (empty string clears it).")] string value) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            GeneratorSpec generator = clip.Generator
                ?? throw new McpException($"clip {clipId} is not a generator clip.");
            api.History.Execute(new SetGeneratorStringCommand(generator, name, value));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"set generator {name}");
        });

    [McpServerTool(Name = "set_generator_parameter")]
    [Description("Sets a numeric attribute of a generator clip to a constant (font size, position, box " +
                 "padding, …). See list_generator_types / get_clip for names.")]
    public Task<string> SetGeneratorParameter(
        [Description("clip_id of the generator clip.")] int clipId,
        [Description("Attribute name, e.g. \"fontSize\", \"positionY\".")] string name,
        [Description("New constant value.")] double value) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            GeneratorSpec generator = clip.Generator
                ?? throw new McpException($"clip {clipId} is not a generator clip.");
            api.History.Execute(new SetGeneratorParameterCommand(generator, name, AnimatableValue.Constant(value)));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"set generator {name} = {value}");
        });

    // ── Audio effect chains (track insert / sequence bus / project master) ─────────────────────────

    [McpServerTool(Name = "list_audio_chain", ReadOnly = true, Idempotent = true)]
    [Description("The ordered audio effect chain at one scope: an audio track's insert chain " +
                 "(scope=\"track\" + trackId), the active sequence's bus (scope=\"sequence\"), or the " +
                 "project master chain (scope=\"master\"). Clip-scope audio effects are the clip's normal " +
                 "effect stack (get_clip).")]
    public Task<string> ListAudioChain(
        [Description("Chain scope: \"track\", \"sequence\", or \"master\".")] string scope,
        [Description("track_id of the audio track (scope=\"track\" only).")] int? trackId = null) =>
        _session.OnModelThreadAsync(api =>
        {
            IList<EffectInstance> chain = ResolveChain(api, scope, trackId);
            var effects = new JsonArray();
            for (int i = 0; i < chain.Count; i++)
                effects.Add(StateFormatter.EffectDetailObject(chain[i], i, 0));
            return new JsonObject { ["scope"] = scope.ToLowerInvariant(), ["effects"] = effects }.ToJsonString();
        });

    [McpServerTool(Name = "add_chain_effect")]
    [Description("Appends an audio effect (category Audio in list_effect_types) to a track / sequence-bus / " +
                 "master chain.")]
    public Task<string> AddChainEffect(
        [Description("Chain scope: \"track\", \"sequence\", or \"master\".")] string scope,
        [Description("Audio effect type id, e.g. \"builtin.audio.eq\".")] string effectTypeId,
        [Description("track_id of the audio track (scope=\"track\" only).")] int? trackId = null) =>
        _session.OnModelThreadAsync(api =>
        {
            IList<EffectInstance> chain = ResolveChain(api, scope, trackId);
            EffectDescriptor descriptor = EffectCatalog.Find(effectTypeId)
                ?? throw new McpException($"unknown effect type '{effectTypeId}' — call list_effect_types.");
            if (descriptor.Category != EffectCategory.Audio)
                throw new McpException($"'{effectTypeId}' is not an audio effect — the chains take category Audio types.");
            EffectInstance effect = descriptor.CreateInstance();
            api.History.Execute(new AddChainEffectCommand(chain, effect));
            return new JsonObject
            {
                ["effect_index"] = chain.IndexOf(effect),
                ["type_id"] = effectTypeId,
                ["history"] = StateFormatter.HistoryObject(api.History, $"added {descriptor.DisplayName} to the {scope} chain"),
            }.ToJsonString();
        });

    [McpServerTool(Name = "remove_chain_effect", Destructive = true)]
    [Description("Removes an effect from a track / sequence-bus / master audio chain (undoable).")]
    public Task<string> RemoveChainEffect(
        [Description("Chain scope: \"track\", \"sequence\", or \"master\".")] string scope,
        [Description("Index of the effect in the chain (see list_audio_chain).")] int effectIndex,
        [Description("track_id of the audio track (scope=\"track\" only).")] int? trackId = null) =>
        _session.OnModelThreadAsync(api =>
        {
            IList<EffectInstance> chain = ResolveChain(api, scope, trackId);
            EffectInstance effect = ChainEffectAt(chain, effectIndex);
            api.History.Execute(new RemoveChainEffectCommand(chain, effect));
            return StateFormatter.HistoryState(api.History, $"removed {effect.EffectTypeId} from the {scope} chain");
        });

    [McpServerTool(Name = "set_chain_effect_parameter")]
    [Description("Sets one parameter of a track / sequence-bus / master chain effect to a constant value.")]
    public Task<string> SetChainEffectParameter(
        [Description("Chain scope: \"track\", \"sequence\", or \"master\".")] string scope,
        [Description("Index of the effect in the chain (see list_audio_chain).")] int effectIndex,
        [Description("Parameter name, e.g. \"gainDb\".")] string parameter,
        [Description("New constant value.")] double value,
        [Description("track_id of the audio track (scope=\"track\" only).")] int? trackId = null) =>
        _session.OnModelThreadAsync(api =>
        {
            IList<EffectInstance> chain = ResolveChain(api, scope, trackId);
            EffectInstance effect = ChainEffectAt(chain, effectIndex);
            api.History.Execute(new SetEffectParameterCommand(effect, parameter, AnimatableValue.Constant(value)));
            return StateFormatter.HistoryState(api.History, $"set {effect.EffectTypeId}.{parameter} = {value}");
        });

    private static IList<EffectInstance> ResolveChain(IEditorApi api, string scope, int? trackId) =>
        scope.ToLowerInvariant() switch
        {
            "track" => trackId is { } id
                ? RuntimeIds.FindTrack(api.Project, id) is AudioTrack audio
                    ? audio.Effects
                    : throw new McpException($"track {trackId} is not an audio track — call get_project_state.")
                : throw new McpException("scope=\"track\" needs a trackId."),
            "sequence" => api.Project.Timeline.AudioEffects,
            "master" => api.Project.Settings.MasterAudioEffects,
            _ => throw new McpException("scope must be \"track\", \"sequence\", or \"master\"."),
        };

    private static EffectInstance ChainEffectAt(IList<EffectInstance> chain, int index) =>
        index >= 0 && index < chain.Count
            ? chain[index]
            : throw new McpException($"the chain has no effect at index {index} (it has {chain.Count}).");

    // ── Edit groups (cross-call transactions) ───────────────────────────────────────────────────────

    [McpServerTool(Name = "begin_edit_group")]
    [Description("Opens an edit group: every edit tool call until end_edit_group collapses into ONE undo " +
                 "entry with the given label. Use for multi-step edits the user should undo as a unit. " +
                 "cancel_edit_group reverts the group instead; the app's own Undo/Redo seals an open group " +
                 "first. Groups do not nest.")]
    public Task<string> BeginEditGroup(
        [Description("The undo-menu label for the group, e.g. \"Build title sequence\".")] string label) =>
        _session.OnModelThreadAsync(api =>
        {
            if (api.History.HasOpenTransaction)
                throw new McpException("an edit group is already open — end_edit_group or cancel_edit_group first.");
            if (string.IsNullOrWhiteSpace(label))
                throw new McpException("pass a non-empty label.");
            _editGroup = api.History.BeginTransaction(label);
            return StateFormatter.HistoryState(api.History, $"opened edit group: {label}");
        });

    [McpServerTool(Name = "end_edit_group")]
    [Description("Commits the open edit group as one undo entry (no entry when nothing was executed).")]
    public Task<string> EndEditGroup() => _session.OnModelThreadAsync(api =>
    {
        if (_editGroup is null || !api.History.HasOpenTransaction)
        {
            _editGroup = null;
            throw new McpException("no edit group is open — begin_edit_group first.");
        }
        int count = _editGroup.Count;
        _editGroup.Commit();
        _editGroup = null;
        return StateFormatter.HistoryState(api.History, $"committed edit group ({count} edit(s))");
    });

    [McpServerTool(Name = "cancel_edit_group", Destructive = true)]
    [Description("Cancels the open edit group, reverting every edit made inside it.")]
    public Task<string> CancelEditGroup() => _session.OnModelThreadAsync(api =>
    {
        if (_editGroup is null || !api.History.HasOpenTransaction)
        {
            _editGroup = null;
            throw new McpException("no edit group is open — begin_edit_group first.");
        }
        int count = _editGroup.Count;
        _editGroup.Cancel();
        _editGroup = null;
        api.RefreshPreview();
        return StateFormatter.HistoryState(api.History, $"cancelled edit group ({count} edit(s) reverted)");
    });
}
