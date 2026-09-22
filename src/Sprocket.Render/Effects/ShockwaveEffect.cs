using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Shockwave (plan/features/special-effects.md, phase 1): a ring of radial displacement expanding from a
/// centre — an explosion's blast wave. A registry SkSL effect, so preview and export run the same program (§5).
/// </summary>
/// <remarks>
/// The ring's position is the ordinary keyframeable <see cref="EffectParamNames.Radius"/> parameter rather
/// than a hidden clock: the user owns the timing (as in After Effects' CC Ripple / Resolve's Ripple), the
/// wave can be retimed with the clip, and the render stays a pure function of (project, time). Radius and
/// Width are fractions of the frame half-diagonal and Amplitude a fraction of its width, so the wave is the
/// same size in preview and in export.
/// </remarks>
public sealed class ShockwaveEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float radius;           // ring position as a fraction of the frame half-diagonal
uniform float ringWidth;        // ring thickness, same units
uniform float amplitude;        // displacement as a fraction of the frame width
uniform float centerX;          // wave origin across the layer, [0, 1]
uniform float centerY;          // wave origin down the layer, [0, 1]
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline

half4 main(float2 coord) {
    float halfDiag = 0.5 * length(sprocket_bounds.zw);
    if (amplitude <= 0.0 || halfDiag <= 0.0) {
        return src.eval(coord);
    }
    float2 c = sprocket_bounds.xy + float2(centerX, centerY) * sprocket_bounds.zw;
    float2 d = coord - c;
    float dist = length(d);
    if (dist <= 0.0) {
        return src.eval(coord);
    }

    // x runs -1 → +1 across the ring; outside it the frame is untouched.
    float x = (dist / halfDiag - radius) / max(ringWidth, 1e-4);
    if (abs(x) >= 1.0) {
        return src.eval(coord);
    }
    // One sine period across the ring (compression ahead, rarefaction behind), windowed by a raised cosine
    // so the displacement dies smoothly at the ring's edges rather than stepping.
    float window = 0.5 + 0.5 * cos(x * 3.1415927);
    float disp = sin(x * 3.1415927) * window * amplitude * sprocket_bounds.z;
    return src.eval(coord - (d / dist) * disp);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.Shockwave)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.Shockwave}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("radius", (float)Math.Clamp(effect.Get(EffectParamNames.Radius, 0.0), 0.0, 1.5));
        uniforms.Set("ringWidth", (float)Math.Clamp(effect.Get(EffectParamNames.RingWidth, 0.1), 0.01, 1.0));
        uniforms.Set("amplitude", (float)Math.Clamp(effect.Get(EffectParamNames.Amplitude, 0.02), 0.0, 0.2));
        uniforms.Set("centerX", (float)Math.Clamp(effect.Get(EffectParamNames.CenterX, 0.5), 0.0, 1.0));
        uniforms.Set("centerY", (float)Math.Clamp(effect.Get(EffectParamNames.CenterY, 0.5), 0.0, 1.0));
        // sprocket_bounds is a reserved uniform auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
