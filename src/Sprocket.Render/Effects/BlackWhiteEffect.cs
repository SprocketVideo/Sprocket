using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Black &amp; White (film-emulation monochrome, plan/features/black-and-white.md), phase 1 — the conversion
/// core: an optical colour filter and Lightroom-style eight-hue channel mixer control <em>how</em> colour maps
/// to tone, then a film tone response (brightness / contrast / shadow toe / highlight shoulder) shapes the grey,
/// and a dry/wet <see cref="EffectParamNames.Mix"/> blends against the colour original. A registry SkSL stage
/// like the rest of the grading toolset, so it runs identically in preview / export / thumbnails and no pixels
/// cross to managed code (§1, §5). Grain, vignette, toning and the preset library land in later phases.
///
/// <para>Shader order (single pass): unpremultiply → sRGB→linear → optical filter (luma-normalised so exposure
/// doesn't shift) → per-hue mixer (blended band weight scales luma, gated by saturation so greys are untouched)
/// → Rec.709 luma → brightness/contrast/toe/shoulder → linear→sRGB → <c>Mix</c> lerp against the original →
/// clamp <c>rgb ≤ a</c>, repremultiply.</para>
/// </summary>
public sealed class BlackWhiteEffect : IVideoEffect
{
    // The eight hue-band centres (degrees) the mixer sliders map to — Lightroom's Reds / Oranges / Yellows /
    // Greens / Aquas / Blues / Purples / Magentas. A pixel's contribution to each band is a triangular window
    // over the circular hue distance (support ≈ one band either side), normalised so the weight is a blend.
    private const string Sksl = @"
uniform shader src;
uniform float mixAmount;      // [0, 1] dry/wet against the colour original
uniform float filterHue;      // degrees [0, 360)
uniform float filterStrength; // [0, 1]
uniform float weights[8];     // per-hue luminance sliders, each in [-1, 1]
uniform float exposure;       // stops
uniform float contrast;       // around mid-grey, 1 = unchanged
uniform float shadows;        // [-1, 1] toe
uniform float highlights;     // [-1, 1] shoulder

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

// HSV hue (degrees) and saturation of a linear-light colour — used only to steer the mixer, so operating in
// linear is fine (hue is scale-invariant and saturation gating just needs to reach 0 for true greys).
float hueOf(float3 c) {
    float mx = max(c.r, max(c.g, c.b));
    float mn = min(c.r, min(c.g, c.b));
    float d = mx - mn;
    if (d <= 1e-6) { return 0.0; }
    float h;
    if (mx == c.r)      { h = mod((c.g - c.b) / d, 6.0); }
    else if (mx == c.g) { h = (c.b - c.r) / d + 2.0; }
    else                { h = (c.r - c.g) / d + 4.0; }
    return h * 60.0;
}

float satOf(float3 c) {
    float mx = max(c.r, max(c.g, c.b));
    float mn = min(c.r, min(c.g, c.b));
    return mx <= 1e-6 ? 0.0 : (mx - mn) / mx;
}

float3 hsvToRgb(float h, float s, float v) {
    float3 k = mod(float3(5.0, 3.0, 1.0) + h / 60.0, 6.0);
    return v - v * s * max(float3(0.0), min(min(k, 4.0 - k), 1.0));
}

// Triangular window (support ≈ one band either side) over the circular hue distance to a band centre.
float bandWeight(float hue, float center) {
    float d = abs(hue - center);
    d = min(d, 360.0 - d);
    return max(0.0, 1.0 - d / 90.0);
}

const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

half4 main(float2 coord) {
    half4 p = src.eval(coord);
    float a = float(p.a);
    if (a <= 0.0) {
        return half4(0.0);
    }
    float3 orig = clamp(float3(p.rgb) / a, 0.0, 1.0); // unpremultiplied sRGB original
    float3 lin = srgbToLinear(orig);

    // Optical filter: multiply by the filter colour, normalised to unit luma so a grey card keeps its
    // brightness, then lerp in by strength.
    float3 filterColor = hsvToRgb(filterHue, 1.0, 1.0);
    filterColor /= max(dot(filterColor, LUMA), 1e-4);
    lin = mix(lin, lin * filterColor, filterStrength);

    // Per-hue mixer: blended slider value at this pixel's hue, gated by saturation so greys are untouched.
    // Band centres: Reds / Oranges / Yellows / Greens / Aquas / Blues / Purples / Magentas (Lightroom's eight).
    float hue = hueOf(lin);
    float sat = satOf(lin);
    float w0 = bandWeight(hue, 0.0);
    float w1 = bandWeight(hue, 30.0);
    float w2 = bandWeight(hue, 60.0);
    float w3 = bandWeight(hue, 120.0);
    float w4 = bandWeight(hue, 180.0);
    float w5 = bandWeight(hue, 240.0);
    float w6 = bandWeight(hue, 285.0);
    float w7 = bandWeight(hue, 315.0);
    float wsum = w0 + w1 + w2 + w3 + w4 + w5 + w6 + w7;
    float mixed = w0 * weights[0] + w1 * weights[1] + w2 * weights[2] + w3 * weights[3]
                + w4 * weights[4] + w5 * weights[5] + w6 * weights[6] + w7 * weights[7];
    mixed = wsum > 0.0 ? mixed / wsum : 0.0;

    float y = dot(lin, LUMA);
    y = max(0.0, y * (1.0 + mixed * sat)); // positive slider lightens that hue; sat=0 → no change

    // Tone response. Brightness in linear (stops), then encode and shape contrast/toe/shoulder in display space
    // where mid-grey and the toe/shoulder pivots are perceptual.
    y *= exp2(exposure);
    float s = clamp(float(linearToSrgb(float3(y)).r), 0.0, 1.0);
    s = clamp((s - 0.5) * contrast + 0.5, 0.0, 1.0);
    float sw = clamp(1.0 - s * 2.0, 0.0, 1.0); // 1 at black → 0 at mid
    float hw = clamp(s * 2.0 - 1.0, 0.0, 1.0); // 0 at mid → 1 at white
    s = clamp(s + shadows * sw * 0.5 + highlights * hw * 0.5, 0.0, 1.0);

    float3 outRgb = mix(orig, float3(s), mixAmount);
    return half4(half3(outRgb * a), p.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.BlackWhite)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.BlackWhite}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("mixAmount", (float)Math.Clamp(effect.Get(EffectParamNames.Mix, 1.0), 0.0, 1.0));
        uniforms.Set("filterHue", (float)Math.Clamp(effect.Get(EffectParamNames.FilterHue, 0.0), 0.0, 360.0));
        uniforms.Set("filterStrength", (float)Math.Clamp(effect.Get(EffectParamNames.FilterStrength, 0.0), 0.0, 1.0));
        // The eight mixer sliders bind as one float[8], each normalised from the [-100, 100] descriptor to [-1, 1].
        // A fresh array per frame (like CurvesEffect's float4s) keeps the effect stateless — the same instance
        // serves every pipeline (§13), so a shared scratch field would not be thread-safe.
        uniforms.Set("weights",
        [
            Weight(effect, EffectParamNames.MixReds), Weight(effect, EffectParamNames.MixOranges),
            Weight(effect, EffectParamNames.MixYellows), Weight(effect, EffectParamNames.MixGreens),
            Weight(effect, EffectParamNames.MixAquas), Weight(effect, EffectParamNames.MixBlues),
            Weight(effect, EffectParamNames.MixPurples), Weight(effect, EffectParamNames.MixMagentas),
        ]);
        uniforms.Set("exposure", (float)Math.Clamp(effect.Get(EffectParamNames.Exposure, 0.0), -3.0, 3.0));
        uniforms.Set("contrast", (float)Math.Max(0.0, effect.Get(EffectParamNames.Contrast, 1.0)));
        uniforms.Set("shadows", (float)Math.Clamp(effect.Get(EffectParamNames.Shadows, 0.0), -1.0, 1.0));
        uniforms.Set("highlights", (float)Math.Clamp(effect.Get(EffectParamNames.Highlights, 0.0), -1.0, 1.0));
    }

    private static float Weight(ResolvedEffect effect, string name) =>
        (float)Math.Clamp(effect.Get(name, 0.0) / 100.0, -1.0, 1.0);
}
