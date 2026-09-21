using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Black &amp; White (film-emulation monochrome, plan/features/black-and-white.md), phase 1 — the conversion
/// core plus finishing: an optical colour filter and Lightroom-style eight-hue channel mixer control
/// <em>how</em> colour maps to tone, a film tone response (brightness / contrast / shadow toe / highlight
/// shoulder) shapes the grey, procedural grain, single/split toning and a vignette finish the look, and a
/// dry/wet <see cref="EffectParamNames.Mix"/> blends against the colour original. A registry SkSL stage like the
/// rest of the grading toolset, so it runs identically in preview / export / thumbnails and no pixels cross to
/// managed code (§1, §5). The preset library lands in later phases.
///
/// <para>Shader order (single pass): unpremultiply → sRGB→linear → optical filter (luma-normalised so exposure
/// doesn't shift) → per-hue mixer (blended band weight scales luma, gated by saturation so greys are untouched)
/// → Rec.709 luma → brightness/contrast/toe/shoulder → linear→sRGB → procedural grain (hash noise per cell,
/// re-seeded each frame from <c>sprocket_time</c> unless Static Grain is on, luma-weighted to the mids) →
/// toning (single tint toward a hue at the pixel's luma; split lerps shadow/highlight hues by luma with a
/// balance) → vignette (radial from the layer rect <c>sprocket_bounds</c>) → <c>Mix</c> lerp against the
/// original → clamp <c>rgb ≤ a</c>, repremultiply.</para>
///
/// <para>The grain and vignette stages are the first consumer of the registry per-frame context seam: the
/// pipeline auto-binds the reserved <c>sprocket_time</c> (seconds) and <c>sprocket_bounds</c> (layer
/// left/top/width/height) uniforms because this program declares them (§13). Grain is deterministic — the same
/// frame time yields identical pixels, so preview and export match (§5). The preset library lands in later
/// phases.</para>
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
uniform float grainAmount;    // [0, 1]
uniform float grainSize;      // source pixels per noise cell, [0.5, 4]
uniform float grainSeedLock;  // 0 = re-seed per frame, 1 = static field
uniform float vignetteAmount; // [-1, 1] (negative darkens edges)
uniform float vignetteSize;   // radius relative to half-diagonal, [0, 1.5]
uniform float vignetteSoftness; // [0, 1]
uniform float toneHue;        // single-tone tint hue, degrees [0, 360)
uniform float toneStrength;   // [0, 1] (0 = neutral)
uniform float splitShadowHue; // split-toning shadow hue, degrees
uniform float splitHighlightHue; // split-toning highlight hue, degrees
uniform float splitStrength;  // [0, 1] (0 = neutral)
uniform float splitBalance;   // [-1, 1] shadow/highlight crossover
uniform float sprocket_time;    // reserved: frame time in seconds (grain seed) — auto-bound by the pipeline
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

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

// Value hash in [0, 1] for a noise cell — cheap, deterministic, no texture. The frame seed shifts the cell
// coordinate so a given cell decorrelates frame to frame (animated grain) while staying identical for a fixed
// (cell, seed) pair — the property that makes preview and export match frame-for-frame.
float grainHash(float2 p) {
    float3 q = fract(float3(p.xyx) * float3(443.897, 441.423, 437.195));
    q += dot(q, q.yzx + 19.19);
    return fract((q.x + q.y) * q.z);
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

    // Procedural grain: one hash sample per noise cell (resolution-aware via grainSize), centred to [-1, 1],
    // luma-weighted so it peaks in the mids like real emulsion and fades toward pure black/white. The seed is
    // the frame time (seconds) unless Static Grain is on, in which case it is fixed so the field is frozen.
    if (grainAmount > 0.0) {
        float seed = grainSeedLock > 0.5 ? 0.0 : sprocket_time;
        float2 cell = floor(coord / max(grainSize, 0.5)) + float2(seed * 71.0, seed * 113.0);
        float g = grainHash(cell) - 0.5;              // [-0.5, 0.5]
        float lumaWeight = 1.0 - abs(s * 2.0 - 1.0);  // 0 at black/white, 1 at mid
        s = clamp(s + g * grainAmount * lumaWeight * 0.5, 0.0, 1.0);
    }

    float3 mono = float3(s);

    // Toning: tint the grey. Single tone lerps each pixel toward its hue carried at the pixel's own luma, so
    // blacks stay black and whites stay white (a monochromatic tint). Split toning then chooses the hue per
    // pixel between the shadow and highlight hues by luma, with balance shifting where the crossover sits.
    if (toneStrength > 0.0) {
        float3 tint = hsvToRgb(toneHue, 1.0, 1.0) * s;
        mono = mix(mono, tint, toneStrength);
    }
    if (splitStrength > 0.0) {
        float t = clamp(s + splitBalance * 0.5, 0.0, 1.0);
        float3 tint = hsvToRgb(mix(splitShadowHue, splitHighlightHue, t), 1.0, 1.0) * s;
        mono = mix(mono, tint, splitStrength);
    }

    // Vignette: radial falloff from the layer-rect centre, distance normalised to the half-diagonal so it is
    // shape-independent. vignetteSize is where it begins (relative to the half-diagonal); softness widens the
    // inner falloff start. Negative amount darkens the edges (classic), positive lightens.
    if (abs(vignetteAmount) > 0.0 && sprocket_bounds.z > 0.0 && sprocket_bounds.w > 0.0) {
        float2 center = sprocket_bounds.xy + sprocket_bounds.zw * 0.5;
        float halfDiag = 0.5 * length(sprocket_bounds.zw);
        float r = length(coord - center) / max(halfDiag, 1.0);
        float inner = vignetteSize * (1.0 - vignetteSoftness);
        float vt = clamp((r - inner) / max(vignetteSize - inner, 1e-3), 0.0, 1.0);
        vt = vt * vt * (3.0 - 2.0 * vt); // smoothstep
        mono = clamp(mono * (1.0 + vignetteAmount * vt), 0.0, 1.0);
    }

    float3 outRgb = mix(orig, mono, mixAmount);
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
        uniforms.Set("grainAmount", (float)Math.Clamp(effect.Get(EffectParamNames.GrainAmount, 0.0), 0.0, 1.0));
        uniforms.Set("grainSize", (float)Math.Clamp(effect.Get(EffectParamNames.GrainSize, 1.0), 0.5, 4.0));
        uniforms.Set("grainSeedLock", effect.Get(EffectParamNames.GrainSeedLock, 0.0) > 0.5 ? 1f : 0f);
        uniforms.Set("vignetteAmount", (float)Math.Clamp(effect.Get(EffectParamNames.VignetteAmount, 0.0), -1.0, 1.0));
        uniforms.Set("vignetteSize", (float)Math.Clamp(effect.Get(EffectParamNames.VignetteSize, 0.7), 0.0, 1.5));
        uniforms.Set("vignetteSoftness", (float)Math.Clamp(effect.Get(EffectParamNames.VignetteSoftness, 0.5), 0.0, 1.0));
        uniforms.Set("toneHue", (float)Math.Clamp(effect.Get(EffectParamNames.ToneHue, 35.0), 0.0, 360.0));
        uniforms.Set("toneStrength", (float)Math.Clamp(effect.Get(EffectParamNames.ToneStrength, 0.0), 0.0, 1.0));
        uniforms.Set("splitShadowHue", (float)Math.Clamp(effect.Get(EffectParamNames.SplitShadowHue, 35.0), 0.0, 360.0));
        uniforms.Set("splitHighlightHue", (float)Math.Clamp(effect.Get(EffectParamNames.SplitHighlightHue, 210.0), 0.0, 360.0));
        uniforms.Set("splitStrength", (float)Math.Clamp(effect.Get(EffectParamNames.SplitStrength, 0.0), 0.0, 1.0));
        uniforms.Set("splitBalance", (float)Math.Clamp(effect.Get(EffectParamNames.SplitBalance, 0.0), -1.0, 1.0));
        // sprocket_time / sprocket_bounds are reserved uniforms auto-bound by SkiaEffectPipeline (§13) — not set here.
    }

    private static float Weight(ResolvedEffect effect, string name) =>
        (float)Math.Clamp(effect.Get(name, 0.0) / 100.0, -1.0, 1.0);
}
