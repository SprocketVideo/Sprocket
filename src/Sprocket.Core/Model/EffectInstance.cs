using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Well-known built-in effect type ids. Plugins use their own namespaced ids
/// (e.g. <c>"plugin.acme.glow"</c>) — see ARCHITECTURE.md §4, §13.
/// </summary>
public static class EffectTypeIds
{
    /// <summary>Brightness multiply. Parameter: <see cref="EffectParamNames.Amount"/>.</summary>
    public const string Brightness = "builtin.brightness";

    /// <summary>
    /// Fade. Drives video alpha (in the shader) and audio gain (in the mixer) from a single
    /// parameter, <see cref="EffectParamNames.Opacity"/>, animated 1→0 (or 0→1) over a range.
    /// </summary>
    public const string Fade = "builtin.fade";

    /// <summary>
    /// Geometric transform: scale, position, rotation around an anchor, plus layer opacity (PLAN.md step 16).
    /// Parameters: <see cref="EffectParamNames.Scale"/>, <see cref="EffectParamNames.PositionX"/>/
    /// <see cref="EffectParamNames.PositionY"/>, <see cref="EffectParamNames.Rotation"/>,
    /// <see cref="EffectParamNames.AnchorX"/>/<see cref="EffectParamNames.AnchorY"/>, and
    /// <see cref="EffectParamNames.Opacity"/>.
    /// </summary>
    public const string Transform = "builtin.transform";

    /// <summary>
    /// Colour / tone adjustment on the same per-pixel SkSL shape as brightness (PLAN.md step 16).
    /// Parameters: <see cref="EffectParamNames.Exposure"/> (stops), <see cref="EffectParamNames.Contrast"/>,
    /// and <see cref="EffectParamNames.Saturation"/>.
    /// </summary>
    public const string Color = "builtin.color";

    /// <summary>
    /// Audio gain/pan (PLAN.md step 31): a static per-chain-stage gain (<see cref="EffectParamNames.GainDb"/>)
    /// and stereo balance (<see cref="EffectParamNames.Pan"/>), the simplest audio DSP stage.
    /// </summary>
    public const string AudioGain = "builtin.audio.gain";

    /// <summary>
    /// Three-band parametric EQ (PLAN.md step 31): low shelf, mid peak (with Q), high shelf — RBJ biquads.
    /// Parameters: <see cref="EffectParamNames.LowGainDb"/>/<see cref="EffectParamNames.LowFreq"/>,
    /// <see cref="EffectParamNames.MidGainDb"/>/<see cref="EffectParamNames.MidFreq"/>/<see cref="EffectParamNames.MidQ"/>,
    /// <see cref="EffectParamNames.HighGainDb"/>/<see cref="EffectParamNames.HighFreq"/>.
    /// </summary>
    public const string AudioEq = "builtin.audio.eq";

    /// <summary>
    /// Dynamic-range compressor (PLAN.md step 31): peak-envelope feed-forward design. Parameters:
    /// <see cref="EffectParamNames.ThresholdDb"/>, <see cref="EffectParamNames.Ratio"/>,
    /// <see cref="EffectParamNames.AttackMs"/>, <see cref="EffectParamNames.ReleaseMs"/>,
    /// <see cref="EffectParamNames.MakeupDb"/>.
    /// </summary>
    public const string AudioCompressor = "builtin.audio.compressor";

    /// <summary>
    /// Reverb (PLAN.md step 31): Freeverb-style comb/allpass network. Parameters:
    /// <see cref="EffectParamNames.RoomSize"/>, <see cref="EffectParamNames.Damping"/>,
    /// <see cref="EffectParamNames.Mix"/> (wet/dry).
    /// </summary>
    public const string AudioReverb = "builtin.audio.reverb";

    /// <summary>
    /// Whether an effect type id names an <b>audio</b> chain stage (PLAN.md step 31). The render graph uses
    /// this to split a clip's single effect stack: audio ids feed the mixer's DSP chain, everything else feeds
    /// the video shader chain (where unknown ids pass through). Built-in audio effects share the
    /// <c>builtin.audio.</c> prefix; hosted audio plugins (VST3/AU, ARCHITECTURE.md §19) will register their own
    /// namespaced audio ids when they land.
    /// </summary>
    public static bool IsAudio(string effectTypeId) =>
        effectTypeId.StartsWith("builtin.audio.", StringComparison.Ordinal);
}

/// <summary>Well-known parameter names used by the built-in effects.</summary>
public static class EffectParamNames
{
    /// <summary>Brightness multiplier (1.0 = unchanged).</summary>
    public const string Amount = "amount";

    /// <summary>Opacity / gain multiplier in [0, 1] — used by <see cref="EffectTypeIds.Fade"/> and
    /// <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string Opacity = "opacity";

    /// <summary>Uniform scale factor (1.0 = unchanged) — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string Scale = "scale";

    /// <summary>Horizontal position offset, as a fraction of the frame width — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string PositionX = "positionX";

    /// <summary>Vertical position offset, as a fraction of the frame height — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string PositionY = "positionY";

    /// <summary>Rotation in degrees, clockwise — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string Rotation = "rotation";

    /// <summary>Anchor X in [0, 1] across the frame (0.5 = centre) — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string AnchorX = "anchorX";

    /// <summary>Anchor Y in [0, 1] down the frame (0.5 = centre) — <see cref="EffectTypeIds.Transform"/>.</summary>
    public const string AnchorY = "anchorY";

    /// <summary>Exposure in stops (0 = unchanged) — <see cref="EffectTypeIds.Color"/>.</summary>
    public const string Exposure = "exposure";

    /// <summary>Contrast around mid-grey (1.0 = unchanged) — <see cref="EffectTypeIds.Color"/>.</summary>
    public const string Contrast = "contrast";

    /// <summary>Saturation (1.0 = unchanged, 0 = greyscale) — <see cref="EffectTypeIds.Color"/>.</summary>
    public const string Saturation = "saturation";

    /// <summary>Gain in decibels (0 = unity) — <see cref="EffectTypeIds.AudioGain"/>.</summary>
    public const string GainDb = "gainDb";

    /// <summary>Stereo balance in [-1, 1] (0 = centre) — <see cref="EffectTypeIds.AudioGain"/>.</summary>
    public const string Pan = "pan";

    /// <summary>Low-shelf gain in dB — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string LowGainDb = "lowGainDb";

    /// <summary>Low-shelf corner frequency in Hz — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string LowFreq = "lowFreq";

    /// <summary>Mid-peak gain in dB — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidGainDb = "midGainDb";

    /// <summary>Mid-peak centre frequency in Hz — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidFreq = "midFreq";

    /// <summary>Mid-peak Q (bandwidth) — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidQ = "midQ";

    /// <summary>High-shelf gain in dB — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string HighGainDb = "highGainDb";

    /// <summary>High-shelf corner frequency in Hz — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string HighFreq = "highFreq";

    /// <summary>Compressor threshold in dBFS — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string ThresholdDb = "thresholdDb";

    /// <summary>Compression ratio (N:1) — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string Ratio = "ratio";

    /// <summary>Compressor attack time in milliseconds — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string AttackMs = "attackMs";

    /// <summary>Compressor release time in milliseconds — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string ReleaseMs = "releaseMs";

    /// <summary>Compressor make-up gain in dB — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string MakeupDb = "makeupDb";

    /// <summary>Reverb room size in [0, 1] — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string RoomSize = "roomSize";

    /// <summary>Reverb high-frequency damping in [0, 1] — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string Damping = "damping";

    /// <summary>Reverb wet/dry mix in [0, 1] (0 = dry only) — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string Mix = "mix";
}

/// <summary>
/// One effect in a clip's ordered effect stack (ARCHITECTURE.md §4). Holds the effect's type id and
/// its parameters as <see cref="AnimatableValue"/>s; the render graph evaluates the parameters at the
/// frame's time and hands the result to the Render layer, which owns the actual shader.
/// </summary>
public sealed class EffectInstance
{
    /// <summary>Creates an effect instance of the given type.</summary>
    public EffectInstance(string effectTypeId)
    {
        if (string.IsNullOrWhiteSpace(effectTypeId))
            throw new ArgumentException("Effect type id is required.", nameof(effectTypeId));
        EffectTypeId = effectTypeId;
    }

    /// <summary>The effect type id, e.g. <see cref="EffectTypeIds.Brightness"/>.</summary>
    public string EffectTypeId { get; }

    /// <summary>Parameters by name, each an <see cref="AnimatableValue"/>.</summary>
    public Dictionary<string, AnimatableValue> Parameters { get; } = new();

    /// <summary>Sets a parameter to a constant value (fluent).</summary>
    public EffectInstance Set(string name, double value)
    {
        Parameters[name] = AnimatableValue.Constant(value);
        return this;
    }

    /// <summary>Sets a parameter to an animatable value (fluent).</summary>
    public EffectInstance Set(string name, AnimatableValue value)
    {
        Parameters[name] = value;
        return this;
    }

    /// <summary>
    /// A copy with the same type and parameters. <see cref="AnimatableValue"/> is immutable so the entries
    /// are shared by reference; only the parameter map is fresh. Used when a blade split copies a clip's
    /// effect stack onto the new right-hand half (PLAN.md step 13).
    /// </summary>
    public EffectInstance Clone()
    {
        var copy = new EffectInstance(EffectTypeId);
        foreach ((string name, AnimatableValue value) in Parameters)
            copy.Parameters[name] = value;
        return copy;
    }

    /// <summary>
    /// A clone whose animated parameters have every keyframe time shifted by <paramref name="delta"/> (see
    /// <see cref="AnimatableValue.Shifted"/>). Used when a clip is pasted/duplicated to a new timeline start so
    /// its keyframed effects (e.g. the default fade in/out) move with the clip instead of staying anchored to
    /// the original clip's timeline span. A zero delta is equivalent to <see cref="Clone"/>.
    /// </summary>
    public EffectInstance CloneShifted(Timecode delta)
    {
        var copy = new EffectInstance(EffectTypeId);
        foreach ((string name, AnimatableValue value) in Parameters)
            copy.Parameters[name] = value.Shifted(delta);
        return copy;
    }
}
