using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Glow / Bloom (plan/features/special-effects.md, phase 1): a threshold bright-pass blurred over a small
/// two-ring kernel and added back over the picture — the halation that seats fire, explosions and practical
/// lights into a plate. A registry SkSL effect like the grading toolset, so preview and export run the same
/// program (§5) and no pixels cross to managed code (§1).
/// </summary>
/// <remarks>
/// Each blur tap re-evaluates the whole upstream chain (the shader-graph model, §7), so the tap count is
/// deliberately small — 13 samples on two rings rather than a true separable Gaussian. The radius is a
/// fraction of the layer rect (<c>sprocket_bounds</c>), so a preview at one resolution and an export at
/// another produce the same spread.
/// </remarks>
public sealed class GlowEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float threshold;        // luma a pixel must exceed before it glows, [0, 1]
uniform float radius;           // glow spread as a fraction of the frame width
uniform float intensity;        // how strongly the blurred bright-pass is added back
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

half4 main(float2 coord) {
    half4 orig = src.eval(coord);
    float r = radius * sprocket_bounds.z;
    if (intensity <= 0.0 || r <= 0.0) {
        return orig;
    }

    // Centre tap plus three rings of eight, each ring rotated so the 24 directions stay distinct. Fewer
    // taps ring visibly around small highlights; more would multiply the upstream chain cost further.
    float3 sum = float3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < 25; i++) {
        float ring = i == 0 ? 0.0 : (i <= 8 ? 0.34 : (i <= 16 ? 0.67 : 1.0));
        float ang = float(i) * 0.7853982 + ring * 2.0943951;
        float2 off = ring <= 0.0 ? float2(0.0) : float2(cos(ang), sin(ang)) * r * ring;
        float w = ring <= 0.0 ? 1.0 : (ring < 0.4 ? 0.8 : (ring < 0.7 ? 0.5 : 0.3));

        half4 p = src.eval(coord + off);
        float a = float(p.a);
        float3 c = a > 0.0 ? clamp(float3(p.rgb) / a, 0.0, 1.0) : float3(0.0);
        float bright = smoothstep(threshold, threshold + 0.2, dot(c, LUMA));
        sum += float3(p.rgb) * bright * w;
        wsum += w;
    }

    // Additive over the premultiplied frame: alpha rises with the glow's own luma so the bloom can spill
    // past a layer's edge (a title, an alpha element) instead of being clipped to it.
    float3 glow = sum / max(wsum, 1e-4) * intensity;
    float a = clamp(float(orig.a) + clamp(dot(glow, LUMA), 0.0, 1.0), 0.0, 1.0);
    float3 rgb = clamp(float3(orig.rgb) + glow, 0.0, a);
    return half4(half3(rgb), half(a));
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.Glow)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.Glow}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("threshold", (float)Math.Clamp(effect.Get(EffectParamNames.Threshold, 0.7), 0.0, 1.0));
        uniforms.Set("radius", (float)Math.Clamp(effect.Get(EffectParamNames.Radius, 0.03), 0.0, 0.25));
        uniforms.Set("intensity", (float)Math.Clamp(effect.Get(EffectParamNames.Intensity, 1.0), 0.0, 4.0));
        // sprocket_bounds is a reserved uniform auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
