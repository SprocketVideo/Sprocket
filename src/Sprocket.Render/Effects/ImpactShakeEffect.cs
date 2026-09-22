using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Impact Shake (plan/features/special-effects.md, phase 1): deterministic noise-driven camera shake —
/// translation plus roll — with an overscan zoom so the shake never reveals the frame edge. The inverse of
/// <see cref="EffectTypeIds.Stabilization"/>, and the standard finishing move on an explosion or a hit. A
/// registry SkSL effect driven by the reserved <c>sprocket_time</c> uniform, so it is a pure function of
/// (project, time) and preview and export produce identical frames (§5).
/// </summary>
/// <remarks>
/// <see cref="EffectParamNames.Amount"/> is the master intensity and scales the roll too, so the whole shake
/// decays from one keyframed lane — the way an editor animates a hit (After Effects' wiggle amplitude, FCP's
/// Earthquake amount). Displacement is a fraction of the layer rect width, so the shake is the same size in
/// preview and in export. Nothing here changes the layer's alpha, so the overscan crops rather than reveals.
/// </remarks>
public sealed class ImpactShakeEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float amount;           // master shake intensity, [0, 1]
uniform float frequency;        // jitters per second
uniform float rotationDeg;      // roll at full amount, degrees
uniform float overscan;         // scale-up that keeps the shake off the frame edge (1.0 = none)
uniform float sprocket_time;    // reserved: frame time in seconds — auto-bound by the pipeline
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

// Value hash in [0, 1] — cheap, deterministic, no texture (the same construction the B&W grain uses).
float hash21(float2 p) {
    float3 q = fract(float3(p.xyx) * float3(443.897, 441.423, 437.195));
    q += dot(q, q.yzx + 19.19);
    return fract((q.x + q.y) * q.z);
}

// Smooth value noise: the shake wanders between jitters rather than snapping frame to frame.
float valueNoise(float2 p) {
    float2 i = floor(p);
    float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash21(i), hash21(i + float2(1.0, 0.0)), u.x),
               mix(hash21(i + float2(0.0, 1.0)), hash21(i + float2(1.0, 1.0)), u.x), u.y);
}

half4 main(float2 coord) {
    if (amount <= 0.0 && overscan <= 1.0) {
        return src.eval(coord);
    }
    float2 center = sprocket_bounds.xy + sprocket_bounds.zw * 0.5;
    float t = sprocket_time * frequency;

    float2 jitter = (float2(valueNoise(float2(t, 0.5)), valueNoise(float2(0.5, t))) - 0.5) * 2.0;
    float2 offset = jitter * amount * 0.04 * sprocket_bounds.z;
    float ang = (valueNoise(float2(t, 7.3)) - 0.5) * 2.0 * radians(rotationDeg) * amount;

    // Output → source: undo the translation, then the roll and the overscan about the frame centre.
    float2 p = coord - offset - center;
    float ca = cos(-ang);
    float sa = sin(-ang);
    p = float2(ca * p.x - sa * p.y, sa * p.x + ca * p.y) / max(overscan, 1e-4);
    return src.eval(p + center);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.ImpactShake)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.ImpactShake}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("amount", (float)Math.Clamp(effect.Get(EffectParamNames.Amount, 0.5), 0.0, 1.0));
        uniforms.Set("frequency", (float)Math.Clamp(effect.Get(EffectParamNames.Frequency, 8.0), 0.1, 30.0));
        uniforms.Set("rotationDeg", (float)Math.Clamp(effect.Get(EffectParamNames.Rotation, 1.0), 0.0, 15.0));
        uniforms.Set("overscan", (float)Math.Clamp(effect.Get(EffectParamNames.Overscan, 1.05), 1.0, 1.5));
        // sprocket_time / sprocket_bounds are reserved uniforms auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
