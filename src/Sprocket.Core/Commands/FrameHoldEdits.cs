using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Commands;

/// <summary>
/// Builders for the frame-level retime gestures (PLAN.md step 43): Add Frame Hold (split at the playhead, the
/// right half becomes a freeze), Insert Frame Hold Segment (insert a freeze and ripple downstream), and the
/// stop-motion frame edits Duplicate Frame / Remove Frame. Each is a pure <see cref="CompositeCommand"/> over
/// the existing blade (<see cref="SplitClipCommand"/>) + ripple (<see cref="ShiftClipsCommand"/>) primitives, so
/// every gesture is one undo entry. Frame edits snap to the exact <em>source</em>-frame grid via
/// <see cref="Rational"/> math (correct on NTSC rates). Like <see cref="RippleTrimCommand"/>, the downstream
/// set is captured by the caller (it owns track/link scope — video ripples must push companion audio too,
/// PLAN.md step 22) and passed in; clips these composites create are added to the shift internally.
/// </summary>
public static class FrameHoldEdits
{
    /// <summary>The exact span of one source frame, mapped onto the timeline: the frame's source start (the
    /// time a freeze of it holds at) and its timeline boundaries <em>unclamped</em> to the clip.</summary>
    public readonly record struct FrameSpan(Timecode SourceFrameStart, Timecode TimelineStart, Timecode TimelineEnd);

    /// <summary>
    /// Locates the source frame under timeline time <paramref name="at"/> within <paramref name="clip"/> on the
    /// exact frame grid of <paramref name="frameRate"/> (the <em>source</em>'s rate): the frame's source start
    /// and its timeline boundaries (source boundaries mapped back through the clip's speed). Exact
    /// <see cref="Rational"/>/<see cref="Int128"/> math, so NTSC grids don't drift. Not valid for a held clip —
    /// its time map is constant, so no frame grid exists on its span.
    /// </summary>
    public static FrameSpan SourceFrameSpan(Clip clip, Timecode at, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (frameRate.Num <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameRate), "Frame rate must be strictly positive.");
        if (clip.IsHeld)
            throw new InvalidOperationException("A held clip has a constant time map — there is no frame grid on its span.");
        if (at < clip.TimelineStart || at >= clip.TimelineEnd)
            throw new ArgumentException("The time must lie inside the clip.", nameof(at));

        Timecode sourceT = clip.MapToSource(at);
        long frame = sourceT.ToFrameIndex(frameRate);
        Timecode s0 = Timecode.FromFrames(frame, frameRate);
        Timecode s1 = Timecode.FromFrames(frame + 1, frameRate);
        return new FrameSpan(s0, MapToTimeline(clip, s0), MapToTimeline(clip, s1));
    }

    /// <summary>
    /// <b>Add Frame Hold</b> (the naming used in leading editors): splits <paramref name="clip"/> at the playhead and freezes the
    /// right half at the playhead's source frame — playback runs normally up to <paramref name="at"/> and holds
    /// that frame for the rest of the clip's span. The clip's overall span is unchanged (no ripple), so linked
    /// audio stays aligned and keeps playing, matching the convention in leading editors. <paramref name="rightLinkGroup"/> is the fresh
    /// group a linked blade gives the right-hand halves (see <see cref="SplitClipCommand"/>).
    /// <paramref name="at"/> must lie strictly inside the clip (at the very start, hold the whole clip via
    /// <see cref="SetClipHoldCommand"/> / Frame Hold Options instead).
    /// </summary>
    /// <returns>The one-undo-entry composite and the frozen right half (e.g. to select it).</returns>
    public static (IEditCommand Command, Clip HeldClip) AddFrameHold(
        Track track, Clip clip, Timecode at, Guid? rightLinkGroup = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        Timecode holdAt = clip.MapToSource(at);
        Timecode holdDuration = clip.TimelineEnd - at;

        var split = new SplitClipCommand(track, clip, at, rightLinkGroup);
        var hold = new SetClipHoldCommand(split.RightClip, holdAt, holdDuration, "Add frame hold");
        return (new CompositeCommand("Add frame hold", [split, hold]), split.RightClip);
    }

    /// <summary>
    /// <b>Insert Frame Hold Segment</b> (the naming used in leading editors): splits <paramref name="clip"/> at the playhead (when
    /// strictly inside), inserts a <paramref name="holdDuration"/>-long freeze of the playhead's source frame,
    /// and ripples downstream by the same amount. <paramref name="downstream"/> is every clip that must shift
    /// right — clips starting at/after <paramref name="at"/> on the affected tracks (companion audio included),
    /// captured with their current starts and <em>excluding</em> <paramref name="clip"/> itself; the split's
    /// right half is added to the shift internally. The inserted segment carries the clip's content and a copy
    /// of its effect stack, but no link group (a freeze is standalone).
    /// </summary>
    /// <returns>The one-undo-entry composite and the inserted freeze segment.</returns>
    public static (IEditCommand Command, Clip HeldClip) InsertFrameHoldSegment(
        Track track, Clip clip, Timecode at, Timecode holdDuration,
        IReadOnlyList<(Clip Clip, Timecode OrigStart)> downstream, Guid? rightLinkGroup = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(downstream);
        if (holdDuration.Ticks <= 0)
            throw new ArgumentOutOfRangeException(nameof(holdDuration), "The hold duration must be strictly positive.");
        if (at < clip.TimelineStart || at >= clip.TimelineEnd)
            throw new ArgumentException("The insertion point must lie inside the clip.", nameof(at));

        return InsertFreeze(track, clip, at, clip.MapToSource(at), holdDuration, downstream, rightLinkGroup,
            "Insert frame hold segment");
    }

    /// <summary>
    /// <b>Duplicate Frame</b> (stop-motion frame edit): snaps the playhead to the source-frame grid, inserts a
    /// one-frame hold of that frame immediately after it, and ripples downstream by exactly one source frame's
    /// timeline span (+1 frame). Repeating it is per-frame "on twos/threes". <paramref name="frameRate"/> is
    /// the <em>source</em>'s frame rate; <paramref name="downstream"/> is captured by the caller as for
    /// <see cref="InsertFrameHoldSegment"/> (clips starting at/after the frame's timeline end, excluding
    /// <paramref name="clip"/>).
    /// </summary>
    /// <returns>The one-undo-entry composite and the inserted one-frame freeze.</returns>
    public static (IEditCommand Command, Clip HeldClip) DuplicateFrame(
        Track track, Clip clip, Timecode at, Rational frameRate,
        IReadOnlyList<(Clip Clip, Timecode OrigStart)> downstream, Guid? rightLinkGroup = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(downstream);

        FrameSpan span = SourceFrameSpan(clip, at, frameRate);
        // The freeze goes at the frame's end boundary, clamped into the clip (the last frame's grid end can map
        // past the clip's out-point when the trim isn't frame-aligned — the freeze then sits at the clip end).
        Timecode insertAt = Timecode.Min(span.TimelineEnd, clip.TimelineEnd);
        Timecode frameDuration = span.TimelineEnd - Timecode.Max(span.TimelineStart, clip.TimelineStart);

        return InsertFreeze(track, clip, insertAt, span.SourceFrameStart, frameDuration, downstream, rightLinkGroup,
            "Duplicate frame");
    }

    /// <summary>
    /// <b>Remove Frame</b> (stop-motion frame edit): extracts the timeline span of the source frame under the
    /// playhead (snapped to the source-frame grid, clamped to the clip) and ripple-closes the gap (−1 frame).
    /// <paramref name="downstream"/> is every clip starting at/after the removed span's end
    /// (<see cref="SourceFrameSpan"/> gives the span first), excluding <paramref name="clip"/>; halves this
    /// composite creates are handled internally. Distinct from ripple delete (PLAN.md step 22), which removes a
    /// whole selected clip's span — this removes one frame on the grid.
    /// </summary>
    /// <returns>The one-undo-entry composite.</returns>
    public static IEditCommand RemoveFrame(
        Track track, Clip clip, Timecode at, Rational frameRate,
        IReadOnlyList<(Clip Clip, Timecode OrigStart)> downstream, Guid? rightLinkGroup = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(downstream);

        FrameSpan span = SourceFrameSpan(clip, at, frameRate);
        Timecode from = Timecode.Max(span.TimelineStart, clip.TimelineStart);
        Timecode to = Timecode.Min(span.TimelineEnd, clip.TimelineEnd);
        long gap = (to - from).Ticks;

        var commands = new List<IEditCommand>();
        var shifted = new List<(Clip, Timecode)>(downstream);

        // Carve [from, to) out of the clip with at most two splits, then remove the middle piece. Which piece is
        // the middle depends on whether each boundary lies strictly inside the clip.
        Clip middle = clip;
        if (from > clip.TimelineStart)
        {
            var splitLeft = new SplitClipCommand(track, clip, from, rightLinkGroup);
            commands.Add(splitLeft);
            middle = splitLeft.RightClip;
        }
        if (to < clip.TimelineEnd)
        {
            var splitRight = new SplitClipCommand(track, middle, to, rightLinkGroup);
            commands.Add(splitRight);
            shifted.Add((splitRight.RightClip, to)); // the kept tail closes the gap with the downstream set
        }
        commands.Add(new RemoveClipCommand(track, middle));
        commands.Add(new ShiftClipsCommand(shifted, -gap, "Remove frame"));

        return new CompositeCommand("Remove frame", commands);
    }

    /// <summary>Shared assembly for the freeze-inserting gestures: optional split at the insertion point, shift
    /// downstream (plus the split's right half) right by the freeze length, insert the held segment.</summary>
    private static (IEditCommand Command, Clip HeldClip) InsertFreeze(
        Track track, Clip clip, Timecode at, Timecode holdAt, Timecode holdDuration,
        IReadOnlyList<(Clip Clip, Timecode OrigStart)> downstream, Guid? rightLinkGroup, string label)
    {
        var commands = new List<IEditCommand>();
        var shifted = new List<(Clip, Timecode)>(downstream);

        if (at > clip.TimelineStart && at < clip.TimelineEnd)
        {
            var split = new SplitClipCommand(track, clip, at, rightLinkGroup);
            commands.Add(split);
            shifted.Add((split.RightClip, at));
        }
        else if (at == clip.TimelineStart)
        {
            // Inserting at the clip's very start: nothing to split — the whole clip shifts right instead.
            shifted.Add((clip, clip.TimelineStart));
        }

        // The freeze carries the clip's content (same source, so un-hold degrades gracefully) and a copy of its
        // effect stack, held at the requested source frame for the requested span. No link group — standalone.
        Clip segment = clip.CloneContentForSpan(clip.SourceIn, clip.SourceOut, at);
        segment.HoldFrameAt = holdAt;
        segment.HoldDuration = holdDuration;
        foreach (EffectInstance e in clip.Effects)
            segment.Effects.Add(e.Clone());

        commands.Add(new ShiftClipsCommand(shifted, holdDuration.Ticks, label));
        commands.Add(new AddClipCommand(track, segment));

        return (new CompositeCommand(label, commands), segment);
    }

    // Maps a source time back onto the timeline through the clip's placement and speed — the inverse of
    // Clip.MapToSource for an unheld clip: t = TimelineStart + (s - SourceIn) / speed.
    private static Timecode MapToTimeline(Clip clip, Timecode sourceT) =>
        clip.TimelineStart + (sourceT - clip.SourceIn).Scale(clip.SpeedRatio.Inverse());
}
