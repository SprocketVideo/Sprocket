using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Commands;

/// <summary>
/// Sets a sequence's <see cref="Sequence.SourcePatch"/> (PLAN.md step 61 phase 3) — moving a <c>V1</c>/<c>A1</c>
/// source indicator or un-patching a stream — as one undo entry. Editing state only; it never touches the render graph.
/// </summary>
public sealed class SetSourcePatchCommand : EditCommand
{
    private readonly Sequence _sequence;
    private readonly SourcePatch? _old;
    private readonly SourcePatch? _new;

    /// <summary>Captures the sequence's current patch and the patch to set (<see langword="null"/> = default).</summary>
    public SetSourcePatchCommand(Sequence sequence, SourcePatch? patch, string label = "Source patch")
        : base(label)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence;
        _old = sequence.SourcePatch;
        _new = patch;
    }

    /// <inheritdoc />
    public override void Apply() => _sequence.SourcePatch = _new;

    /// <inheritdoc />
    public override void Revert() => _sequence.SourcePatch = _old;
}

/// <summary>
/// A resolved three-point edit: the source span to cut in and where it lands in the sequence, plus any notes the
/// shell should surface (a 4-point edit's ignored source Out, a short source).
/// </summary>
/// <param name="SourceIn">The source in point (media time).</param>
/// <param name="SourceOut">The source out point (media time, exclusive).</param>
/// <param name="RecordIn">Where the edit starts in the sequence.</param>
/// <param name="Notes">User-facing notes about how the marks were applied; empty for a plain edit.</param>
public sealed record ThreePointRange(Timecode SourceIn, Timecode SourceOut, Timecode RecordIn, IReadOnlyList<string> Notes)
{
    /// <summary>The edit's length (source and record — three-point edits run at normal speed).</summary>
    public Timecode Duration => SourceOut - SourceIn;

    /// <summary>Where the edit ends in the sequence (exclusive).</summary>
    public Timecode RecordOut => RecordIn + Duration;
}

/// <summary>
/// Resolves the four marks of a three-point edit (PLAN.md step 61 phase 3) with Premiere's precedence: the sequence
/// marks fix where and how long, the source marks fix what. Pure, so every combination is table-tested.
/// </summary>
public static class ThreePointResolver
{
    /// <summary>Note for a 4-point edit: the source In and the sequence range win (Premiere's "Ignore Source Out").</summary>
    public const string SourceOutIgnoredNote = "4-point edit: the source Out was ignored";

    /// <summary>Note when the source can't fill the range the marks ask for, so the edit was shortened.</summary>
    public const string InsufficientSourceNote = "Insufficient source media: the edit was shortened";

    /// <summary>
    /// Resolves the edit. The source range is [<paramref name="srcIn"/> ?? 0, <paramref name="srcOut"/> ??
    /// <paramref name="srcLength"/>); an empty or inverted range (stale marks) falls back to the whole media. Then:
    /// <list type="bullet">
    /// <item>Sequence In and Out set: the edit fills [seqIn, seqOut). With only the source Out marked it's backtimed
    /// from that Out; otherwise it runs from the source In and the source Out is ignored (noted when both were set).</item>
    /// <item>Only sequence In: starts there, source duration.</item>
    /// <item>Only sequence Out: backtimed so it ends there.</item>
    /// <item>No sequence marks: starts at <paramref name="playhead"/>, source duration.</item>
    /// </list>
    /// A range longer than the media available is clamped to it (noted), unless <paramref name="unbounded"/> (a
    /// still, which has any length). Returns <see langword="null"/> when nothing is left to edit.
    /// </summary>
    public static ThreePointRange? Resolve(
        Timecode? srcIn, Timecode? srcOut, Timecode srcLength, bool unbounded,
        Timecode? seqIn, Timecode? seqOut, Timecode playhead)
    {
        if (srcLength <= Timecode.Zero)
            return null;
        var notes = new List<string>();

        Timecode sIn = srcIn is { } i && i > Timecode.Zero ? i : Timecode.Zero;
        Timecode sOut = srcOut ?? srcLength;
        if (!unbounded && sOut > srcLength)
            sOut = srcLength;
        bool markedIn = srcIn is not null, markedOut = srcOut is not null;
        if (sOut <= sIn)
        {
            (sIn, sOut) = (Timecode.Zero, srcLength);
            markedIn = markedOut = false;
        }

        if (seqOut is { } so && seqIn is { } si0 && so <= si0)
            seqOut = null; // an inverted sequence range is treated as an In alone

        Timecode recordIn;
        if (seqIn is { } si)
        {
            recordIn = si;
            if (seqOut is { } sOutSeq)
            {
                Timecode dur = sOutSeq - si;
                if (markedOut && !markedIn)
                {
                    // Backtime from the source Out; a short head clamps the start and shortens the edit.
                    sIn = sOut - dur;
                    if (sIn < Timecode.Zero)
                    {
                        sIn = Timecode.Zero;
                        notes.Add(InsufficientSourceNote);
                    }
                }
                else
                {
                    if (markedIn && markedOut)
                        notes.Add(SourceOutIgnoredNote);
                    sOut = sIn + dur;
                    if (!unbounded && sOut > srcLength)
                    {
                        sOut = srcLength;
                        notes.Add(InsufficientSourceNote);
                    }
                }
            }
        }
        else if (seqOut is { } backFrom)
        {
            recordIn = backFrom - (sOut - sIn);
            if (recordIn < Timecode.Zero)
            {
                // The edit would start before the sequence does: trim the source head so it still ends at the Out.
                sIn += -recordIn;
                recordIn = Timecode.Zero;
            }
        }
        else
        {
            recordIn = playhead < Timecode.Zero ? Timecode.Zero : playhead;
        }

        return sOut > sIn ? new ThreePointRange(sIn, sOut, recordIn, notes) : null;
    }

    /// <summary>
    /// Resolves an edit of <paramref name="media"/> into <paramref name="sequence"/> from the stored marks — the
    /// media's Source-monitor marks and the sequence's in/out marks — and the playhead.
    /// </summary>
    public static ThreePointRange? Resolve(MediaRef media, Sequence sequence, Timecode playhead)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(sequence);
        return Resolve(media.SourceMarkIn, media.SourceMarkOut, media.Info.Duration, media.HasUnboundedDuration,
            sequence.MarkIn, sequence.MarkOut, playhead);
    }
}

/// <summary>Which three-point edit to build.</summary>
public enum ThreePointEditKind
{
    /// <summary>Insert (<c>,</c>): push everything at and after the record In right to make room.</summary>
    Insert,

    /// <summary>Overwrite (<c>.</c>): replace whatever the destination tracks hold in the record range.</summary>
    Overwrite,
}

/// <summary>
/// The outcome of a <see cref="ThreePointEdits"/> build: the one undoable command and where the new clip ends (the
/// playhead parks there, as in leading editors), or an <see cref="Error"/> saying why the edit was refused.
/// </summary>
public sealed record ThreePointEditResult(
    IEditCommand? Command, string? Error, Timecode RecordIn, Timecode RecordOut, Clip? PrimaryClip,
    IReadOnlyList<string> Notes)
{
    internal static ThreePointEditResult Fail(string error) =>
        new(null, error, Timecode.Zero, Timecode.Zero, null, []);
}

/// <summary>
/// The three-point Insert and Overwrite edits (PLAN.md step 61 phase 3), with Premiere's rules. The source streams
/// land on the destination tracks — the sequence's <see cref="Sequence.SourcePatch"/> unless given explicitly.
/// <b>Overwrite</b> carves the record range out of the destination tracks only (no ripple) and adds the clips.
/// <b>Insert</b> blades every destination and sync-locked track at the record In, shifts everything at or after it —
/// clips, transition cuts, and sequence markers — right by the edit's length, and adds the clips. Locked tracks are
/// never touched; a locked destination refuses the edit. Both clear the sequence marks, and each is one
/// <see cref="CompositeCommand"/>, so the gesture is one undo entry.
/// </summary>
public static class ThreePointEdits
{
    /// <summary>Insert: see <see cref="Build"/>.</summary>
    public static ThreePointEditResult Insert(
        Sequence sequence, MediaRef media, ThreePointRange range,
        VideoTrack? videoTrack = null, AudioTrack? audioTrack = null) =>
        Build(ThreePointEditKind.Insert, sequence, media, range, videoTrack, audioTrack, usePatch: true);

    /// <summary>Overwrite: see <see cref="Build"/>.</summary>
    public static ThreePointEditResult Overwrite(
        Sequence sequence, MediaRef media, ThreePointRange range,
        VideoTrack? videoTrack = null, AudioTrack? audioTrack = null) =>
        Build(ThreePointEditKind.Overwrite, sequence, media, range, videoTrack, audioTrack, usePatch: true);

    /// <summary>
    /// Fit to Fill (PLAN.md step 61 phase 6; Resolve's <c>Shift+F11</c>): a 4-point Overwrite that retimes the source's
    /// marked range (the whole media for a missing mark) to exactly fill the sequence In–Out, at the constant speed
    /// source ÷ sequence. It needs both sequence marks, and refuses a speed outside the retime limits
    /// [<see cref="SpeedRamp.MinSpeed"/>, <see cref="SpeedRamp.MaxSpeed"/>]. A still has any length, so it simply
    /// fills the range at normal speed. Lands on the patched tracks and clears the sequence marks, like Overwrite.
    /// </summary>
    public static ThreePointEditResult FitToFill(Sequence sequence, MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(media);
        if (sequence.MarkIn is not { } seqIn || sequence.MarkOut is not { } seqOut || seqOut <= seqIn)
            return ThreePointEditResult.Fail("Fit to Fill needs a sequence In and Out");
        Timecode seqDur = seqOut - seqIn;
        if (media.HasUnboundedDuration)
        {
            Timecode stillIn = media.SourceMarkIn ?? Timecode.Zero;
            return Build(ThreePointEditKind.Overwrite, sequence, media,
                new ThreePointRange(stillIn, stillIn + seqDur, seqIn, []), null, null, usePatch: true);
        }
        Timecode length = media.Info.Duration;
        if (length <= Timecode.Zero)
            return ThreePointEditResult.Fail("The source range is empty");

        Timecode sIn = media.SourceMarkIn is { } i && i > Timecode.Zero ? i : Timecode.Zero;
        Timecode sOut = media.SourceMarkOut is { } o && o < length ? o : length;
        if (sOut <= sIn)
            (sIn, sOut) = (Timecode.Zero, length);

        Rational speed = SpeedOf(sOut - sIn, seqDur);
        double fraction = (double)speed.Num / speed.Den;
        if (fraction < SpeedRamp.MinSpeed || fraction > SpeedRamp.MaxSpeed)
            return ThreePointEditResult.Fail(
                $"Fit to Fill would need {fraction * 100:0.#}% speed, outside the {SpeedRamp.MinSpeed * 100:0.#}%–" +
                $"{SpeedRamp.MaxSpeed * 100:0.#}% limit");
        var notes = new List<string>();
        if (speed != Rational.One)
            notes.Add($"retimed to {fraction * 100:0.##}%");
        ThreePointEditResult result = Build(ThreePointEditKind.Overwrite, sequence, media,
            new ThreePointRange(sIn, sOut, seqIn, []), null, null, usePatch: true, speed: speed);
        return result.Command is null ? result : result with { Notes = [.. result.Notes, .. notes] };
    }

    // The exact speed source ÷ record as a reduced rational. Tick spans of frame-aligned marks reduce to small
    // numbers; a span too long to fit an int after reducing is scaled down (off by at most a tick in length).
    private static Rational SpeedOf(Timecode source, Timecode record)
    {
        long num = source.Ticks, den = record.Ticks;
        long g = Gcd(num, den);
        num /= g;
        den /= g;
        if (num > int.MaxValue || den > int.MaxValue)
        {
            long k = (Math.Max(num, den) + int.MaxValue - 1) / int.MaxValue;
            num = Math.Max(1, num / k);
            den = Math.Max(1, den / k);
        }
        return new Rational((int)num, (int)den);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return Math.Max(1, a);
    }

    /// <summary>
    /// Builds an Insert or Overwrite of <paramref name="media"/>'s <paramref name="range"/>. With
    /// <paramref name="usePatch"/> a null destination falls back to the sequence's patched track for that stream; without
    /// it a null destination leaves that stream out (a video-only or audio-only edit). A source stream with no
    /// destination is left out; if no stream has one, or a destination is locked, the edit is refused.
    /// <paramref name="linked"/> gives the new A/V pair a shared link group. <paramref name="clearSequenceMarks"/> clears
    /// the sequence marks as part of the edit — the keyed edits do (they consumed the marks); a timeline drop, which
    /// never reads them, passes <see langword="false"/> (PLAN.md step 61 phase 4). <paramref name="speed"/> retimes
    /// the new clips to that constant speed, so the edit's sequence length is the source span ÷ speed (Fit to Fill,
    /// phase 6); <see langword="null"/> is normal speed.
    /// </summary>
    public static ThreePointEditResult Build(
        ThreePointEditKind kind, Sequence sequence, MediaRef media, ThreePointRange range,
        VideoTrack? videoTrack, AudioTrack? audioTrack, bool usePatch,
        bool linked = true, bool clearSequenceMarks = true, Rational? speed = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(range);

        if (usePatch)
        {
            (VideoTrack? pv, AudioTrack? pa) = sequence.ResolvePatch();
            videoTrack ??= pv;
            audioTrack ??= pa;
        }
        Timeline timeline = sequence.Timeline;
        VideoTrack? vDest = media.Info.HasVideo && videoTrack is not null && timeline.Tracks.Contains(videoTrack)
            ? videoTrack : null;
        AudioTrack? aDest = media.Info.HasAudio && audioTrack is not null && timeline.Tracks.Contains(audioTrack)
            ? audioTrack : null;
        if (vDest is null && aDest is null)
            return ThreePointEditResult.Fail("No track is patched for this source");
        if (vDest is { Locked: true } || aDest is { Locked: true })
        {
            Track locked = vDest is { Locked: true } ? vDest : aDest!;
            return ThreePointEditResult.Fail($"The destination track \"{locked.Name}\" is locked");
        }
        if (range.Duration <= Timecode.Zero)
            return ThreePointEditResult.Fail("The source range is empty");

        string label = speed is not null ? "Fit to Fill" : kind == ThreePointEditKind.Insert ? "Insert" : "Overwrite";
        Timecode length = speed is { } s ? range.Duration.Scale(s.Inverse()) : range.Duration;
        Timecode recordIn = range.RecordIn, recordOut = recordIn + length;
        var destinations = new List<Track>(2);
        if (vDest is not null)
            destinations.Add(vDest);
        if (aDest is not null)
            destinations.Add(aDest);

        var commands = new List<IEditCommand>();
        var tailGroups = new Dictionary<Guid, Guid>();
        var notes = new List<string>(range.Notes);
        if (kind == ThreePointEditKind.Overwrite)
        {
            foreach (Track track in destinations)
                RangeEdits.CarveTrack(track, recordIn, recordOut, ripple: false, label, commands, tailGroups);
        }
        else
        {
            int lockedSkipped = 0;
            foreach (Track track in timeline.Tracks)
            {
                bool destination = destinations.Contains(track);
                if (!destination && !track.SyncLocked)
                    continue;
                if (track.Locked)
                {
                    if (track.Clips.Any(c => c.TimelineEnd > recordIn))
                        lockedSkipped++;
                    continue;
                }
                RippleOpen(track, recordIn, length, label, commands, tailGroups);
            }
            foreach (Marker marker in timeline.Markers)
                if (marker.Time >= recordIn)
                    commands.Add(new MoveMarkerCommand(marker, marker.Time + length));
            if (lockedSkipped > 0)
                notes.Add(lockedSkipped == 1
                    ? "1 locked track didn't shift and is now out of sync"
                    : $"{lockedSkipped} locked tracks didn't shift and are now out of sync");
        }

        (Clip? videoClip, Clip? audioClip) = SourceClips.Create(
            media, range.SourceIn, range.SourceOut, recordIn, vDest is not null, aDest is not null, linked);
        if (speed is { } retime)
        {
            // New clips, not yet in the model: retime them directly rather than through a speed command.
            if (videoClip is not null)
                videoClip.SpeedRatio = retime;
            if (audioClip is not null)
                audioClip.SpeedRatio = retime;
        }
        if (videoClip is not null)
            commands.Add(new AddClipCommand(vDest!, videoClip));
        if (audioClip is not null)
            commands.Add(new AddClipCommand(aDest!, audioClip));
        if (clearSequenceMarks)
            commands.Add(new SetSequenceMarksCommand(sequence, null, null, label));

        return new ThreePointEditResult(
            new CompositeCommand(label, commands), null, recordIn, recordOut, videoClip ?? audioClip, notes);
    }

    // Opens a gap of `length` at `at` on one track: a clip straddling it is bladed (its right half takes a fresh
    // link group shared with the other right halves of its group), and every clip at or after it shifts right.
    // A transition whose window covers the cut point — or whose cut sits on it — can't survive the gap and is
    // removed; later transition cuts shift with their clips.
    private static void RippleOpen(
        Track track, Timecode at, Timecode length, string label,
        List<IEditCommand> commands, Dictionary<Guid, Guid> tailGroups)
    {
        var shifted = new List<(Clip Clip, Timecode OrigStart)>();
        foreach (Clip clip in track.Clips.ToList())
        {
            if (clip.TimelineEnd <= at)
                continue;
            if (clip.TimelineStart >= at)
            {
                shifted.Add((clip, clip.TimelineStart));
                continue;
            }
            Guid? tailGroup = clip.LinkGroupId is { } g
                ? tailGroups.TryGetValue(g, out Guid fresh) ? fresh : tailGroups[g] = Guid.NewGuid()
                : null;
            var split = new SplitClipCommand(track, clip, at, tailGroup);
            commands.Add(split);
            shifted.Add((split.RightClip, at));
        }

        foreach (Transition transition in track.Transitions.ToList())
        {
            if (transition.CutPoint == at || (transition.Start < at && transition.End > at))
            {
                commands.Add(new RemoveTransitionCommand(track, transition));
            }
            else if (transition.CutPoint > at)
            {
                Transition t = transition;
                commands.Add(SetPropertyCommand<Timecode>.Create(
                    label, () => t.CutPoint, v => t.CutPoint = v, t.CutPoint + length));
            }
        }

        if (shifted.Count > 0)
            commands.Add(new ShiftClipsCommand(shifted, length.Ticks, label));
    }
}
