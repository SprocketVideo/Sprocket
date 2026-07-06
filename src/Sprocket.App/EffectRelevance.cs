using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;

namespace Sprocket.App;

/// <summary>
/// Which catalog effects make sense to offer for a given clip (pure + testable, like
/// <see cref="MediaBrowser.MediaBadges"/>): a clip on an <b>audio track</b> gets the
/// <see cref="EffectCategory.Audio"/> DSP stages; everything else (video tracks — media, generators,
/// adjustment layers, nested sequences) gets the Video/Color shader stages. A video-track clip's audio lives
/// in its linked audio-track clip, so audio stages applied to it would silently no-op — and vice versa —
/// hence the Inspector's "+ Effect" flyout and the Effects menu only offer the relevant half. Draws from
/// <see cref="EffectCatalog.All"/>, so plugin-contributed effects (PLAN.md step 33) are filtered the same way.
/// </summary>
public static class EffectRelevance
{
    /// <summary>Whether the clip sits on one of the timeline's audio tracks.</summary>
    public static bool IsOnAudioTrack(Timeline timeline, Clip clip) =>
        timeline.AudioTracks.Any(t => t.Clips.Contains(clip));

    /// <summary>The catalog effects applicable to <paramref name="clip"/>, in catalog order.</summary>
    public static IEnumerable<EffectDescriptor> For(Timeline timeline, Clip clip)
    {
        bool audio = IsOnAudioTrack(timeline, clip);
        return EffectCatalog.All.Where(d => (d.Category == EffectCategory.Audio) == audio);
    }

    /// <summary>The catalog effects a mixer insert chain offers (track / bus / master, PLAN.md step 31) —
    /// those chains carry audio DSP by construction, so this is the Audio half of the catalog with no clip
    /// involved. Plugin-contributed audio effects (step 33) appear here the same as built-ins.</summary>
    public static IEnumerable<EffectDescriptor> ForAudioChain() =>
        EffectCatalog.All.Where(d => d.Category == EffectCategory.Audio);
}
