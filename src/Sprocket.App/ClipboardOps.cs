using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// Pure logic for the Edit-menu clip clipboard (cut / copy / paste) and clip nudge (PLAN.md step 16c). Kept
/// free of Avalonia types so the copy/paste and clamp math are unit-testable headlessly; the menu wiring in
/// <see cref="MainWindow"/> and the public clip-edit methods on <see cref="Timeline.TimelineControl"/> rest on
/// this + manual verification (the App is a UI-bound WinExe). Cut / paste / delete / nudge all run through the
/// existing command stack (step 10), so every operation is undoable by construction.
/// </summary>
public static class ClipboardOps
{
    /// <summary>
    /// A detached deep copy of <paramref name="clip"/> for the clipboard: the effect stack is cloned (so later
    /// edits to the original don't bleed into the clipboard) and the link group is left cleared — a pasted clip
    /// is an independent copy, not a companion of the original's linked A/V pair (PLAN.md step 13). The source
    /// span and timeline start are preserved as a faithful snapshot; <see cref="Paste"/> chooses the placement.
    /// </summary>
    public static Clip Copy(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        // CloneContentForSpan keeps the clip's whole content nature — kind, generator, speed, gain, and any
        // frame hold (PLAN.md step 43) — not just the media reference.
        Clip copy = clip.CloneContentForSpan(clip.SourceIn, clip.SourceOut, clip.TimelineStart);
        foreach (EffectInstance e in clip.Effects)
            copy.Effects.Add(e.Clone());
        return copy; // LinkGroupId intentionally left null — a pasted clip is independent
    }

    /// <summary>
    /// Builds the clip to paste from a clipboard <paramref name="snapshot"/>, placed at timeline time
    /// <paramref name="at"/> (clamped to the origin). Effects are cloned again so repeated pastes stay
    /// independent of one another and of the snapshot.
    /// </summary>
    public static Clip Paste(Clip snapshot, Timecode at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long start = Math.Max(0, at.Ticks);
        Clip copy = snapshot.CloneContentForSpan(snapshot.SourceIn, snapshot.SourceOut, new Timecode(start));
        // Effect keyframe times are absolute timeline time (AnimatableValue.Evaluate is called with the playhead's
        // timeline time). A clip pasted at a new start must therefore shift its animated parameters by the
        // placement delta so they move with the clip — otherwise the copy's keyframes stay anchored to the
        // ORIGINAL clip's span. The default clip carries a fade in/out keyframed over its span, so a copy placed
        // past the original (e.g. Alt-drag-duplicate with a gap) would sit entirely beyond the fade-out's final
        // keyframe (opacity 0) and render fully transparent — a black clip. Shifting keeps the fade on the copy.
        Timecode shift = new Timecode(start - snapshot.TimelineStart.Ticks);
        foreach (EffectInstance e in snapshot.Effects)
            copy.Effects.Add(e.CloneShifted(shift));
        return copy;
    }

    /// <summary>The one-undo-entry paste command for a multi-clip clipboard, plus the pasted clips (the first
    /// — the copy of the clipboard's primary — becomes the selection).</summary>
    public readonly record struct PasteResult(IEditCommand Command, Clip Primary, IReadOnlyList<Clip> Pasted);

    /// <summary>
    /// Pastes a multi-clip clipboard at timeline time <paramref name="at"/> (PLAN.md step 54): the earliest
    /// snapshot lands at <paramref name="at"/> and the rest keep their relative offsets, video snapshots on
    /// <paramref name="videoTarget"/> and audio on <paramref name="audioTarget"/> — the single-clip
    /// paste-at-playhead convention (step 16c) applied to the set. Snapshots whose target track kind is
    /// missing are skipped; returns <see langword="null"/> when nothing can be placed. Effects are re-cloned
    /// per paste via <see cref="Paste"/>, so repeated pastes stay independent.
    /// </summary>
    public static PasteResult? PasteAll(
        IReadOnlyList<(Clip Snapshot, bool IsVideo)> clipboard, Timecode at,
        Track? videoTarget, Track? audioTarget)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        var placeable = clipboard
            .Where(e => (e.IsVideo ? videoTarget : audioTarget) is not null)
            .ToList();
        if (placeable.Count == 0)
            return null;

        long minStart = placeable.Min(e => e.Snapshot.TimelineStart.Ticks);
        long anchor = Math.Max(0, at.Ticks);

        var commands = new List<IEditCommand>();
        var pasted = new List<Clip>();
        foreach ((Clip snapshot, bool isVideo) in placeable)
        {
            Clip copy = Paste(snapshot, new Timecode(anchor + (snapshot.TimelineStart.Ticks - minStart)));
            commands.Add(new AddClipCommand(isVideo ? videoTarget! : audioTarget!, copy));
            pasted.Add(copy);
        }
        IEditCommand command = commands.Count == 1 ? commands[0] : new CompositeCommand("Paste clips", commands);
        return new PasteResult(command, pasted[0], pasted);
    }

    /// <summary>
    /// Clamps a nudge of <paramref name="deltaTicks"/> so the earliest clip in a moved group never crosses the
    /// timeline origin. <paramref name="groupMinStartTicks"/> is the smallest <see cref="Clip.TimelineStart"/>
    /// among the clips being moved; the returned delta is what every member shifts by (a left nudge can be
    /// shortened to keep the group at <c>t = 0</c>, a right nudge is unaffected).
    /// </summary>
    public static long ClampGroupNudge(long deltaTicks, long groupMinStartTicks) =>
        Math.Max(deltaTicks, -groupMinStartTicks);
}
