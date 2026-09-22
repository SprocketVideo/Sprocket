using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Chromatic Aberration (plan/features/special-effects.md, phase 1): red and blue scaled apart radially from
/// a centre — lens fringing as a realism pass, and as an accent on impacts. A registry SkSL effect, so
/// preview and export run the same program (§5).
/// </summary>
/// <remarks>
/// Transverse (lateral) aberration only — the channels are sampled at slightly different radial scales,
/// which is the fringing an editor actually wants to dial in; longitudinal aberration would need depth.
/// The split grows with distance from the centre, as a real lens's does, and its magnitude is a scale factor
/// rather than a pixel count, so preview and export match at any resolution. The three samples are
/// recombined <em>unpremultiplied</em> so channels taken at different alphas do not contaminate one another,
/// then repremultiplied against the centre sample's alpha — the layer's own shape is unchanged.
/// </remarks>
public sealed class ChromaticAberrationEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float amount;           // radial split strength, [0, 1]
uniform float centerX;          // optical centre across the layer, [0, 1]
uniform float centerY;          // optical centre down the layer, [0, 1]
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

half4 main(float2 coord) {
    half4 p = src.eval(coord);
    float a = float(p.a);
    if (amount <= 0.0 || a <= 0.0) {
        return p;
    }
    float2 c = sprocket_bounds.xy + float2(centerX, centerY) * sprocket_bounds.zw;
    float2 d = coord - c;
    float k = amount * 0.02; // up to a 2% radial split at the frame corner

    half4 rp = src.eval(c + d * (1.0 + k));
    half4 bp = src.eval(c + d * (1.0 - k));
    float ra = float(rp.a);
    float ba = float(bp.a);

    float3 rgb = float3(
        ra > 0.0 ? float(rp.r) / ra : 0.0,
        float(p.g) / a,
        ba > 0.0 ? float(bp.b) / ba : 0.0);
    return half4(half3(clamp(rgb, 0.0, 1.0) * a), p.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.ChromaticAberration)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.ChromaticAberration}' is missing from EffectCatalog.BuiltIns.");

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
