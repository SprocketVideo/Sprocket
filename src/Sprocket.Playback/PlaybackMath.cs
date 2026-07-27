using Sprocket.Core.Timing;

namespace Sprocket.Playback;

/// <summary>
/// The pure timing decisions the playback pump makes, factored out so they are unit-testable without any
/// decode/clock/threading (ARCHITECTURE.md §8). The pump turns the master clock's "now" into a timeline
/// position and decides, per decoded frame, whether to show it yet (hold), advance to it, or drop past it.
/// </summary>
internal static class PlaybackMath
{
    /// <summary>
    /// Clamps a raw clock position to the transport's navigable range <c>[0, <paramref name="end"/>]</c>.
    /// <paramref name="end"/> is the transport's <i>navigable</i> end, which is not always the content end: the
    /// sequence timeline of leading editors (Premiere, Resolve, Vegas — Final Cut is the outlier) lets the playhead
    /// park in the empty space past the last clip, so edits can be targeted there. The Source monitor's transport
    /// passes the media duration, since nothing exists past the end of a source file.
    /// </summary>
    public static Timecode ClampToTimeline(Timecode position, Timecode end)
    {
        if (position.Ticks < 0)
            return Timecode.Zero;
        return position > end ? end : position;
    }

    /// <summary>
    /// The timeline position <paramref name="delta"/> whole frames away from <paramref name="position"/> at
    /// <paramref name="fps"/>, clamped to <c>[0, <paramref name="end"/>]</c> (PLAN.md step 17 frame-step transport;
    /// see <see cref="ClampToTimeline"/> for what <paramref name="end"/> means). The current position is floored to
    /// its containing frame index first, so stepping is always on exact frame boundaries regardless of where a
    /// scrub left the playhead.
    /// </summary>
    public static Timecode StepFrame(Timecode position, Rational fps, int delta, Timecode end)
    {
        if (fps.Num <= 0 || fps.Den <= 0)
            return ClampToTimeline(position, end);

        long target = position.ToFrameIndex(fps) + delta;
        if (target < 0)
            target = 0;
        return ClampToTimeline(Timecode.FromFrames(target, fps), end);
    }

    /// <summary>Whether playback has reached the end of the timeline's <i>content</i> at
    /// <paramref name="position"/> — the auto-stop point, regardless of how far the transport may be navigated.</summary>
    public static bool ReachedEnd(Timecode position, Timecode duration) =>
        duration.Ticks > 0 && position >= duration;

    /// <summary>
    /// Whether the next decoded frame (at <paramref name="nextFramePts"/>, in source time) should become the
    /// presented frame given the playhead maps to <paramref name="targetSourceTime"/>. A frame is shown once
    /// its presentation time has been reached; the pump promotes greedily, so when it is behind it advances
    /// through (drops) every frame already due and lands on the last one ≤ the target (§8 frame drop).
    /// <paramref name="forcePresent"/> overrides this for the first frame after a seek, so a scrub lands on
    /// the freshly decoded frame even when it falls between sample points (its PTS just past the target).
    /// </summary>
    public static bool ShouldPromote(Timecode nextFramePts, Timecode targetSourceTime, bool forcePresent) =>
        forcePresent || nextFramePts <= targetSourceTime;
}
