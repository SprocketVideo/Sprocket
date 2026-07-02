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
    /// transport, and undo/redo state. The <c>get_project_state</c> payload.</summary>
    public static string ProjectState(
        Project project, EditHistory history, string? projectPath,
        long playheadTicks, long durationTicks, bool playing)
    {
        Timeline timeline = project.Timeline;
        var root = new JsonObject
        {
            ["ticks_per_second"] = Timecode.TicksPerSecond,
            ["project_path"] = projectPath,
            ["active_sequence"] = new JsonObject
            {
                ["id"] = project.ActiveSequence.Id.ToString(),
                ["name"] = project.ActiveSequence.Name,
            },
            ["sequences"] = new JsonArray(project.Sequences
                .Select(s => (JsonNode)new JsonObject { ["id"] = s.Id.ToString(), ["name"] = s.Name })
                .ToArray()),
            ["format"] = new JsonObject
            {
                ["frame_rate"] = $"{timeline.FrameRate.Num}/{timeline.FrameRate.Den}",
                ["resolution"] = $"{timeline.Resolution.Width}x{timeline.Resolution.Height}",
                ["sample_rate"] = timeline.SampleRate,
            },
            ["media"] = MediaArray(project),
            ["tracks"] = TracksArray(timeline),
            ["markers"] = MarkersArray(timeline.Markers),
            ["playhead"] = PlayheadObject(playheadTicks, durationTicks, playing),
            ["history"] = HistoryObject(history),
        };
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

    /// <summary>The effect catalog (type ids + parameter ranges) — the <c>list_effect_types</c> payload.</summary>
    public static string EffectTypes()
    {
        var effects = new JsonArray();
        foreach (EffectDescriptor descriptor in EffectCatalog.All)
        {
            var parameters = new JsonArray();
            foreach (EffectParameterDescriptor p in descriptor.Parameters)
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = p.Name,
                    ["display_name"] = p.DisplayName,
                    ["default"] = p.Default,
                    ["min"] = p.Min,
                    ["max"] = p.Max,
                });
            }
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
        return obj;
    }

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

    private static JsonArray MarkersArray(IEnumerable<Marker> markers) =>
        new(markers
            .OrderBy(m => m.Time.Ticks)
            .Select(m => (JsonNode)new JsonObject
            {
                ["time_ticks"] = m.Time.Ticks,
                ["time"] = TimeString(m.Time.Ticks),
                ["name"] = m.Name,
                ["color"] = m.Color.ToString(),
            })
            .ToArray());
}
