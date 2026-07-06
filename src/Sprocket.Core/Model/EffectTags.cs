namespace Sprocket.Core.Model;

/// <summary>
/// Assigns and resolves per-instance effect reference tags (<see cref="EffectInstance.Tag"/>): a short
/// type code plus one project-wide running number, e.g. <c>"RV-1"</c>, <c>"GP-2"</c>. The tag is the
/// stable handle the Inspector shows and MCP/AI clients address an effect by — unlike a stack index it
/// survives reorders, and the number is never reused within a project (it only counts up), so a stale
/// tag fails to resolve rather than silently pointing at a different effect.
/// <para>
/// Assignment is a sweep, not a per-creation hook: <see cref="EnsureAssigned"/> is idempotent and runs
/// at the read points (the Inspector's rebuild, the MCP marshal point), so every creation path — add,
/// blade-split clone, paste attributes, plugin, hand-edited project file — ends up tagged without each
/// command knowing about tags. Clones start untagged by construction (see <see cref="EffectInstance.Tag"/>).
/// </para>
/// </summary>
public static class EffectTags
{
    /// <summary>
    /// Assigns a tag to every untagged effect in the project (clip stacks across all sequences, plus the
    /// master audio chain), and re-tags any duplicate so tags stay unique — duplicates can only come from a
    /// hand-edited project file. New numbers continue from the highest number ever used in the project.
    /// Returns whether anything was assigned.
    /// </summary>
    public static bool EnsureAssigned(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<EffectInstance>();
        int next = 1;
        foreach (EffectInstance effect in AllEffects(project))
        {
            if (effect.Tag is { Length: > 0 } tag && seen.Add(tag))
            {
                if (TryParseNumber(tag, out int n) && n >= next)
                    next = n + 1;
            }
            else
            {
                pending.Add(effect); // untagged, or a duplicate of an earlier instance
            }
        }

        foreach (EffectInstance effect in pending)
            effect.Tag = $"{ShortCode(effect.EffectTypeId)}-{next++}";
        return pending.Count > 0;
    }

    /// <summary>The short code tags of this effect type are built from: the descriptor's explicit
    /// <see cref="EffectDescriptor.ShortCode"/> (every built-in has one), else one derived from the display
    /// name for plugins that don't declare a code.</summary>
    public static string ShortCode(string effectTypeId)
    {
        EffectDescriptor? descriptor = EffectCatalog.Find(effectTypeId);
        return descriptor?.ShortCode is { Length: > 0 } code
            ? code
            : DeriveShortCode(descriptor?.DisplayName ?? effectTypeId);
    }

    /// <summary>Derives a 2-letter code from a display name — initials of the first two words, or the first
    /// two letters of a single-word name (<c>"Warp Zoom"</c> → <c>WZ</c>, <c>"Glitch"</c> → <c>GL</c>);
    /// <c>"FX"</c> if the name has no letters at all.</summary>
    public static string DeriveShortCode(string displayName)
    {
        string[] words = displayName.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var letters = new List<char>(2);
        foreach (string word in words)
        {
            foreach (char c in word)
                if (char.IsLetter(c))
                {
                    letters.Add(char.ToUpperInvariant(c));
                    break;
                }
            if (letters.Count == 2)
                return new string([.. letters]);
        }
        if (letters.Count == 1)
            foreach (char c in words[0].AsSpan(1))
                if (char.IsLetter(c))
                    return new string([letters[0], char.ToUpperInvariant(c)]);
        return letters.Count == 1 ? new string(letters[0], 1) : "FX";
    }

    /// <summary>Every effect instance a project can carry, in a stable walk order: per sequence the clip
    /// stacks then track insert chains then the sequence bus chain, and finally the project master chain —
    /// so a duplicate tag on a later instance is the one re-assigned, not the earlier original's.</summary>
    private static IEnumerable<EffectInstance> AllEffects(Project project)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.Timeline.Tracks)
            {
                foreach (Clip clip in track.Clips)
                    foreach (EffectInstance effect in clip.Effects)
                        yield return effect;
                if (track is AudioTrack audio) // insert chains live on audio tracks only
                    foreach (EffectInstance effect in audio.Effects)
                        yield return effect;
            }
            foreach (EffectInstance effect in sequence.Timeline.AudioEffects)
                yield return effect;
        }
        foreach (EffectInstance effect in project.Settings.MasterAudioEffects)
            yield return effect;
    }

    /// <summary>Parses the running number out of a <c>CODE-N</c> tag; false for tags without one.</summary>
    private static bool TryParseNumber(string tag, out int number)
    {
        int dash = tag.LastIndexOf('-');
        number = 0;
        return dash >= 0 && int.TryParse(tag.AsSpan(dash + 1), out number) && number >= 0;
    }
}
