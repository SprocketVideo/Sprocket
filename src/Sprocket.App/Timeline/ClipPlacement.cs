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
    /// The source range a placement of <paramref name="media"/> uses (PLAN.md step 61 phase 2): its Source-monitor
    /// marks, each missing mark falling back to that end of the media — as Premiere does when a master clip with
    /// marks is dragged from the bin. A range that isn't positive (a stale mark past a relinked, shorter source)
    /// falls back to the whole media.
    /// </summary>
    public static (Timecode In, Timecode Out) MarkedRange(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        Timecode length = media.Info.Duration;
        Timecode sourceIn = media.SourceMarkIn ?? Timecode.Zero;
        Timecode sourceOut = media.SourceMarkOut ?? length;
        if (!media.HasUnboundedDuration && sourceOut > length)
            sourceOut = length;
        return sourceOut > sourceIn ? (sourceIn, sourceOut) : (Timecode.Zero, length);
    }

    /// <summary>
    /// Builds the command to place <paramref name="media"/> at <paramref name="startTicks"/>. A video clip is
    /// created on <paramref name="videoTrack"/> when the source has video and the track is non-null; an audio
    /// clip on <paramref name="audioTrack"/> when the source has audio and the track is non-null. When both are
    /// created and <paramref name="linked"/> is on they share a fresh link group (so they move/blade together,
    /// step 13) and are wrapped in one <see cref="CompositeCommand"/>; otherwise a single
    /// <see cref="AddClipCommand"/> is returned. <paramref name="primaryIsVideo"/> picks which clip to select.
    /// The clips span <paramref name="sourceRange"/>, defaulting to the media's <see cref="MarkedRange"/>.
    /// Returns <see langword="null"/> when no compatible track is available for any of the source's streams.
    /// </summary>
    public static PlacementResult? BuildPlaceCommand(
        MediaRef media, VideoTrack? videoTrack, AudioTrack? audioTrack,
        long startTicks, bool linked, bool primaryIsVideo, (Timecode In, Timecode Out)? sourceRange = null)
    {
        ArgumentNullException.ThrowIfNull(media);

        long start = TimelineMath.ClampNonNegative(startTicks);
        Timecode timelineStart = new(start);
        (Timecode sourceIn, Timecode sourceOut) = sourceRange ?? MarkedRange(media);

        bool wantVideo = media.Info.HasVideo && videoTrack is not null;
        bool wantAudio = media.Info.HasAudio && audioTrack is not null;
        if (!wantVideo && !wantAudio)
            return null;

        (Clip? videoClip, Clip? audioClip) = SourceClips.Create(
            media, sourceIn, sourceOut, timelineStart, wantVideo, wantAudio, linked);

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

    /// <summary>
    /// Where a drop of <paramref name="media"/> onto <paramref name="lane"/> puts each stream (PLAN.md step 61 phase 4),
    /// Premiere's rule: the stream matching the lane lands on it, and the other stream on its patched track — or, when
    /// that's locked, the first editable track of its kind (<see cref="EditableTrack{T}"/>). A stream the source lacks
    /// or <paramref name="streams"/> leaves out gets <see langword="null"/>; so does one whose only track is locked.
    /// <c>PrimaryIsVideo</c> picks which new clip to select: the lane's, else whichever exists.
    /// </summary>
    public static (VideoTrack? Video, AudioTrack? Audio, bool PrimaryIsVideo) DropTargets(
        MediaRef media, SourceStreams streams, Track? lane, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(sequence);
        bool wantVideo = media.Info.HasVideo && streams != SourceStreams.Audio;
        bool wantAudio = media.Info.HasAudio && streams != SourceStreams.Video;
        (VideoTrack? patchedVideo, AudioTrack? patchedAudio) = sequence.ResolvePatch();

        VideoTrack? video = !wantVideo ? null
            : lane as VideoTrack ?? (patchedVideo is { Locked: false } ? patchedVideo : EditableTrack(sequence.Timeline.VideoTracks));
        AudioTrack? audio = !wantAudio ? null
            : lane as AudioTrack ?? (patchedAudio is { Locked: false } ? patchedAudio : EditableTrack(sequence.Timeline.AudioTracks));
        bool primaryIsVideo = video is not null && (lane is VideoTrack || audio is null || lane is not AudioTrack);
        return (video, audio, primaryIsVideo);
    }

    /// <summary>The default destination among <paramref name="tracks"/> for a paste or a drop's companion stream:
    /// the first targeted unlocked track (leading editors paste to the targeted tracks), else the first unlocked
    /// one, else <see langword="null"/> when every track of that kind is locked.</summary>
    public static T? EditableTrack<T>(IEnumerable<T> tracks) where T : Track
    {
        T? fallback = null;
        foreach (T track in tracks)
        {
            if (track.Locked)
                continue;
            if (track.Targeted)
                return track;
            fallback ??= track;
        }
        return fallback;
    }

    /// <summary>Prepends the detected input color transform to a new video clip — see
    /// <see cref="SourceClips.PrependDetectedColorTransform"/>.</summary>
    public static void PrependDetectedColorTransform(Clip clip, ProbedMediaInfo info) =>
        SourceClips.PrependDetectedColorTransform(clip, info);

    /// <summary>
    /// The track a clip may move to for a cross-track drag (PLAN.md step 16e): returns
    /// <paramref name="laneTrack"/> when it is the same kind (video/audio) as <paramref name="sourceTrack"/>,
    /// otherwise <see langword="null"/> — a video clip only drops on a video track and audio only on audio. A
    /// null result tells the caller to keep the clip on its source track (the pointer is over an incompatible
    /// lane, so the track is unchanged). Pure so it is unit-tested headlessly.
    /// </summary>
    public static Track? CompatibleTrack(Track sourceTrack, Track? laneTrack)
    {
        ArgumentNullException.ThrowIfNull(sourceTrack);
        if (laneTrack is null)
            return null;
        bool sameKind = (sourceTrack is VideoTrack && laneTrack is VideoTrack)
            || (sourceTrack is AudioTrack && laneTrack is AudioTrack);
        return sameKind ? laneTrack : null;
    }
}
