using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>How a video track is blended onto the layers beneath it.</summary>
public enum BlendMode
{
    /// <summary>Source-over alpha compositing (the default).</summary>
    Normal,

    /// <summary>Multiply.</summary>
    Multiply,

    /// <summary>Screen.</summary>
    Screen,

    /// <summary>Additive.</summary>
    Add,
}

/// <summary>
/// A lane of non-overlapping clips. For the vertical slice a track holds at most one active clip at
/// any instant; overlaps/transitions are a post-slice extension of clip resolution (ARCHITECTURE.md §17).
/// </summary>
public abstract class Track
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the track contributes to the render. Disabled tracks are skipped entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Clips on this track, in placement order.</summary>
    public List<Clip> Clips { get; } = new();

    /// <summary>
    /// Resolves the single clip active at timeline time <paramref name="t"/>, or <see langword="null"/>.
    /// If clips overlap (out of scope for the slice), the last one in <see cref="Clips"/> wins, so the
    /// result is deterministic and a clip placed later sits on top.
    /// </summary>
    public Clip? ResolveActiveClip(Timecode t)
    {
        Clip? active = null;
        foreach (Clip clip in Clips)
        {
            if (clip.Contains(t))
                active = clip;
        }
        return active;
    }
}

/// <summary>A video track. Composited onto lower tracks with its <see cref="Opacity"/> and <see cref="BlendMode"/>.</summary>
public sealed class VideoTrack : Track
{
    /// <summary>Track opacity in [0, 1], applied when compositing onto the tracks below.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>How this track blends onto the tracks below.</summary>
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
}

/// <summary>An audio track. Summed into the mix at its <see cref="GainDb"/>, honouring mute/solo.</summary>
public sealed class AudioTrack : Track
{
    /// <summary>Track gain in decibels (0 dB = unity).</summary>
    public double GainDb { get; set; }

    /// <summary>Whether the track is muted (excluded from the mix).</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Whether the track is soloed. If any audio track is soloed, only soloed tracks are audible
    /// (ARCHITECTURE.md §6).
    /// </summary>
    public bool Solo { get; set; }
}
