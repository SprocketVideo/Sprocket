using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// ACES filmic tone mapping (PLAN.md step 33) as a scene-linear GPU stage: unpremultiply → sRGB decode to
/// linear light → exposure gain → the fitted ACES RRT + ODT tone curve (Stephen Hill's approximation, the
/// industry-standard fit used when a full OCIO/ACES config isn't hosted) → sRGB encode → repremultiply.
/// All colour math stays in the shader, so preview and export are identical (§5) and no per-frame pixels
/// cross to managed code (§1). This is the first built-in realised through the
/// <see cref="SkiaEffectPipeline.RegisterEffect"/> registry — the exact path plugin effects use — rather
/// than a hard-coded pipeline case, so the plugin seam is exercised on every project that grades with it.
/// </summary>
public sealed class AcesFilmicEffect : IVideoEffect
{
    // The RRT+ODT fit works in ACES rendering space: sRGB/Rec.709 linear is taken into the fit's space by
    // the input matrix, tone-mapped by the rational curve, and brought back by the output matrix. GLSL/SkSL
    // matrix constructors are column-major, so each float3(...) line below is one COLUMN of Hill's
    // (row-major HLSL) matrices — i.e. the constants are the transpose of the commonly quoted tables.
    private const string Sksl = @"
uniform shader src;
uniform float exposure;

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

half4 main(float2 coord) {
    half4 p = src.eval(coord);
    float a = float(p.a);
    if (a <= 0.0) {
        return half4(0.0);
    }
    float3 c = clamp(float3(p.rgb) / a, 0.0, 1.0);      // unpremultiply
    float3 lin = srgbToLinear(c) * exp2(exposure);       // scene-linear working values

    float3x3 acesInput = float3x3(
        float3(0.59719, 0.07600, 0.02840),
        float3(0.35458, 0.90834, 0.13383),
        float3(0.04823, 0.01566, 0.83777));
    float3x3 acesOutput = float3x3(
        float3(1.60475, -0.10208, -0.00327),
        float3(-0.53108, 1.10813, -0.07276),
        float3(-0.07367, -0.00605, 1.07602));

    float3 v = acesInput * lin;
    float3 f = (v * (v + 0.0245786) - 0.000090537)
             / (v * (0.983729 * v + 0.4329510) + 0.238081);
    float3 tone = clamp(acesOutput * f, 0.0, 1.0);

    return half4(half3(linearToSrgb(tone) * a), p.a);    // repremultiply
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.AcesFilmic)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.AcesFilmic}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms) =>
        uniforms.Set("exposure", (float)effect.Get(EffectParamNames.Exposure, 0.0));
}
