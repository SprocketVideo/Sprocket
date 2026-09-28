using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>A canvas size in pixels.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct Resolution(int Width, int Height);

/// <summary>
/// The sequence being edited: its render format (frame rate, canvas size, audio sample rate) and its
/// ordered tracks. Track order is z-order — index 0 is the bottom layer, the last is on top
/// (ARCHITECTURE.md §4).
/// </summary>
public sealed class Timeline
{
    /// <summary>Creates a timeline with the given render format.</summary>
    public Timeline(Rational frameRate, Resolution resolution, int sampleRate)
    {
        FrameRate = frameRate;
        Resolution = resolution;
        SampleRate = sampleRate;
    }

    /// <summary>Project render frame rate.</summary>
    public Rational FrameRate { get; set; }

    /// <summary>Project canvas size.</summary>
    public Resolution Resolution { get; set; }

    /// <summary>Project audio sample rate in Hz (e.g. 48000).</summary>
    public int SampleRate { get; set; }

    /// <summary>Tracks in z-order (index 0 = bottom, last = top).</summary>
    public List<Track> Tracks { get; } = new();

    /// <summary>Sequence markers pinned to timeline positions (PLAN.md step 20). Edited through the command
    /// stack; drawn on the ruler, navigable, and listed in the markers panel. Unordered — navigation sorts.</summary>
    public List<Marker> Markers { get; } = new();

    /// <summary>
    /// The sequence's audio output-bus chain (PLAN.md step 31, ARCHITECTURE.md §19): ordered
    /// <see cref="EffectInstance"/>s of audio effect types, applied by the mixer to this sequence's summed
    /// mix after all tracks — for the active sequence that is the bus before the project master chain; for a
    /// nested sequence it processes the sub-mix the nesting clip receives. Edited through the command stack.
    /// </summary>
    public List<EffectInstance> AudioEffects { get; } = new();

    /// <summary>The video tracks, bottom→top.</summary>
    public IEnumerable<VideoTrack> VideoTracks => Tracks.OfType<VideoTrack>();

    /// <summary>The audio tracks.</summary>
    public IEnumerable<AudioTrack> AudioTracks => Tracks.OfType<AudioTrack>();

    /// <summary>
    /// The clips linked to <paramref name="clip"/> (sharing its non-null <see cref="Clip.LinkGroupId"/>),
    /// excluding the clip itself — its companion A/V (PLAN.md step 13). Empty when the clip is unlinked.
    /// Pairs the found clip with the track it lives on so the editor can mutate it in place. Companions on a
    /// <see cref="Track.Locked"/> track are skipped unless <paramref name="includeLocked"/> is set — an edit on a
    /// linked clip leaves its locked partner where it is, as in leading editors; pass <see langword="true"/> only
    /// to report the link, never to edit through it.
    /// </summary>
    public IEnumerable<(Track Track, Clip Clip)> ClipsLinkedTo(Clip clip, bool includeLocked = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.LinkGroupId is not { } group)
            yield break;
        foreach (Track track in Tracks)
        {
            if (track.Locked && !includeLocked)
                continue;
            foreach (Clip c in track.Clips)
                if (!ReferenceEquals(c, clip) && c.LinkGroupId == group)
                    yield return (track, c);
        }
    }

    /// <summary>The track holding <paramref name="clip"/>, or <see langword="null"/> when it is on none.</summary>
    public Track? TrackOf(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        foreach (Track track in Tracks)
            foreach (Clip c in track.Clips)
                if (ReferenceEquals(c, clip))
                    return track;
        return null;
    }

    /// <summary>
    /// The exclusive end of the timeline — the latest clip end across all tracks (<see cref="Timecode.Zero"/>
    /// when empty).
    /// </summary>
    public Timecode Duration
    {
        get
        {
            Timecode end = Timecode.Zero;
            foreach (Track track in Tracks)
                foreach (Clip clip in track.Clips)
                    end = Timecode.Max(end, clip.TimelineEnd);
            return end;
        }
    }
}
