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
    /// ACES filmic tone mapping (PLAN.md step 33): sRGB → scene-linear → exposure → the fitted ACES
    /// RRT + ODT curve → sRGB, all in the shader. Parameter: <see cref="EffectParamNames.Exposure"/> (stops).
    /// The first built-in realised through the <see cref="Sprocket.Core.Rendering.IVideoEffect"/> registry
    /// (the same path plugin effects use) rather than a hard-coded pipeline case.
    /// </summary>
    public const string AcesFilmic = "builtin.aces.filmic";

    /// <summary>
    /// White balance (PLAN.md step 34): temperature / tint gains applied in linear light, the standard
    /// first grading correction (Lumetri / Resolve convention: warm = positive temperature, magenta =
    /// positive tint). Parameters: <see cref="EffectParamNames.Temperature"/>, <see cref="EffectParamNames.Tint"/>.
    /// </summary>
    public const string WhiteBalance = "builtin.whitebalance";

    /// <summary>
    /// Lift / gamma / gain colour wheels (PLAN.md step 34): the three-way tonal grade every professional
    /// grading page centres on — lift moves shadows, gamma mids, gain highlights, each with a master and
    /// per-channel R/G/B component (<see cref="EffectParamNames.LiftMaster"/> … <see cref="EffectParamNames.GainB"/>).
    /// </summary>
    public const string ColorWheels = "builtin.colorwheels";

    /// <summary>
    /// Parametric curves (PLAN.md step 34): RGB (master) + per-channel red/green/blue curves, each a
    /// five-point parametric curve (blacks / shadows / mids / highlights / whites at fixed inputs
    /// 0 / ¼ / ½ / ¾ / 1) whose points offset the identity — the Lightroom-style parametric form, which
    /// keeps every point an animatable scalar (<see cref="EffectParamNames.CurveMasterBlacks"/> …).
    /// </summary>
    public const string Curves = "builtin.curves";

    /// <summary>
    /// HSL qualifier / secondary (PLAN.md step 34): keys a hue/saturation/luma range and grades only the
    /// keyed pixels (hue shift, saturation, exposure), with a mask preview — the standard secondary
    /// correction. Parameters: <see cref="EffectParamNames.HueCenter"/>, <see cref="EffectParamNames.HueWidth"/>,
    /// <see cref="EffectParamNames.HueSoftness"/>, <see cref="EffectParamNames.SatLow"/>/<see cref="EffectParamNames.SatHigh"/>,
    /// <see cref="EffectParamNames.LumaLow"/>/<see cref="EffectParamNames.LumaHigh"/>,
    /// <see cref="EffectParamNames.RangeSoftness"/>, <see cref="EffectParamNames.HueShift"/>,
    /// <see cref="EffectParamNames.Saturation"/>, <see cref="EffectParamNames.Exposure"/>,
    /// <see cref="EffectParamNames.ShowMask"/>.
    /// </summary>
    public const string HslQualifier = "builtin.hsl.qualify";

    /// <summary>
    /// Input color transform (PLAN.md step 37): converts a log-encoded source (DJI D-Log / D-Log M) to the
    /// working/display space by sampling a bundled camera-vendor 3D LUT on the GPU — the per-clip "input
    /// transform" every professional NLE applies before creative grading, so it belongs at the <b>front</b>
    /// of the clip's effect stack. Parameter: <see cref="EffectParamNames.SourceProfile"/> (an index into
    /// <see cref="ColorProfiles.All"/>). Bypass = the standard <see cref="EffectInstance.Enabled"/> toggle;
    /// the target space is fixed at Rec.709 until the step-33 OCIO/ACES upgrade.
    /// </summary>
    public const string ColorTransform = "builtin.colortransform";

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
    /// Studio Reverb (PLAN.md step 41): the realtime high-quality algorithmic tier — a Dattorro-style
    /// plate/hall with predelay, early reflections, modulated tank delay lines, independent low/high tail
    /// damping, and stereo width, next to the cheap Freeverb-style <see cref="AudioReverb"/> ("Reverb (Lite)").
    /// Parameters: <see cref="EffectParamNames.PreDelayMs"/>, <see cref="EffectParamNames.Decay"/>,
    /// <see cref="EffectParamNames.Size"/>, <see cref="EffectParamNames.Diffusion"/>,
    /// <see cref="EffectParamNames.ModDepth"/>/<see cref="EffectParamNames.ModRateHz"/>,
    /// <see cref="EffectParamNames.EarlyLate"/>, <see cref="EffectParamNames.Width"/>,
    /// <see cref="EffectParamNames.LowDamp"/>/<see cref="EffectParamNames.HighDamp"/>,
    /// <see cref="EffectParamNames.Mix"/>.
    /// </summary>
    public const string AudioStudioReverb = "builtin.audio.reverb.studio";

    /// <summary>
    /// Digital Delay (PLAN.md step 46): the clean baseline feedback delay — a single sample-accurate delay
    /// line with feedback and a high-cut in the feedback path so repeats can darken. Parameters:
    /// <see cref="EffectParamNames.DelayMs"/>, <see cref="EffectParamNames.Feedback"/>,
    /// <see cref="EffectParamNames.HighCutHz"/>, <see cref="EffectParamNames.Mix"/>.
    /// </summary>
    public const string AudioDelayDigital = "builtin.audio.delay.digital";

    /// <summary>
    /// Tape Delay (PLAN.md step 46): the feedback-delay core plus tape coloration — soft saturation and a
    /// fixed gentle low-pass in the feedback path, and deterministic wow &amp; flutter delay-time modulation.
    /// Parameters: <see cref="EffectParamNames.DelayMs"/>, <see cref="EffectParamNames.Feedback"/>,
    /// <see cref="EffectParamNames.WowFlutterDepth"/>/<see cref="EffectParamNames.WowFlutterRateHz"/>,
    /// <see cref="EffectParamNames.Drive"/>, <see cref="EffectParamNames.Mix"/>.
    /// </summary>
    public const string AudioDelayTape = "builtin.audio.delay.tape";

    /// <summary>
    /// Multi-Tap Delay (PLAN.md step 46): up to <see cref="EffectParamNames.MultiTapCount"/> independent taps,
    /// each with its own enable / delay time / level / pan (<see cref="EffectParamNames.TapEnable"/> …),
    /// summed into the output — rhythmic echo patterns from one instance. Plus <see cref="EffectParamNames.Mix"/>.
    /// </summary>
    public const string AudioDelayMultiTap = "builtin.audio.delay.multitap";

    /// <summary>
    /// Stereo Delay (PLAN.md step 46): independent left/right delay times with shared feedback and a
    /// Ping Pong mode that cross-feeds each channel's repeats into the opposite channel. Parameters:
    /// <see cref="EffectParamNames.LeftTimeMs"/>/<see cref="EffectParamNames.RightTimeMs"/>,
    /// <see cref="EffectParamNames.Feedback"/>, <see cref="EffectParamNames.PingPong"/>,
    /// <see cref="EffectParamNames.CrossFeed"/>, <see cref="EffectParamNames.Mix"/>.
    /// </summary>
    public const string AudioDelayStereo = "builtin.audio.delay.stereo";

    /// <summary>
    /// Noise Gate (PLAN.md step 47): the standard DAW gate design — an envelope follower drives a gain that
    /// opens above <see cref="EffectParamNames.ThresholdDb"/> and closes once the signal has stayed below the
    /// close threshold (threshold − <see cref="EffectParamNames.HysteresisDb"/>) for
    /// <see cref="EffectParamNames.HoldMs"/>, ramping through <see cref="EffectParamNames.AttackMs"/> /
    /// <see cref="EffectParamNames.ReleaseMs"/> and attenuating to the <see cref="EffectParamNames.RangeDb"/>
    /// floor rather than hard on/off (the Pro Tools / Logic "Range" convention).
    /// </summary>
    public const string AudioNoiseGate = "builtin.audio.noisegate";

    /// <summary>
    /// Shelving EQ (PLAN.md step 48): standalone low-shelf + high-shelf tone shaping — the DAW convention
    /// (Ableton EQ Three / Logic Channel EQ's shelf-only use) for the quick tilt/warmth/air pass where a full
    /// 3-band parametric is heavier than needed. Each shelf is an RBJ biquad (the same derivation as
    /// <see cref="AudioEq"/>'s shelf bands, generalized from the fixed slope S = 1 to a variable
    /// <see cref="EffectParamNames.LowSlope"/>/<see cref="EffectParamNames.HighSlope"/>) with its own
    /// <see cref="EffectParamNames.LowEnable"/>/<see cref="EffectParamNames.HighEnable"/> so either shelf can
    /// run alone. Parameters: <see cref="EffectParamNames.LowFreq"/>/<see cref="EffectParamNames.LowGainDb"/>/
    /// <see cref="EffectParamNames.LowSlope"/>/<see cref="EffectParamNames.LowEnable"/>,
    /// <see cref="EffectParamNames.HighFreq"/>/<see cref="EffectParamNames.HighGainDb"/>/
    /// <see cref="EffectParamNames.HighSlope"/>/<see cref="EffectParamNames.HighEnable"/>.
    /// </summary>
    public const string AudioShelvingEq = "builtin.audio.shelvingeq";

    /// <summary>
    /// Shimmer Reverb (PLAN.md step 50): a Freeverb-style tail with an octave-shifted (granular
    /// pitch-shifted) feedback path layered underneath — the ethereal, pitched-up ambient wash, shipped
    /// as its own dedicated effect rather than a mode of <see cref="AudioReverb"/> (the DAW convention:
    /// Valhalla Shimmer, Ableton's Shimmer device). Parameters: <see cref="EffectParamNames.ShimmerAmount"/>,
    /// <see cref="EffectParamNames.ShimmerInterval"/>, <see cref="EffectParamNames.Size"/>,
    /// <see cref="EffectParamNames.Decay"/>, <see cref="EffectParamNames.Damping"/>,
    /// <see cref="EffectParamNames.Mix"/>.
    /// </summary>
    public const string AudioShimmerReverb = "builtin.audio.reverb.shimmer";

    /// <summary>
    /// Whether an effect type id names an <b>audio</b> chain stage (PLAN.md step 31). The render graph uses
    /// this to split a clip's single effect stack: audio ids feed the mixer's DSP chain, everything else feeds
    /// the video shader chain (where unknown ids pass through). Built-in audio effects share the
    /// <c>builtin.audio.</c> prefix (the fast path); a plugin audio effect (PLAN.md step 33) is recognised by
    /// its registered <see cref="EffectCatalog"/> descriptor carrying <see cref="EffectCategory.Audio"/>.
    /// An unregistered (missing-plugin) audio id therefore routes to the video chain, where unknown ids
    /// pass through — a no-op either way.
    /// </summary>
    public static bool IsAudio(string effectTypeId) =>
        effectTypeId.StartsWith("builtin.audio.", StringComparison.Ordinal)
        || (!effectTypeId.StartsWith("builtin.", StringComparison.Ordinal)
            && EffectCatalog.Find(effectTypeId)?.Category == EffectCategory.Audio);

    /// <summary>
    /// The position of <paramref name="target"/> among <paramref name="effects"/>'s <em>enabled, audio</em>
    /// entries, in order — the same filter <c>RenderGraph.ResolveAudioChain</c> applies when building the live
    /// DSP chain (enabled + <see cref="IsAudio"/>), so a UI can map a model effect to its index in the mixer's
    /// running <see cref="Audio.IAudioEffect"/> array (<c>AudioMixer.TryPeekEffect</c>) for effect-specific
    /// metering (e.g. the Compressor's gain-reduction readout, PLAN.md step 31). Returns -1 if the target is
    /// disabled, not an audio effect, or not present — any of which mean it isn't actually running in the chain.
    /// </summary>
    public static int AudioChainIndexOf(IReadOnlyList<EffectInstance> effects, EffectInstance target)
    {
        int index = 0;
        foreach (EffectInstance effect in effects)
        {
            if (!effect.Enabled || !IsAudio(effect.EffectTypeId))
                continue;
            if (ReferenceEquals(effect, target))
                return index;
            index++;
        }
        return -1;
    }
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

    /// <summary>Saturation (1.0 = unchanged, 0 = greyscale) — <see cref="EffectTypeIds.Color"/> and the
    /// <see cref="EffectTypeIds.HslQualifier"/> correction.</summary>
    public const string Saturation = "saturation";

    /// <summary>Vibrance in [-1, 1] (0 = unchanged): saturation weighted toward already-muted colours —
    /// <see cref="EffectTypeIds.Color"/> (PLAN.md step 34).</summary>
    public const string Vibrance = "vibrance";

    /// <summary>Colour temperature in [-100, 100] (0 = neutral, positive = warmer) — <see cref="EffectTypeIds.WhiteBalance"/>.</summary>
    public const string Temperature = "temperature";

    /// <summary>Tint in [-100, 100] (0 = neutral, positive = magenta, negative = green) — <see cref="EffectTypeIds.WhiteBalance"/>.</summary>
    public const string Tint = "tint";

    // ── Lift / gamma / gain wheels (PLAN.md step 34) — each in [-1, 1], 0 = neutral. ──
    /// <summary>Lift (shadows) master — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftMaster = "liftMaster";
    /// <summary>Lift red component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftR = "liftR";
    /// <summary>Lift green component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftG = "liftG";
    /// <summary>Lift blue component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string LiftB = "liftB";
    /// <summary>Gamma (mids) master — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaMaster = "gammaMaster";
    /// <summary>Gamma red component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaR = "gammaR";
    /// <summary>Gamma green component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaG = "gammaG";
    /// <summary>Gamma blue component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GammaB = "gammaB";
    /// <summary>Gain (highlights) master — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainMaster = "gainMaster";
    /// <summary>Gain red component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainR = "gainR";
    /// <summary>Gain green component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainG = "gainG";
    /// <summary>Gain blue component — <see cref="EffectTypeIds.ColorWheels"/>.</summary>
    public const string GainB = "gainB";

    // ── Parametric curves (PLAN.md step 34) — five points per channel at inputs 0/¼/½/¾/1, each an
    // output offset in [-1, 1] added to the identity (0 = unchanged). ──
    /// <summary>Master (RGB) curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterBlacks = "curveMasterBlacks";
    /// <summary>Master (RGB) curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterShadows = "curveMasterShadows";
    /// <summary>Master (RGB) curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterMids = "curveMasterMids";
    /// <summary>Master (RGB) curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterHighlights = "curveMasterHighlights";
    /// <summary>Master (RGB) curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveMasterWhites = "curveMasterWhites";
    /// <summary>Red curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedBlacks = "curveRedBlacks";
    /// <summary>Red curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedShadows = "curveRedShadows";
    /// <summary>Red curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedMids = "curveRedMids";
    /// <summary>Red curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedHighlights = "curveRedHighlights";
    /// <summary>Red curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveRedWhites = "curveRedWhites";
    /// <summary>Green curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenBlacks = "curveGreenBlacks";
    /// <summary>Green curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenShadows = "curveGreenShadows";
    /// <summary>Green curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenMids = "curveGreenMids";
    /// <summary>Green curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenHighlights = "curveGreenHighlights";
    /// <summary>Green curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveGreenWhites = "curveGreenWhites";
    /// <summary>Blue curve blacks point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueBlacks = "curveBlueBlacks";
    /// <summary>Blue curve shadows point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueShadows = "curveBlueShadows";
    /// <summary>Blue curve mids point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueMids = "curveBlueMids";
    /// <summary>Blue curve highlights point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueHighlights = "curveBlueHighlights";
    /// <summary>Blue curve whites point — <see cref="EffectTypeIds.Curves"/>.</summary>
    public const string CurveBlueWhites = "curveBlueWhites";

    // ── HSL qualifier (PLAN.md step 34). ──
    /// <summary>Keyed hue centre in degrees [0, 360) — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueCenter = "hueCenter";
    /// <summary>Keyed hue half-width in degrees — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueWidth = "hueWidth";
    /// <summary>Hue key edge softness in degrees — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueSoftness = "hueSoftness";
    /// <summary>Keyed saturation range lower bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string SatLow = "satLow";
    /// <summary>Keyed saturation range upper bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string SatHigh = "satHigh";
    /// <summary>Keyed luma range lower bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string LumaLow = "lumaLow";
    /// <summary>Keyed luma range upper bound in [0, 1] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string LumaHigh = "lumaHigh";
    /// <summary>Saturation/luma key edge softness in [0, 0.5] — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string RangeSoftness = "rangeSoftness";
    /// <summary>Hue rotation applied to keyed pixels, in degrees — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string HueShift = "hueShift";
    /// <summary>Mask preview toggle (≥ 0.5 shows the key as greyscale) — <see cref="EffectTypeIds.HslQualifier"/>.</summary>
    public const string ShowMask = "showMask";

    /// <summary>Source log profile index into <see cref="ColorProfiles.All"/> —
    /// <see cref="EffectTypeIds.ColorTransform"/>.</summary>
    public const string SourceProfile = "sourceProfile";

    /// <summary>Gain in decibels (0 = unity) — <see cref="EffectTypeIds.AudioGain"/>.</summary>
    public const string GainDb = "gainDb";

    /// <summary>Stereo balance in [-1, 1] (0 = centre) — <see cref="EffectTypeIds.AudioGain"/>.</summary>
    public const string Pan = "pan";

    /// <summary>Low-shelf gain in dB — <see cref="EffectTypeIds.AudioEq"/> and
    /// <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string LowGainDb = "lowGainDb";

    /// <summary>Low-shelf corner frequency in Hz — <see cref="EffectTypeIds.AudioEq"/> and
    /// <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string LowFreq = "lowFreq";

    /// <summary>Mid-peak gain in dB — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidGainDb = "midGainDb";

    /// <summary>Mid-peak centre frequency in Hz — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidFreq = "midFreq";

    /// <summary>Mid-peak Q (bandwidth) — <see cref="EffectTypeIds.AudioEq"/>.</summary>
    public const string MidQ = "midQ";

    /// <summary>High-shelf gain in dB — <see cref="EffectTypeIds.AudioEq"/> and
    /// <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string HighGainDb = "highGainDb";

    /// <summary>High-shelf corner frequency in Hz — <see cref="EffectTypeIds.AudioEq"/> and
    /// <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string HighFreq = "highFreq";

    /// <summary>Threshold in dBFS — the compressor's knee (<see cref="EffectTypeIds.AudioCompressor"/>) and
    /// the noise gate's open threshold (<see cref="EffectTypeIds.AudioNoiseGate"/>).</summary>
    public const string ThresholdDb = "thresholdDb";

    /// <summary>Compression ratio (N:1) — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string Ratio = "ratio";

    /// <summary>Attack time in milliseconds — <see cref="EffectTypeIds.AudioCompressor"/> and
    /// <see cref="EffectTypeIds.AudioNoiseGate"/> (gain-opening ramp).</summary>
    public const string AttackMs = "attackMs";

    /// <summary>Release time in milliseconds — <see cref="EffectTypeIds.AudioCompressor"/> and
    /// <see cref="EffectTypeIds.AudioNoiseGate"/> (gain-closing ramp).</summary>
    public const string ReleaseMs = "releaseMs";

    /// <summary>Compressor make-up gain in dB — <see cref="EffectTypeIds.AudioCompressor"/>.</summary>
    public const string MakeupDb = "makeupDb";

    /// <summary>Reverb room size in [0, 1] — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string RoomSize = "roomSize";

    /// <summary>Reverb high-frequency damping in [0, 1] — <see cref="EffectTypeIds.AudioReverb"/>.</summary>
    public const string Damping = "damping";

    /// <summary>Reverb wet/dry mix in [0, 1] (0 = dry only) — <see cref="EffectTypeIds.AudioReverb"/> and
    /// <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string Mix = "mix";

    // ── Studio Reverb (PLAN.md step 41). ──
    /// <summary>Pre-delay before the reverb onset, in milliseconds — <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string PreDelayMs = "preDelayMs";

    /// <summary>Reverb decay (RT60-style tail length) in seconds — <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string Decay = "decay";

    /// <summary>Room size in [0, 1] (scales the tank delay lengths) — <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string Size = "size";

    /// <summary>Diffusion in [0, 1] (input/tank allpass density — low = discrete echoes, high = smooth wash) —
    /// <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string Diffusion = "diffusion";

    /// <summary>Tank delay-line modulation depth in [0, 1] (chorusing that breaks up metallic ringing) —
    /// <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string ModDepth = "modDepth";

    /// <summary>Tank delay-line modulation rate in Hz — <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string ModRateHz = "modRateHz";

    /// <summary>Early-reflections vs late-tail balance in [0, 1] (0 = early only, 1 = tail only) —
    /// <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string EarlyLate = "earlyLate";

    /// <summary>Stereo width of the wet signal in [0, 1] (0 = mono, 1 = full decorrelated stereo) —
    /// <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string Width = "width";

    /// <summary>Low-frequency tail damping in [0, 1] (0 = lows ring as long as everything else) —
    /// <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string LowDamp = "lowDamp";

    /// <summary>High-frequency tail damping in [0, 1] (0 = bright tail, 1 = dark) —
    /// <see cref="EffectTypeIds.AudioStudioReverb"/>.</summary>
    public const string HighDamp = "highDamp";

    // ── Delay family (PLAN.md step 46). ──
    /// <summary>Delay time in milliseconds — <see cref="EffectTypeIds.AudioDelayDigital"/> and
    /// <see cref="EffectTypeIds.AudioDelayTape"/>. (Note-synced divisions await a project tempo model.)</summary>
    public const string DelayMs = "delayMs";

    /// <summary>Feedback amount in [0, 1] (repeat-to-repeat level; the DSP clamps below unity so the loop
    /// always decays) — the step-46 delays.</summary>
    public const string Feedback = "feedback";

    /// <summary>Feedback-path high-cut corner in Hz (repeats darken with each pass; at the 20 kHz ceiling the
    /// filter is bypassed for a bit-clean repeat) — <see cref="EffectTypeIds.AudioDelayDigital"/>.</summary>
    public const string HighCutHz = "highCutHz";

    /// <summary>Wow &amp; flutter depth in [0, 1] (delay-time modulation; deterministic LFOs, no RNG) —
    /// <see cref="EffectTypeIds.AudioDelayTape"/>.</summary>
    public const string WowFlutterDepth = "wowFlutterDepth";

    /// <summary>Wow rate in Hz (flutter runs at a fixed multiple above it) —
    /// <see cref="EffectTypeIds.AudioDelayTape"/>.</summary>
    public const string WowFlutterRateHz = "wowFlutterRateHz";

    /// <summary>Tape saturation amount in [0, 1] (soft-clip drive in the feedback path) —
    /// <see cref="EffectTypeIds.AudioDelayTape"/>.</summary>
    public const string Drive = "drive";

    /// <summary>Left-channel delay time in milliseconds — <see cref="EffectTypeIds.AudioDelayStereo"/>.</summary>
    public const string LeftTimeMs = "leftTimeMs";

    /// <summary>Right-channel delay time in milliseconds — <see cref="EffectTypeIds.AudioDelayStereo"/>.</summary>
    public const string RightTimeMs = "rightTimeMs";

    /// <summary>Ping Pong mode toggle (≥ 0.5 = on): each channel's repeats cross-feed into the opposite
    /// channel — <see cref="EffectTypeIds.AudioDelayStereo"/>.</summary>
    public const string PingPong = "pingPong";

    /// <summary>Cross-feed amount in [0, 1] (how much of each repeat crosses channels when Ping Pong is on;
    /// 1 = the classic full bounce) — <see cref="EffectTypeIds.AudioDelayStereo"/>.</summary>
    public const string CrossFeed = "crossFeed";

    // ── Noise Gate (PLAN.md step 47). ──
    /// <summary>Hold time in milliseconds — how long the gate stays open after the signal falls below the
    /// close threshold, so it doesn't clip off decaying tails or close on a brief dip —
    /// <see cref="EffectTypeIds.AudioNoiseGate"/>.</summary>
    public const string HoldMs = "holdMs";

    /// <summary>Range (closed-gate floor) in dB — how far the gain closes below the threshold: 0 = no
    /// attenuation, the −80 dB minimum ≈ full mute (a partial attenuation floor, matching Pro Tools /
    /// Logic's "Range", rather than a hard on/off) — <see cref="EffectTypeIds.AudioNoiseGate"/>.</summary>
    public const string RangeDb = "rangeDb";

    /// <summary>Hysteresis in dB — the close threshold sits this far below the open threshold so input
    /// straddling the threshold cannot rapidly re-trigger the gate (chatter) —
    /// <see cref="EffectTypeIds.AudioNoiseGate"/>.</summary>
    public const string HysteresisDb = "hysteresisDb";

    // ── Shelving EQ (PLAN.md step 48). ──
    /// <summary>Low-shelf RBJ shelf slope S (1 = the classic maximally steep monotonic shelf; smaller =
    /// gentler transition) — <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string LowSlope = "lowSlope";

    /// <summary>Low-shelf enable toggle (≥ 0.5 = on) so either shelf can run alone —
    /// <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string LowEnable = "lowEnable";

    /// <summary>High-shelf RBJ shelf slope S — <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string HighSlope = "highSlope";

    /// <summary>High-shelf enable toggle (≥ 0.5 = on) — <see cref="EffectTypeIds.AudioShelvingEq"/>.</summary>
    public const string HighEnable = "highEnable";

    // ── Shimmer Reverb (PLAN.md step 50). ──
    /// <summary>Shimmer amount in [0, 1] — the level of the pitch-shifted feedback path re-entering the
    /// reverb (0 = the plain base tail; 1 = a near-sustained pitched wash). The effect maps this onto a
    /// loop gain clamped below unity-with-margin, so no setting can run away —
    /// <see cref="EffectTypeIds.AudioShimmerReverb"/>.</summary>
    public const string ShimmerAmount = "shimmerAmount";

    /// <summary>Shimmer pitch interval in semitones (+12 = the canonical octave; +7 = the fifth variant) —
    /// <see cref="EffectTypeIds.AudioShimmerReverb"/>.</summary>
    public const string ShimmerInterval = "shimmerInterval";

    /// <summary>The fixed tap cap of the Multi-Tap Delay (PLAN.md step 46) — matches typical DAW
    /// multi-tap plugins.</summary>
    public const int MultiTapCount = 8;

    /// <summary>Per-tap enable toggles (≥ 0.5 = on), <c>tap1Enable</c> … <c>tap8Enable</c> —
    /// <see cref="EffectTypeIds.AudioDelayMultiTap"/>. Indexed 0-based; precomputed so per-block DSP
    /// parameter lookups allocate nothing.</summary>
    public static readonly IReadOnlyList<string> TapEnable = BuildTapNames("Enable");

    /// <summary>Per-tap delay times in milliseconds, <c>tap1TimeMs</c> … — <see cref="EffectTypeIds.AudioDelayMultiTap"/>.</summary>
    public static readonly IReadOnlyList<string> TapTimeMs = BuildTapNames("TimeMs");

    /// <summary>Per-tap levels in [0, 1], <c>tap1Level</c> … — <see cref="EffectTypeIds.AudioDelayMultiTap"/>.</summary>
    public static readonly IReadOnlyList<string> TapLevel = BuildTapNames("Level");

    /// <summary>Per-tap pans in [-1, 1], <c>tap1Pan</c> … — <see cref="EffectTypeIds.AudioDelayMultiTap"/>.</summary>
    public static readonly IReadOnlyList<string> TapPan = BuildTapNames("Pan");

    private static string[] BuildTapNames(string suffix)
    {
        var names = new string[MultiTapCount];
        for (int i = 0; i < MultiTapCount; i++)
            names[i] = $"tap{i + 1}{suffix}";
        return names;
    }
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

    /// <summary>
    /// Whether the effect is applied when rendering. Disabling an effect (rather than removing it) keeps its
    /// parameters/keyframes intact for later re-enabling. The render graph skips disabled effects entirely
    /// (<see cref="Sprocket.Core.Rendering.RenderGraph"/>), and it is part of the persisted/hashed state
    /// (§12, §20) so toggling it invalidates any render-cache segment covering the clip.
    /// </summary>
    public bool Enabled { get; set; } = true;

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
        var copy = new EffectInstance(EffectTypeId) { Enabled = Enabled };
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
        var copy = new EffectInstance(EffectTypeId) { Enabled = Enabled };
        foreach ((string name, AnimatableValue value) in Parameters)
            copy.Parameters[name] = value.Shifted(delta);
        return copy;
    }

    /// <summary>
    /// Shifts every animated parameter's keyframes by <paramref name="delta"/> <em>in place</em> (see
    /// <see cref="AnimatableValue.Shifted"/>; constants are untouched) — the mutating counterpart of
    /// <see cref="CloneShifted"/>. Keyframe times are absolute timeline time, so when a clip is <em>moved</em>
    /// its effects' keyframes (e.g. a fade's opacity ramp) must shift by the same delta to stay aligned with
    /// the clip (PLAN.md step 39); the placement commands call this alongside setting
    /// <see cref="Clip.TimelineStart"/>.
    /// </summary>
    public void ShiftKeyframes(Timecode delta)
    {
        if (delta.Ticks == 0)
            return;
        List<KeyValuePair<string, AnimatableValue>>? shifted = null;
        foreach ((string name, AnimatableValue value) in Parameters)
        {
            AnimatableValue s = value.Shifted(delta);
            if (!ReferenceEquals(s, value))
                (shifted ??= []).Add(new(name, s));
        }
        if (shifted is null)
            return;
        foreach ((string name, AnimatableValue value) in shifted)
            Parameters[name] = value;
    }
}
