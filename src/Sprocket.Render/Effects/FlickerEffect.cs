using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Flicker / Exposure Pulse (plan/features/special-effects.md, phase 1): a time-driven exposure modulation
/// that blends from a clean sine to hashed noise — firelight, failing practicals, muzzle-flash throb. A
/// registry SkSL effect driven by the reserved <c>sprocket_time</c> uniform, so it is a pure function of
/// (project, time) and preview and export produce identical frames (§5).
/// </summary>
/// <remarks>
/// The gain swings symmetrically about 1.0 (±<see cref="EffectParamNames.Amount"/>), so the shot's average
/// exposure is unchanged and the flicker reads as a pulse rather than a fade. Purely tonal: the premultiplied
/// colour is scaled and clamped back to the pixel's own alpha, so the layer's shape and compositing are
/// untouched.
/// </remarks>
public sealed class FlickerEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float amount;        // exposure swing about unity, [0, 1]
uniform float frequency;     // pulses per second
uniform float randomness;    // 0 = clean sine, 1 = hashed noise
uniform float sprocket_time; // reserved: frame time in seconds — auto-bound by the pipeline

// Value hash in [0, 1] — cheap, deterministic, no texture (the same construction the B&W grain uses).
float hash21(float2 p) {
    float3 q = fract(float3(p.xyx) * float3(443.897, 441.423, 437.195));
    q += dot(q, q.yzx + 19.19);
    return fract((q.x + q.y) * q.z);
}

// Smooth value noise in one dimension: the irregular flicker wanders rather than strobing per frame.
float valueNoise1(float t) {
    float i = floor(t);
    float f = fract(t);
    float u = f * f * (3.0 - 2.0 * f);
    return mix(hash21(float2(i, 3.7)), hash21(float2(i + 1.0, 3.7)), u);
}

half4 main(float2 coord) {
    half4 p = src.eval(coord);
    float a = float(p.a);
    if (amount <= 0.0 || a <= 0.0) {
        return p;
    }
    float t = sprocket_time * frequency;
    float wave = 0.5 + 0.5 * sin(t * 6.2831853);
    float pulse = mix(wave, valueNoise1(t), randomness);
    float gain = 1.0 + amount * (pulse * 2.0 - 1.0);
    return half4(half3(clamp(float3(p.rgb) * gain, 0.0, a)), p.a);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.Flicker)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.Flicker}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("amount", (float)Math.Clamp(effect.Get(EffectParamNames.Amount, 0.2), 0.0, 1.0));
        uniforms.Set("frequency", (float)Math.Clamp(effect.Get(EffectParamNames.Frequency, 6.0), 0.1, 30.0));
        uniforms.Set("randomness", (float)Math.Clamp(effect.Get(EffectParamNames.Randomness, 0.5), 0.0, 1.0));
        // sprocket_time is a reserved uniform auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
