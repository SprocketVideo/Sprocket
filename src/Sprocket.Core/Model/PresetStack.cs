using Sprocket.Core.Commands;

namespace Sprocket.Core.Model;

/// <summary>Where a <see cref="PresetStackEntry"/>'s effect lands in a clip's stack when it is not already there.</summary>
public enum PresetStackPlacement
{
    /// <summary>Appended after the clip's existing effects (the correct-then-look order of any added effect).</summary>
    Append,

    /// <summary>At the front of the stack — after any leading input color transform, which always runs first, and
    /// after earlier front-placed entries of the same stack. For effects that must act on the source itself: a time
    /// modifier (Posterize Time), or later a temporal effect such as Echo.</summary>
    Front,
}

/// <summary>
/// One effect of a <see cref="PresetStack"/>: an effect type and the factory preset it is added with
/// (<see cref="EffectDescriptor.FindPreset"/>), plus optional constant values applied over the preset — for an
/// effect without presets (Posterize Time's frame rate).
/// </summary>
/// <param name="EffectTypeId">The effect type (see <see cref="EffectTypeIds"/>).</param>
/// <param name="PresetName">The descriptor's factory preset to apply, or <see langword="null"/> for its defaults.</param>
/// <param name="Values">Constant parameter values applied over the preset, or <see langword="null"/> for none.</param>
/// <param name="Placement">Where a newly added instance goes in the clip's stack.</param>
public sealed record PresetStackEntry(
    string EffectTypeId,
    string? PresetName,
    IReadOnlyDictionary<string, double>? Values = null,
    PresetStackPlacement Placement = PresetStackPlacement.Append);

/// <summary>
/// A one-tap multi-effect look spanning picture and sound (plan/features/toy-cassette-camera.md, phase 5): video
/// entries go on the target's video-track clips, audio entries on its linked audio-track clips, in one undoable
/// edit (<see cref="PresetStackApplication.Build"/>). Unlike a <see cref="Look"/> (Color-only, single clip) it may
/// carry any registered effect, and every entry is an ordinary effect instance afterwards — editable in the
/// Inspector, removable on its own.
/// </summary>
/// <param name="Name">The variant's display name (e.g. <c>"Worn Tape"</c>) — generic, never a brand.</param>
/// <param name="Group">The Effects-browser group it is listed under (e.g. <c>"Toy Cassette Camera"</c>).</param>
/// <param name="Description">One-line summary for the row and tooltip (may credit an inspiration).</param>
/// <param name="VideoEntries">Effects added to video-track clips, in stack order.</param>
/// <param name="AudioEntries">Effects added to audio-track clips, in stack order.</param>
public sealed record PresetStack(
    string Name,
    string Group,
    string Description,
    IReadOnlyList<PresetStackEntry> VideoEntries,
    IReadOnlyList<PresetStackEntry> AudioEntries)
{
    /// <summary>The unambiguous title — <c>"Toy Cassette Camera ▸ Worn Tape"</c> — used for the undo label and as the
    /// drag payload.</summary>
    public string Title => $"{Group} ▸ {Name}";
}

/// <summary>An entry of a <see cref="PresetStack"/> that applying it did not add, and why (a warning, never an error).</summary>
/// <param name="EffectTypeId">The skipped entry's effect type.</param>
/// <param name="Reason">Human-readable reason, e.g. <c>"no linked audio clip"</c>.</param>
public sealed record PresetStackSkip(string EffectTypeId, string Reason);

/// <summary>What applying a <see cref="PresetStack"/> produced (see <see cref="PresetStackApplication.Build"/>).</summary>
/// <param name="Command">The one undoable edit, or <see langword="null"/> when no entry could be applied.</param>
/// <param name="Applied">Each clip touched with the instance the command puts on it, in application order.</param>
/// <param name="Replaced">How many of <paramref name="Applied"/> replace an existing entry of the same effect type
/// (a re-apply) rather than adding a new one.</param>
/// <param name="Skipped">Entries that were not applied, with the reason.</param>
public sealed record PresetStackApplyResult(
    IEditCommand? Command,
    IReadOnlyList<(Clip Clip, EffectInstance Effect)> Applied,
    int Replaced,
    IReadOnlyList<PresetStackSkip> Skipped);

/// <summary>Expands a <see cref="PresetStack"/> into one undoable edit over a clip and its linked companions.</summary>
public static class PresetStackApplication
{
    /// <summary>Reason reported for video entries when neither the clip nor a linked companion is on a video track.</summary>
    public const string NoVideoClip = "no video clip";

    /// <summary>Reason reported for audio entries when neither the clip nor a linked companion is on an audio track.</summary>
    public const string NoAudioClip = "no linked audio clip";

    /// <summary>Reason reported for an entry whose effect type is not registered (an uninstalled plugin).</summary>
    public const string UnknownEffect = "effect not available";

    /// <summary>Reason reported for an entry whose named preset the descriptor does not declare.</summary>
    public const string UnknownPreset = "preset not available";

    /// <summary>
    /// Builds the edit that applies <paramref name="stack"/> to <paramref name="clip"/> and every clip linked to it
    /// (<see cref="Timeline.ClipsLinkedTo"/>) as one <see cref="CompositeCommand"/> labelled
    /// <c>"Apply {Group} ▸ {Name}"</c>: video entries on the clips that sit on video tracks, audio entries on those
    /// on audio tracks. Dropping on either half of a linked pair therefore applies both halves; an unlinked clip gets
    /// only the entries of its track kind and the rest are reported in <see cref="PresetStackApplyResult.Skipped"/>.
    /// <para>
    /// <b>Idempotent re-apply:</b> when a target clip already carries an effect of an entry's type, that instance is
    /// <em>replaced in place</em> (same stack position, same reference tag) instead of a second copy being added, and
    /// any further instances of the type are removed — so dropping the same or another variant again swaps the look
    /// rather than stacking it. Parameters the preset does not set (Toy Cassette Camera's Seed, Cassette's Mix) and
    /// asset references carry over from the replaced instance — the same "a change of look keeps your seed / blend"
    /// rule the presets follow in the Inspector. New instances are placed per
    /// <see cref="PresetStackEntry.Placement"/>. Nothing is applied; the caller executes the command through its
    /// <see cref="EditHistory"/>.
    /// </para>
    /// </summary>
    public static PresetStackApplyResult Build(Timeline timeline, Clip clip, PresetStack stack)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(stack);

        var videoClips = new List<Clip>();
        var audioClips = new List<Clip>();
        var targets = new List<Clip> { clip };
        targets.AddRange(timeline.ClipsLinkedTo(clip).Select(l => l.Clip));
        foreach (Clip target in targets)
        {
            switch (TrackOf(timeline, target))
            {
                case VideoTrack:
                    videoClips.Add(target);
                    break;
                case AudioTrack:
                    audioClips.Add(target);
                    break;
            }
        }

        var commands = new List<IEditCommand>();
        var applied = new List<(Clip, EffectInstance)>();
        var skipped = new List<PresetStackSkip>();
        int replaced = 0;
        Collect(stack.VideoEntries, videoClips, NoVideoClip);
        Collect(stack.AudioEntries, audioClips, NoAudioClip);

        return commands.Count == 0
            ? new PresetStackApplyResult(null, [], 0, skipped)
            : new PresetStackApplyResult(new CompositeCommand($"Apply {stack.Title}", commands), applied, replaced, skipped);

        void Collect(IReadOnlyList<PresetStackEntry> entries, List<Clip> clips, string noClipReason)
        {
            // Resolve each entry once; the same fresh values go onto every clip of the kind (one instance per clip).
            var resolved = new List<(PresetStackEntry Entry, EffectDescriptor Descriptor, EffectPreset? Preset)>(entries.Count);
            foreach (PresetStackEntry entry in entries)
            {
                if (clips.Count == 0)
                    skipped.Add(new PresetStackSkip(entry.EffectTypeId, noClipReason));
                else if (EffectCatalog.Find(entry.EffectTypeId) is not { } descriptor)
                    skipped.Add(new PresetStackSkip(entry.EffectTypeId, UnknownEffect));
                else if (entry.PresetName is { } presetName && descriptor.FindPreset(presetName) is not { } preset)
                    skipped.Add(new PresetStackSkip(entry.EffectTypeId, UnknownPreset));
                else
                    resolved.Add((entry, descriptor, entry.PresetName is null ? null : descriptor.FindPreset(entry.PresetName)));
            }
            if (resolved.Count == 0)
                return;

            foreach (Clip target in clips)
            {
                // Simulate the stack so each InsertEffectAtCommand index is right when the composite applies in order.
                List<EffectInstance> sim = [.. target.Effects];
                int front = 0;
                while (front < sim.Count && sim[front].EffectTypeId == EffectTypeIds.ColorTransform)
                    front++;

                foreach ((PresetStackEntry entry, EffectDescriptor descriptor, EffectPreset? preset) in resolved)
                {
                    EffectInstance fresh = CreateInstance(descriptor, preset, entry.Values);
                    List<EffectInstance> existing = [.. sim.Where(e => e.EffectTypeId == entry.EffectTypeId)];
                    if (existing.Count > 0)
                    {
                        EffectInstance old = existing[0];
                        CarryOver(old, fresh, preset, entry.Values);
                        int index = sim.IndexOf(old);
                        foreach (EffectInstance e in existing)
                        {
                            if (sim.IndexOf(e) < front)
                                front--;
                            commands.Add(new RemoveEffectCommand(target, e));
                            sim.Remove(e);
                        }
                        index = Math.Min(index, sim.Count);
                        commands.Add(new InsertEffectAtCommand(target, fresh, index));
                        sim.Insert(index, fresh);
                        if (index < front)
                            front++;
                        // A front-placed entry replaced where it stood still leads the stack's later front entries
                        // (Echo goes after an existing Posterize Time, not ahead of it).
                        if (entry.Placement == PresetStackPlacement.Front && index + 1 > front)
                            front = index + 1;
                        replaced++;
                    }
                    else if (entry.Placement == PresetStackPlacement.Front)
                    {
                        commands.Add(new InsertEffectAtCommand(target, fresh, front));
                        sim.Insert(front, fresh);
                        front++;
                    }
                    else
                    {
                        commands.Add(new AddEffectCommand(target, fresh));
                        sim.Add(fresh);
                    }
                    applied.Add((target, fresh));
                }
            }
        }
    }

    /// <summary>The track in <paramref name="timeline"/> that holds <paramref name="clip"/>, or <see langword="null"/>.</summary>
    private static Track? TrackOf(Timeline timeline, Clip clip)
    {
        foreach (Track track in timeline.Tracks)
            foreach (Clip c in track.Clips)
                if (ReferenceEquals(c, clip))
                    return track;
        return null;
    }

    private static EffectInstance CreateInstance(EffectDescriptor descriptor, EffectPreset? preset, IReadOnlyDictionary<string, double>? values)
    {
        EffectInstance instance = preset is null ? descriptor.CreateInstance() : descriptor.CreateInstance(preset);
        if (values is not null)
            foreach ((string name, double value) in values)
                if (descriptor.Parameters.FirstOrDefault(p => p.Name == name) is { Kind: not ParameterKind.Asset } p
                    && double.IsFinite(value))
                    instance.Set(name, Math.Clamp(value, p.Min, p.Max));
        return instance;
    }

    /// <summary>Keeps what the stack does not set from the instance being replaced: parameters the preset/values leave
    /// alone, asset references, and the reference tag (so MCP/AI references to it stay valid across a re-apply).</summary>
    private static void CarryOver(EffectInstance old, EffectInstance fresh, EffectPreset? preset, IReadOnlyDictionary<string, double>? values)
    {
        foreach ((string name, AnimatableValue value) in old.Parameters)
            if (preset?.Values.ContainsKey(name) != true && values?.ContainsKey(name) != true)
                fresh.Parameters[name] = value;
        foreach ((string name, string path) in old.Assets)
            fresh.Assets[name] = path;
        fresh.Tag = old.Tag;
    }
}

/// <summary>Every built-in <see cref="PresetStack"/>, for the Effects browser and MCP.</summary>
public static class PresetStackCatalog
{
    /// <summary>All built-in stacks, grouped in browser order.</summary>
    public static IReadOnlyList<PresetStack> All { get; } = [.. ToyCassetteCameraStacks.All];

    /// <summary>
    /// The stack named <paramref name="name"/> (case-insensitive): either its <see cref="PresetStack.Title"/>
    /// (<c>"Toy Cassette Camera ▸ Worn Tape"</c>) or, when that is unambiguous, its bare <see cref="PresetStack.Name"/>.
    /// <see langword="null"/> when none (or several) match.
    /// </summary>
    public static PresetStack? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string wanted = name.Trim();
        if (All.FirstOrDefault(s => string.Equals(s.Title, wanted, StringComparison.OrdinalIgnoreCase)) is { } byTitle)
            return byTitle;
        List<PresetStack> byName = [.. All.Where(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))];
        return byName.Count == 1 ? byName[0] : null;
    }
}
