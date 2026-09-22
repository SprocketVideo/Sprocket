using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Day for Night (plan/features/special-effects.md, phase 4): the guided night-exterior grade, as one registry
/// SkSL stage. It applies the corrections a colourist stacks by hand for day-for-night, in the order they have to
/// go — the reason it is one stage rather than a preset spread over the separate grading effects, where each
/// control's position in the stack would be the user's problem:
/// <list type="number">
///   <item>underexpose in linear light (<c>Exposure</c>);</item>
///   <item>pull the sky down — a bright, blue-or-pale pixel in the upper part of the frame (<c>Sky</c>);</item>
///   <item>roll the remaining highlights off under a soft luma shoulder, relative to the new white (<c>Highlights</c>);</item>
///   <item>restrain saturation about luma (<c>Saturation</c>) and cast a luma-preserving moonlight tint (<c>Moonlight Tint/Hue</c>);</item>
///   <item>back in display space, lift the blacks to a tinted moonlit floor (<c>Shadow Floor</c>) and vignette the edges;</item>
///   <item>finally restore what night keeps: warm, bright practicals (lamps, windows, fire) keep their original
///   colour and level, and skin tones keep their natural chroma at the new brightness (<c>Protect Skin</c>).</item>
/// </list>
/// Every key is computed from the <em>source</em> pixel, so e.g. a lamp is found before exposure has pulled it
/// out of the practical range. <c>Night Strength</c> blends the whole result against the original.
/// </summary>
/// <remarks>
/// The keys are plain hue/saturation/luma qualifiers plus a vertical weight for the sky (read from the reserved
/// <c>sprocket_bounds</c> uniform, so it is resolution-independent) — not semantic segmentation. A blue shirt (or a
/// near-white wall) at the top of the frame darkens with the sky; real isolation waits for the mask/tracking work (phase 5), which
/// is the plan's scope guard. Deterministic and stateless: a pure function of the pixel and its position, so
/// preview and export match (§5). Premultiplied-safe: unpremultiply → grade → repremultiply.
/// </remarks>
public sealed class DayForNightEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float strength;    // [0, 1] blend from the original to the night grade
uniform float exposure;    // stops, <= 0
uniform float sky;         // [0, 1]
uniform float highlights;  // [0, 1] shoulder strength
uniform float floorLevel;  // [0, 0.25] display-space black lift, fading out through the shadows
uniform float saturation;  // [0, 1] colour kept
uniform float tintAmount;  // [0, 1]
uniform float tintHue;     // degrees
uniform float practical;   // [0, 1]
uniform float skin;        // [0, 1]
uniform float vignette;    // [0, 1]
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

const float3 W = float3(0.2126, 0.7152, 0.0722);

float3 srgbToLinear(float3 c) {
    float3 lo = c / 12.92;
    float3 hi = pow((c + 0.055) / 1.055, float3(2.4));
    return mix(lo, hi, step(0.04045, c));
}

float3 linearToSrgb(float3 c) {
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, float3(1.0 / 2.4)) - 0.055;
    return mix(lo, hi, step(0.0031308, c));
}

float3 rgb2hsv(float3 c) {
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = mix(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = mix(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-7;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 hsv2rgb(float3 c) {
    float3 p = abs(fract(c.xxx + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * mix(float3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

// Angular distance in degrees between a hue in [0, 1) and a centre in degrees, with wraparound.
float hueDistance(float hue, float centerDeg) {
    float d = abs(hue * 360.0 - centerDeg);
    return min(d, 360.0 - d);
}

half4 main(float2 coord) {
    half4 p4 = src.eval(coord);
    float a = float(p4.a);
    if (a <= 0.0) {
        return half4(0.0);
    }
    if (strength <= 0.0) {
        return p4;
    }
    float3 c = clamp(float3(p4.rgb) / a, 0.0, 1.0);
    float3 hsv = rgb2hsv(c);
    float luma = dot(c, W);
    float2 uv = (coord - sprocket_bounds.xy) / max(sprocket_bounds.zw, float2(1.0e-4));

    // ── Keys, all from the source pixel ──
    // Sky: bright, blue or pale (overcast), in the upper frame — fading out over the middle band.
    float blue = clamp((70.0 - hueDistance(hsv.x, 215.0)) / 30.0, 0.0, 1.0);
    // Pale (overcast) sky must be much brighter than blue sky to count, so a white wall or grey building in
    // the upper frame is spared unless it is near the sky's own level.
    float pale = (1.0 - smoothstep(0.08, 0.3, hsv.y)) * smoothstep(0.7, 0.9, luma);
    float skyKey = smoothstep(0.35, 0.75, luma) * max(blue, pale) * (1.0 - smoothstep(0.2, 0.65, uv.y));
    // Skin: the orange skin-tone line, moderate saturation, not black or clipped.
    float skinKey = clamp((35.0 - hueDistance(hsv.x, 25.0)) / 15.0, 0.0, 1.0)
                  * smoothstep(0.1, 0.2, hsv.y) * (1.0 - smoothstep(0.5, 0.65, hsv.y))
                  * smoothstep(0.08, 0.2, luma) * (1.0 - smoothstep(0.85, 0.95, luma));
    // Practicals: near-clipped, warm and saturated (sodium / tungsten / fire / lit windows) — and never skin, so
    // a sunlit face or tan wall is not restored to daylight. Plain qualifiers: a clipped orange sign still counts.
    float practicalKey = smoothstep(0.85, 0.97, hsv.z) * smoothstep(0.02, 0.2, c.r - c.b)
                       * smoothstep(0.3, 0.6, hsv.y) * (1.0 - skinKey);

    // ── The grade, in linear light ──
    float white = exp2(exposure);
    float3 lin = srgbToLinear(c) * white;
    lin *= exp2(-3.0 * sky * skyKey);

    // Highlight shoulder on luma, relative to the exposed white so it bites whatever the exposure.
    float y = dot(lin, W);
    float knee = white * mix(1.0, 0.3, highlights);
    float k = highlights * 8.0 / white;
    float over = max(y - knee, 0.0);
    float yOut = min(y, knee) + over / (1.0 + k * over);
    lin *= yOut / max(y, 1.0e-5);

    lin = mix(float3(yOut), lin, saturation);
    float3 tintRgb = hsv2rgb(float3(fract(tintHue / 360.0), 0.5, 1.0));
    tintRgb /= max(dot(tintRgb, W), 1.0e-4);           // unit luma: the cast shifts colour, not brightness
    float3 tint = mix(float3(1.0), tintRgb, tintAmount);
    lin *= tint;

    // ── Display space: moonlit floor, vignette ──
    float3 g = linearToSrgb(clamp(lin, 0.0, 1.0));
    float3 floorColor = clamp(floorLevel * tint, 0.0, 1.0);
    float3 shadowWeight = (1.0 - g) * (1.0 - g) * (1.0 - g);
    g = g + floorColor * shadowWeight;                  // lifts the blacks only; mids keep their depth
    // Circular, measured against the half-diagonal (the Black & White vignette's shape): 0 centre, 1 corners.
    float2 centre = sprocket_bounds.xy + 0.5 * sprocket_bounds.zw;
    float r = length(coord - centre) / max(0.5 * length(sprocket_bounds.zw), 1.0e-4);
    g *= 1.0 - 0.8 * vignette * smoothstep(0.35, 1.0, r);

    // ── What night keeps ──
    float nightLuma = dot(g, W);
    float3 skinColor = clamp(c * (nightLuma / max(luma, 1.0e-4)), 0.0, 1.0);
    float practicalMix = practical * practicalKey;
    g = mix(g, skinColor, skin * skinKey * (1.0 - practicalMix));
    g = mix(g, c, practicalMix);

    float3 outRgb = clamp(mix(c, g, strength), 0.0, 1.0);
    return half4(half3(outRgb * a), p4.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.DayForNight)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.DayForNight}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("strength", (float)Math.Clamp(effect.Get(EffectParamNames.NightStrength, 1.0), 0.0, 1.0));
        uniforms.Set("exposure", (float)Math.Clamp(effect.Get(EffectParamNames.Exposure, -2.0), -4.0, 0.0));
        uniforms.Set("sky", (float)Math.Clamp(effect.Get(EffectParamNames.SkyDarken, 0.6), 0.0, 1.0));
        uniforms.Set("highlights", (float)Math.Clamp(effect.Get(EffectParamNames.HighlightRolloff, 0.6), 0.0, 1.0));
        uniforms.Set("floorLevel", (float)Math.Clamp(effect.Get(EffectParamNames.ShadowFloor, 0.02), 0.0, 0.25));
        uniforms.Set("saturation", (float)Math.Clamp(effect.Get(EffectParamNames.Saturation, 0.4), 0.0, 1.0));
        uniforms.Set("tintAmount", (float)Math.Clamp(effect.Get(EffectParamNames.MoonlightTint, 0.6), 0.0, 1.0));
        // Wrapped to [0, 360) here so a huge value from a hand-edited project cannot overflow the float cast to ∞
        // (fract(∞) is NaN, which would poison every pixel of the clip).
        double hue = effect.Get(EffectParamNames.MoonlightHue, 215.0);
        uniforms.Set("tintHue", (float)(double.IsFinite(hue) ? ((hue % 360.0) + 360.0) % 360.0 : 215.0));
        uniforms.Set("practical", (float)Math.Clamp(effect.Get(EffectParamNames.PracticalLights, 0.5), 0.0, 1.0));
        uniforms.Set("skin", (float)Math.Clamp(effect.Get(EffectParamNames.ProtectSkin, 0.3), 0.0, 1.0));
        uniforms.Set("vignette", (float)Math.Clamp(effect.Get(EffectParamNames.VignetteAmount, 0.3), 0.0, 1.0));
        // sprocket_bounds is a reserved uniform auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
