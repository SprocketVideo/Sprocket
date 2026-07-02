using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Mcp;

/// <summary>
/// Builds the JSON payloads the read-side MCP tools return (PLAN.md step 38). Pure functions of the model,
/// headlessly testable. Every clip/track carries its <see cref="RuntimeIds"/> id (the handle edit tools
/// take), and every time value is emitted both as raw ticks (240000/s — what tool parameters use) and as a
/// human <c>h:mm:ss.fff</c> string. Deliberately not <c>ProjectSerializer.Serialize</c>: the document format
/// is positional and id-less, the wrong shape for a client that reads state and then addresses edits.
/// </summary>
public static class StateFormatter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The full project summary: format, media pool, tracks/clips (with runtime ids), markers,
    /// transport, and undo/redo state. The <c>get_project_state</c> payload. <paramref name="sections"/>
    /// (comma-separated: <c>media,tracks,markers,sequences,playhead,history</c>) restricts the heavyweight
    /// sections; <see langword="null"/>/empty emits everything.</summary>
    public static string ProjectState(
        Project project, EditHistory history, string? projectPath, bool dirty,
        long playheadTicks, long durationTicks, bool playing, string? sections = null)
    {
        HashSet<string>? include = null;
        if (!string.IsNullOrWhiteSpace(sections))
            include = sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Wants(string section) => include is null || include.Contains(section);

        Timeline timeline = project.Timeline;
        var root = new JsonObject
        {
            ["ticks_per_second"] = Timecode.TicksPerSecond,
            ["project_path"] = projectPath,
            ["dirty"] = dirty,
            ["active_sequence"] = new JsonObject
            {
                ["id"] = project.ActiveSequence.Id.ToString(),
                ["name"] = project.ActiveSequence.Name,
            },
            ["format"] = new JsonObject
            {
                ["frame_rate"] = $"{timeline.FrameRate.Num}/{timeline.FrameRate.Den}",
                ["resolution"] = $"{timeline.Resolution.Width}x{timeline.Resolution.Height}",
                ["sample_rate"] = timeline.SampleRate,
            },
        };
        if (Wants("sequences"))
            root["sequences"] = new JsonArray(project.Sequences
                .Select(s => (JsonNode)new JsonObject { ["id"] = s.Id.ToString(), ["name"] = s.Name })
                .ToArray());
        if (Wants("media"))
            root["media"] = MediaArray(project);
        if (Wants("tracks"))
            root["tracks"] = TracksArray(timeline);
        if (Wants("markers"))
            root["markers"] = MarkersArray(timeline.Markers);
        if (Wants("playhead"))
            root["playhead"] = PlayheadObject(playheadTicks, durationTicks, playing);
        if (Wants("history"))
            root["history"] = HistoryObject(history);
        return root.ToJsonString(Indented);
    }

    /// <summary>The media pool listing — the <c>list_media</c> payload.</summary>
    public static string MediaList(Project project) =>
        new JsonObject { ["media"] = MediaArray(project) }.ToJsonString(Indented);

    /// <summary>The clip listing for one track (by runtime id) or all tracks — the <c>list_clips</c> payload.
    /// Returns <see langword="null"/> when <paramref name="trackId"/> names no current track.</summary>
    public static string? ClipList(Project project, int? trackId)
    {
        if (trackId is { } id)
        {
            Track? track = RuntimeIds.FindTrack(project, id);
            return track is null
                ? null
                : new JsonObject { ["tracks"] = new JsonArray(TrackObject(track)) }.ToJsonString(Indented);
        }
        return new JsonObject { ["tracks"] = TracksArray(project.Timeline) }.ToJsonString(Indented);
    }

    /// <summary>Transport state — the <c>get_playhead</c> payload.</summary>
    public static string PlayheadState(long playheadTicks, long durationTicks, bool playing) =>
        new JsonObject
        {
            ["ticks_per_second"] = Timecode.TicksPerSecond,
            ["playhead"] = PlayheadObject(playheadTicks, durationTicks, playing),
        }.ToJsonString(Indented);

    /// <summary>The effect catalog (type ids + parameter ranges) — the <c>list_effect_types</c> payload.
    /// <paramref name="category"/> filters to one <see cref="EffectCategory"/> (case-insensitive name);
    /// <paramref name="nameQuery"/> filters by substring over id / display name. Returns
    /// <see langword="null"/> when <paramref name="category"/> names no known category.</summary>
    public static string? EffectTypes(string? category = null, string? nameQuery = null)
    {
        EffectCategory? wanted = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!Enum.TryParse(category, ignoreCase: true, out EffectCategory parsed))
                return null;
            wanted = parsed;
        }

        var effects = new JsonArray();
        foreach (EffectDescriptor descriptor in EffectCatalog.All)
        {
            if (wanted is { } c && descriptor.Category != c)
                continue;
            if (!string.IsNullOrWhiteSpace(nameQuery)
                && !descriptor.Id.Contains(nameQuery, StringComparison.OrdinalIgnoreCase)
                && !descriptor.DisplayName.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            var parameters = new JsonArray();
            foreach (EffectParameterDescriptor p in descriptor.Parameters)
                parameters.Add(ParameterDescriptorObject(p));
            effects.Add(new JsonObject
            {
                ["type_id"] = descriptor.Id,
                ["display_name"] = descriptor.DisplayName,
                ["category"] = descriptor.Category.ToString(),
                ["description"] = descriptor.Description,
                ["parameters"] = parameters,
            });
        }
        return new JsonObject { ["effect_types"] = effects }.ToJsonString(Indented);
    }

    /// <summary>The transition catalog — the <c>list_transition_types</c> payload.</summary>
    public static string TransitionTypes()
    {
        var transitions = new JsonArray();
        foreach (TransitionDescriptor descriptor in TransitionCatalog.BuiltIns)
        {
            var parameters = new JsonArray();
            foreach (EffectParameterDescriptor p in descriptor.Parameters)
                parameters.Add(ParameterDescriptorObject(p));
            transitions.Add(new JsonObject
            {
                ["type_id"] = descriptor.Id,
                ["display_name"] = descriptor.DisplayName,
                ["description"] = descriptor.Description,
                ["parameters"] = parameters,
            });
        }
        return new JsonObject
        {
            ["default_type_id"] = TransitionCatalog.DefaultTransitionId,
            ["default_duration_ticks"] = TransitionCatalog.DefaultDuration.Ticks,
            ["transition_types"] = transitions,
        }.ToJsonString(Indented);
    }

    /// <summary>The generator catalog — the <c>list_generator_types</c> payload.</summary>
    public static string GeneratorTypes()
    {
        var generators = new JsonArray();
        foreach (GeneratorDescriptor descriptor in GeneratorCatalog.BuiltIns)
        {
            GeneratorSpec defaults = descriptor.CreateSpec();
            generators.Add(new JsonObject
            {
                ["type_id"] = descriptor.Id,
                ["display_name"] = descriptor.DisplayName,
                ["description"] = descriptor.Description,
                ["default_parameters"] = new JsonObject(defaults.Parameters
                    .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)AnimatableValueObject(kv.Value, 0)))),
                ["default_strings"] = new JsonObject(defaults.Strings
                    .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            });
        }
        return new JsonObject
        {
            ["default_duration_ticks"] = GeneratorCatalog.DefaultDuration.Ticks,
            ["generator_types"] = generators,
        }.ToJsonString(Indented);
    }

    private static JsonObject ParameterDescriptorObject(EffectParameterDescriptor p)
    {
        var obj = new JsonObject
        {
            ["name"] = p.Name,
            ["display_name"] = p.DisplayName,
            ["default"] = p.Default,
            ["min"] = p.Min,
            ["max"] = p.Max,
            ["step"] = p.Step,
        };
        if (p.Unit is { } unit)
            obj["unit"] = unit;
        return obj;
    }

    /// <summary>Undo/redo state plus an optional note about the action just performed — returned by the
    /// mutating tools so the client sees the outcome without a second round-trip.</summary>
    public static string HistoryState(EditHistory history, string? performed) =>
        HistoryObject(history, performed).ToJsonString(Indented);

    /// <summary>Human <c>h:mm:ss.fff</c> for a tick count (display companion to the raw ticks).</summary>
    public static string TimeString(long ticks)
    {
        long absTicks = Math.Abs(ticks);
        long totalMs = absTicks * 1000 / Timecode.TicksPerSecond;
        long h = totalMs / 3_600_000;
        long m = totalMs / 60_000 % 60;
        long s = totalMs / 1000 % 60;
        long ms = totalMs % 1000;
        string sign = ticks < 0 ? "-" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{h}:{m:00}:{s:00}.{ms:000}");
    }

    // ── Shared object builders (internal so tool code and tests can compose them) ──────────────────

    internal static JsonObject PlayheadObject(long playheadTicks, long durationTicks, bool playing) => new()
    {
        ["position_ticks"] = playheadTicks,
        ["position"] = TimeString(playheadTicks),
        ["duration_ticks"] = durationTicks,
        ["duration"] = TimeString(durationTicks),
        ["playing"] = playing,
    };

    /// <summary>Undo/redo summary for a history instance.</summary>
    internal static JsonObject HistoryObject(EditHistory history, string? performed = null)
    {
        var obj = new JsonObject
        {
            ["can_undo"] = history.CanUndo,
            ["undo_label"] = history.CanUndo ? history.UndoLabel : null,
            ["can_redo"] = history.CanRedo,
            ["redo_label"] = history.CanRedo ? history.RedoLabel : null,
        };
        if (performed is not null)
            obj["performed"] = performed;
        return obj;
    }

    private static JsonArray MediaArray(Project project) =>
        new(project.MediaPool.Items
            .Select(m => (JsonNode)new JsonObject
            {
                ["media_id"] = m.Id.Value.ToString("D"),
                ["name"] = Path.GetFileName(m.AbsolutePath),
                ["path"] = m.AbsolutePath,
                ["duration_ticks"] = m.Info.Duration.Ticks,
                ["duration"] = TimeString(m.Info.Duration.Ticks),
                ["has_video"] = m.Info.HasVideo,
                ["has_audio"] = m.Info.HasAudio,
            })
            .ToArray());

    private static JsonArray TracksArray(Timeline timeline) =>
        new(timeline.Tracks.Select(t => (JsonNode)TrackObject(t)).ToArray());

    private static JsonObject TrackObject(Track track)
    {
        var obj = new JsonObject
        {
            ["track_id"] = RuntimeIds.IdOf(track),
            ["kind"] = track is VideoTrack ? "video" : "audio",
            ["name"] = track.Name,
            ["enabled"] = track.Enabled,
        };
        switch (track)
        {
            case VideoTrack v:
                obj["opacity"] = v.Opacity;
                obj["blend_mode"] = v.BlendMode.ToString();
                break;
            case AudioTrack a:
                obj["gain_db"] = a.GainDb;
                obj["pan"] = a.Pan;
                obj["muted"] = a.Muted;
                obj["solo"] = a.Solo;
                break;
        }
        obj["clips"] = new JsonArray(track.Clips
            .OrderBy(c => c.TimelineStart.Ticks)
            .Select(c => (JsonNode)ClipObject(c))
            .ToArray());
        if (track.Transitions.Count > 0)
            obj["transitions"] = new JsonArray(track.Transitions
                .OrderBy(t => t.CutPoint.Ticks)
                .Select(t => (JsonNode)TransitionObject(t))
                .ToArray());
        return obj;
    }

    internal static JsonObject TransitionObject(Transition transition) => new()
    {
        ["transition_id"] = RuntimeIds.IdOf(transition),
        ["type_id"] = transition.TransitionTypeId,
        ["display_name"] = TransitionCatalog.DisplayName(transition.TransitionTypeId),
        ["cut_ticks"] = transition.CutPoint.Ticks,
        ["cut"] = TimeString(transition.CutPoint.Ticks),
        ["duration_ticks"] = transition.Duration.Ticks,
        ["alignment"] = transition.Alignment.ToString(),
        ["start_ticks"] = transition.Start.Ticks,
        ["end_ticks"] = transition.End.Ticks,
    };

    private static JsonObject ClipObject(Clip clip)
    {
        var obj = new JsonObject
        {
            ["clip_id"] = RuntimeIds.IdOf(clip),
            ["kind"] = clip.Kind.ToString(),
            ["start_ticks"] = clip.TimelineStart.Ticks,
            ["start"] = TimeString(clip.TimelineStart.Ticks),
            ["end_ticks"] = clip.TimelineEnd.Ticks,
            ["end"] = TimeString(clip.TimelineEnd.Ticks),
            ["duration_ticks"] = clip.Duration.Ticks,
            ["source_in_ticks"] = clip.SourceIn.Ticks,
            ["source_out_ticks"] = clip.SourceOut.Ticks,
        };
        if (clip.Kind == ClipKind.Media)
            obj["media_id"] = clip.MediaRefId.Value.ToString("D");
        if (clip.Generator is { } generator)
            obj["generator"] = generator.GeneratorTypeId;
        if (clip.SourceSequenceId is { } seq)
            obj["source_sequence_id"] = seq.ToString();
        if (clip.SpeedRatio != Rational.One)
            obj["speed"] = $"{clip.SpeedRatio.Num}/{clip.SpeedRatio.Den}";
        if (clip.GainDb != 0)
            obj["gain_db"] = clip.GainDb;
        if (clip.LinkGroupId is { } link)
            obj["link_group"] = link.ToString("D");
        if (clip.Effects.Count > 0)
        {
            obj["effects"] = new JsonArray(clip.Effects
                .Select((e, i) => (JsonNode)new JsonObject
                {
                    ["index"] = i,
                    ["type_id"] = e.EffectTypeId,
                    ["display_name"] = EffectCatalog.DisplayName(e.EffectTypeId),
                    ["enabled"] = e.Enabled,
                })
                .ToArray());
        }
        if (clip.Markers.Count > 0)
            obj["markers"] = MarkersArray(clip.Markers);
        return obj;
    }

    /// <summary>
    /// The full detail of one clip — the <c>get_clip</c> payload: placement + source span, media reference,
    /// link partners, speed/gain, fade lengths, markers, and the effect stack with every parameter's constant
    /// value or keyframes.
    /// </summary>
    public static string ClipDetail(Project project, Clip clip, Track track)
    {
        JsonObject obj = ClipObject(clip);
        obj["track_id"] = RuntimeIds.IdOf(track);
        obj["track_kind"] = track is VideoTrack ? "video" : "audio";
        if (clip.Kind == ClipKind.Media && project.MediaPool.Get(clip.MediaRefId) is { } media)
        {
            obj["media"] = new JsonObject
            {
                ["media_id"] = media.Id.Value.ToString("D"),
                ["name"] = Path.GetFileName(media.AbsolutePath),
                ["duration_ticks"] = media.Info.Duration.Ticks,
            };
        }
        if (clip.LinkGroupId is not null)
        {
            obj["linked_clips"] = new JsonArray(project.Timeline.ClipsLinkedTo(clip)
                .Select(l => (JsonNode)new JsonObject
                {
                    ["clip_id"] = RuntimeIds.IdOf(l.Clip),
                    ["track_id"] = RuntimeIds.IdOf(l.Track),
                    ["kind"] = l.Track is VideoTrack ? "video" : "audio",
                })
                .ToArray());
        }
        (long fadeIn, long fadeOut) = FadeOps.ReadFades(clip);
        obj["fade_in_ticks"] = fadeIn;
        obj["fade_out_ticks"] = fadeOut;
        obj["speed"] = $"{clip.SpeedRatio.Num}/{clip.SpeedRatio.Den}";
        obj["gain_db"] = clip.GainDb;

        // Replace the summary effects array with the full parameter detail.
        var effects = new JsonArray();
        for (int i = 0; i < clip.Effects.Count; i++)
            effects.Add(EffectDetailObject(clip.Effects[i], i, clip.TimelineStart.Ticks));
        obj["effects"] = effects;

        if (clip.Generator is { } generator)
        {
            obj["generator"] = new JsonObject
            {
                ["type_id"] = generator.GeneratorTypeId,
                ["display_name"] = GeneratorCatalog.DisplayName(generator.GeneratorTypeId),
                ["parameters"] = new JsonObject(generator.Parameters
                    .Select(kv => KeyValuePair.Create(kv.Key,
                        (JsonNode?)AnimatableValueObject(kv.Value, clip.TimelineStart.Ticks)))),
                ["strings"] = new JsonObject(generator.Strings
                    .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            };
        }
        return obj.ToJsonString(Indented);
    }

    /// <summary>One effect with its full parameter values — shared by <see cref="ClipDetail"/> and the audio
    /// chain listing. <paramref name="clipStartTicks"/> anchors each keyframe's <c>clip_offset_ticks</c>.</summary>
    internal static JsonObject EffectDetailObject(EffectInstance effect, int index, long clipStartTicks)
    {
        return new JsonObject
        {
            ["index"] = index,
            ["type_id"] = effect.EffectTypeId,
            ["display_name"] = EffectCatalog.DisplayName(effect.EffectTypeId),
            ["enabled"] = effect.Enabled,
            ["parameters"] = new JsonObject(effect.Parameters
                .Select(kv => KeyValuePair.Create(kv.Key,
                    (JsonNode?)AnimatableValueObject(kv.Value, clipStartTicks)))),
        };
    }

    /// <summary>An <see cref="AnimatableValue"/> as <c>{"constant": x}</c> or <c>{"keyframes": [...]}</c> —
    /// never silently flattened. Keyframe times are emitted both absolute (<c>time_ticks</c>) and relative to
    /// <paramref name="clipStartTicks"/> (<c>clip_offset_ticks</c>, the form the keyframe tools take).</summary>
    internal static JsonObject AnimatableValueObject(AnimatableValue value, long clipStartTicks)
    {
        if (!value.IsAnimated)
            return new JsonObject { ["constant"] = value.Evaluate(Timecode.Zero) };
        return new JsonObject
        {
            ["keyframes"] = new JsonArray(value.Keyframes
                .Select(k => (JsonNode)new JsonObject
                {
                    ["time_ticks"] = k.Time.Ticks,
                    ["clip_offset_ticks"] = k.Time.Ticks - clipStartTicks,
                    ["value"] = k.Value,
                    ["interpolation"] = k.Interpolation.ToString(),
                })
                .ToArray()),
        };
    }

    /// <summary>The export state — the <c>export_video</c> / <c>get_export_status</c> payload.</summary>
    public static string ExportStatus(McpExportStatus status)
    {
        var obj = new JsonObject
        {
            ["running"] = status.Running,
            ["progress"] = Math.Round(status.Progress, 4),
            ["output_path"] = status.OutputPath,
            ["completed"] = status.Completed,
            ["cancelled"] = status.Cancelled,
        };
        if (status.Error is { } error)
            obj["error"] = error;
        return obj.ToJsonString(Indented);
    }

    private static JsonArray MarkersArray(IEnumerable<Marker> markers) =>
        new(markers
            .OrderBy(m => m.Time.Ticks)
            .Select(m =>
            {
                var obj = new JsonObject
                {
                    ["time_ticks"] = m.Time.Ticks,
                    ["time"] = TimeString(m.Time.Ticks),
                    ["name"] = m.Name,
                    ["color"] = m.Color.ToString(),
                };
                if (m.Comment.Length > 0)
                    obj["comment"] = m.Comment;
                if (m.IsSpan)
                    obj["duration_ticks"] = m.Duration.Ticks;
                return (JsonNode)obj;
            })
            .ToArray());
}
