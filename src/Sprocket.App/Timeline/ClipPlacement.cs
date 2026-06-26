using System;
using System.Collections.Generic;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// Pure logic for placing a new clip on the timeline by dragging a source from the media bin (PLAN.md step
/// 16b). Kept free of Avalonia types so the snapping + command-building are unit-testable headlessly; the
/// drag/drop plumbing in <see cref="MediaBrowser.MediaBrowserPanel"/> / <see cref="Timeline.TimelineControl"/>
/// rests on this and on manual verification. Placement reuses the existing <see cref="AddClipCommand"/> (and a
/// <see cref="CompositeCommand"/> with a shared link group for A/V sources, mirroring step 13), so a dropped
/// clip is undoable by construction (step 10).
/// </summary>
public static class ClipPlacement
{
    /// <summary>The command that places the clip(s) plus the clip to select afterwards.</summary>
    /// <param name="Command">The (possibly composite) command to run through the edit history.</param>
    /// <param name="PrimaryClip">The clip to select once placed (the dropped lane's clip).</param>
    public readonly record struct PlacementResult(IEditCommand Command, Clip PrimaryClip);

    /// <summary>
    /// Snaps a drop start (ticks) to nearby <paramref name="snapPoints"/> — and snaps the resulting clip end as
    /// well so a dropped clip butts cleanly against an existing edge — then clamps to the timeline origin.
    /// Returns the raw, clamped start when snapping is off or nothing is within tolerance.
    /// </summary>
    public static long SnapStart(
        long dropTicks, long clipDuration, IReadOnlyList<long> snapPoints,
        bool snapping, double tolerancePx, double pxPerSecond)
    {
        long start = TimelineMath.ClampNonNegative(dropTicks);
        if (!snapping || snapPoints.Count == 0)
            return start;

        long snappedStart = TimelineMath.Snap(start, snapPoints, tolerancePx, pxPerSecond);
        if (snappedStart != start)
            return TimelineMath.ClampNonNegative(snappedStart);

        // Try snapping the trailing edge so the clip lands flush to the left of an existing clip/playhead.
        long end = start + clipDuration;
        long snappedEnd = TimelineMath.Snap(end, snapPoints, tolerancePx, pxPerSecond);
        if (snappedEnd != end)
            return TimelineMath.ClampNonNegative(snappedEnd - clipDuration);

        return start;
    }

    /// <summary>
    /// Builds the command to place <paramref name="media"/> at <paramref name="startTicks"/>. A video clip is
    /// created on <paramref name="videoTrack"/> when the source has video and the track is non-null; an audio
    /// clip on <paramref name="audioTrack"/> when the source has audio and the track is non-null. When both are
    /// created and <paramref name="linked"/> is on they share a fresh link group (so they move/blade together,
    /// step 13) and are wrapped in one <see cref="CompositeCommand"/>; otherwise a single
    /// <see cref="AddClipCommand"/> is returned. <paramref name="primaryIsVideo"/> picks which clip to select.
    /// Returns <see langword="null"/> when no compatible track is available for any of the source's streams.
    /// </summary>
    public static PlacementResult? BuildPlaceCommand(
        MediaRef media, VideoTrack? videoTrack, AudioTrack? audioTrack,
        long startTicks, bool linked, bool primaryIsVideo)
    {
        ArgumentNullException.ThrowIfNull(media);

        long start = TimelineMath.ClampNonNegative(startTicks);
        Timecode timelineStart = new(start);
        Timecode sourceOut = media.Info.Duration;

        bool wantVideo = media.Info.HasVideo && videoTrack is not null;
        bool wantAudio = media.Info.HasAudio && audioTrack is not null;
        if (!wantVideo && !wantAudio)
            return null;

        Guid? linkGroup = (linked && wantVideo && wantAudio) ? Guid.NewGuid() : null;

        Clip? videoClip = wantVideo
            ? new Clip(media.Id, Timecode.Zero, sourceOut, timelineStart) { LinkGroupId = linkGroup }
            : null;
        Clip? audioClip = wantAudio
            ? new Clip(media.Id, Timecode.Zero, sourceOut, timelineStart) { LinkGroupId = linkGroup }
            : null;

        var commands = new List<IEditCommand>();
        if (videoClip is not null)
            commands.Add(new AddClipCommand(videoTrack!, videoClip));
        if (audioClip is not null)
            commands.Add(new AddClipCommand(audioTrack!, audioClip));

        IEditCommand command = commands.Count == 1
            ? commands[0]
            : new CompositeCommand("Place linked clips", commands);

        Clip primary = (primaryIsVideo ? videoClip : audioClip) ?? videoClip ?? audioClip!;
        return new PlacementResult(command, primary);
    }
}
