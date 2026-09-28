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
/// The range edits over a sequence's in/out marks: <b>Lift</b> (<c>;</c>) removes everything in [in, out) on every
/// track and leaves a gap; <b>Extract</b> (<c>'</c>) removes it and ripples everything downstream left to close the
/// gap — the names and keys used by leading editors. Clips straddling a mark are bladed at it
/// (<see cref="SplitClipCommand"/>) so only the in-range piece goes; transitions whose cut falls inside the range
/// are removed, and on Extract the downstream ones shift with their clips. Both clear the marks afterwards (as
/// leading editors do) and build a single <see cref="CompositeCommand"/>, so each gesture is one undo entry.
/// </summary>
/// <remarks>
/// Deliberate departure: leading editors apply these to the <em>targeted</em> tracks; Sprocket has no track
/// targeting yet, so every track is affected (which also keeps linked A/V in sync across the cut).
/// </remarks>
public static class RangeEdits
{
    /// <summary>Lift: removes [<paramref name="markIn"/>, <paramref name="markOut"/>) from every track, leaving a
    /// gap. Returns <see langword="null"/> when the range is empty or holds nothing to remove.</summary>
    public static IEditCommand? Lift(Sequence sequence, Timecode markIn, Timecode markOut) =>
        Build(sequence, markIn, markOut, ripple: false);

    /// <summary>Extract: removes [<paramref name="markIn"/>, <paramref name="markOut"/>) from every track and
    /// ripples everything after it left by the range's length. Returns <see langword="null"/> when the range is
    /// empty or nothing lies in or after it.</summary>
    public static IEditCommand? Extract(Sequence sequence, Timecode markIn, Timecode markOut) =>
        Build(sequence, markIn, markOut, ripple: true);

    /// <summary>
    /// The span Mark Clip (<c>X</c>) marks: the clip under <paramref name="at"/> on the topmost video track that has
    /// one, else on the first audio track that does (leading editors use the targeted tracks; with no targeting,
    /// the visible picture's clip is the natural pick). <see langword="null"/> when no clip lies under the time.
    /// </summary>
    public static (Timecode In, Timecode Out)? ClipSpanAt(Timeline timeline, Timecode at)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        foreach (VideoTrack track in timeline.VideoTracks.Reverse())
            if (track.ResolveActiveClip(at) is { } clip)
                return (clip.TimelineStart, clip.TimelineEnd);
        foreach (AudioTrack track in timeline.AudioTracks)
            if (track.ResolveActiveClip(at) is { } clip)
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

    private static IEditCommand? Build(Sequence sequence, Timecode markIn, Timecode markOut, bool ripple)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (markIn < Timecode.Zero)
            markIn = Timecode.Zero;
        if (markOut <= markIn)
            return null;

        string label = ripple ? "Extract" : "Lift";
        long gap = (markOut - markIn).Ticks;
        var commands = new List<IEditCommand>();
        bool changed = false;
        // A tail cut off at the out mark takes a fresh link group shared with the other tails of its original
        // group, so the surviving A/V after the range stays linked independently of the pieces before it.
        var tailGroups = new Dictionary<Guid, Guid>();

        foreach (Track track in sequence.Timeline.Tracks)
        {
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
        }

        if (!changed)
            return null;
        commands.Add(new SetSequenceMarksCommand(sequence, null, null, label));
        return new CompositeCommand(label, commands);
    }
}
