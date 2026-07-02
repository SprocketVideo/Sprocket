using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.TestPlugin;

/// <summary>A shader plugin effect: premultiplied colour invert, mixed by <c>amount</c> (1 = fully inverted).</summary>
public sealed class InvertEffect : IVideoEffect
{
    public const string Id = "plugin.test.invert";

    public EffectDescriptor Descriptor { get; } = new(
        Id,
        "Invert (Test)",
        EffectCategory.Color,
        "Inverts the image colour (test plugin).",
        [new EffectParameterDescriptor("amount", "Amount", 1.0, 0.0, 1.0, 0.05)]);

    public string SkslSource => @"
uniform shader src;
uniform float amount;
half4 main(float2 coord) {
    half4 c = src.eval(coord);
    float3 inverted = float3(c.a) - float3(c.rgb); // premultiplied invert keeps rgb within [0, a]
    return half4(half3(mix(float3(c.rgb), inverted, amount)), c.a);
}";

    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms) =>
        uniforms.Set("amount", (float)effect.Get("amount", 1.0));
}

/// <summary>An audio plugin effect type: multiplies every sample by <c>gain</c>.</summary>
public sealed class TestGainProvider : IAudioEffectProvider
{
    public const string Id = "plugin.test.gain";

    public EffectDescriptor Descriptor { get; } = new(
        Id,
        "Gain (Test)",
        EffectCategory.Audio,
        "Multiplies the signal by a linear gain (test plugin).",
        [new EffectParameterDescriptor("gain", "Gain", 2.0, 0.0, 4.0, 0.1)]);

    public IAudioEffect CreateEffect() => new TestGainEffect();

    private sealed class TestGainEffect : IAudioEffect
    {
        public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
        {
            float gain = (float)parameters.Get("gain", 2.0);
            for (int i = 0; i < frames * channels; i++)
                interleaved[i] *= gain;
        }

        public void Reset()
        {
        }
    }
}

/// <summary>A hostile plugin type claiming a reserved built-in id — the host must reject it with an error.</summary>
public sealed class ReservedIdEffect : IVideoEffect
{
    public EffectDescriptor Descriptor { get; } = new(
        "builtin.hijack",
        "Hijack",
        EffectCategory.Color,
        "Tries to claim a reserved id.",
        []);

    public string SkslSource => "uniform shader src; half4 main(float2 p) { return src.eval(p); }";

    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
    }
}

/// <summary>A plugin type whose constructor throws — the host must record the error and keep the rest.</summary>
public sealed class ThrowingEffect : IVideoEffect
{
    public ThrowingEffect() => throw new InvalidOperationException("Deliberately broken test effect.");

    public EffectDescriptor Descriptor => null!;

    public string SkslSource => string.Empty;

    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
    }
}
