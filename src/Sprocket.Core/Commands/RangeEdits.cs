using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Commands;

/// <summary>
/// Sets a sequence's in/out marks (the I / O keys) together, as one undo entry. Both are set at once so a gesture
/// that moves one mark and drops the other (an in placed past the out) or sets both (Mark Clip) is atomic. Marks
/// are undoable and dirty the project, as in leading editors, but never touch the render graph.
/// </summary>
public sealed class SetSequenceMarksCommand : EditCommand
{
    private readonly Sequence _sequence;
    private readonly Timecode? _oldIn, _oldOut;
    private readonly Timecode? _newIn, _newOut;

    /// <summary>Captures the sequence's current marks and the marks to set (<see langword="null"/> clears).</summary>
    public SetSequenceMarksCommand(Sequence sequence, Timecode? markIn, Timecode? markOut, string label = "Set In/Out")
        : base(label)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence;
        _oldIn = sequence.MarkIn;
        _oldOut = sequence.MarkOut;
        _newIn = markIn;
        _newOut = markOut;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _sequence.MarkIn = _newIn;
        _sequence.MarkOut = _newOut;
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _sequence.MarkIn = _oldIn;
        _sequence.MarkOut = _oldOut;
    }
}

/// <summary>
/// Sets a media item's Source-monitor in/out marks together, as one undo entry (PLAN.md step 61 phase 2) — the
/// source-side twin of <see cref="SetSequenceMarksCommand"/>. The marks live on the <see cref="MediaRef"/> (the bin
/// item), are undoable and dirty the project, and never touch the render graph.
/// </summary>
public sealed class SetSourceMarksCommand : EditCommand
{
    private readonly MediaRef _media;
    private readonly Timecode? _oldIn, _oldOut;
    private readonly Timecode? _newIn, _newOut;

    /// <summary>Captures the media's current marks and the marks to set (<see langword="null"/> clears).</summary>
    public SetSourceMarksCommand(MediaRef media, Timecode? markIn, Timecode? markOut, string label = "Set Source In/Out")
        : base(label)
    {
        ArgumentNullException.ThrowIfNull(media);
        _media = media;
        _oldIn = media.SourceMarkIn;
        _oldOut = media.SourceMarkOut;
        _newIn = markIn;
        _newOut = markOut;
    }

    /// <inheritdoc />
    public override void Apply()
    {
        _media.SourceMarkIn = _newIn;
        _media.SourceMarkOut = _newOut;
    }

    /// <inheritdoc />
    public override void Revert()
    {
        _media.SourceMarkIn = _oldIn;
        _media.SourceMarkOut = _oldOut;
    }
}

/// <summary>
/// The outcome of a <see cref="RangeEdits"/> Lift / Extract: the one undoable command, plus what the edit had to
/// leave alone so the shell can say so — <see cref="SyncBreaks"/> sync-locked tracks that couldn't ripple because
/// they hold material inside the range (they stay put, so they drift out of sync, as in leading editors) and
/// <see cref="LockedTracksInRange"/> targeted-but-locked tracks whose material in the range was kept.
/// </summary>
public sealed record RangeEditResult(IEditCommand Command, int SyncBreaks, int LockedTracksInRange);

/// <summary>
/// The range edits over a sequence's in/out marks: <b>Lift</b> (<c>;</c>) removes everything in [in, out) on the
/// <see cref="Track.Targeted"/> tracks and leaves a gap; <b>Extract</b> (<c>'</c>) removes it and ripples
/// everything downstream left to close the gap — the names, keys, and track-targeting scope of leading editors.
/// Clips straddling a mark are bladed at it (<see cref="SplitClipCommand"/>) so only the in-range piece goes;
/// transitions whose cut falls inside the range are removed, and on Extract the downstream ones shift with their
/// clips. Extract also shifts every <see cref="Track.SyncLocked"/> track that isn't targeted, so it stays in sync;
/// one that holds material inside the range can't close a gap it doesn't have and is left alone (a sync break).
/// <see cref="Track.Locked"/> tracks are never touched. Both clear the marks afterwards (as leading editors do)
/// and build a single <see cref="CompositeCommand"/>, so each gesture is one undo entry.
/// </summary>
public static class RangeEdits
{
    /// <summary>Lift: removes [<paramref name="markIn"/>, <paramref name="markOut"/>) from the targeted tracks,
    /// leaving a gap. Returns <see langword="null"/> when the range is empty or holds nothing to remove.</summary>
    public static RangeEditResult? Lift(Sequence sequence, Timecode markIn, Timecode markOut) =>
        Build(sequence, markIn, markOut, ripple: false);

    /// <summary>Extract: removes [<paramref name="markIn"/>, <paramref name="markOut"/>) from the targeted tracks
    /// and ripples everything after it — on those and the sync-locked tracks — left by the range's length.
    /// Returns <see langword="null"/> when the range is empty or nothing lies in or after it.</summary>
    public static RangeEditResult? Extract(Sequence sequence, Timecode markIn, Timecode markOut) =>
        Build(sequence, markIn, markOut, ripple: true);

    /// <summary>Whether any track is targeted and unlocked — with none, Lift / Extract have nothing to act on.</summary>
    public static bool HasEditableTarget(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return timeline.Tracks.Any(t => t.Targeted && !t.Locked);
    }

    /// <summary>
    /// The span Mark Clip (<c>X</c>) marks: the clip under <paramref name="at"/> on the topmost targeted video track
    /// that has one, else on the first targeted audio track that does — leading editors mark from the targeted
    /// tracks. <see langword="null"/> when no targeted track has a clip under the time.
    /// </summary>
    public static (Timecode In, Timecode Out)? ClipSpanAt(Timeline timeline, Timecode at)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        foreach (VideoTrack track in timeline.VideoTracks.Reverse())
            if (track.Targeted && track.ResolveActiveClip(at) is { } clip)
                return (clip.TimelineStart, clip.TimelineEnd);
        foreach (AudioTrack track in timeline.AudioTracks)
            if (track.Targeted && track.ResolveActiveClip(at) is { } clip)
                return (clip.TimelineStart, clip.TimelineEnd);
        return null;
    }

    /// <summary>The span Mark Selection (<c>/</c>) marks: from the earliest selected clip start to the latest end,
    /// or <see langword="null"/> for an empty selection.</summary>
    public static (Timecode In, Timecode Out)? SelectionSpan(IEnumerable<Clip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        Timecode? first = null, last = null;
        foreach (Clip clip in clips)
        {
            first = first is { } f ? Timecode.Min(f, clip.TimelineStart) : clip.TimelineStart;
            last = last is { } l ? Timecode.Max(l, clip.TimelineEnd) : clip.TimelineEnd;
        }
        return first is { } a && last is { } b ? (a, b) : null;
    }

    private static RangeEditResult? Build(Sequence sequence, Timecode markIn, Timecode markOut, bool ripple)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (markIn < Timecode.Zero)
            markIn = Timecode.Zero;
        if (markOut <= markIn)
            return null;

        string label = ripple ? "Extract" : "Lift";
        var commands = new List<IEditCommand>();
        bool changed = false;
        int syncBreaks = 0, lockedInRange = 0;
        // A tail cut off at the out mark takes a fresh link group shared with the other tails of its original
        // group, so the surviving A/V after the range stays linked independently of the pieces before it.
        var tailGroups = new Dictionary<Guid, Guid>();

        foreach (Track track in sequence.Timeline.Tracks)
        {
            bool inRange = track.Clips.Any(c => c.TimelineEnd > markIn && c.TimelineStart < markOut);
            if (track.Locked)
            {
                if (track.Targeted && inRange)
                    lockedInRange++;
                continue;
            }
            bool carve = track.Targeted;
            if (!carve)
            {
                // A sync-locked track outside the target set only follows the ripple; one with material inside the
                // range has no gap to close, so it keeps its place (the edit reports the sync break).
                if (!ripple || !track.SyncLocked)
                    continue;
                if (inRange)
                {
                    syncBreaks++;
                    continue;
                }
            }

            if (CarveTrack(track, markIn, markOut, ripple, label, commands, tailGroups))
                changed = true;
        }

        if (!changed)
            return null;
        commands.Add(new SetSequenceMarksCommand(sequence, null, null, label));
        return new RangeEditResult(new CompositeCommand(label, commands), syncBreaks, lockedInRange);
    }

    /// <summary>
    /// Carves [<paramref name="markIn"/>, <paramref name="markOut"/>) out of one track, appending the commands to
    /// <paramref name="commands"/>: straddling clips are bladed at the marks (a tail cut off at the out mark takes a
    /// fresh link group from <paramref name="tailGroups"/>, shared with the other tails of its original group), the
    /// in-range pieces removed, and transitions whose cut falls in the range removed. With <paramref name="ripple"/>
    /// everything after the range — clips and transition cuts — shifts left by its length. Returns whether anything
    /// changed. Shared by Lift / Extract and the three-point Overwrite (<see cref="ThreePointEdits"/>).
    /// </summary>
    internal static bool CarveTrack(
        Track track, Timecode markIn, Timecode markOut, bool ripple, string label,
        List<IEditCommand> commands, Dictionary<Guid, Guid> tailGroups)
    {
        long gap = (markOut - markIn).Ticks;
        bool changed = false;
        var shifted = new List<(Clip Clip, Timecode OrigStart)>();
        foreach (Clip clip in track.Clips.ToList())
        {
            if (clip.TimelineEnd <= markIn)
                continue;
            if (clip.TimelineStart >= markOut)
            {
                if (ripple)
                    shifted.Add((clip, clip.TimelineStart));
                continue;
            }

            // The clip overlaps [in, out): carve the in-range piece out with at most two blades.
            Clip middle = clip;
            if (clip.TimelineStart < markIn)
            {
                var splitIn = new SplitClipCommand(track, clip, markIn);
                commands.Add(splitIn);
                middle = splitIn.RightClip;
            }
            if (middle.TimelineEnd > markOut)
            {
                Guid? tailGroup = clip.LinkGroupId is { } g
                    ? tailGroups.TryGetValue(g, out Guid fresh) ? fresh : tailGroups[g] = Guid.NewGuid()
                    : null;
                var splitOut = new SplitClipCommand(track, middle, markOut, tailGroup);
                commands.Add(splitOut);
                if (ripple)
                    shifted.Add((splitOut.RightClip, markOut));
            }
            commands.Add(new RemoveClipCommand(track, middle));
            changed = true;
        }

        foreach (Transition transition in track.Transitions.ToList())
        {
            if (transition.CutPoint >= markIn && transition.CutPoint <= markOut)
            {
                commands.Add(new RemoveTransitionCommand(track, transition));
                changed = true;
            }
            else if (ripple && transition.CutPoint > markOut)
            {
                Transition t = transition;
                commands.Add(SetPropertyCommand<Timecode>.Create(
                    label, () => t.CutPoint, v => t.CutPoint = v, new Timecode(t.CutPoint.Ticks - gap)));
            }
        }

        if (shifted.Count > 0)
        {
            commands.Add(new ShiftClipsCommand(shifted, -gap, label));
            changed = true;
        }
        return changed;
    }
}
