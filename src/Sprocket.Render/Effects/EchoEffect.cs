using System.Text;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Echo (plan/features/toy-cassette-camera.md, phase 6): the After Effects primitive — the current frame combined
/// with up to eight <em>earlier</em> frames of the same clip, Echo Time apart, each fainter by Decay, via one of AE's
/// seven operators. A registry effect like the rest of the family, so preview and export run the same program (§5).
/// </summary>
/// <remarks>
/// <para><b>Temporal inputs.</b> The program declares <c>uniform shader echo0 … echo7</c> beside <c>src</c>. The planner
/// resolves which earlier frames they are (<see cref="ResolvedEffect.TemporalInputs"/>, through the clip's video time
/// map, so speed / ramp / reverse / hold / Posterize Time all apply); <see cref="SkiaEffectPipeline"/> wraps each
/// prior native frame as an <c>SKImage</c> (no managed pixels, §1), folds the effects below Echo over it, and binds it
/// to <c>echoK</c>. An unavailable prior frame (e.g. the live preview's history does not reach that far yet) is marked
/// in the reserved <c>sprocket_echo_valid0/1</c> uniforms and contributes nothing — never a stand-in frame.</para>
/// <para><b>Weights.</b> Image k (k = 0 is the current frame) has intensity <c>Starting Intensity × Decay^k</c> (AE's
/// definition: Decay is the ratio of each echo to the one before it) and scales the premultiplied pixel, so an
/// intensity below 1 is also an opacity. <b>Highlight Key</b> (our addition): echo k only contributes where its own
/// luma exceeds the key (a 10 % soft knee); 0 = every pixel echoes (AE), 100 % = none do.</para>
/// <para><b>Operators</b>, accumulated from the current frame outward (k = 1 first): Add (sum), Maximum / Minimum (per
/// channel), Screen (<c>a + e − a·e</c>), Composite in Back (each echo under the stack so far), Composite in Front (each
/// echo over it), Blend (the key-weighted mean of every image). A key-masked echo mixes its operator result back toward
/// the stack by its mask, so it is neutral for every operator — including Minimum and Blend.</para>
/// <para><b>Cost.</b> One upstream evaluation per bound echo per output pixel — each echo re-runs the effects below
/// Echo on its own frame, so put Echo first in the chain (the toy cassette camera stacks do) and it costs one texture
/// read per echo. Premultiplied output with <c>rgb ≤ a ≤ 1</c>.</para>
/// </remarks>
public sealed class EchoEffect : IVideoEffect
{
    /// <summary>The names of the per-echo shader children, <c>echo0</c> … <c>echo7</c>, in echo order.</summary>
    internal static IReadOnlyList<string> EchoChildNames { get; } =
        [.. Enumerable.Range(0, TemporalFootprint.MaxPriorFrames).Select(k => $"echo{k}")];

    /// <summary>The reserved uniforms (two <c>float4</c>, echoes 1–4 and 5–8) the pipeline sets to 1 for each echo
    /// child it bound to a real prior frame and 0 for a missing one.</summary>
    internal const string ValidUniformLow = "sprocket_echo_valid0";

    /// <inheritdoc cref="ValidUniformLow"/>
    internal const string ValidUniformHigh = "sprocket_echo_valid1";

    private static readonly string Sksl = BuildSksl();

    private static string BuildSksl()
    {
        var sb = new StringBuilder();
        sb.Append(@"
uniform shader src;
");
        foreach (string child in EchoChildNames)
            sb.Append("uniform shader ").Append(child).Append(";\n");
        sb.Append(@"
uniform float startingIntensity; // [0, 1] intensity of the current frame (image 0)
uniform float decay;             // [0, 1] intensity ratio of each echo to the one before it
uniform float echoCount;         // echoes the plan resolved (0..8)
uniform float echoOperator;      // EchoOperators index
uniform float highlightKey;      // [0, 1]; 0 = every pixel echoes, 1 = none
uniform float4 sprocket_echo_valid0; // 1 = echo k bound to a real prior frame (echoes 1-4)
uniform float4 sprocket_echo_valid1; // (echoes 5-8)
" + SkslSnippets.Rec709Luma + @"
float echoKeyMask(float4 e) {
    if (highlightKey <= 0.0) return 1.0;
    if (highlightKey >= 1.0) return 0.0;
    float3 rgb = e.a > 0.0 ? e.rgb / e.a : float3(0.0);
    return smoothstep(highlightKey, min(highlightKey + 0.1, 1.0), rec709Luma(rgb));
}

float4 echoCombine(float4 acc, float4 e) {
    if (echoOperator < 0.5) return acc + e;                    // Add
    if (echoOperator < 1.5) return max(acc, e);                // Maximum
    if (echoOperator < 2.5) return min(acc, e);                // Minimum
    if (echoOperator < 3.5) return acc + e - acc * e;          // Screen
    if (echoOperator < 4.5) return acc + e * (1.0 - acc.a);    // Composite in Back
    return e + acc * (1.0 - e.a);                              // Composite in Front
}

half4 main(float2 coord) {
    float4 acc = float4(src.eval(coord)) * startingIntensity;
    float4 sum = acc;
    float images = 1.0;
    float w = startingIntensity;
    bool blend = echoOperator > 5.5;
");
        for (int k = 0; k < EchoChildNames.Count; k++)
        {
            string valid = (k < 4 ? "sprocket_echo_valid0." : "sprocket_echo_valid1.") + "xyzw"[k % 4];
            sb.Append($@"
    w *= decay;
    if (echoCount > {k}.5 && {valid} > 0.5) {{
        float4 e = float4({EchoChildNames[k]}.eval(coord));
        float m = echoKeyMask(e);
        float4 ew = e * w;
        if (blend) {{ sum += ew * m; images += m; }}
        else {{ acc = mix(acc, echoCombine(acc, ew), m); }}
    }}");
        }
        sb.Append(@"
    if (blend) acc = sum / images;
    float a = clamp(acc.a, 0.0, 1.0);
    return half4(half3(clamp(acc.rgb, 0.0, a)), half(a));
}");
        return sb.ToString();
    }

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.Echo)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.Echo}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        uniforms.Set("startingIntensity", (float)Math.Clamp(effect.Get(EffectParamNames.StartingIntensity, 1.0), 0.0, 1.0));
        uniforms.Set("decay", (float)Math.Clamp(effect.Get(EffectParamNames.Decay, 1.0), 0.0, 1.0));
        // The echo count is what the planner resolved (Number of Echoes, capped per layer), not the raw parameter:
        // a layer with no prior frames to reach into (a generator, a transition side) renders with none.
        uniforms.Set("echoCount", (float)Math.Min(effect.TemporalInputs?.Count ?? 0, TemporalFootprint.MaxPriorFrames));
        uniforms.Set("echoOperator", (float)Math.Clamp(Math.Round(effect.Get(EffectParamNames.EchoOperator, EchoOperators.Add)),
            0.0, EchoOperators.Names.Count - 1));
        uniforms.Set("highlightKey", (float)Math.Clamp(effect.Get(EffectParamNames.HighlightKey, 0.0), 0.0, 1.0));
        // echo0..7 children and sprocket_echo_valid0/1 are bound by SkiaEffectPipeline from the layer's prior frames.
    }
}
