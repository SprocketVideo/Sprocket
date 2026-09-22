using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Heat Distortion (plan/features/special-effects.md, phase 1): animated smooth-noise refraction — the
/// shimmer over fire, exhaust and hot ground. A registry SkSL effect driven by the reserved
/// <c>sprocket_time</c> uniform, so it is a pure function of (project, time) and preview and export produce
/// identical frames (§5).
/// </summary>
/// <remarks>
/// Value noise (hashed lattice + smoothstep interpolation) rather than a texture, so nothing is uploaded and
/// the result is deterministic on every GPU. The two noise fields are decorrelated and the vertical one
/// drifts upward, which is what rising hot air does. Displacement is a fraction of the layer rect width, so
/// the shimmer is the same size in preview and in export.
/// </remarks>
public sealed class HeatDistortionEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float amount;           // refraction distance as a fraction of the frame width
uniform float noiseScale;       // shimmer cells across the frame
uniform float speed;            // animation rate multiplier
uniform float sprocket_time;    // reserved: frame time in seconds — auto-bound by the pipeline
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

// Value hash in [0, 1] — cheap, deterministic, no texture (the same construction the B&W grain uses).
float hash21(float2 p) {
    float3 q = fract(float3(p.xyx) * float3(443.897, 441.423, 437.195));
    q += dot(q, q.yzx + 19.19);
    return fract((q.x + q.y) * q.z);
}

// Smooth value noise: bilinear blend of the four lattice hashes with a smoothstep weight.
float valueNoise(float2 p) {
    float2 i = floor(p);
    float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash21(i), hash21(i + float2(1.0, 0.0)), u.x),
               mix(hash21(i + float2(0.0, 1.0)), hash21(i + float2(1.0, 1.0)), u.x), u.y);
}

half4 main(float2 coord) {
    if (amount <= 0.0 || sprocket_bounds.z <= 0.0 || sprocket_bounds.w <= 0.0) {
        return src.eval(coord);
    }
    float2 uv = (coord - sprocket_bounds.xy) / sprocket_bounds.zw;
    float t = sprocket_time * speed;
    float nx = valueNoise(uv * noiseScale + float2(t * 0.6, -t));
    float ny = valueNoise(uv * noiseScale + float2(11.7 - t * 0.4, 5.3 - t * 1.3));
    float2 off = (float2(nx, ny) - 0.5) * 2.0 * amount * sprocket_bounds.z;
    return src.eval(coord + off);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.HeatDistortion)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.HeatDistortion}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("amount", (float)Math.Clamp(effect.Get(EffectParamNames.Amount, 0.01), 0.0, 0.1));
        uniforms.Set("noiseScale", (float)Math.Clamp(effect.Get(EffectParamNames.NoiseScale, 12.0), 1.0, 60.0));
        uniforms.Set("speed", (float)Math.Clamp(effect.Get(EffectParamNames.Speed, 1.0), 0.0, 5.0));
        // sprocket_time / sprocket_bounds are reserved uniforms auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
