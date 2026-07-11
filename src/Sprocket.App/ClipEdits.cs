using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// Pure command-builders for the clip context-menu operations (PLAN.md step 53): split at a timeline point,
/// duplicate, and the Enable toggle. Kept free of Avalonia types so the linked-companion collection, link-group
/// bookkeeping, and placement math are unit-testable headlessly (the <see cref="ClipboardOps"/> idiom); the
/// public clip-edit methods on <see cref="Timeline.TimelineControl"/> and the menu wiring in
/// <see cref="MainWindow"/> rest on these. Every builder returns a single command (composite when multi-part),
/// so each operation is one undo entry through the step-10 command stack.
/// </summary>
public static class ClipEdits
{
    /// <summary>The one-undo-entry split command plus the primary clip's new right-hand half (to select).</summary>
    public readonly record struct SplitResult(IEditCommand Command, Clip Right);

    /// <summary>
    /// Splits <paramref name="clip"/> at timeline time <paramref name="at"/> — the shared core of the Blade tool
    /// and Split at Playhead (Ctrl+K). With <paramref name="linked"/> on, every companion clip that also spans the
    /// cut is split too and the right-hand halves share a fresh link group, so each side stays an independently
    /// linked A/V pair. Returns <see langword="null"/> when the cut falls on/outside the clip's edges (a no-op).
    /// </summary>
    public static SplitResult? Split(Timeline timeline, Track track, Clip clip, Timecode at, bool linked)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        if (at <= clip.TimelineStart || at >= clip.TimelineEnd)
            return null;

        List<(Track Track, Clip Clip)> companions = linked
            ? timeline.ClipsLinkedTo(clip).Where(l => l.Clip.Contains(at)).ToList()
            : [];
        Guid? rightGroup = (linked && clip.LinkGroupId is not null && companions.Count > 0) ? Guid.NewGuid() : null;

        var primary = new SplitClipCommand(track, clip, at, rightGroup);
        if (companions.Count == 0)
            return new SplitResult(primary, primary.RightClip);

        var commands = new List<IEditCommand> { primary };
        foreach ((Track ctrack, Clip cclip) in companions)
            commands.Add(new SplitClipCommand(ctrack, cclip, at, rightGroup));
        return new SplitResult(new CompositeCommand("Split clips", commands), primary.RightClip);
    }

    /// <summary>
    /// Duplicates <paramref name="clip"/> onto its own track, placed butted against the original — effects
    /// (keyframes rebased by the placement shift, so animation moves with the copy) and clip markers included.
    /// With <paramref name="linked"/> on, companion clips duplicate together, keep their relative offsets, and
    /// the copies share a fresh link group (never the original's). The shift is the whole group's extent so no
    /// copy overlaps an original; for a lone clip that is simply its <see cref="Clip.TimelineEnd"/>. Returns the
    /// one-undo-entry command plus the addressed clip's copy (to select).
    /// </summary>
    public static (IEditCommand Command, Clip Copy) Duplicate(Timeline timeline, Track track, Clip clip, bool linked)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);

        var members = new List<(Track Track, Clip Clip)> { (track, clip) };
        if (linked)
            members.AddRange(timeline.ClipsLinkedTo(clip));

        long groupMin = members.Min(m => m.Clip.TimelineStart.Ticks);
        long groupMaxEnd = members.Max(m => m.Clip.TimelineEnd.Ticks);
        var shift = new Timecode(groupMaxEnd - groupMin);
        Guid? newGroup = members.Count > 1 && clip.LinkGroupId is not null ? Guid.NewGuid() : null;

        var commands = new List<IEditCommand>();
        Clip? primaryCopy = null;
        foreach ((Track mtrack, Clip member) in members)
        {
            Clip copy = member.CloneContentForSpan(member.SourceIn, member.SourceOut, member.TimelineStart + shift);
            copy.LinkGroupId = newGroup;
            foreach (EffectInstance e in member.Effects)
                copy.Effects.Add(e.CloneShifted(shift)); // keyframe times are absolute — move them with the copy
            foreach (Marker m in member.Markers)
                copy.Markers.Add(m.Clone()); // clip markers are source-relative — no shift
            commands.Add(new AddClipCommand(mtrack, copy));
            primaryCopy ??= copy;
        }

        return (commands.Count == 1 ? commands[0] : new CompositeCommand("Duplicate clips", commands), primaryCopy!);
    }

    // ── Batch operations over the multi-selection (PLAN.md step 54) ─────────────────────────────────

    /// <summary>
    /// Expands a selection into the full operation target: every selected clip plus, with
    /// <paramref name="linked"/> on, each one's linked companions — deduplicated (a linked pair that is
    /// wholly selected expands to itself, not to four entries) and paired with its track. Selection order is
    /// preserved; clips no longer on any track are dropped. The shared first step of every batch builder.
    /// </summary>
    public static List<(Track Track, Clip Clip)> ExpandWithLinked(
        Timeline timeline, IEnumerable<Clip> clips, bool linked)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clips);

        // One up-front clip→track map + hash-set dedupe keep a Select All-sized expansion O(total clips).
        var trackOf = new Dictionary<Clip, Track>(ReferenceEqualityComparer.Instance);
        foreach (Track t in timeline.Tracks)
            foreach (Clip c in t.Clips)
                trackOf[c] = t;

        var seen = new HashSet<Clip>(ReferenceEqualityComparer.Instance);
        var members = new List<(Track Track, Clip Clip)>();
        foreach (Clip c in clips)
        {
            if (!trackOf.TryGetValue(c, out Track? track) || !seen.Add(c))
                continue;
            members.Add((track, c));
            if (!linked)
                continue;
            foreach ((Track ctrack, Clip cclip) in timeline.ClipsLinkedTo(c))
                if (seen.Add(cclip))
                    members.Add((ctrack, cclip));
        }
        return members;
    }

    /// <summary>
    /// Deletes every selected clip (plus linked companions with <paramref name="linked"/> on) as one undo
    /// entry. Returns <see langword="null"/> when nothing resolves to a track (a no-op).
    /// </summary>
    public static IEditCommand? DeleteAll(Timeline timeline, IEnumerable<Clip> clips, bool linked)
    {
        List<(Track Track, Clip Clip)> members = ExpandWithLinked(timeline, clips, linked);
        if (members.Count == 0)
            return null;
        var commands = members
            .Select(m => (IEditCommand)new RemoveClipCommand(m.Track, m.Clip))
            .ToList();
        return commands.Count == 1 ? commands[0] : new CompositeCommand("Delete clips", commands);
    }

    /// <summary>
    /// Ripple-deletes every selected clip (plus linked companions with <paramref name="linked"/> on) as one
    /// undo entry: each removed clip's gap closes on its own track, and a surviving clip downstream of several
    /// removals shifts by their combined duration (the shifts are cumulative, so a track with two selected
    /// clips closes both gaps exactly). Returns <see langword="null"/> when nothing resolves to a track.
    /// </summary>
    public static IEditCommand? RippleDeleteAll(Timeline timeline, IEnumerable<Clip> clips, bool linked)
    {
        List<(Track Track, Clip Clip)> members = ExpandWithLinked(timeline, clips, linked);
        if (members.Count == 0)
            return null;

        var commands = new List<IEditCommand>();
        foreach ((Track track, Clip clip) in members)
            commands.Add(new RemoveClipCommand(track, clip));

        // One placement command per survivor, carrying its TOTAL shift — sequential per-removal shifts with
        // absolute targets would clobber one another when two removed clips sit upstream of the same survivor.
        foreach (Track track in timeline.Tracks)
        {
            List<Clip> removedHere = members.Where(m => ReferenceEquals(m.Track, track)).Select(m => m.Clip).ToList();
            if (removedHere.Count == 0)
                continue;
            var removedSet = new HashSet<Clip>(removedHere, ReferenceEqualityComparer.Instance);
            foreach (Clip survivor in track.Clips)
            {
                if (removedSet.Contains(survivor))
                    continue;
                long shift = removedHere
                    .Where(r => r.TimelineEnd <= survivor.TimelineStart)
                    .Sum(r => r.Duration.Ticks);
                if (shift > 0)
                    commands.Add(new SetClipPlacementCommand(
                        survivor, survivor.SourceIn, survivor.SourceOut,
                        new Timecode(survivor.TimelineStart.Ticks - shift), "Ripple"));
            }
        }
        return commands.Count == 1 ? commands[0] : new CompositeCommand("Ripple delete", commands);
    }

    /// <summary>
    /// Nudges every selected clip (plus linked companions with <paramref name="linked"/> on) by
    /// <paramref name="deltaTicks"/>, group-clamped so no member crosses the timeline origin — the whole set
    /// shifts rigidly by one delta, as one undo entry. Returns <see langword="null"/> when the clamp leaves
    /// nothing to move.
    /// </summary>
    public static IEditCommand? NudgeAll(Timeline timeline, IEnumerable<Clip> clips, long deltaTicks, bool linked)
    {
        List<(Track Track, Clip Clip)> members = ExpandWithLinked(timeline, clips, linked);
        if (members.Count == 0)
            return null;

        long groupMin = members.Min(m => m.Clip.TimelineStart.Ticks);
        long delta = ClipboardOps.ClampGroupNudge(deltaTicks, groupMin);
        if (delta == 0)
            return null;

        var commands = members
            .Select(m => (IEditCommand)new SetClipPlacementCommand(
                m.Clip, m.Clip.SourceIn, m.Clip.SourceOut,
                new Timecode(m.Clip.TimelineStart.Ticks + delta), "Nudge clip"))
            .ToList();
        return commands.Count == 1 ? commands[0] : new CompositeCommand("Nudge clips", commands);
    }

    /// <summary>
    /// Sets every selected clip (plus linked companions with <paramref name="linked"/> on) to the opposite of
    /// <paramref name="primary"/>'s <see cref="Clip.Enabled"/> state — a mixed selection converges on the
    /// primary's new state rather than each member flipping independently, matching Premiere — as one undo
    /// entry. Returns <see langword="null"/> when nothing resolves to a track.
    /// </summary>
    public static IEditCommand? ToggleEnabledAll(Timeline timeline, Clip primary, IEnumerable<Clip> clips, bool linked)
    {
        ArgumentNullException.ThrowIfNull(primary);
        List<(Track Track, Clip Clip)> members = ExpandWithLinked(timeline, clips, linked);
        if (members.Count == 0)
            return null;

        bool target = !primary.Enabled;
        string name = target ? "Enable clips" : "Disable clips";
        var commands = members
            .Select(m =>
            {
                Clip c = m.Clip;
                return (IEditCommand)SetPropertyCommand<bool>.Create(
                    name, () => c.Enabled, v => c.Enabled = v, target);
            })
            .ToList();
        return commands.Count == 1 ? commands[0] : new CompositeCommand(name, commands);
    }

    /// <summary>
    /// The one-undo-entry commit of a completed move drag (PLAN.md steps 16e/54): the primary lands at
    /// <paramref name="newStartTicks"/> — on <paramref name="dst"/> when the drag crossed lanes — and every
    /// other moved clip (multi-selection members and linked companions alike) shifts rigidly by the same
    /// delta on its own track. Returns <see langword="null"/> for a pure click (no movement, no track change).
    /// </summary>
    public static IEditCommand? MoveSet(
        Clip primary, Track src, Track dst, long newStartTicks,
        Timecode origIn, Timecode origOut, long origStartTicks,
        IReadOnlyList<(Clip Clip, Timecode OrigStart)> others)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(others);

        bool trackChanged = !ReferenceEquals(dst, src);
        long delta = newStartTicks - origStartTicks;
        if (!trackChanged && delta == 0)
            return null;

        var commands = new List<IEditCommand>
        {
            trackChanged
                ? new MoveClipToTrackCommand(src, dst, primary, new Timecode(newStartTicks))
                : new SetClipPlacementCommand(primary, origIn, origOut, new Timecode(newStartTicks), "Move clip"),
        };
        if (delta != 0)
            foreach ((Clip other, Timecode origStart) in others)
                commands.Add(new SetClipPlacementCommand(
                    other, other.SourceIn, other.SourceOut,
                    new Timecode(origStart.Ticks + delta), "Move clip"));

        return commands.Count == 1
            ? commands[0]
            : new CompositeCommand(trackChanged ? "Move clip to track" : "Move clips", commands);
    }

    // ── Link / Unlink (PLAN.md steps 13/55) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the selection is eligible for Clip ▸ Link (Premiere's rule, PLAN.md step 55): at least two
    /// clips still on tracks, spanning at least one video-track clip and one audio-track clip — and not
    /// already exactly one whole link group (there Ctrl+L toggles to Unlink instead). A partial subset of
    /// a larger group stays linkable: re-pointing it at its own fresh group is a real edit.
    /// </summary>
    public static bool CanLink(Timeline timeline, IEnumerable<Clip> clips)
    {
        List<(Track Track, Clip Clip)> members = ExpandWithLinked(timeline, clips, linked: false);
        if (members.Count < 2)
            return false;
        if (!members.Any(m => m.Track is VideoTrack) || !members.Any(m => m.Track is AudioTrack))
            return false;
        Guid? shared = members[0].Clip.LinkGroupId;
        return shared is null
            || members.Any(m => m.Clip.LinkGroupId != shared)
            || timeline.ClipsLinkedTo(members[0].Clip).Count() != members.Count - 1;
    }

    /// <summary>
    /// Links the selected clips: one fresh shared <see cref="Clip.LinkGroupId"/> on every selected clip,
    /// as one undo entry — the exact mirror of <see cref="Unlink"/>. Only the selected clips are
    /// re-pointed; an unselected companion of a previously-linked member keeps its old group. Returns
    /// <see langword="null"/> when the selection is ineligible (see <see cref="CanLink"/>).
    /// </summary>
    public static IEditCommand? LinkAll(Timeline timeline, IEnumerable<Clip> clips)
    {
        if (!CanLink(timeline, clips))
            return null;
        Guid group = Guid.NewGuid();
        var commands = ExpandWithLinked(timeline, clips, linked: false)
            .Select(m =>
            {
                Clip c = m.Clip;
                return (IEditCommand)SetPropertyCommand<Guid?>.Create(
                    "Link", () => c.LinkGroupId, v => c.LinkGroupId = v, group);
            })
            .ToList();
        return new CompositeCommand("Link clips", commands);
    }

    /// <summary>
    /// Unlinks <paramref name="clip"/> and its companions (clears the whole group's link ids) as one undo
    /// entry (PLAN.md step 13). Returns <see langword="null"/> when the clip isn't linked.
    /// </summary>
    public static IEditCommand? Unlink(Timeline timeline, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.LinkGroupId is null)
            return null;
        var members = new List<Clip> { clip };
        members.AddRange(timeline.ClipsLinkedTo(clip).Select(l => l.Clip));
        var commands = members
            .Select(c => (IEditCommand)SetPropertyCommand<Guid?>.Create(
                "Unlink", () => c.LinkGroupId, v => c.LinkGroupId = v, null))
            .ToList();
        return commands.Count == 1 ? commands[0] : new CompositeCommand("Unlink clips", commands);
    }

    /// <summary>
    /// The Ctrl+L toggle (PLAN.md step 55, the Premiere/Resolve shortcut): links the selection when
    /// eligible, otherwise unlinks <paramref name="primary"/>'s group. Returns <see langword="null"/>
    /// when neither applies.
    /// </summary>
    public static IEditCommand? ToggleLink(Timeline timeline, Clip? primary, IEnumerable<Clip> clips) =>
        LinkAll(timeline, clips) ?? (primary is null ? null : Unlink(timeline, primary));

    /// <summary>
    /// Toggles <paramref name="clip"/>'s <see cref="Clip.Enabled"/> flag (Shift+E, Premiere's convention). With
    /// <paramref name="linked"/> on, companion clips are set to the same new state — the whole group toggles
    /// together rather than each member flipping independently — as one undo entry.
    /// </summary>
    public static IEditCommand ToggleEnabled(Timeline timeline, Clip clip, bool linked)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);

        bool target = !clip.Enabled;
        var members = new List<Clip> { clip };
        if (linked)
            members.AddRange(timeline.ClipsLinkedTo(clip).Select(l => l.Clip));

        string name = target ? "Enable clips" : "Disable clips";
        var commands = members
            .Select(c => (IEditCommand)SetPropertyCommand<bool>.Create(
                name, () => c.Enabled, v => c.Enabled = v, target))
            .ToList();
        return commands.Count == 1 ? commands[0] : new CompositeCommand(name, commands);
    }
}
