using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Directional Blur (plan/features/special-effects.md, phase 1): a symmetric linear smear along one angle —
/// the After Effects / Resolve primitive used for speed, impacts and faked motion blur. A registry SkSL
/// effect, so preview and export run the same program (§5).
/// </summary>
/// <remarks>
/// Fifteen evenly spaced taps centred on the pixel (the smear extends half the length either way, so the
/// image does not drift as Length rises — the After Effects convention). Length is a fraction of the layer
/// rect width, keeping the look resolution-independent. Each tap re-evaluates the upstream chain (§7).
/// </remarks>
public sealed class DirectionalBlurEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float angleRad;         // smear direction, radians clockwise from horizontal
uniform float blurLength;       // smear length as a fraction of the frame width ('length' is a built-in)
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

half4 main(float2 coord) {
    float len = blurLength * sprocket_bounds.z;
    if (len <= 0.0) {
        return src.eval(coord);
    }
    float2 dir = float2(cos(angleRad), sin(angleRad));
    float4 sum = float4(0.0);
    for (int i = 0; i < 15; i++) {
        float t = float(i) / 14.0 - 0.5; // [-0.5, 0.5] — centred, so the image stays put
        sum += float4(src.eval(coord + dir * (t * len)));
    }
    return half4(sum / 15.0);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.DirectionalBlur)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.DirectionalBlur}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        double degrees = Math.Clamp(effect.Get(EffectParamNames.Angle, 0.0), -180.0, 180.0);
        uniforms.Set("angleRad", (float)(degrees * Math.PI / 180.0));
        uniforms.Set("blurLength", (float)Math.Clamp(effect.Get(EffectParamNames.BlurLength, 0.02), 0.0, 0.25));
        // sprocket_bounds is a reserved uniform auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
