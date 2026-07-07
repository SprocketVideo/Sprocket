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
    IReadOnlyList<string>? Choices = null);

/// <summary>
/// A named factory preset for an effect (PLAN.md step 41): the parameter values that give a recognisable
/// starting point (Room / Plate / Hall / Cathedral …). Values are constants applied over the descriptor's
/// defaults — parameters a preset omits keep their current value, so a preset can share the tweaks that
/// define it without flattening unrelated edits. The Inspector offers a preset picker for any descriptor
/// that carries presets; applying one is an ordinary undoable parameter edit.
/// </summary>
/// <param name="Name">Human-readable preset name shown in the picker.</param>
/// <param name="Values">Parameter values by name (keys match <see cref="EffectParamNames"/>).</param>
public sealed record EffectPreset(string Name, IReadOnlyDictionary<string, double> Values);

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
    /// Builds a fresh <see cref="EffectInstance"/> of this type with every parameter set to its
    /// <see cref="EffectParameterDescriptor.Default"/>. Each call yields an independent instance.
    /// </summary>
    public EffectInstance CreateInstance()
    {
        var instance = new EffectInstance(Id);
        foreach (EffectParameterDescriptor p in Parameters)
            instance.Set(p.Name, p.Default);
        return instance;
    }
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
                new EffectParameterDescriptor(EffectParamNames.Scale, "Scale", 1.0, 0.0, 4.0, 0.05,
                    Description: "Uniform size of the layer (1.0 = original size)."),
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
                new EffectParameterDescriptor(EffectParamNames.Opacity, "Opacity", 1.0, 0.0, 1.0, 0.05,
                    Description: "Layer transparency (1.0 = fully opaque, 0 = invisible)."),
            ]) { ShortCode = "TR" },

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
                new EffectParameterDescriptor(EffectParamNames.Opacity, "Opacity", 1.0, 0.0, 1.0, 0.05,
                    Description: "Clip opacity — also scales the clip's audio gain in step."),
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
                new EffectParameterDescriptor(EffectParamNames.RoomSize, "Room Size", 0.5, 0.0, 1.0, 0.05,
                    Description: "Apparent size of the simulated room — larger = longer tail."),
                new EffectParameterDescriptor(EffectParamNames.Damping, "Damping", 0.5, 0.0, 1.0, 0.05,
                    Description: "How quickly high frequencies die away in the tail."),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05,
                    Description: "Wet/dry balance (0 = dry only, 1 = effect only)."),
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
                new EffectParameterDescriptor(EffectParamNames.Size, "Size", 0.5, 0.0, 1.0, 0.05,
                    Description: "Apparent size of the simulated space."),
                new EffectParameterDescriptor(EffectParamNames.Diffusion, "Diffusion", 0.7, 0.0, 1.0, 0.05,
                    Description: "Echo density — low = discrete repeats, high = smooth wash."),
                new EffectParameterDescriptor(EffectParamNames.ModDepth, "Mod Depth", 0.3, 0.0, 1.0, 0.05,
                    Description: "Amount of pitch modulation in the tail (adds chorus-like movement)."),
                new EffectParameterDescriptor(EffectParamNames.ModRateHz, "Mod Rate", 0.5, 0.05, 5.0, 0.05, "Hz",
                    "Speed of the tail's pitch modulation."),
                new EffectParameterDescriptor(EffectParamNames.EarlyLate, "Early / Late", 0.7, 0.0, 1.0, 0.05,
                    Description: "Balance of early reflections (0) versus the late tail (1)."),
                new EffectParameterDescriptor(EffectParamNames.Width, "Width", 1.0, 0.0, 1.0, 0.05,
                    Description: "Stereo spread of the reverb (0 = mono, 1 = full stereo)."),
                new EffectParameterDescriptor(EffectParamNames.LowDamp, "Low Damp", 0.1, 0.0, 1.0, 0.05,
                    Description: "How quickly low frequencies die away in the tail."),
                new EffectParameterDescriptor(EffectParamNames.HighDamp, "High Damp", 0.4, 0.0, 1.0, 0.05,
                    Description: "How quickly high frequencies die away in the tail."),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05,
                    Description: "Wet/dry balance (0 = dry only, 1 = effect only)."),
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
                new EffectParameterDescriptor(EffectParamNames.Feedback, "Feedback", 0.35, 0.0, 1.0, 0.05,
                    Description: "How much of each repeat feeds back — higher = more repeats."),
                new EffectParameterDescriptor(EffectParamNames.HighCutHz, "High Cut", 8000.0, 200.0, 20000.0, 100.0, "Hz",
                    "Filters highs out of the repeats — lower = darker echoes."),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05,
                    Description: "Wet/dry balance (0 = dry only, 1 = effect only)."),
            ]) { ShortCode = "DD" },

        new EffectDescriptor(
            EffectTypeIds.AudioDelayTape,
            "Tape Delay",
            EffectCategory.Audio,
            "Feedback delay with tape coloration: saturation, darkening repeats, wow & flutter.",
            [
                new EffectParameterDescriptor(EffectParamNames.DelayMs, "Time", 500.0, 1.0, 2000.0, 1.0, "ms",
                    "Gap between the dry sound and each repeat."),
                new EffectParameterDescriptor(EffectParamNames.Feedback, "Feedback", 0.4, 0.0, 1.0, 0.05,
                    Description: "How much of each repeat feeds back — higher = more repeats."),
                new EffectParameterDescriptor(EffectParamNames.WowFlutterDepth, "Wow / Flutter", 0.25, 0.0, 1.0, 0.05,
                    Description: "Amount of tape-style pitch wobble on the repeats."),
                new EffectParameterDescriptor(EffectParamNames.WowFlutterRateHz, "Wow Rate", 1.0, 0.1, 10.0, 0.1, "Hz",
                    "Speed of the pitch wobble."),
                new EffectParameterDescriptor(EffectParamNames.Drive, "Saturation", 0.3, 0.0, 1.0, 0.05,
                    Description: "Tape drive — adds warmth and grit to the repeats."),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05,
                    Description: "Wet/dry balance (0 = dry only, 1 = effect only)."),
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
                new EffectParameterDescriptor(EffectParamNames.Feedback, "Feedback", 0.35, 0.0, 1.0, 0.05,
                    Description: "How much of each repeat feeds back — higher = more repeats."),
                new EffectParameterDescriptor(EffectParamNames.PingPong, "Ping Pong", 0.0, 0.0, 1.0, 1.0,
                    Description: "Bounces the repeats alternately between left and right.",
                    Kind: ParameterKind.Toggle),
                new EffectParameterDescriptor(EffectParamNames.CrossFeed, "Cross-Feed", 1.0, 0.0, 1.0, 0.05,
                    Description: "How much each channel's repeats bleed into the other side."),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05,
                    Description: "Wet/dry balance (0 = dry only, 1 = effect only)."),
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
                new EffectParameterDescriptor(EffectParamNames.ShimmerAmount, "Shimmer", 0.5, 0.0, 1.0, 0.05,
                    Description: "How much pitched-up signal feeds the tail — higher = more ethereal."),
                new EffectParameterDescriptor(EffectParamNames.ShimmerInterval, "Interval", 12.0, 1.0, 12.0, 1.0, "st",
                    "Pitch shift of the shimmer path, in semitones (12 = one octave up).",
                    Kind: ParameterKind.Integer),
                new EffectParameterDescriptor(EffectParamNames.Size, "Size", 0.5, 0.0, 1.0, 0.05,
                    Description: "Apparent size of the simulated space."),
                new EffectParameterDescriptor(EffectParamNames.Decay, "Decay", 4.0, 0.1, 20.0, 0.1, "s",
                    "How long the reverb tail takes to die away."),
                new EffectParameterDescriptor(EffectParamNames.Damping, "Damping", 0.3, 0.0, 1.0, 0.05,
                    Description: "How quickly high frequencies die away in the tail."),
                new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05,
                    Description: "Wet/dry balance (0 = dry only, 1 = effect only)."),
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
                EffectParamNames.TapLevel[i], $"Tap {tap} Level", Math.Round(1.0 - i * 0.1, 2), 0.0, 1.0, 0.05,
                Description: $"Volume of tap {tap}.");
            parameters[i * 4 + 3] = new EffectParameterDescriptor(
                EffectParamNames.TapPan[i], $"Tap {tap} Pan", 0.0, -1.0, 1.0, 0.05,
                Description: $"Stereo position of tap {tap} (−1 = left, +1 = right).");
        }
        parameters[^1] = new EffectParameterDescriptor(EffectParamNames.Mix, "Mix", 0.3, 0.0, 1.0, 0.05,
            Description: "Wet/dry balance (0 = dry only, 1 = effect only).");
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
