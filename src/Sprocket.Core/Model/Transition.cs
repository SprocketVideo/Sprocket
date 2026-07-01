using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Well-known built-in transition type ids (PLAN.md step 25). Plugins would use their own namespaced ids,
/// exactly as effects do (<see cref="EffectTypeIds"/>).
/// </summary>
public static class TransitionTypeIds
{
    /// <summary>A cross dissolve (the NLE default): the outgoing clip fades directly into the incoming one.</summary>
    public const string CrossDissolve = "builtin.crossdissolve";

    /// <summary>The outgoing clip fades to black, then black fades to the incoming clip.</summary>
    public const string DipToBlack = "builtin.diptoblack";

    /// <summary>The outgoing clip fades to white, then white fades to the incoming clip.</summary>
    public const string DipToWhite = "builtin.diptowhite";

    /// <summary>A linear left-to-right wipe revealing the incoming clip over the outgoing one.</summary>
    public const string Wipe = "builtin.wipe";
}

/// <summary>
/// Where a transition sits relative to the cut between its two clips (PLAN.md step 25), mirroring the
/// Premiere/Resolve alignment options. The cut is the edit point where the outgoing clip ends and the
/// incoming clip begins; the transition window extends over the clips' <em>handles</em> (the trimmed-off
/// source beyond each edge) so both clips are visible while it plays.
/// </summary>
public enum TransitionAlignment
{
    /// <summary>Centred on the cut — half the duration before it, half after (the NLE default).</summary>
    CenterOnCut,

    /// <summary>Ends at the cut — the whole transition covers the tail of the outgoing clip.</summary>
    EndAtCut,

    /// <summary>Starts at the cut — the whole transition covers the head of the incoming clip.</summary>
    StartAtCut,
}

/// <summary>
/// A transition placed at the cut between two adjacent clips on a <see cref="Track"/> (PLAN.md step 25,
/// ARCHITECTURE.md §17 "transitions extend clip resolution in the render graph"). It is a non-destructive
/// overlay anchored at <see cref="CutPoint"/>: it does not move or overlap the clips' timeline spans, it just
/// tells the render graph to blend the two clips over its window — sampling each clip's handle frames (the
/// trimmed-off source beyond the cut) the way every NLE does. The same model carries a future audio crossfade
/// (deferred for v1; the video blend ships first, like the slice's other compositing features).
/// </summary>
/// <remarks>
/// The transition is identified by a <see cref="TransitionTypeId"/> (e.g. <see cref="TransitionTypeIds.CrossDissolve"/>)
/// and carries an optional <see cref="Parameters"/> bag of <see cref="AnimatableValue"/>s for parameterised types
/// (none of the v1 built-ins use it) — the same shape effects use, so the type-driven Inspector can edit them later.
/// </remarks>
public sealed class Transition
{
    /// <summary>Creates a transition of the given type at <paramref name="cutPoint"/> spanning <paramref name="duration"/>.</summary>
    public Transition(
        string transitionTypeId,
        Timecode cutPoint,
        Timecode duration,
        TransitionAlignment alignment = TransitionAlignment.CenterOnCut)
    {
        if (string.IsNullOrWhiteSpace(transitionTypeId))
            throw new ArgumentException("Transition type id is required.", nameof(transitionTypeId));
        if (duration.Ticks <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Transition duration must be strictly positive.");

        TransitionTypeId = transitionTypeId;
        CutPoint = cutPoint;
        Duration = duration;
        Alignment = alignment;
    }

    /// <summary>The transition type id, e.g. <see cref="TransitionTypeIds.CrossDissolve"/>.</summary>
    public string TransitionTypeId { get; }

    /// <summary>The edit point this transition sits on — where the outgoing clip ends and the incoming begins.</summary>
    public Timecode CutPoint { get; set; }

    /// <summary>The transition's total length on the timeline. Strictly positive.</summary>
    public Timecode Duration { get; set; }

    /// <summary>How the window sits relative to <see cref="CutPoint"/>.</summary>
    public TransitionAlignment Alignment { get; set; }

    /// <summary>Type parameters by name (e.g. a wipe's softness), each an <see cref="AnimatableValue"/>. Empty for
    /// the v1 built-ins.</summary>
    public Dictionary<string, AnimatableValue> Parameters { get; } = new();

    /// <summary>The inclusive timeline time the transition begins (where it shows the full outgoing clip).</summary>
    public Timecode Start => Alignment switch
    {
        TransitionAlignment.EndAtCut => CutPoint - Duration,
        TransitionAlignment.StartAtCut => CutPoint,
        _ => CutPoint - new Timecode(Duration.Ticks / 2),
    };

    /// <summary>The exclusive timeline time the transition ends (where it shows the full incoming clip).</summary>
    public Timecode End => Start + Duration;

    /// <summary>Whether the transition is active at timeline time <paramref name="t"/> (start inclusive, end exclusive).</summary>
    public bool Contains(Timecode t) => t >= Start && t < End;

    /// <summary>
    /// The blend progress at timeline time <paramref name="t"/>: 0 at <see cref="Start"/> (full outgoing clip) ramping
    /// linearly to 1 at <see cref="End"/> (full incoming clip), clamped to [0, 1]. The Render layer turns this into
    /// the transition shader's mix factor.
    /// </summary>
    public double ProgressAt(Timecode t)
    {
        long span = Duration.Ticks;
        if (span <= 0)
            return 1.0;
        double p = (double)(t - Start).Ticks / span;
        return Math.Clamp(p, 0.0, 1.0);
    }

    /// <summary>Sets a parameter to a constant value (fluent).</summary>
    public Transition Set(string name, double value)
    {
        Parameters[name] = AnimatableValue.Constant(value);
        return this;
    }

    /// <summary>Sets a parameter to an animatable value (fluent).</summary>
    public Transition Set(string name, AnimatableValue value)
    {
        Parameters[name] = value;
        return this;
    }
}
