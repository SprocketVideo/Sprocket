using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Builds the new, not-yet-placed clips for a media source — the one construction shared by bin drops, the MCP
/// <c>add_clip_to_timeline</c> tool, and the three-point Insert / Overwrite (PLAN.md step 61 phase 3), so every path
/// places a source the same way: the video and audio clips span the same source range, share a fresh link group
/// when both are made, and the video clip gets the detected input color transform.
/// </summary>
public static class SourceClips
{
    /// <summary>
    /// Creates the clips for <paramref name="media"/>'s [<paramref name="sourceIn"/>, <paramref name="sourceOut"/>)
    /// at <paramref name="timelineStart"/>: a video clip when <paramref name="video"/> is set and the media has video,
    /// an audio clip likewise. When both are made and <paramref name="linked"/> is on they share a fresh
    /// <see cref="Clip.LinkGroupId"/>.
    /// </summary>
    public static (Clip? Video, Clip? Audio) Create(
        MediaRef media, Timecode sourceIn, Timecode sourceOut, Timecode timelineStart,
        bool video, bool audio, bool linked = true)
    {
        ArgumentNullException.ThrowIfNull(media);
        bool wantVideo = video && media.Info.HasVideo;
        bool wantAudio = audio && media.Info.HasAudio;
        Guid? linkGroup = linked && wantVideo && wantAudio ? Guid.NewGuid() : null;

        Clip? videoClip = wantVideo
            ? new Clip(media.Id, sourceIn, sourceOut, timelineStart) { LinkGroupId = linkGroup }
            : null;
        Clip? audioClip = wantAudio
            ? new Clip(media.Id, sourceIn, sourceOut, timelineStart) { LinkGroupId = linkGroup }
            : null;
        if (videoClip is not null)
            PrependDetectedColorTransform(videoClip, media.Info);
        return (videoClip, audioClip);
    }

    /// <summary>
    /// Prepends the input color transform (PLAN.md step 37) to a <b>new, not-yet-placed</b> video clip when
    /// the source's import probe detected a log profile (e.g. DJI D-Log), so log footage previews correctly
    /// the moment it lands on the timeline. The effect is built into the clip before its
    /// <see cref="Commands.AddClipCommand"/> runs, so undo removes clip and transform together; the user can still
    /// disable/remove or re-profile the effect in the Inspector (the manual-tag fallback).
    /// </summary>
    public static void PrependDetectedColorTransform(Clip clip, ProbedMediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(info);
        int profile = ColorProfiles.IndexOf(info.DetectedColorProfile);
        if (profile < 0)
            return;
        EffectInstance transform = new EffectInstance(EffectTypeIds.ColorTransform)
            .Set(EffectParamNames.SourceProfile, profile);
        clip.Effects.Insert(0, transform);
    }
}
