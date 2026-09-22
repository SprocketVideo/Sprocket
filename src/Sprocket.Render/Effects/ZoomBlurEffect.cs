using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Zoom Blur (plan/features/special-effects.md, phase 1): a radial smear along the rays from a centre point
/// — the "Radial Blur (Zoom)" primitive used for punch-ins, hits and blast moments. A registry SkSL effect,
/// so preview and export run the same program (§5).
/// </summary>
/// <remarks>
/// Thirteen taps sweep the sample scale symmetrically around 1.0 (±25% at full Amount), so the image
/// neither grows nor shrinks as Amount rises — only its radial smear does, and the streak lengthens with
/// distance from the centre the way a real zoom does. The centre is expressed in normalised layer coordinates, so
/// preview and export match at any resolution. Each tap re-evaluates the upstream chain (§7).
/// </remarks>
public sealed class ZoomBlurEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float amount;           // radial smear strength, [0, 1]
uniform float centerX;          // zoom centre across the layer, [0, 1]
uniform float centerY;          // zoom centre down the layer, [0, 1]
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

half4 main(float2 coord) {
    if (amount <= 0.0) {
        return src.eval(coord);
    }
    float2 c = sprocket_bounds.xy + float2(centerX, centerY) * sprocket_bounds.zw;
    float2 d = coord - c;
    float4 sum = float4(0.0);
    for (int i = 0; i < 13; i++) {
        float t = float(i) / 12.0 - 0.5;   // [-0.5, 0.5]
        float s = 1.0 + t * amount * 0.5;  // ±25% sample scale at full amount
        sum += float4(src.eval(c + d * s));
    }
    return half4(sum / 13.0);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.ZoomBlur)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.ZoomBlur}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("amount", (float)Math.Clamp(effect.Get(EffectParamNames.Amount, 0.0), 0.0, 1.0));
        uniforms.Set("centerX", (float)Math.Clamp(effect.Get(EffectParamNames.CenterX, 0.5), 0.0, 1.0));
        uniforms.Set("centerY", (float)Math.Clamp(effect.Get(EffectParamNames.CenterY, 0.5), 0.0, 1.0));
        // sprocket_bounds is a reserved uniform auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
