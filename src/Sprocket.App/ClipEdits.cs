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
