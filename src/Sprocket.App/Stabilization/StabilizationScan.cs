using System.Collections.Generic;
using Sprocket.Core.Model;

namespace Sprocket.App.Stabilization;

/// <summary>
/// Pure enumeration of the clips in a project that carry an (enabled) Stabilization effect on real source media
/// (plan/features/stabilization.md phase 6). Shared by auto-analyze-on-apply, the export pre-check, and the media
/// bin batch analyse — and testable without a UI. Scans every sequence, so nested sequences are covered.
/// </summary>
public static class StabilizationScan
{
    /// <summary>One stabilized media clip and the source + detail flag its analysis keys off.</summary>
    /// <param name="Media">The source to analyse.</param>
    /// <param name="Clip">The clip carrying the effect (for its used source range).</param>
    /// <param name="Detailed">Whether the effect requests Detailed Analysis.</param>
    public readonly record struct Item(MediaRef Media, Clip Clip, bool Detailed);

    /// <summary>Every media clip across the project's sequences that has an enabled Stabilization effect and a source
    /// still in the media pool. A clip with two stabilization instances yields one item per instance.</summary>
    public static IEnumerable<Item> StabilizedClips(Project project)
    {
        System.ArgumentNullException.ThrowIfNull(project);
        foreach (Sequence sequence in project.Sequences)
        {
            foreach (VideoTrack track in sequence.Timeline.VideoTracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    if (clip.Kind != ClipKind.Media)
                        continue;
                    MediaRef? media = project.MediaPool.Get(clip.MediaRefId);
                    if (media is null)
                        continue;
                    foreach (EffectInstance effect in clip.Effects)
                    {
                        if (effect.Enabled && effect.EffectTypeId == EffectTypeIds.Stabilization)
                            yield return new Item(media, clip, ReadDetailed(effect));
                    }
                }
            }
        }
    }

    private static bool ReadDetailed(EffectInstance effect) =>
        effect.Parameters.TryGetValue(EffectParamNames.DetailedAnalysis, out AnimatableValue? v)
        && v is not null && v.Evaluate(Sprocket.Core.Timing.Timecode.Zero) >= 0.5;
}
