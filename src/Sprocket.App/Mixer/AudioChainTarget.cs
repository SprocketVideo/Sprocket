using System;
using System.Collections.Generic;
using System.Linq;
using Sprocket.Core.Model;

namespace Sprocket.App.Mixer;

/// <summary>Which insert chain beyond the clip stack an <see cref="AudioChainTarget"/> names (PLAN.md step 31).</summary>
public enum AudioChainScope
{
    /// <summary>A track's pre-fader insert chain (<see cref="AudioTrack.Effects"/>).</summary>
    Track,

    /// <summary>The active sequence's output bus (<see cref="Timeline.AudioEffects"/>).</summary>
    SequenceBus,

    /// <summary>The project master chain (<see cref="ProjectSettings.MasterAudioEffects"/>).</summary>
    Master,
}

/// <summary>
/// One editable mixer insert chain (PLAN.md step 31): a track's pre-fader inserts, the active sequence's
/// output bus, or the project master chain. Bundles what the chain UI needs — the model list the chain
/// commands mutate, the DSP state key the live mixer persists effect state under (the same identity
/// <c>RenderGraph.ResolveAudioChain</c> keys the chain by, so <c>AudioMixer.TryPeekEffect</c> metering works
/// at chain scope), and display text. Pure data + pure helpers, headlessly testable like
/// <see cref="EffectReorder"/>; the clip-scope stack stays the Inspector's ordinary clip path.
/// </summary>
public sealed class AudioChainTarget
{
    private AudioChainTarget(AudioChainScope scope, List<EffectInstance> chain, object stateKey, string title, string description)
    {
        Scope = scope;
        Chain = chain;
        StateKey = stateKey;
        Title = title;
        Description = description;
    }

    /// <summary>Which of the three beyond-clip chain scopes this is.</summary>
    public AudioChainScope Scope { get; }

    /// <summary>The model chain the chain commands (<c>Add/Remove/MoveChainEffectCommand</c>) mutate.</summary>
    public List<EffectInstance> Chain { get; }

    /// <summary>The identity the mixer persists the chain's DSP state under — the track, timeline, or
    /// settings object, matching the render graph's chain keying.</summary>
    public object StateKey { get; }

    /// <summary>Pane/section heading, e.g. <c>"Track Inserts — Music"</c>.</summary>
    public string Title { get; }

    /// <summary>One-line description of where in the signal flow this chain runs.</summary>
    public string Description { get; }

    /// <summary>A track's pre-fader insert chain.</summary>
    public static AudioChainTarget ForTrack(AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return new AudioChainTarget(
            AudioChainScope.Track, track.Effects, track,
            $"Track Inserts — {TrackName(track)}",
            "Pre-fader inserts: run after each clip's own effects and before the track fader and pan.");
    }

    /// <summary>The active sequence's output-bus chain.</summary>
    public static AudioChainTarget ForSequenceBus(Timeline timeline, string sequenceName)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return new AudioChainTarget(
            AudioChainScope.SequenceBus, timeline.AudioEffects, timeline,
            string.IsNullOrEmpty(sequenceName) ? "Bus Inserts" : $"Bus Inserts — {sequenceName}",
            "Sequence output bus: processes the sequence's summed mix, before the project master chain.");
    }

    /// <summary>The project master chain.</summary>
    public static AudioChainTarget ForMaster(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AudioChainTarget(
            AudioChainScope.Master, settings.MasterAudioEffects, settings,
            "Master Inserts",
            "Master bus: processes the final mix, before the master fader.");
    }

    /// <summary>The display name a track strip / chain title uses (tracks can be unnamed).</summary>
    public static string TrackName(AudioTrack track) =>
        string.IsNullOrEmpty(track.Name) ? "Audio" : track.Name;

    /// <summary>
    /// Whether this chain still belongs to the project's current state — the track is still in the active
    /// sequence's timeline, the bus is the active sequence's bus, the settings are the project's. False after
    /// the track was removed (undo, Remove Track) or the open sequence changed; the Inspector then drops the
    /// stale target instead of editing a chain that is no longer audible.
    /// </summary>
    public bool IsAlive(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Scope switch
        {
            AudioChainScope.Track => project.Timeline.AudioTracks.Any(t => ReferenceEquals(t.Effects, Chain)),
            AudioChainScope.SequenceBus => ReferenceEquals(project.Timeline.AudioEffects, Chain),
            _ => ReferenceEquals(project.Settings.MasterAudioEffects, Chain),
        };
    }

    /// <summary>
    /// A change-detection snapshot of every mixer-visible chain (per-track, bus, master): the chain identities
    /// plus each effect instance and its enabled state. The mixer compares snapshots across history changes
    /// (<see cref="System.Linq.Enumerable.SequenceEqual{T}(IEnumerable{T}, IEnumerable{T})"/>) and rebuilds its
    /// insert rows only when one differs — so a gain-fader drag's command stream doesn't tear the strips down
    /// mid-gesture (the same reason MixerView compares the track set before rebuilding).
    /// </summary>
    public static List<object> Signature(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var signature = new List<object>();
        foreach (AudioTrack track in project.Timeline.AudioTracks)
            Append(signature, track, track.Effects);
        Append(signature, project.Timeline, project.Timeline.AudioEffects);
        Append(signature, project.Settings, project.Settings.MasterAudioEffects);
        return signature;

        static void Append(List<object> signature, object key, List<EffectInstance> chain)
        {
            signature.Add(key);
            foreach (EffectInstance effect in chain)
            {
                signature.Add(effect);
                signature.Add(effect.Enabled);
            }
        }
    }
}
