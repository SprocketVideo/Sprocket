using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Mcp;

/// <summary>One input keyframe for the keyframe-authoring tools: a time relative to the clip's timeline
/// start, a value, and an optional interpolation mode (Hold, Linear, EaseIn, EaseOut, EaseInOut; default
/// Linear).</summary>
public sealed record KeyframeInput(long ClipOffsetTicks, double Value, string? Interpolation = null);

/// <summary>
/// Clip-level tools beyond the basic place/trim/move/split/delete set (PLAN.md step 38 follow-on):
/// duplicate, link management, fades, keyframe authoring, effect utilities, retime/gain, and the
/// professional trim variants (ripple / roll / slide / ripple delete). Same contract as every edit tool:
/// resolve ids → build commands → one <see cref="EditHistory.Execute"/> (composite where multi-part).
/// </summary>
public sealed partial class SprocketTools
{
    // ── Clip detail ─────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_clip", ReadOnly = true, Idempotent = true)]
    [Description("Full detail of one clip: placement and source span, media reference, linked partners, " +
                 "speed/gain, fade-in/out lengths, markers, and every effect with its parameter values " +
                 "(constants or keyframes, with clip-relative keyframe offsets).")]
    public Task<string> GetClip([Description("clip_id from list_clips.")] int clipId) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            return StateFormatter.ClipDetail(api.Project, clip, track);
        });

    // ── Duplicate ───────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "duplicate_clip")]
    [Description("Duplicates a clip — effects (with keyframes rebased) and clip markers included — onto its " +
                 "own track. By default its linked partners duplicate too and the copies share a fresh link " +
                 "group, all as one undo entry. The default placement is immediately after the original " +
                 "(link group); pass targetStartTicks to place the primary copy elsewhere (partners keep " +
                 "their relative offsets).")]
    public Task<string> DuplicateClip(
        [Description("clip_id of the clip to duplicate.")] int clipId,
        [Description("Timeline start for the duplicate of the addressed clip; omit to butt the copies " +
                     "against the original group's end.")] long? targetStartTicks = null,
        [Description("Whether linked partner clips duplicate together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            var members = new List<(Track Track, Clip Clip)> { (track, clip) };
            if (includeLinked)
                members.AddRange(api.Project.Timeline.ClipsLinkedTo(clip));

            long groupMin = members.Min(m => m.Clip.TimelineStart.Ticks);
            long groupMaxEnd = members.Max(m => m.Clip.TimelineEnd.Ticks);
            long delta = targetStartTicks is { } target
                ? target - clip.TimelineStart.Ticks
                : groupMaxEnd - groupMin; // butt the copies against the group's end
            if (groupMin + delta < 0)
                throw new McpException("the duplicate would start before the timeline origin.");

            Guid? newGroup = members.Count > 1 && clip.LinkGroupId is not null ? Guid.NewGuid() : null;
            var shift = new Timecode(delta);
            var commands = new List<IEditCommand>();
            var copies = new List<(Track Track, Clip Copy)>();
            foreach ((Track mtrack, Clip member) in members)
            {
                Clip copy = member.CloneContentForSpan(
                    member.SourceIn, member.SourceOut, member.TimelineStart + shift);
                copy.LinkGroupId = newGroup;
                foreach (EffectInstance e in member.Effects)
                    copy.Effects.Add(e.CloneShifted(shift)); // keyframes move with the copy
                foreach (Marker m in member.Markers)
                    copy.Markers.Add(m.Clone()); // clip markers are source-relative — no shift
                commands.Add(new AddClipCommand(mtrack, copy));
                copies.Add((mtrack, copy));
            }

            api.History.Execute(commands.Count == 1
                ? commands[0]
                : new CompositeCommand("Duplicate clips", commands));
            api.RefreshPreview();

            (Track _, Clip primaryCopy) = copies[0];
            var payload = new JsonObject
            {
                ["clip_id"] = RuntimeIds.IdOf(primaryCopy),
                ["start_ticks"] = primaryCopy.TimelineStart.Ticks,
                ["end_ticks"] = primaryCopy.TimelineEnd.Ticks,
                ["effects_copied"] = primaryCopy.Effects.Count,
                ["history"] = StateFormatter.HistoryObject(api.History, "duplicated clip"),
            };
            if (copies.Count > 1)
                payload["linked_clip_ids"] = new JsonArray(copies.Skip(1)
                    .Select(c => (JsonNode)RuntimeIds.IdOf(c.Copy)).ToArray());
            return payload.ToJsonString();
        });

    // ── Link management ─────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "unlink_clip")]
    [Description("Unlinks a clip and its companions: clears the whole group's link_group so each edits " +
                 "independently, as one undo entry.")]
    public Task<string> UnlinkClip([Description("clip_id of any member of the linked group.")] int clipId) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            if (clip.LinkGroupId is null)
                throw new McpException($"clip {clipId} is not linked.");
            var members = new List<Clip> { clip };
            members.AddRange(api.Project.Timeline.ClipsLinkedTo(clip).Select(l => l.Clip));
            var commands = members
                .Select(c => (IEditCommand)SetPropertyCommand<Guid?>.Create(
                    "Unlink", () => c.LinkGroupId, v => c.LinkGroupId = v, null))
                .ToList();
            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Unlink clips", commands));
            return StateFormatter.HistoryState(api.History, $"unlinked {members.Count} clip(s)");
        });

    [McpServerTool(Name = "link_clips")]
    [Description("Links two or more clips into one group (companion A/V): they then move, trim, split, and " +
                 "delete together by default. Clips already in another group are re-pointed to the new one.")]
    public Task<string> LinkClips(
        [Description("clip_ids of the clips to link (at least two).")] int[] clipIds) =>
        _session.OnModelThreadAsync(api =>
        {
            if (clipIds is null || clipIds.Length < 2)
                throw new McpException("pass at least two clip_ids to link.");
            var clips = clipIds.Distinct().Select(id => ResolveClip(api, id).Clip).ToList();
            Guid group = Guid.NewGuid();
            var commands = clips
                .Select(c => (IEditCommand)SetPropertyCommand<Guid?>.Create(
                    "Link", () => c.LinkGroupId, v => c.LinkGroupId = v, group))
                .ToList();
            api.History.Execute(new CompositeCommand("Link clips", commands));
            return new JsonObject
            {
                ["link_group"] = group.ToString("D"),
                ["history"] = StateFormatter.HistoryObject(api.History, $"linked {clips.Count} clips"),
            }.ToJsonString();
        });

    // ── Fades & keyframes ───────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "set_clip_fade")]
    [Description("Sets a clip's fade-in and/or fade-out length in ticks — the same envelope the on-timeline " +
                 "fade handles author (a keyframed opacity ramp on the clip's Fade effect, driving video " +
                 "alpha and audio gain together). Creates the Fade effect when absent; 0 removes that edge's " +
                 "ramp. One undoable edit; an omitted parameter keeps that edge's current fade.")]
    public Task<string> SetClipFade(
        [Description("clip_id of the target clip.")] int clipId,
        [Description("Fade-in length in ticks from the clip's start (0 = none); omit to keep the current value.")] long? fadeInTicks = null,
        [Description("Fade-out length in ticks ending at the clip's end (0 = none); omit to keep the current value.")] long? fadeOutTicks = null) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            if (fadeInTicks is null && fadeOutTicks is null)
                throw new McpException("pass fadeInTicks and/or fadeOutTicks.");
            if (fadeInTicks is < 0 || fadeOutTicks is < 0)
                throw new McpException("fade lengths cannot be negative.");

            AnimatableValue? existing = FadeOps.FadeOpacity(clip);
            (long currentIn, long currentOut) = FadeOps.ReadFades(clip);
            long fadeIn = fadeInTicks ?? currentIn;
            long fadeOut = fadeOutTicks ?? currentOut;
            long duration = clip.Duration.Ticks;
            if (fadeIn + fadeOut > duration)
                throw new McpException(
                    $"the fades overlap: fade-in {fadeIn} + fade-out {fadeOut} exceeds the clip's duration {duration}.");

            AnimatableValue opacity = FadeOps.BuildOpacity(
                existing, clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks, fadeIn, fadeOut);
            api.History.Execute(new SetClipFadeCommand(clip, opacity));
            api.RefreshPreview();
            return new JsonObject
            {
                ["clip_id"] = clipId,
                ["fade_in_ticks"] = fadeIn,
                ["fade_out_ticks"] = fadeOut,
                ["history"] = StateFormatter.HistoryObject(api.History, "set clip fade"),
            }.ToJsonString();
        });

    [McpServerTool(Name = "set_effect_parameter_keyframes")]
    [Description("Animates one parameter of a clip's effect with a list of keyframes (replacing the " +
                 "parameter's previous value or keyframes). Keyframe times are relative to the clip's " +
                 "timeline start; interpolation per keyframe is Hold, Linear (default), EaseIn, EaseOut, or " +
                 "EaseInOut. Discrete parameter kinds (see list_effect_types) get their values rounded; " +
                 "toggle/dropdown keyframes are always Hold, and integer keyframes default to Hold. Use " +
                 "set_effect_parameter for a constant instead. Identify the effect by effect_tag (preferred " +
                 "— stable across reorders) or effect_index.")]
    public Task<string> SetEffectParameterKeyframes(
        [Description("clip_id of the clip carrying the effect.")] int clipId,
        [Description("Parameter name, e.g. \"opacity\".")] string parameter,
        [Description("The keyframes: {clipOffsetTicks, value, interpolation?}. At least one.")] KeyframeInput[] keyframes,
        [Description("The effect's reference tag, e.g. \"RV-1\" (see the clip's effects list).")] string? effectTag = null,
        [Description("Index of the effect in the clip's effect stack (alternative to effect_tag).")] int effectIndex = -1) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            EffectInstance effect = ResolveEffect(clip, effectIndex, effectTag);
            if (keyframes is null || keyframes.Length == 0)
                throw new McpException("pass at least one keyframe.");

            // Discrete kinds keep their keyframes honest: values snap, and toggles/dropdowns are always
            // Hold (Linear/eased would interpolate a boolean through the DSP's ≥ 0.5 threshold); integers
            // default to Hold but honour an explicit ease.
            EffectParameterDescriptor? descriptor = FindParameter(effect, parameter);
            ParameterKind kind = descriptor?.Kind ?? ParameterKind.Continuous;
            Interpolation fallback = kind == ParameterKind.Continuous ? Interpolation.Linear : Interpolation.Hold;

            var points = new List<Keyframe>(keyframes.Length);
            foreach (KeyframeInput k in keyframes)
            {
                Interpolation interpolation = fallback;
                if (!string.IsNullOrWhiteSpace(k.Interpolation)
                    && !Enum.TryParse(k.Interpolation, ignoreCase: true, out interpolation))
                    throw new McpException(
                        $"unknown interpolation '{k.Interpolation}' — use Hold, Linear, EaseIn, EaseOut, or EaseInOut.");
                if (kind is ParameterKind.Toggle or ParameterKind.Dropdown)
                    interpolation = Interpolation.Hold;
                points.Add(new Keyframe(
                    clip.TimelineStart + new Timecode(k.ClipOffsetTicks),
                    CoerceParameterValue(descriptor, k.Value), interpolation));
            }

            api.History.Execute(new SetEffectParameterCommand(
                effect, parameter, AnimatableValue.Animated(points)));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History,
                $"keyframed {effect.EffectTypeId}.{parameter} ({points.Count} keyframes)");
        });

    // ── Effect utilities ────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "copy_effects")]
    [Description("Copies one clip's effect stack onto another clip (\"paste attributes\"): every effect is " +
                 "cloned with its parameters, keyframes rebased to the target clip's position. One undo entry.")]
    public Task<string> CopyEffects(
        [Description("clip_id of the clip to copy effects from.")] int sourceClipId,
        [Description("clip_id of the clip to copy effects onto.")] int targetClipId,
        [Description("Whether to remove the target's existing effects first (default false = append).")] bool replace = false) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip source, Track _) = ResolveClip(api, sourceClipId);
            (Clip target, Track _) = ResolveClip(api, targetClipId);
            if (ReferenceEquals(source, target))
                throw new McpException("source and target are the same clip.");
            if (source.Effects.Count == 0)
                throw new McpException($"clip {sourceClipId} has no effects to copy.");

            var shift = new Timecode(target.TimelineStart.Ticks - source.TimelineStart.Ticks);
            var commands = new List<IEditCommand>();
            if (replace)
                foreach (EffectInstance e in target.Effects)
                    commands.Add(new RemoveEffectCommand(target, e));
            foreach (EffectInstance e in source.Effects)
                commands.Add(new AddEffectCommand(target, e.CloneShifted(shift)));

            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Copy effects", commands));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History,
                $"copied {source.Effects.Count} effect(s) from clip {sourceClipId} to clip {targetClipId}");
        });

    [McpServerTool(Name = "set_effect_enabled")]
    [Description("Enables or disables (bypasses) an effect without removing it — parameters and keyframes " +
                 "are kept for re-enabling. Identify the effect by effect_tag (preferred — stable across " +
                 "reorders) or effect_index.")]
    public Task<string> SetEffectEnabled(
        [Description("clip_id of the clip carrying the effect.")] int clipId,
        [Description("true to apply the effect, false to bypass it.")] bool enabled,
        [Description("The effect's reference tag, e.g. \"RV-1\" (see the clip's effects list).")] string? effectTag = null,
        [Description("Index of the effect in the clip's effect stack (alternative to effect_tag).")] int effectIndex = -1) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            EffectInstance effect = ResolveEffect(clip, effectIndex, effectTag);
            api.History.Execute(new SetEffectEnabledCommand(effect, enabled));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History,
                $"{(enabled ? "enabled" : "disabled")} {effect.EffectTypeId}");
        });

    // ── Retime & gain ───────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "set_clip_speed")]
    [Description("Retimes a clip to an exact speed ratio (source time / timeline time): 2/1 = double speed, " +
                 "1/2 = half-speed slow motion. The source span is unchanged; the clip's timeline duration " +
                 "derives from the speed. By default linked partners retime together so A/V stays in sync.")]
    public Task<string> SetClipSpeed(
        [Description("clip_id of the clip to retime.")] int clipId,
        [Description("Speed ratio numerator (must be positive).")] int numerator,
        [Description("Speed ratio denominator (default 1).")] int denominator = 1,
        [Description("Whether linked partner clips retime together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            if (numerator <= 0 || denominator <= 0)
                throw new McpException("the speed ratio must be strictly positive.");
            var speed = new Rational(numerator, denominator);

            var members = new List<Clip> { clip };
            if (includeLinked)
                members.AddRange(api.Project.Timeline.ClipsLinkedTo(clip).Select(l => l.Clip));
            var commands = members
                .Select(c => (IEditCommand)new SetClipSpeedCommand(c, speed))
                .ToList();
            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Change speed", commands));
            api.RefreshPreview();
            return ClipResult(api, clip, $"set speed to {numerator}/{denominator}");
        });

    [McpServerTool(Name = "set_clip_gain")]
    [Description("Sets a clip's audio gain in decibels (0 dB = unity), applied on top of fades and the " +
                 "track fader. Audio clips only — it has no effect on video pixels.")]
    public Task<string> SetClipGain(
        [Description("clip_id of the target clip.")] int clipId,
        [Description("Gain in dB (0 = unity).")] double gainDb) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track _) = ResolveClip(api, clipId);
            api.History.Execute(SetPropertyCommand<double>.Create(
                "Clip gain", () => clip.GainDb, v => clip.GainDb = v, gainDb));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"set clip {clipId} gain to {gainDb} dB");
        });

    // ── Ripple / roll / slide (PLAN.md step 22 parity) ─────────────────────────────────────────────

    [McpServerTool(Name = "ripple_trim")]
    [Description("Ripple-trims one edge of a clip: the edge moves to the new timeline position and every " +
                 "later clip on the track shifts so no gap opens (or closes). By default linked partners " +
                 "ripple with it. One undo entry.")]
    public Task<string> RippleTrim(
        [Description("clip_id of the clip to trim.")] int clipId,
        [Description("Which edge to trim: \"in\" or \"out\".")] string edge,
        [Description("The edge's new timeline position in ticks (before the ripple closes the gap).")] long newTimelineTicks,
        [Description("Whether linked partner clips ripple together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            bool trimIn = edge.Equals("in", StringComparison.OrdinalIgnoreCase);
            if (!trimIn && !edge.Equals("out", StringComparison.OrdinalIgnoreCase))
                throw new McpException("edge must be \"in\" or \"out\".");

            long delta = trimIn
                ? newTimelineTicks - clip.TimelineStart.Ticks
                : newTimelineTicks - clip.TimelineEnd.Ticks;
            if (delta == 0)
                throw new McpException("the edge is already at that position.");

            var units = new List<(Track Track, Clip Clip)> { (track, clip) };
            if (includeLinked)
                units.AddRange(api.Project.Timeline.ClipsLinkedTo(clip));

            var commands = new List<IEditCommand>();
            foreach ((Track utrack, Clip unit) in units)
            {
                long sourceDelta = new Timecode(delta).Scale(unit.SpeedRatio).Ticks;
                Timecode newIn = unit.SourceIn, newOut = unit.SourceOut;
                long shift;
                if (trimIn)
                {
                    newIn = new Timecode(unit.SourceIn.Ticks + sourceDelta);
                    if (newIn < Timecode.Zero || newIn >= unit.SourceOut)
                        throw new McpException($"that trim runs clip {RuntimeIds.IdOf(unit)} out of source media.");
                    shift = -delta;
                }
                else
                {
                    newOut = new Timecode(unit.SourceOut.Ticks + sourceDelta);
                    long mediaDuration = MediaDurationTicks(api, unit);
                    if (newOut <= unit.SourceIn || newOut.Ticks > mediaDuration)
                        throw new McpException($"that trim runs clip {RuntimeIds.IdOf(unit)} out of source media.");
                    shift = delta;
                }
                var downstream = new List<(Clip, Timecode)>();
                foreach (Clip c in utrack.Clips)
                    if (!ReferenceEquals(c, unit) && c.TimelineStart >= unit.TimelineEnd)
                        downstream.Add((c, c.TimelineStart));
                commands.Add(new RippleTrimCommand(unit, newIn, newOut, downstream, shift));
            }

            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Ripple trim", commands));
            api.RefreshPreview();
            return ClipResult(api, clip, "ripple trimmed");
        });

    [McpServerTool(Name = "roll_edit")]
    [Description("Rolls the cut between a clip and its adjacent neighbour: the shared edit point moves to " +
                 "the new position while the two clips' combined span (and everything downstream) stays fixed.")]
    public Task<string> RollEdit(
        [Description("clip_id of either clip at the cut.")] int clipId,
        [Description("Which of the clip's cuts to roll: \"start\" (with the previous clip) or \"end\" (with the next).")] string cutEdge,
        [Description("The cut's new timeline position in ticks.")] long newCutTicks) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            bool atEnd = cutEdge.Equals("end", StringComparison.OrdinalIgnoreCase);
            if (!atEnd && !cutEdge.Equals("start", StringComparison.OrdinalIgnoreCase))
                throw new McpException("cutEdge must be \"start\" or \"end\".");

            Clip? left = atEnd ? clip : AdjacentBefore(track, clip);
            Clip? right = atEnd ? AdjacentAfter(track, clip) : clip;
            if (left is null || right is null)
                throw new McpException("there is no adjacent clip sharing that cut to roll with.");

            long oldCut = left.TimelineEnd.Ticks;
            long delta = newCutTicks - oldCut;
            if (delta == 0)
                throw new McpException("the cut is already at that position.");

            var newLeftOut = new Timecode(left.SourceOut.Ticks + new Timecode(delta).Scale(left.SpeedRatio).Ticks);
            var newRightIn = new Timecode(right.SourceIn.Ticks + new Timecode(delta).Scale(right.SpeedRatio).Ticks);
            var newRightStart = new Timecode(right.TimelineStart.Ticks + delta);
            if (newLeftOut <= left.SourceIn || newLeftOut.Ticks > MediaDurationTicks(api, left))
                throw new McpException("the roll runs the outgoing clip out of source media.");
            if (newRightIn < Timecode.Zero || newRightIn >= right.SourceOut)
                throw new McpException("the roll runs the incoming clip out of source media.");

            api.History.Execute(new RollEditCommand(left, right, newLeftOut, newRightIn, newRightStart));
            api.RefreshPreview();
            return new JsonObject
            {
                ["left_clip_id"] = RuntimeIds.IdOf(left),
                ["right_clip_id"] = RuntimeIds.IdOf(right),
                ["cut_ticks"] = newCutTicks,
                ["history"] = StateFormatter.HistoryObject(api.History, "rolled edit"),
            }.ToJsonString();
        });

    [McpServerTool(Name = "slide_clip")]
    [Description("Slides a clip along the timeline while its butted neighbours absorb the change: the clip's " +
                 "content and duration are unchanged, the previous clip's out-point and the next clip's " +
                 "in-point move to keep the track gap-free.")]
    public Task<string> SlideClip(
        [Description("clip_id of the clip to slide.")] int clipId,
        [Description("How far to slide, in ticks (negative = earlier).")] long deltaTicks) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            if (deltaTicks == 0)
                throw new McpException("deltaTicks must be non-zero.");
            Clip? prev = AdjacentBefore(track, clip);
            Clip? next = AdjacentAfter(track, clip);
            if (prev is null && next is null)
                throw new McpException("the clip has no butted neighbour to slide against.");

            var newStart = new Timecode(clip.TimelineStart.Ticks + deltaTicks);
            if (newStart < Timecode.Zero)
                throw new McpException("the slide would push the clip before the timeline origin.");

            Timecode newPrevOut = default, newNextIn = default, newNextStart = default;
            if (prev is not null)
            {
                newPrevOut = new Timecode(prev.SourceOut.Ticks + new Timecode(deltaTicks).Scale(prev.SpeedRatio).Ticks);
                if (newPrevOut <= prev.SourceIn || newPrevOut.Ticks > MediaDurationTicks(api, prev))
                    throw new McpException("the slide runs the previous clip out of source media.");
            }
            if (next is not null)
            {
                newNextIn = new Timecode(next.SourceIn.Ticks + new Timecode(deltaTicks).Scale(next.SpeedRatio).Ticks);
                newNextStart = new Timecode(next.TimelineStart.Ticks + deltaTicks);
                if (newNextIn < Timecode.Zero || newNextIn >= next.SourceOut)
                    throw new McpException("the slide runs the next clip out of source media.");
            }

            api.History.Execute(new SlideClipCommand(clip, newStart, prev, newPrevOut, next, newNextIn, newNextStart));
            api.RefreshPreview();
            return ClipResult(api, clip, "slid clip");
        });

    [McpServerTool(Name = "ripple_delete", Destructive = true)]
    [Description("Ripple-deletes a clip (Shift+Delete in the app): removes it and shifts every later clip on " +
                 "its track left by its duration so the gap closes. By default linked partners are removed " +
                 "and their tracks rippled too — all one undo entry.")]
    public Task<string> RippleDelete(
        [Description("clip_id of the clip to ripple-delete.")] int clipId,
        [Description("Whether linked partner clips are removed and rippled together (default true).")] bool includeLinked = true) =>
        _session.OnModelThreadAsync(api =>
        {
            (Clip clip, Track track) = ResolveClip(api, clipId);
            var removed = new List<(Track Track, Clip Clip)> { (track, clip) };
            if (includeLinked)
                removed.AddRange(api.Project.Timeline.ClipsLinkedTo(clip));
            var removedClips = removed.Select(r => r.Clip).ToHashSet();

            var commands = new List<IEditCommand>();
            foreach ((Track rtrack, Clip rclip) in removed)
            {
                long shift = -rclip.Duration.Ticks;
                Timecode end = rclip.TimelineEnd;
                commands.Add(new RemoveClipCommand(rtrack, rclip));
                foreach (Clip d in rtrack.Clips)
                    if (!removedClips.Contains(d) && d.TimelineStart >= end)
                        commands.Add(new SetClipPlacementCommand(
                            d, d.SourceIn, d.SourceOut, new Timecode(d.TimelineStart.Ticks + shift), "Ripple"));
            }
            api.History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Ripple delete", commands));
            api.RefreshPreview();
            return StateFormatter.HistoryState(api.History, $"ripple-deleted clip {clipId}");
        });

    // ── Shared clip helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>The clip's source-media duration in ticks, falling back to the current out-point for
    /// offline/unknown media (so edge math never runs past an unknown end).</summary>
    private static long MediaDurationTicks(IEditorApi api, Clip clip)
    {
        MediaRef? media = clip.Kind == ClipKind.Media ? api.Project.MediaPool.Get(clip.MediaRefId) : null;
        return media is { Info.Duration.Ticks: > 0 } ? media.Info.Duration.Ticks : clip.SourceOut.Ticks;
    }

    private static Clip? AdjacentBefore(Track track, Clip clip)
    {
        Clip? best = null;
        foreach (Clip c in track.Clips)
            if (!ReferenceEquals(c, clip) && c.TimelineEnd == clip.TimelineStart)
                best = c;
        return best;
    }

    private static Clip? AdjacentAfter(Track track, Clip clip)
    {
        foreach (Clip c in track.Clips)
            if (!ReferenceEquals(c, clip) && c.TimelineStart == clip.TimelineEnd)
                return c;
        return null;
    }
}
