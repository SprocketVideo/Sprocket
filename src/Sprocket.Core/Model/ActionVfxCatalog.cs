using Sprocket.Core.Commands;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Well-known ids of the built-in action-VFX presets (plan/features/special-effects.md, phase 3). Namespaced like
/// effect and generator ids so a plugin preset (later) can register its own without colliding.
/// </summary>
public static class ActionVfxIds
{
    /// <summary>A one-shot light pulse and kick for a gunshot — interactive light, bloom, fringe and a short shake.</summary>
    public const string MuzzleFlash = "builtin.vfx.muzzleflash";

    /// <summary>A short burst of flame: embers flare and fall away over flickering firelight and heat haze.</summary>
    public const string SmallFireBurst = "builtin.vfx.smallfireburst";

    /// <summary>A full blast: flash, shockwave, shake, sparks, embers and a smoke column rising off the ground.</summary>
    public const string GroundExplosion = "builtin.vfx.groundexplosion";

    /// <summary>The sustained after-look of a blast: hanging smoke, drifting embers and dust, heat shimmer.</summary>
    public const string ExplosionAftermath = "builtin.vfx.explosionaftermath";

    /// <summary>A fire burning just out of frame: a warm edge glow, rising embers and flickering light.</summary>
    public const string BurningEdge = "builtin.vfx.burningedge";

    /// <summary>A bullet or body hitting the dirt: a puff of dust and grit with a small camera kick.</summary>
    public const string DustHit = "builtin.vfx.dusthit";

    /// <summary>The camera-side reaction to an off-screen blast — shockwave, shake, fringe and a punch-in blur,
    /// with no fire elements of its own.</summary>
    public const string Aftershock = "builtin.vfx.aftershock";
}

/// <summary>
/// One layer of an action-VFX preset: either a generator overlay (<see cref="GeneratorTypeId"/> set) on a track
/// with <see cref="BlendMode"/>, or — when <see cref="GeneratorTypeId"/> is <see langword="null"/> — an adjustment
/// layer whose effects act on everything beneath it (shake, shockwave, interactive light on the plate itself).
/// The factories take the preset's start time because keyframe times are absolute timeline time
/// (<see cref="AnimatableValue.Shifted"/>): a layer's decay is stated relative to the hit and anchored here.
/// </summary>
/// <param name="Name">What the layer contributes, for tests, MCP and the status line (e.g. "Sparks").</param>
/// <param name="GeneratorTypeId">The generator this layer draws, or <see langword="null"/> for an adjustment layer.</param>
/// <param name="BlendMode">How the layer's track composites onto the tracks below.</param>
public sealed record ActionVfxLayer(string Name, string? GeneratorTypeId, BlendMode BlendMode)
{
    /// <summary>Overrides applied over the generator's catalog defaults, given the preset start (keyframes are
    /// absolute). Unused for an adjustment layer.</summary>
    public Action<GeneratorSpec, Timecode>? Configure { get; init; }

    /// <summary>The layer's effect stack, in processing order, given the preset start.</summary>
    public IReadOnlyList<Func<Timecode, EffectInstance>> Effects { get; init; } = [];

    /// <summary>Whether this is an adjustment layer rather than a generator overlay.</summary>
    public bool IsAdjustment => GeneratorTypeId is null;

    /// <summary>Builds this layer's clip placed at <paramref name="start"/>, <paramref name="duration"/> long.</summary>
    public Clip CreateClip(Timecode start, Timecode duration)
    {
        Clip clip;
        if (GeneratorTypeId is null)
        {
            clip = Clip.CreateAdjustment(duration, start);
        }
        else
        {
            GeneratorDescriptor descriptor = GeneratorCatalog.Find(GeneratorTypeId)
                ?? throw new InvalidOperationException($"Action VFX layer '{Name}' uses unknown generator '{GeneratorTypeId}'.");
            GeneratorSpec spec = descriptor.CreateSpec();
            Configure?.Invoke(spec, start);
            clip = Clip.CreateGenerator(spec, duration, start);
        }
        foreach (Func<Timecode, EffectInstance> effect in Effects)
            clip.Effects.Add(effect(start));
        return clip;
    }
}

/// <summary>
/// A browsable action-VFX preset (plan/features/special-effects.md, phase 3): a coordinated stack of generator
/// overlays and an adjustment layer, built only from the phase-1 primitive effects and the phase-2 atmospheric
/// generators. Inserting one produces ordinary tracks, clips, effects and keyframes — nothing the render graph,
/// persistence or the Inspector has to know about — so every layer stays editable, trimmable and removable
/// afterwards. This is the multi-effect bundle a single <see cref="EffectPreset"/> (one effect's values) cannot
/// express; the fire/explosion plates themselves remain best sourced as imported alpha stock (step 26).
/// </summary>
/// <param name="Id">Stable id (<see cref="ActionVfxIds"/>).</param>
/// <param name="DisplayName">Human-readable name for the menu and browser.</param>
/// <param name="Description">One-line summary of the look.</param>
/// <param name="PlacementHint">How to use it well — where to park the playhead, what to pair it with.</param>
/// <param name="DurationSeconds">The span the preset's clips cover from the playhead.</param>
/// <param name="Layers">The layers, bottom-up in composite order.</param>
public sealed record ActionVfxDescriptor(
    string Id,
    string DisplayName,
    string Description,
    string PlacementHint,
    double DurationSeconds,
    IReadOnlyList<ActionVfxLayer> Layers)
{
    /// <summary>The preset's span, rounded up to whole frames of <paramref name="frameRate"/> (seconds when the
    /// rate is unset) so every layer ends on a frame boundary.</summary>
    public Timecode Duration(Rational frameRate)
    {
        if (frameRate.Num <= 0 || frameRate.Den <= 0)
            return Timecode.FromSeconds(DurationSeconds);
        long frames = Math.Max(1, (long)Math.Ceiling(DurationSeconds * frameRate.Num / frameRate.Den - 1e-9));
        return Timecode.FromFrames(frames, frameRate);
    }

    /// <summary>
    /// Plans inserting this preset at <paramref name="at"/> as <b>one</b> undoable <see cref="CompositeCommand"/>.
    /// Each layer lands above every video track that has content over the preset's span, so overlays composite
    /// over the plate and the adjustment layer shakes/grades all of it. A layer reuses an existing track above
    /// that floor when it is free over the span and already has the layer's blend mode (so repeated hits share
    /// the same VFX tracks); otherwise a new track with that blend mode is stacked on top. Nothing is applied —
    /// the caller executes <see cref="ActionVfxInsertion.Command"/> through its <see cref="EditHistory"/>.
    /// </summary>
    public ActionVfxInsertion PlanInsert(Timeline timeline, Timecode at)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (at < Timecode.Zero)
            at = Timecode.Zero;
        Timecode duration = Duration(timeline.FrameRate);
        Timecode end = at + duration;

        // The planned stack: existing video tracks bottom-up, then any tracks this insertion adds.
        List<VideoTrack> stack = [.. timeline.VideoTracks];
        int floor = -1;
        for (int i = 0; i < stack.Count; i++)
            if (Overlaps(stack[i], at, end))
                floor = i;

        var commands = new List<IEditCommand>();
        var clips = new List<Clip>(Layers.Count);
        foreach (ActionVfxLayer layer in Layers)
        {
            int index = -1;
            for (int i = floor + 1; i < stack.Count; i++)
                if (stack[i].BlendMode == layer.BlendMode && !Overlaps(stack[i], at, end))
                {
                    index = i;
                    break;
                }

            VideoTrack track;
            if (index >= 0)
            {
                track = stack[index];
            }
            else
            {
                track = new VideoTrack { Name = $"V{stack.Count + 1}", BlendMode = layer.BlendMode };
                commands.Add(new AddTrackCommand(timeline, track));
                stack.Add(track);
                index = stack.Count - 1;
            }

            Clip clip = layer.CreateClip(at, duration);
            commands.Add(new AddClipCommand(track, clip));
            clips.Add(clip);
            floor = index; // the next layer stacks above this one, never onto a track this insertion already used
        }

        return new ActionVfxInsertion(new CompositeCommand($"Insert {DisplayName}", commands), clips);
    }

    private static bool Overlaps(Track track, Timecode start, Timecode end) =>
        track.Clips.Any(c => c.TimelineStart < end && c.TimelineEnd > start);
}

/// <summary>The planned insertion of an action-VFX preset: the one undoable command, and the clips it adds
/// (bottom-up, one per <see cref="ActionVfxDescriptor.Layers"/> entry) so the caller can select or report them.</summary>
public sealed record ActionVfxInsertion(CompositeCommand Command, IReadOnlyList<Clip> Clips);

/// <summary>
/// The registry of built-in action-VFX presets (plan/features/special-effects.md, phase 3). Clip ▸ Insert ▸ Action
/// VFX, the Effects browser's Action VFX group and MCP all list over this. Timing convention, matching how editors
/// cut a hit: the playhead is the impact frame, so every preset's flash/decay keyframes start there and the clips
/// run forward from it. Values are the look's starting point, not a lock — each layer is an ordinary clip.
/// </summary>
public static class ActionVfxCatalog
{
    /// <summary>All registered presets, in display order.</summary>
    public static IReadOnlyList<ActionVfxDescriptor> BuiltIns { get; } =
    [
        new ActionVfxDescriptor(
            ActionVfxIds.MuzzleFlash, "Muzzle Flash",
            "A gunshot's interactive light: a two-frame exposure pop with bloom, fringe and a short kick.",
            "Park the playhead on the firing frame. Layer a stock muzzle-flash plate above it (Screen) and a shot SFX.",
            0.5,
            [
                Adjustment("Flash + kick",
                    Fx(EffectTypeIds.Color, s => (EffectParamNames.Exposure, Pulse(s, 1.4, 0.03, 0.12))),
                    Fx(EffectTypeIds.Glow,
                        s => (EffectParamNames.Threshold, Constant(0.55)),
                        s => (EffectParamNames.Intensity, Pulse(s, 2.0, 0.03, 0.15))),
                    Fx(EffectTypeIds.ChromaticAberration, s => (EffectParamNames.Amount, Decay(s, 0.3, 0.15))),
                    Fx(EffectTypeIds.ImpactShake,
                        s => (EffectParamNames.Amount, Decay(s, 0.3, 0.25)),
                        s => (EffectParamNames.Frequency, Constant(14)))),
            ]),

        new ActionVfxDescriptor(
            ActionVfxIds.SmallFireBurst, "Small Fire Burst",
            "A short flare of flame: embers burst and fall away over flickering firelight and heat haze.",
            "Park the playhead where the fire ignites. Pair with a stock flame plate and a whoosh SFX.",
            2.0,
            [
                Generator("Embers", GeneratorTypeIds.Embers, BlendMode.Add,
                    (g, s) => g
                        .Set(GeneratorParamNames.Amount, Keys(s, (0, 0), (0.08, 0.95), (1.2, 0.5), (2.0, 0)))
                        .Set(GeneratorParamNames.Count, 22)
                        .Set(GeneratorParamNames.Speed, 0.9),
                    Fx(EffectTypeIds.Glow,
                        s => (EffectParamNames.Threshold, Constant(0.3)),
                        s => (EffectParamNames.Intensity, Constant(1.5)))),
                Adjustment("Firelight",
                    Fx(EffectTypeIds.Color, s => (EffectParamNames.Exposure, Pulse(s, 0.8, 0.05, 0.4))),
                    Fx(EffectTypeIds.Flicker,
                        s => (EffectParamNames.Amount, Keys(s, (0, 0), (0.08, 0.3), (1.2, 0.2), (2.0, 0))),
                        s => (EffectParamNames.Frequency, Constant(8)),
                        s => (EffectParamNames.Randomness, Constant(0.85))),
                    Fx(EffectTypeIds.HeatDistortion, s => (EffectParamNames.Amount, Constant(0.006)))),
            ]),

        new ActionVfxDescriptor(
            ActionVfxIds.GroundExplosion, "Ground Explosion",
            "A full blast: flash, shockwave, shake, sparks, embers and a smoke column rising off the ground.",
            "Park the playhead on the detonation frame. Pair with a stock fireball plate (Screen) and a boom SFX.",
            3.0,
            [
                Generator("Smoke column", GeneratorTypeIds.Smoke, BlendMode.Normal,
                    (g, s) => g
                        .SetString(GeneratorParamNames.Color, "#FF4A4440")
                        .Set(GeneratorParamNames.Amount, Keys(s, (0, 0), (0.4, 0.7), (3.0, 0.5)))
                        .Set(GeneratorParamNames.Speed, 1.6)
                        .Set(GeneratorParamNames.Direction, 90)
                        .Set(GeneratorParamNames.Falloff, 0.55)),
                Generator("Sparks", GeneratorTypeIds.Sparks, BlendMode.Add,
                    (g, s) => g
                        .Set(GeneratorParamNames.Amount, Decay(s, 1.0, 0.8))
                        .Set(GeneratorParamNames.Speed, 2.2)
                        .Set(GeneratorParamNames.Direction, 90)
                        .Set(GeneratorParamNames.Spread, 0.9)),
                Generator("Embers", GeneratorTypeIds.Embers, BlendMode.Add,
                    (g, s) => g.Set(GeneratorParamNames.Amount, Keys(s, (0, 0), (0.3, 0.8), (3.0, 0))),
                    Fx(EffectTypeIds.Glow,
                        s => (EffectParamNames.Threshold, Constant(0.3)),
                        s => (EffectParamNames.Intensity, Constant(1.4)))),
                Adjustment("Blast",
                    Fx(EffectTypeIds.Color, s => (EffectParamNames.Exposure, Pulse(s, 2.0, 0.04, 0.3))),
                    Fx(EffectTypeIds.Shockwave,
                        s => (EffectParamNames.Radius, Keys(s, (0, 0), (0.6, 1.2))),
                        s => (EffectParamNames.Amplitude, Constant(0.03)),
                        s => (EffectParamNames.CenterY, Constant(0.8))),
                    Fx(EffectTypeIds.ZoomBlur,
                        s => (EffectParamNames.Amount, Decay(s, 0.25, 0.3)),
                        s => (EffectParamNames.CenterY, Constant(0.8))),
                    Fx(EffectTypeIds.ChromaticAberration, s => (EffectParamNames.Amount, Decay(s, 0.5, 0.4))),
                    Fx(EffectTypeIds.ImpactShake, s => (EffectParamNames.Amount, Decay(s, 1.0, 1.2)))),
            ]),

        new ActionVfxDescriptor(
            ActionVfxIds.ExplosionAftermath, "Explosion Aftermath",
            "The lingering after-look of a blast: hanging smoke, drifting embers and dust, heat shimmer.",
            "Place right after the blast cuts out; trim or extend freely — it holds a steady mood.",
            6.0,
            [
                Generator("Hanging smoke", GeneratorTypeIds.Smoke, BlendMode.Normal,
                    (g, s) => g
                        .SetString(GeneratorParamNames.Color, "#FF5A544E")
                        .Set(GeneratorParamNames.Amount, 0.55)
                        .Set(GeneratorParamNames.Speed, 0.4)
                        .Set(GeneratorParamNames.Direction, 80)
                        .Set(GeneratorParamNames.Falloff, 0.3)),
                Generator("Dust", GeneratorTypeIds.Dust, BlendMode.Screen,
                    (g, s) => g.Set(GeneratorParamNames.Amount, 0.35)),
                Generator("Embers", GeneratorTypeIds.Embers, BlendMode.Add,
                    (g, s) => g
                        .Set(GeneratorParamNames.Amount, 0.5)
                        .Set(GeneratorParamNames.Count, 10)
                        .Set(GeneratorParamNames.Speed, 0.35)),
                Adjustment("Heat + flicker",
                    Fx(EffectTypeIds.HeatDistortion, s => (EffectParamNames.Amount, Constant(0.004))),
                    Fx(EffectTypeIds.Flicker,
                        s => (EffectParamNames.Amount, Constant(0.12)),
                        s => (EffectParamNames.Frequency, Constant(3)),
                        s => (EffectParamNames.Randomness, Constant(0.8)))),
            ]),

        new ActionVfxDescriptor(
            ActionVfxIds.BurningEdge, "Burning Edge",
            "A fire burning just below frame: a warm edge glow, rising embers and flickering light.",
            "Use over a shot that should read as near a fire. Move the glow's Position Y to put the fire on another edge.",
            5.0,
            [
                Generator("Edge glow", GeneratorTypeIds.LightLeak, BlendMode.Screen,
                    (g, s) => g
                        .SetString(GeneratorParamNames.Color, "#FFFF7A2A")
                        .Set(GeneratorParamNames.Amount, 0.6)
                        .Set(GeneratorParamNames.Direction, 0)
                        .Set(GeneratorParamNames.PositionX, 0.5)
                        .Set(GeneratorParamNames.PositionY, 1.0)
                        .Set(GeneratorParamNames.Size, 0.18)
                        .Set(GeneratorParamNames.Softness, 0.85),
                    Fx(EffectTypeIds.Flicker,
                        s => (EffectParamNames.Amount, Constant(0.35)),
                        s => (EffectParamNames.Frequency, Constant(7)),
                        s => (EffectParamNames.Randomness, Constant(0.9)))),
                Generator("Embers", GeneratorTypeIds.Embers, BlendMode.Add,
                    (g, s) => g.Set(GeneratorParamNames.Amount, 0.7)),
                Adjustment("Firelight",
                    Fx(EffectTypeIds.Flicker,
                        s => (EffectParamNames.Amount, Constant(0.15)),
                        s => (EffectParamNames.Frequency, Constant(7)),
                        s => (EffectParamNames.Randomness, Constant(0.9))),
                    Fx(EffectTypeIds.HeatDistortion, s => (EffectParamNames.Amount, Constant(0.008)))),
            ]),

        new ActionVfxDescriptor(
            ActionVfxIds.DustHit, "Dust Hit",
            "A bullet or body hitting the dirt: a puff of dust and grit with a small camera kick.",
            "Park the playhead on the impact frame. Pair with a stock debris plate and an impact SFX.",
            1.5,
            [
                Generator("Dust puff", GeneratorTypeIds.Smoke, BlendMode.Normal,
                    (g, s) => g
                        .SetString(GeneratorParamNames.Color, "#FFB8A88C")
                        .Set(GeneratorParamNames.Amount, Keys(s, (0, 0), (0.15, 0.45), (1.5, 0)))
                        .Set(GeneratorParamNames.Speed, 1.2)
                        .Set(GeneratorParamNames.Direction, 90)
                        .Set(GeneratorParamNames.Falloff, 0.8)),
                Generator("Grit", GeneratorTypeIds.Dust, BlendMode.Normal,
                    (g, s) => g
                        .SetString(GeneratorParamNames.Color, "#FFC8B898")
                        .Set(GeneratorParamNames.Amount, Keys(s, (0, 0), (0.1, 0.8), (1.5, 0)))
                        .Set(GeneratorParamNames.Count, 24)
                        .Set(GeneratorParamNames.Speed, 0.8)
                        .Set(GeneratorParamNames.Direction, 90)
                        .Set(GeneratorParamNames.Spread, 0.9)),
                Adjustment("Kick",
                    Fx(EffectTypeIds.ImpactShake,
                        s => (EffectParamNames.Amount, Decay(s, 0.5, 0.5)),
                        s => (EffectParamNames.Frequency, Constant(12)))),
            ]),

        new ActionVfxDescriptor(
            ActionVfxIds.Aftershock, "Aftershock",
            "The camera-side reaction to a nearby blast: shockwave, decaying shake, fringe and a punch-in blur.",
            "Park the playhead on the moment the blast reaches camera — often a few frames after an off-screen boom.",
            1.5,
            [
                Adjustment("Aftershock",
                    Fx(EffectTypeIds.Shockwave,
                        s => (EffectParamNames.Radius, Keys(s, (0, 0), (0.8, 1.4))),
                        s => (EffectParamNames.Amplitude, Constant(0.025))),
                    Fx(EffectTypeIds.ZoomBlur, s => (EffectParamNames.Amount, Decay(s, 0.2, 0.25))),
                    Fx(EffectTypeIds.ChromaticAberration, s => (EffectParamNames.Amount, Decay(s, 0.4, 0.5))),
                    Fx(EffectTypeIds.ImpactShake,
                        s => (EffectParamNames.Amount, Decay(s, 1.0, 1.5)),
                        s => (EffectParamNames.Frequency, Constant(10)))),
            ]),
    ];

    /// <summary>Looks up a preset by id, or <see langword="null"/> if it is not registered.</summary>
    public static ActionVfxDescriptor? Find(string id) => BuiltIns.FirstOrDefault(d => d.Id == id);

    // ── Builders ────────────────────────────────────────────────────────────────────────────────────────
    // Keyframe times are absolute (AnimatableValue.Shifted), so each helper takes the preset start and offsets
    // from it in seconds. Decays use EaseIn (fast departure, gentle landing) — the shape of a real hit settling.

    private static ActionVfxLayer Generator(string name, string generatorTypeId, BlendMode blend,
        Action<GeneratorSpec, Timecode> configure, params Func<Timecode, EffectInstance>[] effects) =>
        new(name, generatorTypeId, blend) { Configure = configure, Effects = effects };

    private static ActionVfxLayer Adjustment(string name, params Func<Timecode, EffectInstance>[] effects) =>
        new(name, null, BlendMode.Normal) { Effects = effects };

    /// <summary>An effect from the catalog (its defaults) with the given parameters overridden.</summary>
    private static Func<Timecode, EffectInstance> Fx(string effectTypeId,
        params Func<Timecode, (string Name, AnimatableValue Value)>[] overrides) => start =>
    {
        EffectInstance instance = EffectCatalog.Find(effectTypeId)?.CreateInstance()
            ?? throw new InvalidOperationException($"Action VFX preset uses unknown effect '{effectTypeId}'.");
        foreach (Func<Timecode, (string Name, AnimatableValue Value)> o in overrides)
        {
            (string n, AnimatableValue v) = o(start);
            instance.Set(n, v);
        }
        return instance;
    };

    private static AnimatableValue Constant(double value) => AnimatableValue.Constant(value);

    /// <summary>Keyframes at (seconds after <paramref name="start"/>, value), linear between them.</summary>
    private static AnimatableValue Keys(Timecode start, params (double Seconds, double Value)[] keys) =>
        AnimatableValue.Animated(keys.Select(k => new Keyframe(start + Timecode.FromSeconds(k.Seconds), k.Value)));

    /// <summary><paramref name="peak"/> at the hit, easing down to 0 over <paramref name="seconds"/>.</summary>
    private static AnimatableValue Decay(Timecode start, double peak, double seconds) =>
        AnimatableValue.Animated(
        [
            new Keyframe(start, peak, Interpolation.EaseIn),
            new Keyframe(start + Timecode.FromSeconds(seconds), 0),
        ]);

    /// <summary>0 → <paramref name="peak"/> over <paramref name="attack"/> seconds, then easing back to 0 by
    /// <paramref name="release"/> seconds — a flash that pops then falls away.</summary>
    private static AnimatableValue Pulse(Timecode start, double peak, double attack, double release) =>
        AnimatableValue.Animated(
        [
            new Keyframe(start, 0),
            new Keyframe(start + Timecode.FromSeconds(attack), peak, Interpolation.EaseIn),
            new Keyframe(start + Timecode.FromSeconds(release), 0),
        ]);
}
