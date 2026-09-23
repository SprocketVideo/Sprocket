using Sprocket.Core.Stabilization;

namespace Sprocket.Core.Model;

/// <summary>The broad grouping an effect falls under, used to organise the Effects/Audio browsers (PLAN.md step 15).</summary>
public enum EffectCategory
{
    /// <summary>Geometry / compositing on the video frame (alpha, transform).</summary>
    Video,

    /// <summary>Colour / tone adjustments on the video frame (brightness, exposure, contrast).</summary>
    Color,

    /// <summary>Effects that act on the audio signal (gain, fade gain).</summary>
    Audio,
}

/// <summary>
/// The control a parameter descriptor asks the Inspector to build. The kind is always declared
/// explicitly on the descriptor — it is never inferred from <c>Min</c>/<c>Max</c>/<c>Step</c>, because
/// genuinely continuous parameters (Rotation, Temperature, delay times…) also use a step of 1.
/// </summary>
public enum ParameterKind
{
    /// <summary>A continuous scalar — slider + numeric entry (the default).</summary>
    Continuous,

    /// <summary>An on/off flag stored as 0/1 and read with a ≥ 0.5 threshold — a checkbox; keyframes
    /// use <see cref="Interpolation.Hold"/> so the value never interpolates through the threshold.</summary>
    Toggle,

    /// <summary>A whole-number scalar — slider snapped to integers; new keyframes default to
    /// <see cref="Interpolation.Hold"/> but may be re-eased.</summary>
    Integer,

    /// <summary>A choice from <see cref="EffectParameterDescriptor.Choices"/> stored as its index —
    /// a dropdown; constant-only (not keyframeable).</summary>
    Dropdown,

    /// <summary>A file/asset reference (PLAN.md step 49) — e.g. the Convolution Reverb's impulse-response
    /// WAV. Lives in <see cref="EffectInstance.Assets"/> as a path string rather than in
    /// <see cref="EffectInstance.Parameters"/>, so it is constant-only and the numeric
    /// <c>Default</c>/<c>Min</c>/<c>Max</c> on its descriptor are unused; the Inspector builds a file-picker
    /// row (name + Browse… + clear) and <see cref="EffectDescriptor.CreateInstance"/> leaves it unset.</summary>
    Asset,
}

/// <summary>
/// A type-driven description of one editable effect parameter (PLAN.md step 16): its stable name (matches
/// the key in <see cref="EffectInstance.Parameters"/>), a display label, the default value a fresh instance
/// gets, the slider range, an editing step for numeric nudge, an optional unit suffix, an optional
/// one-line description shown as the label's tooltip, and the control kind the Inspector should build
/// (with the choice labels when the kind is <see cref="ParameterKind.Dropdown"/>). The Inspector builds
/// the control per descriptor, so a new effect's UI falls out of its registration with no bespoke
/// control code (and a plugin gets the same treatment, ARCHITECTURE.md §4).
/// </summary>
/// <param name="Name">The parameter key (matches <see cref="EffectParamNames"/>).</param>
/// <param name="DisplayName">Human-readable label shown in the Inspector.</param>
/// <param name="Default">The value a freshly created instance is given.</param>
/// <param name="Min">Minimum of the slider range.</param>
/// <param name="Max">Maximum of the slider range.</param>
/// <param name="Step">Suggested increment for numeric nudge / arrow keys.</param>
/// <param name="Unit">Optional unit suffix for display (e.g. <c>"°"</c>, <c>"EV"</c>).</param>
/// <param name="Description">One-line plain-language explanation of what the parameter does, shown as the
/// Inspector label's tooltip (and surfaced to MCP clients). <see langword="null"/> = no tooltip.</param>
/// <param name="Kind">The control the Inspector builds for this parameter (default
/// <see cref="ParameterKind.Continuous"/>).</param>
/// <param name="Choices">Display labels for a <see cref="ParameterKind.Dropdown"/> parameter, indexed by
/// the parameter's value; <see langword="null"/> for every other kind.</param>
public sealed record EffectParameterDescriptor(
    string Name,
    string DisplayName,
    double Default,
    double Min,
    double Max,
    double Step = 0.01,
    string? Unit = null,
    string? Description = null,
    ParameterKind Kind = ParameterKind.Continuous,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>
    /// Multiplier applied to the model value for display only (1.0 = shown exactly as stored). A 0–1 ratio
    /// presented as a percentage sets 100 alongside <c>Unit = "%"</c>, so Opacity 0.5 reads <c>"50%"</c> and
    /// typing <c>"50"</c> commits 0.5 — matching how Premiere / Final Cut / After Effects surface opacity,
    /// scale and audio mix amounts.
    /// <para>
    /// <see cref="Default"/>, <see cref="Min"/>, <see cref="Max"/> and <see cref="Step"/> stay in
    /// <em>model</em> units: the slider, the clamp, the keyframe lane, every command and every MCP tool
    /// argument are unscaled. Only the Inspector's numeric field converts.
    /// </para>
    /// Declared explicitly like <see cref="Kind"/> — never inferred from <see cref="Unit"/>, since a
    /// parameter may legitimately store 0–100 and display <c>"%"</c> with no scaling at all.
    /// </summary>
    public double DisplayScale { get; init; } = 1.0;

    /// <summary>
    /// For a <see cref="ParameterKind.Asset"/> parameter, the file kind the Inspector's picker filters to: a
    /// display name (e.g. <c>"WAV audio"</c>) and its extensions without the dot (e.g. <c>["wav"]</c>).
    /// <see langword="null"/> for every other kind, and for an asset that accepts any file.
    /// </summary>
    public AssetFileType? AssetType { get; init; }
}

/// <summary>The file kind an <see cref="ParameterKind.Asset"/> parameter accepts (see
/// <see cref="EffectParameterDescriptor.AssetType"/>): a display name and its extensions without the dot.</summary>
public sealed record AssetFileType(string Name, IReadOnlyList<string> Extensions);

/// <summary>
/// A named factory preset for an effect (PLAN.md step 41): the parameter values that give a recognisable
/// starting point (Room / Plate / Hall / Cathedral …). Values are constants applied over the descriptor's
/// defaults — parameters a preset omits keep their current value, so a preset can share the tweaks that
/// define it without flattening unrelated edits. The Inspector offers a preset picker for any descriptor
/// that carries presets; applying one is an ordinary undoable parameter edit.
/// </summary>
/// <param name="Name">Human-readable preset name shown in the picker.</param>
/// <param name="Values">Parameter values by name (keys match <see cref="EffectParamNames"/>).</param>
/// <param name="Description">Optional one-line note shown as the picker item's tooltip (e.g. the B&amp;W
/// film presets' "Inspired by &lt;stock&gt;. &lt;character&gt;."). <see langword="null"/> for presets without one.</param>
public sealed record EffectPreset(string Name, IReadOnlyDictionary<string, double> Values, string? Description = null);

/// <summary>
/// A browsable description of one effect type: its stable id (<see cref="EffectTypeIds"/>), a display name,
/// a category, a one-line description, and the ordered list of its editable <see cref="EffectParameterDescriptor"/>s.
/// This is the "effect registry" the Effects browser lists over (PLAN.md step 15); the Inspector (step 16)
/// builds its per-effect controls from <see cref="Parameters"/>, and a future plugin host (step 23) registers
/// here too, so every browser and the Inspector draw from one list rather than hard-coding the built-ins.
/// </summary>
/// <param name="Id">The effect type id (matches <see cref="EffectInstance.EffectTypeId"/>).</param>
/// <param name="DisplayName">Human-readable name for the browser.</param>
/// <param name="Category">Which browser section this effect belongs to.</param>
/// <param name="Description">A one-line summary shown under the name.</param>
/// <param name="Parameters">The effect's editable parameters, in display order.</param>
public sealed record EffectDescriptor(
    string Id,
    string DisplayName,
    EffectCategory Category,
    string Description,
    IReadOnlyList<EffectParameterDescriptor> Parameters)
{
    /// <summary>Named factory presets (PLAN.md step 41), empty for effects without any.</summary>
    public IReadOnlyList<EffectPreset> Presets { get; init; } = [];

    /// <summary>
    /// The 2-letter code instance reference tags are built from (<see cref="EffectTags"/>, e.g. <c>"RV"</c>
    /// → tag <c>"RV-1"</c>). Unique across the built-ins; a plugin descriptor that omits it gets one derived
    /// from its display name (<see cref="EffectTags.DeriveShortCode"/>).
    /// </summary>
    public string? ShortCode { get; init; }

    /// <summary>
    /// Builds a fresh <see cref="EffectInstance"/> of this type with every numeric parameter set to its
    /// <see cref="EffectParameterDescriptor.Default"/>. <see cref="ParameterKind.Asset"/> descriptors are
    /// left unset (there is no default file). Each call yields an independent instance.
    /// </summary>
    public EffectInstance CreateInstance()
    {
        var instance = new EffectInstance(Id);
        foreach (EffectParameterDescriptor p in Parameters)
        {
            if (p.Kind != ParameterKind.Asset)
                instance.Set(p.Name, p.Default);
        }
        return instance;
    }

    /// <summary>
    /// Builds a fresh instance (as <see cref="CreateInstance()"/>) with <paramref name="preset"/>'s values
    /// applied over the defaults — what a one-click look entry point (the Effects browser's DAY FOR NIGHT
    /// group, MCP <c>add_effect</c> with a preset) adds, so the result is identical to adding the effect and
    /// then picking the preset in the Inspector, in one undo step instead of two.
    /// </summary>
    /// <exception cref="ArgumentException">The preset sets a parameter this descriptor does not declare as numeric.</exception>
    public EffectInstance CreateInstance(EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        EffectInstance instance = CreateInstance();
        foreach ((string name, double value) in preset.Values)
        {
            if (!Parameters.Any(p => p.Name == name && p.Kind != ParameterKind.Asset))
                throw new ArgumentException($"Preset '{preset.Name}' sets '{name}', which {Id} does not declare.", nameof(preset));
            instance.Set(name, value);
        }
        return instance;
    }

    /// <summary>The factory preset named <paramref name="name"/> (case-insensitive), or <see langword="null"/>.</summary>
    public EffectPreset? FindPreset(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The registry of effect descriptors (ARCHITECTURE.md §4/§7/§13). <see cref="BuiltIns"/> holds the built-in
/// effects; plugin-contributed effects (PLAN.md step 33) are added at load time via <see cref="Register"/> and
/// removed on unload via <see cref="Unregister"/>, so every browser and the Inspector draw from one combined
/// list (<see cref="All"/>) rather than hard-coding the built-ins.
/// </summary>
public static class EffectCatalog
{
    /// <summary>All built-in effect descriptors, in display order.</summary>
    public static IReadOnlyList<EffectDescriptor> BuiltIns { get; } =
    [
        new EffectDescriptor(
            EffectTypeIds.Transform,
            "Transform",
            EffectCategory.Video,
            "Scale, position, and rotate the layer around an anchor, with layer opacity.",
            [
                new EffectParameterDescriptor(EffectParamNames.Scale, "Scale", 1.0, 0.0, 4.0, 0.05, "%",
                    "Uniform size of the layer (100% = original size).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.PositionX, "Position X", 0.0, -1.0, 1.0, 0.01,
                    Description: "Horizontal offset as a fraction of frame width (0 = centered)."),
                new EffectParameterDescriptor(EffectParamNames.PositionY, "Position Y", 0.0, -1.0, 1.0, 0.01,
                    Description: "Vertical offset as a fraction of frame height (0 = centered)."),
                new EffectParameterDescriptor(EffectParamNames.Rotation, "Rotation", 0.0, -180.0, 180.0, 1.0, "°",
                    "Rotation angle around the anchor point, in degrees."),
                new EffectParameterDescriptor(EffectParamNames.AnchorX, "Anchor X", 0.5, 0.0, 1.0, 0.01,
                    Description: "Horizontal pivot for scale and rotation (0 = left edge, 1 = right edge)."),
                new EffectParameterDescriptor(EffectParamNames.AnchorY, "Anchor Y", 0.5, 0.0, 1.0, 0.01,
                    Description: "Vertical pivot for scale and rotation (0 = top edge, 1 = bottom edge)."),
                new EffectParameterDescriptor(EffectParamNames.Opacity, "Opacity", 1.0, 0.0, 1.0, 0.05, "%",
                    "Layer transparency (100% = fully opaque, 0% = invisible).") { DisplayScale = 100 },
            ]) { ShortCode = "TR" },

        // ── Stabilization (plan/features/stabilization.md) — a hard-coded pipeline stage (like Transform),
        // driven by the headless motion analysis. Dropdowns store their choice index; toggles store 0/1.
        // Inspector order follows the leading editors (FCP/Resolve): mode + master smoothness/strength first,
        // then the per-channel controls, the framing controls, and the analysis/workflow toggles last. ──
        new EffectDescriptor(
            EffectTypeIds.Stabilization,
            "Stabilization",
            EffectCategory.Video,
            "Removes unwanted camera shake — pan/tilt jitter, roll, and scale wobble — from analysed motion.",
            [
                new EffectParameterDescriptor(EffectParamNames.StabMode, "Mode", 0.0, 0.0,
                    StabilizationSettings.ModeChoices.Count - 1, 1.0,
                    Description: "How the camera path is smoothed: Smooth Camera keeps deliberate moves, Smooth Motion low-passes the whole path, Camera Lock holds it still (tripod).",
                    Kind: ParameterKind.Dropdown, Choices: StabilizationSettings.ModeChoices),
                new EffectParameterDescriptor(EffectParamNames.Smoothness, "Smoothness", 0.5, 0.0, 1.0, 0.05, "%",
                    "How much of the camera path is smoothed — larger values average over a longer window.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Strength, "Strength", 1.0, 0.0, 1.0, 0.05, "%",
                    "Blends between the original path (0%) and the fully smoothed one (100%), so some original movement can be kept.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.StabMethod, "Method", 1.0, 0.0,
                    StabilizationSettings.MethodChoices.Count - 1, 1.0,
                    Description: "The motion model the solve uses: Translation (pan/tilt only), Similarity (adds rotation + scale), or Perspective.",
                    Kind: ParameterKind.Dropdown, Choices: StabilizationSettings.MethodChoices),
                new EffectParameterDescriptor(EffectParamNames.PositionSmooth, "Position Smoothing", 1.0, 0.0, 2.0, 0.05,
                    Description: "Multiplies the smoothing window for pan/tilt (1.0 = master smoothness, 0 = leave position untouched)."),
                new EffectParameterDescriptor(EffectParamNames.RotationSmooth, "Rotation Smoothing", 1.0, 0.0, 2.0, 0.05,
                    Description: "Multiplies the smoothing window for roll (1.0 = master smoothness, 0 = leave rotation untouched)."),
                new EffectParameterDescriptor(EffectParamNames.ScaleMode, "Scale", 0.0, 0.0,
                    StabilizationSettings.ScaleModeChoices.Count - 1, 1.0,
                    Description: "How scale is handled: Smooth it like the other channels, Preserve the original zoom, or Lock it to a reference (the focus-breathing fix).",
                    Kind: ParameterKind.Dropdown, Choices: StabilizationSettings.ScaleModeChoices),
                new EffectParameterDescriptor(EffectParamNames.ScaleSmooth, "Scale Smoothing", 1.0, 0.0, 2.0, 0.05,
                    Description: "Multiplies the smoothing window for scale when Scale = Smooth (1.0 = master smoothness)."),
                new EffectParameterDescriptor(EffectParamNames.ScaleLockRef, "Lock Reference", 0.0, 0.0,
                    StabilizationSettings.ScaleLockRefChoices.Count - 1, 1.0,
                    Description: "Which scale Scale = Lock holds every frame to (Tightest never scales down, so it introduces no borders).",
                    Kind: ParameterKind.Dropdown, Choices: StabilizationSettings.ScaleLockRefChoices),
                new EffectParameterDescriptor(EffectParamNames.LockRotation, "Lock Horizon", 0.0, 0.0, 1.0, 1.0,
                    Description: "Removes all rotation relative to the first frame, holding the horizon level.",
                    Kind: ParameterKind.Toggle),
                new EffectParameterDescriptor(EffectParamNames.Zoom, "Auto Zoom", 1.0, 0.0, 1.0, 1.0,
                    Description: "Scales up to crop the stabilized borders away; off shows transparent borders (\"Stabilize Only\").",
                    Kind: ParameterKind.Toggle),
                new EffectParameterDescriptor(EffectParamNames.CroppingRatio, "Cropping Ratio", 0.8, 0.5, 1.0, 0.05, "%",
                    "The fraction of the frame that must survive stabilization — caps the auto zoom at 1 / this value.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.DetailedAnalysis, "Detailed Analysis", 0.0, 0.0, 1.0, 1.0,
                    Description: "Higher analysis resolution and more tracked features (slower); changes the analysis cache key.",
                    Kind: ParameterKind.Toggle),
                new EffectParameterDescriptor(EffectParamNames.ShowTrackPoints, "Show Track Points", 0.0, 0.0, 1.0, 1.0,
                    Description: "A preview-only overlay of the tracked features (does not affect the render).",
                    Kind: ParameterKind.Toggle),
                new EffectParameterDescriptor(EffectParamNames.HideBanner, "Hide Warning Banner", 0.0, 0.0, 1.0, 1.0,
                    Description: "Suppresses the monitor's \"needs analysis / analyzing / low confidence\" banner.",
                    Kind: ParameterKind.Toggle),
            ])
        {
            ShortCode = "ST",
            Presets = StabilizationPresets.All,
        },

        // ── Action-VFX primitives (plan/features/special-effects.md, phase 1) — registry SkSL effects, the
        // reusable optical/geometric building blocks the later action, atmospheric and day-for-night presets
        // combine. Naming and default values follow the equivalent After Effects / Resolve primitives so the
        // controls read the way editors expect. Every spatial value is a fraction of the layer rect, so a
        // preview at one resolution and an export at another match (§5). ──
        new EffectDescriptor(
            EffectTypeIds.Glow,
            "Glow",
            EffectCategory.Video,
            "Blooms the brightest parts of the image outward — halation for fire, explosions and practical lights.",
            [
                new EffectParameterDescriptor(EffectParamNames.Threshold, "Threshold", 0.7, 0.0, 1.0, 0.01, "%",
                    "How bright a pixel must be before it glows (lower = more of the image blooms).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Radius, "Radius", 0.03, 0.0, 0.25, 0.005, "%",
                    "How far the glow spreads, as a fraction of the frame width.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Intensity, "Intensity", 1.0, 0.0, 4.0, 0.05, "%",
                    "How strongly the glow is added back over the picture (100% = unity).") { DisplayScale = 100 },
            ]) { ShortCode = "GL" },

        new EffectDescriptor(
            EffectTypeIds.DirectionalBlur,
            "Directional Blur",
            EffectCategory.Video,
            "Smears the image along one angle — speed, impacts, and faked motion blur.",
            [
                new EffectParameterDescriptor(EffectParamNames.Angle, "Angle", 0.0, -180.0, 180.0, 1.0, "°",
                    "Direction of the smear, in degrees clockwise from horizontal."),
                new EffectParameterDescriptor(EffectParamNames.BlurLength, "Length", 0.02, 0.0, 0.25, 0.005, "%",
                    "How long the smear is, as a fraction of the frame width.") { DisplayScale = 100 },
            ]) { ShortCode = "DB" },

        new EffectDescriptor(
            EffectTypeIds.ZoomBlur,
            "Zoom Blur",
            EffectCategory.Video,
            "Smears the image along the rays from a centre point — punch-ins, hits and blast moments.",
            [
                new EffectParameterDescriptor(EffectParamNames.Amount, "Amount", 0.0, 0.0, 1.0, 0.01, "%",
                    "How far the image is smeared toward and away from the centre.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.CenterX, "Center X", 0.5, 0.0, 1.0, 0.01,
                    Description: "Horizontal centre of the zoom (0 = left edge, 1 = right edge)."),
                new EffectParameterDescriptor(EffectParamNames.CenterY, "Center Y", 0.5, 0.0, 1.0, 0.01,
                    Description: "Vertical centre of the zoom (0 = top edge, 1 = bottom edge)."),
            ]) { ShortCode = "ZB" },

        new EffectDescriptor(
            EffectTypeIds.HeatDistortion,
            "Heat Distortion",
            EffectCategory.Video,
            "Animated refraction shimmer — the air over fire, exhaust, or hot ground.",
            [
                new EffectParameterDescriptor(EffectParamNames.Amount, "Amount", 0.01, 0.0, 0.1, 0.001, "%",
                    "How far the image is refracted, as a fraction of the frame width.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.NoiseScale, "Detail", 12.0, 1.0, 60.0, 1.0,
                    Description: "How many shimmer cells fit across the frame — higher is finer, tighter shimmer."),
                new EffectParameterDescriptor(EffectParamNames.Speed, "Speed", 1.0, 0.0, 5.0, 0.05,
                    Description: "How fast the shimmer moves (0 = a frozen, still distortion)."),
            ]) { ShortCode = "HD" },

        new EffectDescriptor(
            EffectTypeIds.Shockwave,
            "Shockwave",
            EffectCategory.Video,
            "A ring of displacement expanding from a centre — an explosion's blast wave. Keyframe Radius to fire it.",
            [
                new EffectParameterDescriptor(EffectParamNames.Radius, "Radius", 0.0, 0.0, 1.5, 0.01, "%",
                    "Where the ring currently is, as a fraction of the frame's half-diagonal — keyframe this to make the wave travel.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.RingWidth, "Width", 0.1, 0.01, 1.0, 0.01, "%",
                    "How thick the ring is — wider is a softer, slower-looking wave.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Amplitude, "Amplitude", 0.02, 0.0, 0.2, 0.005, "%",
                    "How far the ring pushes the picture, as a fraction of the frame width.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.CenterX, "Center X", 0.5, 0.0, 1.0, 0.01,
                    Description: "Horizontal origin of the wave (0 = left edge, 1 = right edge)."),
                new EffectParameterDescriptor(EffectParamNames.CenterY, "Center Y", 0.5, 0.0, 1.0, 0.01,
                    Description: "Vertical origin of the wave (0 = top edge, 1 = bottom edge)."),
            ]) { ShortCode = "SW" },

        new EffectDescriptor(
            EffectTypeIds.ChromaticAberration,
            "Chromatic Aberration",
            EffectCategory.Video,
            "Splits red and blue radially from a centre — lens fringing, and an accent on impacts.",
            [
                new EffectParameterDescriptor(EffectParamNames.Amount, "Amount", 0.0, 0.0, 1.0, 0.01, "%",
                    "How far the red and blue channels are pushed apart toward the frame edges.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.CenterX, "Center X", 0.5, 0.0, 1.0, 0.01,
                    Description: "Horizontal centre the fringing radiates from (0 = left edge, 1 = right edge)."),
                new EffectParameterDescriptor(EffectParamNames.CenterY, "Center Y", 0.5, 0.0, 1.0, 0.01,
                    Description: "Vertical centre the fringing radiates from (0 = top edge, 1 = bottom edge)."),
            ]) { ShortCode = "CA" },

        new EffectDescriptor(
            EffectTypeIds.ImpactShake,
            "Impact Shake",
            EffectCategory.Video,
            "Adds camera shake — the finishing move on an explosion or hit. Keyframe Amount to make it decay.",
            [
                new EffectParameterDescriptor(EffectParamNames.Amount, "Amount", 0.5, 0.0, 1.0, 0.05, "%",
                    "Master intensity of the shake — scales both the throw and the roll; keyframe it down to decay the hit.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Frequency, "Frequency", 8.0, 0.1, 30.0, 0.5, "Hz",
                    "How rapidly the shake jitters, in shakes per second."),
                new EffectParameterDescriptor(EffectParamNames.Rotation, "Rotation", 1.0, 0.0, 15.0, 0.5, "°",
                    "How much roll the shake adds, in degrees."),
                new EffectParameterDescriptor(EffectParamNames.Overscan, "Overscan", 1.05, 1.0, 1.5, 0.01, "%",
                    "Scales the frame up so the shake never reveals the edge (100% = no scale-up).") { DisplayScale = 100 },
            ]) { ShortCode = "IS" },

        new EffectDescriptor(
            EffectTypeIds.Flicker,
            "Flicker",
            EffectCategory.Video,
            "Pulses exposure over time — firelight, failing practicals, muzzle-flash throb.",
            [
                new EffectParameterDescriptor(EffectParamNames.Amount, "Amount", 0.2, 0.0, 1.0, 0.01, "%",
                    "How far the exposure swings above and below normal.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Frequency, "Frequency", 6.0, 0.1, 30.0, 0.5, "Hz",
                    "How rapidly the exposure pulses, in cycles per second."),
                new EffectParameterDescriptor(EffectParamNames.Randomness, "Randomness", 0.5, 0.0, 1.0, 0.05, "%",
                    "Blends from a steady pulse (0%) to irregular, firelight-like flicker (100%).") { DisplayScale = 100 },
            ]) { ShortCode = "FL" },

        new EffectDescriptor(
            EffectTypeIds.Color,
            "Color",
            EffectCategory.Color,
            "Exposure, contrast, saturation, and vibrance adjustment.",
            [
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", 0.0, -3.0, 3.0, 0.1, "EV",
                    "Brightens or darkens in photographic stops (+1 EV doubles the light)."),
                new EffectParameterDescriptor(EffectParamNames.Contrast, "Contrast", 1.0, 0.0, 2.0, 0.05,
                    Description: "Steepens or flattens the tones around mid-grey (1.0 = unchanged)."),
                new EffectParameterDescriptor(EffectParamNames.Saturation, "Saturation", 1.0, 0.0, 2.0, 0.05,
                    Description: "Overall colour intensity (0 = greyscale, 1.0 = unchanged)."),
                new EffectParameterDescriptor(EffectParamNames.Vibrance, "Vibrance", 0.0, -1.0, 1.0, 0.05,
                    Description: "Boosts muted colours more than already-vivid ones, protecting skin tones."),
            ]) { ShortCode = "CO" },

        // ── Colour grading toolset (PLAN.md step 34) — SkSL registry effects, like ACES Filmic. ──
        new EffectDescriptor(
            EffectTypeIds.WhiteBalance,
            "White Balance",
            EffectCategory.Color,
            "Temperature and tint correction, applied in linear light.",
            [
                new EffectParameterDescriptor(EffectParamNames.Temperature, "Temperature", 0.0, -100.0, 100.0, 1.0,
                    Description: "Shifts colours warmer (orange, +) or cooler (blue, −)."),
                new EffectParameterDescriptor(EffectParamNames.Tint, "Tint", 0.0, -100.0, 100.0, 1.0,
                    Description: "Shifts colours toward magenta (+) or green (−)."),
            ]) { ShortCode = "WB" },

        new EffectDescriptor(
            EffectTypeIds.ColorWheels,
            "Color Wheels",
            EffectCategory.Color,
            "Three-way grade: lift (shadows), gamma (mids), gain (highlights), master + RGB.",
            [
                new EffectParameterDescriptor(EffectParamNames.LiftMaster, "Lift", 0.0, -1.0, 1.0, 0.005,
                    Description: "Raises or lowers the shadows (darkest tones)."),
                new EffectParameterDescriptor(EffectParamNames.LiftR, "Lift R", 0.0, -1.0, 1.0, 0.005,
                    Description: "Red balance of the shadows."),
                new EffectParameterDescriptor(EffectParamNames.LiftG, "Lift G", 0.0, -1.0, 1.0, 0.005,
                    Description: "Green balance of the shadows."),
                new EffectParameterDescriptor(EffectParamNames.LiftB, "Lift B", 0.0, -1.0, 1.0, 0.005,
                    Description: "Blue balance of the shadows."),
                new EffectParameterDescriptor(EffectParamNames.GammaMaster, "Gamma", 0.0, -1.0, 1.0, 0.005,
                    Description: "Raises or lowers the midtones."),
                new EffectParameterDescriptor(EffectParamNames.GammaR, "Gamma R", 0.0, -1.0, 1.0, 0.005,
                    Description: "Red balance of the midtones."),
                new EffectParameterDescriptor(EffectParamNames.GammaG, "Gamma G", 0.0, -1.0, 1.0, 0.005,
                    Description: "Green balance of the midtones."),
                new EffectParameterDescriptor(EffectParamNames.GammaB, "Gamma B", 0.0, -1.0, 1.0, 0.005,
                    Description: "Blue balance of the midtones."),
                new EffectParameterDescriptor(EffectParamNames.GainMaster, "Gain", 0.0, -1.0, 1.0, 0.005,
                    Description: "Raises or lowers the highlights (brightest tones)."),
                new EffectParameterDescriptor(EffectParamNames.GainR, "Gain R", 0.0, -1.0, 1.0, 0.005,
                    Description: "Red balance of the highlights."),
                new EffectParameterDescriptor(EffectParamNames.GainG, "Gain G", 0.0, -1.0, 1.0, 0.005,
                    Description: "Green balance of the highlights."),
                new EffectParameterDescriptor(EffectParamNames.GainB, "Gain B", 0.0, -1.0, 1.0, 0.005,
                    Description: "Blue balance of the highlights."),
            ]) { ShortCode = "CW" },

        new EffectDescriptor(
            EffectTypeIds.Curves,
            "Curves",
            EffectCategory.Color,
            "Parametric RGB + per-channel curves: five points offset the identity per channel.",
            [
                new EffectParameterDescriptor(EffectParamNames.CurveMasterBlacks, "RGB Blacks", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the black point on all channels."),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterShadows, "RGB Shadows", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the shadows on all channels."),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterMids, "RGB Mids", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the midtones on all channels."),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterHighlights, "RGB Highlights", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the highlights on all channels."),
                new EffectParameterDescriptor(EffectParamNames.CurveMasterWhites, "RGB Whites", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the white point on all channels."),
                new EffectParameterDescriptor(EffectParamNames.CurveRedBlacks, "Red Blacks", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the black point on the red channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveRedShadows, "Red Shadows", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the shadows on the red channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveRedMids, "Red Mids", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the midtones on the red channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveRedHighlights, "Red Highlights", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the highlights on the red channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveRedWhites, "Red Whites", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the white point on the red channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenBlacks, "Green Blacks", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the black point on the green channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenShadows, "Green Shadows", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the shadows on the green channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenMids, "Green Mids", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the midtones on the green channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenHighlights, "Green Highlights", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the highlights on the green channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveGreenWhites, "Green Whites", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the white point on the green channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueBlacks, "Blue Blacks", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the black point on the blue channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueShadows, "Blue Shadows", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the shadows on the blue channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueMids, "Blue Mids", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the midtones on the blue channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueHighlights, "Blue Highlights", 0.0, -1.0, 1.0, 0.01,
                    Description: "Lifts or lowers the highlights on the blue channel."),
                new EffectParameterDescriptor(EffectParamNames.CurveBlueWhites, "Blue Whites", 0.0, -1.0, 1.0, 0.01,
                    Description: "Offsets the white point on the blue channel."),
            ]) { ShortCode = "CV" },

        new EffectDescriptor(
            EffectTypeIds.HslQualifier,
            "HSL Qualifier",
            EffectCategory.Color,
            "Keys a hue/saturation/luma range and grades only the keyed pixels.",
            [
                new EffectParameterDescriptor(EffectParamNames.HueCenter, "Hue Center", 0.0, 0.0, 360.0, 1.0, "°",
                    "The hue the key selects, in degrees on the colour wheel (0° = red)."),
                new EffectParameterDescriptor(EffectParamNames.HueWidth, "Hue Width", 60.0, 0.0, 180.0, 1.0, "°",
                    "How far either side of the centre hue the key reaches."),
                new EffectParameterDescriptor(EffectParamNames.HueSoftness, "Hue Softness", 20.0, 0.0, 90.0, 1.0, "°",
                    "Feathered falloff beyond the hue width."),
                new EffectParameterDescriptor(EffectParamNames.SatLow, "Sat Low", 0.0, 0.0, 1.0, 0.01,
                    Description: "Lower bound of the saturation range the key selects."),
                new EffectParameterDescriptor(EffectParamNames.SatHigh, "Sat High", 1.0, 0.0, 1.0, 0.01,
                    Description: "Upper bound of the saturation range the key selects."),
                new EffectParameterDescriptor(EffectParamNames.LumaLow, "Luma Low", 0.0, 0.0, 1.0, 0.01,
                    Description: "Lower bound of the brightness range the key selects."),
                new EffectParameterDescriptor(EffectParamNames.LumaHigh, "Luma High", 1.0, 0.0, 1.0, 0.01,
                    Description: "Upper bound of the brightness range the key selects."),
                new EffectParameterDescriptor(EffectParamNames.RangeSoftness, "Softness", 0.1, 0.0, 0.5, 0.01,
                    Description: "Feathered falloff at the edges of the saturation and luma ranges."),
                new EffectParameterDescriptor(EffectParamNames.HueShift, "Hue Shift", 0.0, -180.0, 180.0, 1.0, "°",
                    "Rotates the hue of the keyed pixels."),
                new EffectParameterDescriptor(EffectParamNames.Saturation, "Saturation", 1.0, 0.0, 2.0, 0.05,
                    Description: "Colour intensity of the keyed pixels (1.0 = unchanged)."),
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", 0.0, -3.0, 3.0, 0.1, "EV",
                    "Brightens or darkens the keyed pixels, in photographic stops."),
                new EffectParameterDescriptor(EffectParamNames.ShowMask, "Show Mask", 0.0, 0.0, 1.0, 1.0,
                    Description: "Shows the key as a black-and-white matte instead of the graded image.",
                    Kind: ParameterKind.Toggle),
            ]) { ShortCode = "HQ" },

        // ── Black & White (plan/features/black-and-white.md) — a registry SkSL effect like the grading toolset.
        // Phase 1: conversion core + film tone response. Grain/vignette/toning and the preset library follow. ──
        new EffectDescriptor(
            EffectTypeIds.BlackWhite,
            "Black & White",
            EffectCategory.Color,
            "Film-style monochrome: per-hue channel mixer, optical colour filter, and a tone response.",
            [
                // Conversion
                new EffectParameterDescriptor(EffectParamNames.Mix, "Amount", 1.0, 0.0, 1.0, 0.05, "%",
                    "Blend against the colour original (100% = full monochrome, 0% = unchanged).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.FilterHue, "Filter Hue", 0.0, 0.0, 360.0, 1.0, "°",
                    "Colour of a virtual optical filter over the lens (e.g. a red filter darkens skies)."),
                new EffectParameterDescriptor(EffectParamNames.FilterStrength, "Filter Strength", 0.0, 0.0, 1.0, 0.05, "%",
                    "How strongly the optical filter is applied (0% = no filter).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.MixReds, "Reds", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark red tones render (positive = lighter)."),
                new EffectParameterDescriptor(EffectParamNames.MixOranges, "Oranges", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark orange tones render (positive = lighter)."),
                new EffectParameterDescriptor(EffectParamNames.MixYellows, "Yellows", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark yellow tones render (positive = lighter)."),
                new EffectParameterDescriptor(EffectParamNames.MixGreens, "Greens", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark green tones render (positive = lighter)."),
                new EffectParameterDescriptor(EffectParamNames.MixAquas, "Aquas", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark aqua tones render (positive = lighter)."),
                new EffectParameterDescriptor(EffectParamNames.MixBlues, "Blues", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark blue tones render (positive = lighter)."),
                new EffectParameterDescriptor(EffectParamNames.MixPurples, "Purples", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark purple tones render (positive = lighter)."),
                new EffectParameterDescriptor(EffectParamNames.MixMagentas, "Magentas", 0.0, -100.0, 100.0, 1.0,
                    Description: "How light or dark magenta tones render (positive = lighter)."),
                // Film (tone response)
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Brightness", 0.0, -3.0, 3.0, 0.1, "EV",
                    "Brightens or darkens the monochrome image, in photographic stops."),
                new EffectParameterDescriptor(EffectParamNames.Contrast, "Contrast", 1.0, 0.0, 2.0, 0.05,
                    Description: "Steepens or flattens the tones around mid-grey (1.0 = unchanged)."),
                new EffectParameterDescriptor(EffectParamNames.Shadows, "Shadows", 0.0, -1.0, 1.0, 0.05,
                    Description: "Lifts (+) or crushes (−) the darkest tones — the film toe."),
                new EffectParameterDescriptor(EffectParamNames.Highlights, "Highlights", 0.0, -1.0, 1.0, 0.05,
                    Description: "Lifts (+) or rolls off (−) the brightest tones — the film shoulder."),
                new EffectParameterDescriptor(EffectParamNames.GrainAmount, "Grain", 0.0, 0.0, 1.0, 0.05, "%",
                    "Procedural film grain (luma-weighted — strongest in the mids, like real emulsion).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.GrainSize, "Grain Size", 1.0, 0.5, 4.0, 0.1,
                    Description: "Grain cell size in source pixels — resolution-aware, so 1080p and 4K match."),
                new EffectParameterDescriptor(EffectParamNames.GrainSeedLock, "Static Grain", 0.0, 0.0, 1.0, 1.0,
                    Description: "One fixed grain field for the whole clip (for stills / stop-motion) instead of re-seeding per frame.",
                    Kind: ParameterKind.Toggle),
                // Finishing
                new EffectParameterDescriptor(EffectParamNames.ToneHue, "Tone Hue", 35.0, 0.0, 360.0, 1.0, "°",
                    "Colour of the overall tint (35° ≈ sepia)."),
                new EffectParameterDescriptor(EffectParamNames.ToneStrength, "Tone Strength", 0.0, 0.0, 1.0, 0.05, "%",
                    "How strongly the single-tone tint is applied (0% = neutral).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.SplitShadowHue, "Shadow Hue", 35.0, 0.0, 360.0, 1.0, "°",
                    "Split-toning colour for the shadows."),
                new EffectParameterDescriptor(EffectParamNames.SplitHighlightHue, "Highlight Hue", 210.0, 0.0, 360.0, 1.0, "°",
                    "Split-toning colour for the highlights."),
                new EffectParameterDescriptor(EffectParamNames.SplitStrength, "Split Strength", 0.0, 0.0, 1.0, 0.05, "%",
                    "How strongly split toning is applied (0% = neutral).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.SplitBalance, "Split Balance", 0.0, -1.0, 1.0, 0.05,
                    Description: "Shifts the shadow/highlight crossover toward shadows (−) or highlights (+)."),
                new EffectParameterDescriptor(EffectParamNames.VignetteAmount, "Vignette", 0.0, -1.0, 1.0, 0.05,
                    Description: "Darkens (−) or lightens (+) the frame edges."),
                new EffectParameterDescriptor(EffectParamNames.VignetteSize, "Vignette Size", 0.7, 0.0, 1.5, 0.05,
                    Description: "Vignette radius relative to the frame's half-diagonal."),
                new EffectParameterDescriptor(EffectParamNames.VignetteSoftness, "Vignette Softness", 0.5, 0.0, 1.0, 0.05,
                    Description: "How gradually the vignette fades from centre to edge."),
            ])
        {
            ShortCode = "BW",
            // The non-film preset library (Neutral / Filters / Toning / Cinematic, ~30 presets) lives in
            // BlackWhitePresets to keep this file readable; the 19 film-stock presets land in phase 5. Every
            // preset leaves Mix untouched (the Studio Reverb rule) and touches only its family's parameters so
            // filters/tonings/tonal looks layer — see the BlackWhitePresets doc comment.
            Presets = BlackWhitePresets.All,
        },

        // ── Day for Night (plan/features/special-effects.md, phase 4) — one purpose-built grade, the Final Cut
        // Pro "Day into Night" / Magic Bullet shape, rather than a hand-assembled stack: the stage applies its
        // corrections in the fixed order a colourist would (exposure → sky → shoulder → saturation → tint →
        // floor → vignette), so no control can be stacked in the wrong place. The defaults are a usable
        // general-purpose night at full strength; the presets are the three looks in the roadmap. ──
        new EffectDescriptor(
            EffectTypeIds.DayForNight,
            "Day for Night",
            EffectCategory.Color,
            "Turns a daylight exterior into moonlit night: darker sky, rolled-off highlights, cool tint, lamps kept lit.",
            [
                new EffectParameterDescriptor(EffectParamNames.NightStrength, "Night Strength", 1.0, 0.0, 1.0, 0.05, "%",
                    "Blend from the original daylight plate (0%) to the full night grade (100%).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", -2.0, -4.0, 0.0, 0.1, "EV",
                    "How far the whole shot is underexposed, in photographic stops."),
                new EffectParameterDescriptor(EffectParamNames.SkyDarken, "Sky", 0.6, 0.0, 1.0, 0.05, "%",
                    "Pulls a bright or blue sky in the upper frame down so it reads as night, not overcast.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.HighlightRolloff, "Highlights", 0.6, 0.0, 1.0, 0.05, "%",
                    "Rolls off bright daylight highlights under a soft shoulder — moonlight has no hot whites.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.ShadowFloor, "Shadow Floor", 0.02, 0.0, 0.25, 0.005, "%",
                    "Lifts the blacks to a faint moonlit floor so shadows keep a little detail instead of crushing.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Saturation, "Saturation", 0.4, 0.0, 1.0, 0.05, "%",
                    "How much colour survives (night vision is nearly colourless; 100% = unchanged).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.MoonlightTint, "Moonlight Tint", 0.6, 0.0, 1.0, 0.05, "%",
                    "Strength of the cool moonlight cast, which keeps brightness while shifting colour.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.MoonlightHue, "Moonlight Hue", 215.0, 0.0, 360.0, 1.0, "°",
                    "Colour of the moonlight cast (215° ≈ classic blue night; lower = teal, higher = violet)."),
                new EffectParameterDescriptor(EffectParamNames.PracticalLights, "Practical Lights", 0.5, 0.0, 1.0, 0.05, "%",
                    "Keeps bright, warm sources — street lamps, lit windows, fire — glowing through the grade.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.ProtectSkin, "Protect Skin", 0.3, 0.0, 1.0, 0.05, "%",
                    "Keeps some natural colour in skin tones so faces do not go grey-blue (brightness still darkens).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.VignetteAmount, "Vignette", 0.3, 0.0, 1.0, 0.05, "%",
                    "Darkens the frame edges, drawing the eye to a moonlit centre.") { DisplayScale = 100 },
            ])
        {
            ShortCode = "DN",
            Presets = DayForNightPresets.All,
        },

        // ── Creative LUT (plan/features/looks-browser.md) — a tier-2 creative look from a user .cube file, through
        // the input color transform's packed-LUT GPU stage. Kept a separate type from the tier-1 transform below
        // (ARCHITECTURE §18) so a camera conversion can never be mistaken for, or stacked as, a look. ──
        new EffectDescriptor(
            EffectTypeIds.CreativeLut,
            "Creative LUT",
            EffectCategory.Color,
            "Applies a creative .cube look LUT to Rec.709 footage, blended by Intensity.",
            [
                new EffectParameterDescriptor(EffectParamNames.LutFile, "LUT File", 0.0, 0.0, 0.0, 1.0,
                    Description: "A creative 3D LUT in .cube format, made for Rec.709 footage (not a camera log conversion).",
                    Kind: ParameterKind.Asset) { AssetType = new AssetFileType("Cube LUT", ["cube"]) },
                new EffectParameterDescriptor(EffectParamNames.Mix, "Intensity", 1.0, 0.0, 1.0, 0.05, "%",
                    "Blend between the original (0%) and the full look (100%).") { DisplayScale = 100 },
            ]) { ShortCode = "LU" },

        new EffectDescriptor(
            EffectTypeIds.ColorTransform,
            "Input Color Transform",
            EffectCategory.Color,
            "Converts a log source (DJI, ARRI, Sony, Panasonic, Canon, Blackmagic, Fujifilm, or Nikon) to Rec.709.",
            [
                new EffectParameterDescriptor(EffectParamNames.SourceProfile, "Source Profile",
                    0.0, 0.0, ColorProfiles.All.Count - 1, 1.0,
                    Description: "The camera log profile the footage was recorded in.",
                    Kind: ParameterKind.Dropdown, Choices: ColorProfiles.DisplayNames),
            ]) { ShortCode = "CT" },

        new EffectDescriptor(
            EffectTypeIds.AcesFilmic,
            "ACES Filmic",
            EffectCategory.Color,
            "Scene-linear ACES filmic tone mapping (RRT + ODT fit) with exposure.",
            [
                new EffectParameterDescriptor(EffectParamNames.Exposure, "Exposure", 0.0, -8.0, 8.0, 0.1, "EV",
                    "Scene exposure applied before tone mapping, in photographic stops."),
            ]) { ShortCode = "AF" },

        new EffectDescriptor(
            EffectTypeIds.Brightness,
            "Brightness",
            EffectCategory.Color,
            "Multiplies the image brightness (1.0 = unchanged).",
            [
                new EffectParameterDescriptor(EffectParamNames.Amount, "Amount", 1.0, 0.0, 4.0, 0.05,
                    Description: "Multiplies image brightness (1.0 = unchanged, 2.0 = twice as bright)."),
            ]) { ShortCode = "BR" },

        new EffectDescriptor(
            EffectTypeIds.Fade,
            "Fade",
            EffectCategory.Video,
            "Ramps opacity — drives video alpha and audio gain together.",
            [
                new EffectParameterDescriptor(EffectParamNames.Opacity, "Opacity", 1.0, 0.0, 1.0, 0.05, "%",
                    "Clip opacity — also scales the clip's audio gain in step.") { DisplayScale = 100 },
            ]) { ShortCode = "FD" },

        // ── Audio chain stages (PLAN.md step 31) — executed by the mixer, not the shader pipeline. ──
        new EffectDescriptor(
            EffectTypeIds.AudioGain,
            "Gain / Pan",
            EffectCategory.Audio,
            "Adjusts level and stereo balance.",
            [
                new EffectParameterDescriptor(EffectParamNames.GainDb, "Gain", 0.0, -24.0, 24.0, 0.5, "dB",
                    "Volume adjustment in decibels (0 = unchanged)."),
                new EffectParameterDescriptor(EffectParamNames.Pan, "Pan", 0.0, -1.0, 1.0, 0.05,
                    Description: "Stereo balance (−1 = full left, +1 = full right)."),
            ]) { ShortCode = "GP" },

        new EffectDescriptor(
            EffectTypeIds.AudioEq,
            "Parametric EQ",
            EffectCategory.Audio,
            "Three-band EQ: low shelf, mid peak, high shelf.",
            [
                new EffectParameterDescriptor(EffectParamNames.LowGainDb, "Low Gain", 0.0, -15.0, 15.0, 0.5, "dB",
                    "Boost or cut below the low shelf frequency."),
                new EffectParameterDescriptor(EffectParamNames.LowFreq, "Low Freq", 100.0, 20.0, 500.0, 5.0, "Hz",
                    "Corner frequency of the low shelf."),
                new EffectParameterDescriptor(EffectParamNames.MidGainDb, "Mid Gain", 0.0, -15.0, 15.0, 0.5, "dB",
                    "Boost or cut around the mid band's centre frequency."),
                new EffectParameterDescriptor(EffectParamNames.MidFreq, "Mid Freq", 1000.0, 200.0, 8000.0, 50.0, "Hz",
                    "Centre frequency of the mid peak band."),
                new EffectParameterDescriptor(EffectParamNames.MidQ, "Mid Q", 1.0, 0.3, 8.0, 0.1,
                    Description: "Width of the mid band (higher = narrower)."),
                new EffectParameterDescriptor(EffectParamNames.HighGainDb, "High Gain", 0.0, -15.0, 15.0, 0.5, "dB",
                    "Boost or cut above the high shelf frequency."),
                new EffectParameterDescriptor(EffectParamNames.HighFreq, "High Freq", 8000.0, 2000.0, 16000.0, 100.0, "Hz",
                    "Corner frequency of the high shelf."),
            ]) { ShortCode = "EQ" },

        new EffectDescriptor(
            EffectTypeIds.AudioCompressor,
            "Compressor",
            EffectCategory.Audio,
            "Evens out dynamics: attenuates peaks above the threshold.",
            [
                new EffectParameterDescriptor(EffectParamNames.ThresholdDb, "Threshold", -18.0, -60.0, 0.0, 0.5, "dB",
                    "Level above which compression starts."),
                new EffectParameterDescriptor(EffectParamNames.Ratio, "Ratio", 4.0, 1.0, 20.0, 0.5,
                    Description: "How strongly peaks above the threshold are reduced (4 = 4 dB in → 1 dB out)."),
                new EffectParameterDescriptor(EffectParamNames.AttackMs, "Attack", 10.0, 0.1, 200.0, 1.0, "ms",
                    "How quickly compression clamps down once the signal exceeds the threshold."),
                new EffectParameterDescriptor(EffectParamNames.ReleaseMs, "Release", 100.0, 10.0, 1000.0, 10.0, "ms",
                    "How quickly compression lets go after the signal falls below the threshold."),
                new EffectParameterDescriptor(EffectParamNames.MakeupDb, "Make-up", 0.0, 0.0, 24.0, 0.5, "dB",
                    "Output gain to restore loudness lost to compression."),
            ]) { ShortCode = "CP" },

        new EffectDescriptor(
            EffectTypeIds.AudioReverb,
            "Reverb (Lite)",
            EffectCategory.Audio,
            "Adds room ambience (Freeverb-style) — the low-CPU editorial reverb.",
            [
                new EffectParameterDescriptor(EffectParamNames.RoomSize, "Room Size", 0.5, 0.0, 1.0, 0.05, "%",
                    "Apparent size of the simulated room — larger = longer tail.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Damping, "Damping", 0.5, 0.0, 1.0, 0.05, "%",
                    "How quickly high frequencies die away in the tail.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
                    "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 },
            ]) { ShortCode = "RV" },

        new EffectDescriptor(
            EffectTypeIds.AudioStudioReverb,
            "Studio Reverb",
            EffectCategory.Audio,
            "High-quality algorithmic reverb: rooms, plates, halls (Dattorro-style tank).",
            [
                new EffectParameterDescriptor(EffectParamNames.PreDelayMs, "Pre-Delay", 10.0, 0.0, 200.0, 1.0, "ms",
                    "Gap between the dry sound and the start of the reverb."),
                new EffectParameterDescriptor(EffectParamNames.Decay, "Decay", 2.0, 0.1, 20.0, 0.1, "s",
                    "How long the reverb tail takes to die away."),
                new EffectParameterDescriptor(EffectParamNames.Size, "Size", 0.5, 0.0, 1.0, 0.05, "%",
                    "Apparent size of the simulated space.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Diffusion, "Diffusion", 0.7, 0.0, 1.0, 0.05, "%",
                    "Echo density — low = discrete repeats, high = smooth wash.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.ModDepth, "Mod Depth", 0.3, 0.0, 1.0, 0.05, "%",
                    "Amount of pitch modulation in the tail (adds chorus-like movement).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.ModRateHz, "Mod Rate", 0.5, 0.05, 5.0, 0.05, "Hz",
                    "Speed of the tail's pitch modulation."),
                new EffectParameterDescriptor(EffectParamNames.EarlyLate, "Early / Late", 0.7, 0.0, 1.0, 0.05, "%",
                    "Balance of early reflections (0%) versus the late tail (100%).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Width, "Width", 1.0, 0.0, 1.0, 0.05, "%",
                    "Stereo spread of the reverb (0% = mono, 100% = full stereo).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.LowDamp, "Low Damp", 0.1, 0.0, 1.0, 0.05, "%",
                    "How quickly low frequencies die away in the tail.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.HighDamp, "High Damp", 0.4, 0.0, 1.0, 0.05, "%",
                    "How quickly high frequencies die away in the tail.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
                    "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 },
            ])
        {
            ShortCode = "SR",
            // The step-41 preset families (room / chamber / plate / hall / cathedral / ambient bloom); the
            // shimmer/cloud/nonlinear creative tiers ship as their own effects (steps 49–50). Every preset
            // leaves Mix untouched so switching character keeps the user's wet/dry blend.
            Presets =
            [
                new EffectPreset("Room", new Dictionary<string, double>
                {
                    [EffectParamNames.PreDelayMs] = 5, [EffectParamNames.Decay] = 0.6,
                    [EffectParamNames.Size] = 0.35, [EffectParamNames.Diffusion] = 0.65,
                    [EffectParamNames.ModDepth] = 0.15, [EffectParamNames.ModRateHz] = 0.8,
                    [EffectParamNames.EarlyLate] = 0.45, [EffectParamNames.Width] = 0.8,
                    [EffectParamNames.LowDamp] = 0.25, [EffectParamNames.HighDamp] = 0.55,
                }),
                new EffectPreset("Chamber", new Dictionary<string, double>
                {
                    [EffectParamNames.PreDelayMs] = 10, [EffectParamNames.Decay] = 1.1,
                    [EffectParamNames.Size] = 0.45, [EffectParamNames.Diffusion] = 0.75,
                    [EffectParamNames.ModDepth] = 0.2, [EffectParamNames.ModRateHz] = 0.6,
                    [EffectParamNames.EarlyLate] = 0.55, [EffectParamNames.Width] = 0.9,
                    [EffectParamNames.LowDamp] = 0.2, [EffectParamNames.HighDamp] = 0.45,
                }),
                new EffectPreset("Plate", new Dictionary<string, double>
                {
                    [EffectParamNames.PreDelayMs] = 0, [EffectParamNames.Decay] = 1.8,
                    [EffectParamNames.Size] = 0.5, [EffectParamNames.Diffusion] = 0.9,
                    [EffectParamNames.ModDepth] = 0.35, [EffectParamNames.ModRateHz] = 1.0,
                    [EffectParamNames.EarlyLate] = 0.85, [EffectParamNames.Width] = 1.0,
                    [EffectParamNames.LowDamp] = 0.1, [EffectParamNames.HighDamp] = 0.35,
                }),
                new EffectPreset("Hall", new Dictionary<string, double>
                {
                    [EffectParamNames.PreDelayMs] = 20, [EffectParamNames.Decay] = 2.8,
                    [EffectParamNames.Size] = 0.75, [EffectParamNames.Diffusion] = 0.7,
                    [EffectParamNames.ModDepth] = 0.25, [EffectParamNames.ModRateHz] = 0.4,
                    [EffectParamNames.EarlyLate] = 0.7, [EffectParamNames.Width] = 1.0,
                    [EffectParamNames.LowDamp] = 0.15, [EffectParamNames.HighDamp] = 0.4,
                }),
                new EffectPreset("Cathedral", new Dictionary<string, double>
                {
                    [EffectParamNames.PreDelayMs] = 40, [EffectParamNames.Decay] = 6.0,
                    [EffectParamNames.Size] = 1.0, [EffectParamNames.Diffusion] = 0.8,
                    [EffectParamNames.ModDepth] = 0.2, [EffectParamNames.ModRateHz] = 0.3,
                    [EffectParamNames.EarlyLate] = 0.85, [EffectParamNames.Width] = 1.0,
                    [EffectParamNames.LowDamp] = 0.05, [EffectParamNames.HighDamp] = 0.5,
                }),
                new EffectPreset("Ambient Bloom", new Dictionary<string, double>
                {
                    [EffectParamNames.PreDelayMs] = 60, [EffectParamNames.Decay] = 10.0,
                    [EffectParamNames.Size] = 0.9, [EffectParamNames.Diffusion] = 1.0,
                    [EffectParamNames.ModDepth] = 0.5, [EffectParamNames.ModRateHz] = 0.7,
                    [EffectParamNames.EarlyLate] = 1.0, [EffectParamNames.Width] = 1.0,
                    [EffectParamNames.LowDamp] = 0.3, [EffectParamNames.HighDamp] = 0.25,
                }),
            ],
        },

        // ── Delay family (PLAN.md step 46) — separate purpose-built effects, the DAW convention. ──
        new EffectDescriptor(
            EffectTypeIds.AudioDelayDigital,
            "Digital Delay",
            EffectCategory.Audio,
            "Clean feedback delay with a high-cut in the feedback path.",
            [
                new EffectParameterDescriptor(EffectParamNames.DelayMs, "Time", 500.0, 1.0, 2000.0, 1.0, "ms",
                    "Gap between the dry sound and each repeat."),
                new EffectParameterDescriptor(EffectParamNames.Feedback, "Feedback", 0.35, 0.0, 1.0, 0.05, "%",
                    "How much of each repeat feeds back — higher = more repeats.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.HighCutHz, "High Cut", 8000.0, 200.0, 20000.0, 100.0, "Hz",
                    "Filters highs out of the repeats — lower = darker echoes."),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
                    "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 },
            ]) { ShortCode = "DD" },

        new EffectDescriptor(
            EffectTypeIds.AudioDelayTape,
            "Tape Delay",
            EffectCategory.Audio,
            "Feedback delay with tape coloration: saturation, darkening repeats, wow & flutter.",
            [
                new EffectParameterDescriptor(EffectParamNames.DelayMs, "Time", 500.0, 1.0, 2000.0, 1.0, "ms",
                    "Gap between the dry sound and each repeat."),
                new EffectParameterDescriptor(EffectParamNames.Feedback, "Feedback", 0.4, 0.0, 1.0, 0.05, "%",
                    "How much of each repeat feeds back — higher = more repeats.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.WowFlutterDepth, "Wow / Flutter", 0.25, 0.0, 1.0, 0.05, "%",
                    "Amount of tape-style pitch wobble on the repeats.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.WowFlutterRateHz, "Wow Rate", 1.0, 0.1, 10.0, 0.1, "Hz",
                    "Speed of the pitch wobble."),
                new EffectParameterDescriptor(EffectParamNames.Drive, "Saturation", 0.3, 0.0, 1.0, 0.05, "%",
                    "Tape drive — adds warmth and grit to the repeats.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
                    "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 },
            ]) { ShortCode = "TD" },

        new EffectDescriptor(
            EffectTypeIds.AudioDelayMultiTap,
            "Multi-Tap Delay",
            EffectCategory.Audio,
            "Up to eight independent taps, each with its own time, level, and pan.",
            MultiTapParameters()) { ShortCode = "MT" },

        new EffectDescriptor(
            EffectTypeIds.AudioDelayStereo,
            "Stereo Delay",
            EffectCategory.Audio,
            "Independent left/right delay times with a Ping Pong cross-feed mode.",
            [
                new EffectParameterDescriptor(EffectParamNames.LeftTimeMs, "Left Time", 375.0, 1.0, 2000.0, 1.0, "ms",
                    "Delay time of the left channel's repeats."),
                new EffectParameterDescriptor(EffectParamNames.RightTimeMs, "Right Time", 500.0, 1.0, 2000.0, 1.0, "ms",
                    "Delay time of the right channel's repeats."),
                new EffectParameterDescriptor(EffectParamNames.Feedback, "Feedback", 0.35, 0.0, 1.0, 0.05, "%",
                    "How much of each repeat feeds back — higher = more repeats.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.PingPong, "Ping Pong", 0.0, 0.0, 1.0, 1.0,
                    Description: "Bounces the repeats alternately between left and right.",
                    Kind: ParameterKind.Toggle),
                new EffectParameterDescriptor(EffectParamNames.CrossFeed, "Cross-Feed", 1.0, 0.0, 1.0, 0.05, "%",
                    "How much each channel's repeats bleed into the other side.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
                    "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 },
            ]) { ShortCode = "SD" },

        // ── Noise Gate (PLAN.md step 47) — the standard DAW gate/expander design. ──
        new EffectDescriptor(
            EffectTypeIds.AudioNoiseGate,
            "Noise Gate",
            EffectCategory.Audio,
            "Attenuates signal below a threshold: attack / hold / release, range floor, hysteresis.",
            [
                new EffectParameterDescriptor(EffectParamNames.ThresholdDb, "Threshold", -40.0, -80.0, 0.0, 0.5, "dB",
                    "Level below which the gate closes and attenuates the signal."),
                new EffectParameterDescriptor(EffectParamNames.AttackMs, "Attack", 1.0, 0.01, 100.0, 0.1, "ms",
                    "How quickly the gate opens when the signal rises above the threshold."),
                new EffectParameterDescriptor(EffectParamNames.HoldMs, "Hold", 50.0, 0.0, 1000.0, 5.0, "ms",
                    "Minimum time the gate stays open after the signal drops."),
                new EffectParameterDescriptor(EffectParamNames.ReleaseMs, "Release", 100.0, 1.0, 2000.0, 10.0, "ms",
                    "How quickly the gate closes after the hold time expires."),
                new EffectParameterDescriptor(EffectParamNames.RangeDb, "Range", -80.0, -80.0, 0.0, 1.0, "dB",
                    "How far the closed gate turns the signal down (−80 dB ≈ silence)."),
                new EffectParameterDescriptor(EffectParamNames.HysteresisDb, "Hysteresis", 3.0, 0.0, 24.0, 0.5, "dB",
                    "Gap between the open and close thresholds, preventing rapid chatter."),
            ]) { ShortCode = "NG" },

        // ── Shelving EQ (PLAN.md step 48) — standalone low/high shelves for quick tone shaping. ──
        new EffectDescriptor(
            EffectTypeIds.AudioShelvingEq,
            "Shelving EQ",
            EffectCategory.Audio,
            "Standalone low + high shelves (frequency, gain, slope, per-shelf enable) for quick tilt / warmth / air.",
            [
                new EffectParameterDescriptor(EffectParamNames.LowFreq, "Low Freq", 100.0, 20.0, 500.0, 5.0, "Hz",
                    "Corner frequency of the low shelf."),
                new EffectParameterDescriptor(EffectParamNames.LowGainDb, "Low Gain", 0.0, -15.0, 15.0, 0.5, "dB",
                    "Boost or cut below the low shelf frequency."),
                new EffectParameterDescriptor(EffectParamNames.LowSlope, "Low Slope", 1.0, 0.1, 2.0, 0.05,
                    Description: "Steepness of the low shelf's transition."),
                new EffectParameterDescriptor(EffectParamNames.LowEnable, "Low Shelf", 1.0, 0.0, 1.0, 1.0,
                    Description: "Enables the low shelf.", Kind: ParameterKind.Toggle),
                new EffectParameterDescriptor(EffectParamNames.HighFreq, "High Freq", 8000.0, 2000.0, 16000.0, 100.0, "Hz",
                    "Corner frequency of the high shelf."),
                new EffectParameterDescriptor(EffectParamNames.HighGainDb, "High Gain", 0.0, -15.0, 15.0, 0.5, "dB",
                    "Boost or cut above the high shelf frequency."),
                new EffectParameterDescriptor(EffectParamNames.HighSlope, "High Slope", 1.0, 0.1, 2.0, 0.05,
                    Description: "Steepness of the high shelf's transition."),
                new EffectParameterDescriptor(EffectParamNames.HighEnable, "High Shelf", 1.0, 0.0, 1.0, 1.0,
                    Description: "Enables the high shelf.", Kind: ParameterKind.Toggle),
            ]) { ShortCode = "SE" },

        // ── Shimmer Reverb (PLAN.md step 50) — the "Creative Reverb ▸ shimmer" tier as its own effect. ──
        new EffectDescriptor(
            EffectTypeIds.AudioShimmerReverb,
            "Shimmer Reverb",
            EffectCategory.Audio,
            "Ethereal pitched-up reverb wash: an octave-shifted feedback path under a conventional tail.",
            [
                new EffectParameterDescriptor(EffectParamNames.ShimmerAmount, "Shimmer", 0.5, 0.0, 1.0, 0.05, "%",
                    "How much pitched-up signal feeds the tail — higher = more ethereal.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.ShimmerInterval, "Interval", 12.0, 1.0, 12.0, 1.0, "st",
                    "Pitch shift of the shimmer path, in semitones (12 = one octave up).",
                    Kind: ParameterKind.Integer),
                new EffectParameterDescriptor(EffectParamNames.Size, "Size", 0.5, 0.0, 1.0, 0.05, "%",
                    "Apparent size of the simulated space.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Decay, "Decay", 4.0, 0.1, 20.0, 0.1, "s",
                    "How long the reverb tail takes to die away."),
                new EffectParameterDescriptor(EffectParamNames.Damping, "Damping", 0.3, 0.0, 1.0, 0.05, "%",
                    "How quickly high frequencies die away in the tail.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
                    "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 },
            ])
        {
            ShortCode = "SH",
            // The step-50 preset family. Like the Studio Reverb presets, every preset leaves Mix untouched
            // so switching character keeps the user's wet/dry blend.
            Presets =
            [
                new EffectPreset("Classic Shimmer", new Dictionary<string, double>
                {
                    [EffectParamNames.ShimmerAmount] = 0.6, [EffectParamNames.ShimmerInterval] = 12,
                    [EffectParamNames.Size] = 0.7, [EffectParamNames.Decay] = 5.0,
                    [EffectParamNames.Damping] = 0.25,
                }),
                new EffectPreset("Dark Shimmer", new Dictionary<string, double>
                {
                    [EffectParamNames.ShimmerAmount] = 0.45, [EffectParamNames.ShimmerInterval] = 12,
                    [EffectParamNames.Size] = 0.6, [EffectParamNames.Decay] = 4.0,
                    [EffectParamNames.Damping] = 0.75,
                }),
                new EffectPreset("Fifth Shimmer", new Dictionary<string, double>
                {
                    [EffectParamNames.ShimmerAmount] = 0.55, [EffectParamNames.ShimmerInterval] = 7,
                    [EffectParamNames.Size] = 0.65, [EffectParamNames.Decay] = 4.5,
                    [EffectParamNames.Damping] = 0.3,
                }),
                new EffectPreset("Drone / Infinite", new Dictionary<string, double>
                {
                    [EffectParamNames.ShimmerAmount] = 1.0, [EffectParamNames.ShimmerInterval] = 12,
                    [EffectParamNames.Size] = 0.9, [EffectParamNames.Decay] = 20.0,
                    [EffectParamNames.Damping] = 0.1,
                }),
            ],
        },

        // ── Convolution Reverb (PLAN.md step 49) — the acoustic-emulation tier as its own effect. The IR is
        // an Asset descriptor (a file reference, not a number) so the Inspector builds a file-picker row; no
        // IRs are bundled at day one (licensing), so a fresh instance passes the dry signal through until the
        // user imports one. No factory presets: with user IRs the "preset" IS the impulse response.
        new EffectDescriptor(
            EffectTypeIds.AudioConvolutionReverb,
            "Convolution Reverb",
            EffectCategory.Audio,
            "Acoustic space emulation: convolves the signal with a captured impulse response (WAV).",
            [
                new EffectParameterDescriptor(EffectParamNames.ImpulseResponse, "Impulse Response", 0.0, 0.0, 0.0, 1.0,
                    Description: "The captured space — a mono or stereo WAV impulse response (up to 10 s).",
                    Kind: ParameterKind.Asset) { AssetType = new AssetFileType("WAV audio", ["wav"]) },
                new EffectParameterDescriptor(EffectParamNames.PreDelayMs, "Pre-Delay", 0.0, 0.0, 200.0, 1.0, "ms",
                    "Gap between the dry sound and the start of the reverb."),
                new EffectParameterDescriptor(EffectParamNames.IrLength, "Length", 1.0, 0.05, 1.0, 0.05, "%",
                    "How much of the impulse response plays — shorter trims the tail without a new IR.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.LowDamp, "Low Damp", 0.0, 0.0, 1.0, 0.05, "%",
                    "Thins low frequencies out of the reverb tail.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.HighDamp, "High Damp", 0.0, 0.0, 1.0, 0.05, "%",
                    "Darkens the reverb tail by rolling off high frequencies.") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Width, "Width", 1.0, 0.0, 1.0, 0.05, "%",
                    "Stereo spread of the reverb (0% = mono, 100% = as captured).") { DisplayScale = 100 },
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
                    "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 },
            ]) { ShortCode = "IR" },
    ];

    /// <summary>
    /// The Multi-Tap Delay's per-tap parameter grid (PLAN.md step 46): enable / time / level / pan ×
    /// <see cref="EffectParamNames.MultiTapCount"/> taps, then Mix. Defaults give two audible taps (an
    /// eighth-note-ish 150/300 ms pattern) with the rest staged at later times, disabled. The Inspector's
    /// generic one-row-per-parameter layout renders all 33 rows — a compact custom tap-grid section is a
    /// flagged UI follow-up, not a blocker (per the step-46 note).
    /// </summary>
    private static EffectParameterDescriptor[] MultiTapParameters()
    {
        var parameters = new EffectParameterDescriptor[EffectParamNames.MultiTapCount * 4 + 1];
        for (int i = 0; i < EffectParamNames.MultiTapCount; i++)
        {
            int tap = i + 1;
            parameters[i * 4 + 0] = new EffectParameterDescriptor(
                EffectParamNames.TapEnable[i], $"Tap {tap}", i < 2 ? 1.0 : 0.0, 0.0, 1.0, 1.0,
                Description: $"Enables tap {tap}.", Kind: ParameterKind.Toggle);
            parameters[i * 4 + 1] = new EffectParameterDescriptor(
                EffectParamNames.TapTimeMs[i], $"Tap {tap} Time", 150.0 * tap, 1.0, 2000.0, 1.0, "ms",
                $"Delay time of tap {tap}.");
            parameters[i * 4 + 2] = new EffectParameterDescriptor(
                EffectParamNames.TapLevel[i], $"Tap {tap} Level", Math.Round(1.0 - i * 0.1, 2), 0.0, 1.0, 0.05, "%",
                $"Volume of tap {tap}.") { DisplayScale = 100 };
            parameters[i * 4 + 3] = new EffectParameterDescriptor(
                EffectParamNames.TapPan[i], $"Tap {tap} Pan", 0.0, -1.0, 1.0, 0.05,
                Description: $"Stereo position of tap {tap} (−1 = left, +1 = right).");
        }
        parameters[^1] = new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05, "%",
            "Wet/dry balance (0% = dry only, 100% = effect only).") { DisplayScale = 100 };
        return parameters;
    }

    // Plugin-registered descriptors (PLAN.md step 33). Swapped atomically as a whole array so readers
    // (including the render graph's per-frame IsAudio routing) never see a partially-mutated list.
    private static readonly object RegistrationGate = new();
    private static volatile EffectDescriptor[] _registered = [];

    /// <summary>Built-in plus plugin-registered descriptors, built-ins first, in registration order.</summary>
    public static IReadOnlyList<EffectDescriptor> All
    {
        get
        {
            EffectDescriptor[] registered = _registered;
            return registered.Length == 0 ? BuiltIns : [.. BuiltIns, .. registered];
        }
    }

    /// <summary>
    /// Registers a plugin effect descriptor (PLAN.md step 33). Returns <see langword="false"/> — leaving the
    /// catalog unchanged — if the id is already taken or uses the reserved <c>builtin.</c> prefix.
    /// </summary>
    public static bool Register(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (RegistrationGate)
        {
            if (descriptor.Id.StartsWith("builtin.", StringComparison.Ordinal) || Find(descriptor.Id) is not null)
                return false;
            _registered = [.. _registered, descriptor];
            return true;
        }
    }

    /// <summary>Removes a plugin-registered descriptor (built-ins cannot be removed). Returns whether it was present.</summary>
    public static bool Unregister(string effectTypeId)
    {
        lock (RegistrationGate)
        {
            EffectDescriptor[] next = _registered.Where(d => d.Id != effectTypeId).ToArray();
            if (next.Length == _registered.Length)
                return false;
            _registered = next;
            return true;
        }
    }

    /// <summary>The descriptors in a given category, in display order.</summary>
    public static IEnumerable<EffectDescriptor> InCategory(EffectCategory category) =>
        All.Where(d => d.Category == category);

    /// <summary>Looks up a descriptor by effect type id, or returns <see langword="null"/> if it is not registered.</summary>
    public static EffectDescriptor? Find(string effectTypeId)
    {
        foreach (EffectDescriptor d in BuiltIns)
            if (d.Id == effectTypeId)
                return d;
        foreach (EffectDescriptor d in _registered)
            if (d.Id == effectTypeId)
                return d;
        return null;
    }

    /// <summary>A friendly display name for an effect type id, falling back to the id itself for unknown (plugin) ids.</summary>
    public static string DisplayName(string effectTypeId) => Find(effectTypeId)?.DisplayName ?? effectTypeId;
}
