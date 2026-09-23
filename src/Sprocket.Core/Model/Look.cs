using Sprocket.Core.Commands;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// One effect of a <see cref="Look"/>: a tier-2 grading effect type and the constant values it is applied
/// with (parameters a look omits keep the descriptor default), plus any asset references — the creative
/// LUT's <c>.cube</c> path under <see cref="EffectParamNames.LutFile"/>.
/// </summary>
/// <param name="EffectTypeId">The effect type (see <see cref="LookRules.IsLookEffect"/>).</param>
/// <param name="Values">Constant parameter values by name (keys match <see cref="EffectParamNames"/>).</param>
/// <param name="Assets">Asset paths by parameter name, or <see langword="null"/> for none.</param>
public sealed record LookEntry(
    string EffectTypeId,
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyDictionary<string, string>? Assets = null);

/// <summary>
/// A creative look (plan/features/looks-browser.md): a named, saved stack of tier-2 grading effects — the
/// one-click grade of Premiere's Lumetri Creative looks, Resolve's PowerGrades and Final Cut's effect presets.
/// A single-effect look (a creative LUT, one Color tweak) is the one-entry case. Looks are
/// <b>tier 2</b> of the ARCHITECTURE §18 preset taxonomy: they grade normalized Rec.709 footage and never
/// carry the tier-1 camera-log conversion (<see cref="EffectTypeIds.ColorTransform"/>), which is what keeps a
/// look from double-applying a camera transform.
/// </summary>
/// <param name="Id">Stable identifier — <c>builtin.look.*</c> for the curated catalog
/// (<see cref="LooksCatalog"/>), <c>user.*</c> for a saved look.</param>
/// <param name="Name">Display name.</param>
/// <param name="Group">The browser section the look is listed under (e.g. <c>"Cinematic"</c>).</param>
/// <param name="Description">One-line summary shown under the name, or <see langword="null"/>.</param>
/// <param name="Entries">The effects the look adds, in stack order.</param>
public sealed record Look(
    string Id,
    string Name,
    string Group,
    string? Description,
    IReadOnlyList<LookEntry> Entries)
{
    /// <summary>The id prefix of the curated built-in looks.</summary>
    public const string BuiltInPrefix = "builtin.look.";

    /// <summary>The id prefix of user-saved looks.</summary>
    public const string UserPrefix = "user.";

    /// <summary>The group every user-saved look is listed under.</summary>
    public const string UserGroup = "My Looks";

    /// <summary>Whether this is one of the curated built-ins (read-only in the browser).</summary>
    public bool IsBuiltIn => Id.StartsWith(BuiltInPrefix, StringComparison.Ordinal);

    /// <summary>A fresh, unique id for a user look.</summary>
    public static string NewUserId() => UserPrefix + Guid.NewGuid().ToString("N");
}

/// <summary>
/// The tier-2 scope guard of ARCHITECTURE §18: which effect types a look may contain.
/// </summary>
public static class LookRules
{
    /// <summary>
    /// Whether <paramref name="effectTypeId"/> is a creative grading effect a look may carry: any registered
    /// <see cref="EffectCategory.Color"/> effect <em>except</em> the tier-1 input color transform. An
    /// unregistered id (an uninstalled plugin) is not — it is skipped when the look is applied.
    /// </summary>
    public static bool IsLookEffect(string effectTypeId) =>
        effectTypeId != EffectTypeIds.ColorTransform
        && EffectCatalog.Find(effectTypeId) is { Category: EffectCategory.Color };
}

/// <summary>What applying a look to a clip produced (see <see cref="LookApplication.Build"/>).</summary>
/// <param name="Command">The one undoable edit that adds the look's effects, or <see langword="null"/> when no
/// entry survived (every effect type was unknown or not a look effect).</param>
/// <param name="Added">The effect instances the command adds, in stack order.</param>
/// <param name="Skipped">Effect type ids of entries that were skipped (unregistered or not tier-2), for a
/// warning — never an error.</param>
public sealed record LookApplyResult(
    IEditCommand? Command,
    IReadOnlyList<EffectInstance> Added,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Turns a <see cref="Look"/> into edits and back: <see cref="Build"/> expands a look into one undoable
/// <see cref="CompositeCommand"/> on a clip, and <see cref="Capture"/> snapshots a clip's tier-2 grade as a
/// new look ("Save Look…").
/// </summary>
public static class LookApplication
{
    /// <summary>
    /// Builds the edit that applies <paramref name="look"/> to <paramref name="clip"/>: one new effect instance
    /// per entry, <b>appended</b> to the clip's stack in the look's order — so it always grades after any tier-1
    /// input color transform (which sits at the front) and after any correction already on the clip, the
    /// correct-then-look order colourists use. Applying a second look stacks on the first, like adding any effect
    /// (undo to try another). Entries whose type is unregistered or not a look effect are skipped and reported;
    /// values the descriptor does not declare, or that are not finite, are ignored; the rest are clamped to the
    /// parameter's range so a hand-edited looks file cannot push a shader out of its domain.
    /// </summary>
    public static LookApplyResult Build(Look look, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(clip);

        var added = new List<EffectInstance>(look.Entries.Count);
        var skipped = new List<string>();
        foreach (LookEntry entry in look.Entries)
        {
            if (!LookRules.IsLookEffect(entry.EffectTypeId) || EffectCatalog.Find(entry.EffectTypeId) is not { } descriptor)
            {
                skipped.Add(entry.EffectTypeId);
                continue;
            }
            added.Add(CreateInstance(descriptor, entry));
        }

        if (added.Count == 0)
            return new LookApplyResult(null, [], skipped);
        List<IEditCommand> commands = [.. added.Select(e => (IEditCommand)new AddEffectCommand(clip, e))];
        return new LookApplyResult(new CompositeCommand($"Apply look {look.Name}", commands), added, skipped);
    }

    /// <summary>
    /// Snapshots <paramref name="clip"/>'s enabled tier-2 grading effects as a new user look named
    /// <paramref name="name"/>, in stack order. Each parameter is captured as the constant it evaluates to at
    /// <paramref name="at"/> (a look is a still grade — keyframes are not carried), with asset paths copied. The
    /// tier-1 input transform and non-grading effects (Transform, Glow, audio …) are left out, as are disabled
    /// effects. Returns <see langword="null"/> when the clip has no grading effect to save.
    /// </summary>
    public static Look? Capture(Clip clip, string name, Timecode at)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var entries = new List<LookEntry>();
        foreach (EffectInstance effect in clip.Effects)
        {
            if (!effect.Enabled || !LookRules.IsLookEffect(effect.EffectTypeId))
                continue;
            var values = new Dictionary<string, double>(effect.Parameters.Count);
            foreach ((string param, AnimatableValue value) in effect.Parameters)
            {
                double v = value.Evaluate(at);
                if (double.IsFinite(v))
                    values[param] = v;
            }
            Dictionary<string, string>? assets = effect.Assets.Count > 0 ? new(effect.Assets) : null;
            entries.Add(new LookEntry(effect.EffectTypeId, values, assets));
        }
        return entries.Count == 0 ? null : new Look(Look.NewUserId(), name.Trim(), Look.UserGroup, null, entries);
    }

    private static EffectInstance CreateInstance(EffectDescriptor descriptor, LookEntry entry)
    {
        EffectInstance instance = descriptor.CreateInstance();
        foreach ((string name, double value) in entry.Values)
        {
            EffectParameterDescriptor? p = descriptor.Parameters.FirstOrDefault(d => d.Name == name);
            if (p is null || p.Kind == ParameterKind.Asset || !double.IsFinite(value))
                continue;
            instance.Set(name, Math.Clamp(value, p.Min, p.Max));
        }
        if (entry.Assets is not null)
            foreach ((string name, string path) in entry.Assets)
                if (descriptor.Parameters.Any(d => d.Name == name && d.Kind == ParameterKind.Asset))
                    instance.SetAsset(name, path);
        return instance;
    }
}
