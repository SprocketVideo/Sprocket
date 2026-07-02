using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The registered-effect path of <see cref="SkiaEffectPipeline"/> (PLAN.md step 33): the built-in ACES
/// filmic stage and dynamically registered (plugin) <see cref="IVideoEffect"/>s, executed on the offscreen
/// CPU backend like <see cref="SkiaEffectPipelineTests"/> — the same SkSL the GPU runs in preview/export.
/// </summary>
public sealed class RegisteredEffectTests
{
    private const int Size = 8;

    // ── ACES Filmic (builtin.aces.filmic) ────────────────────────────────────────────────────────────

    [Fact]
    public void Aces_Black_StaysBlack()
    {
        SKColor c = RenderCenter(new SKColor(0, 0, 0, 255), [Aces(0.0)]);
        Assert.InRange(c.Red, 0, 2);
    }

    [Fact]
    public void Aces_MidGray_FollowsTheFilmicCurve()
    {
        // sRGB 100/255 → linear 0.127 → Hill RRT/ODT fit ≈ 0.063 linear → sRGB ≈ 0.28 → ~71.
        SKColor c = RenderCenter(new SKColor(100, 100, 100, 255), [Aces(0.0)]);
        Assert.InRange(c.Red, 71 - 8, 71 + 8);
        Assert.Equal(c.Red, c.Green); // neutral input stays neutral through the fit
        Assert.Equal(c.Red, c.Blue);
    }

    [Fact]
    public void Aces_ExposureIsMonotonic()
    {
        byte under = RenderCenter(new SKColor(100, 100, 100, 255), [Aces(-2.0)]).Red;
        byte mid = RenderCenter(new SKColor(100, 100, 100, 255), [Aces(0.0)]).Red;
        byte over = RenderCenter(new SKColor(100, 100, 100, 255), [Aces(2.0)]).Red;
        Assert.True(under < mid && mid < over, $"Exposure should brighten monotonically ({under}, {mid}, {over}).");
    }

    [Fact]
    public void Aces_White_RollsOffBelowClip()
    {
        // The filmic shoulder maps display white below 1.0 (≈ 0.81 sRGB) — the signature ACES highlight roll-off.
        SKColor c = RenderCenter(new SKColor(255, 255, 255, 255), [Aces(0.0)]);
        Assert.InRange(c.Red, 190, 235);
    }

    [Fact]
    public void Aces_PreservesAlpha()
    {
        SKColor c = RenderLayerCenter(new SKColor(0, 255, 0, 128), hasAlpha: true, [Aces(0.0)]);
        Assert.InRange(c.Alpha, 124, 132);
    }

    // ── Dynamic registration (the plugin path) ───────────────────────────────────────────────────────

    [Fact]
    public void RegisteredEffect_Applies_AndUnregisterRestoresPassThrough()
    {
        var invert = new TestInvertEffect("plugin.rendertest.invert");
        SkiaEffectPipeline.RegisterEffect(invert);
        try
        {
            SKColor inverted = RenderCenter(new SKColor(100, 100, 100, 255), [ById(invert.Descriptor.Id)]);
            Assert.InRange(inverted.Red, 155 - 3, 155 + 3); // 255 − 100
        }
        finally
        {
            Assert.True(SkiaEffectPipeline.UnregisterEffect(invert.Descriptor.Id));
        }

        SKColor passthrough = RenderCenter(new SKColor(100, 100, 100, 255), [ById(invert.Descriptor.Id)]);
        Assert.InRange(passthrough.Red, 100 - 2, 100 + 2);
    }

    [Fact]
    public void RegisterEffect_BrokenSksl_ThrowsAtRegistration()
    {
        var broken = new TestInvertEffect("plugin.rendertest.broken") { SkslOverride = "this is not SkSL" };
        Assert.Throws<InvalidOperationException>(() => SkiaEffectPipeline.RegisterEffect(broken));
    }

    [Fact]
    public void UnregisterEffect_BuiltinId_IsRefused()
    {
        Assert.False(SkiaEffectPipeline.UnregisterEffect(EffectTypeIds.AcesFilmic));
        // …and ACES still renders (not pass-through): black in, black out but mid-gray is graded.
        SKColor c = RenderCenter(new SKColor(100, 100, 100, 255), [Aces(0.0)]);
        Assert.NotInRange(c.Red, 98, 102);
    }

    [Fact]
    public void RegisteredEffect_FaultingBind_DegradesToPassThrough()
    {
        var faulty = new TestInvertEffect("plugin.rendertest.faulty") { ThrowOnBind = true };
        SkiaEffectPipeline.RegisterEffect(faulty);
        try
        {
            SKColor c = RenderCenter(new SKColor(100, 100, 100, 255), [ById(faulty.Descriptor.Id)]);
            Assert.InRange(c.Red, 100 - 2, 100 + 2);
        }
        finally
        {
            SkiaEffectPipeline.UnregisterEffect(faulty.Descriptor.Id);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private sealed class TestInvertEffect(string id) : IVideoEffect
    {
        public string? SkslOverride { get; init; }
        public bool ThrowOnBind { get; init; }

        public EffectDescriptor Descriptor { get; } = new(
            id, "Invert (render test)", EffectCategory.Color, "test",
            [new EffectParameterDescriptor("amount", "Amount", 1.0, 0.0, 1.0)]);

        public string SkslSource => SkslOverride ?? @"
uniform shader src;
uniform float amount;
half4 main(float2 coord) {
    half4 c = src.eval(coord);
    float3 inverted = float3(c.a) - float3(c.rgb);
    return half4(half3(mix(float3(c.rgb), inverted, amount)), c.a);
}";

        public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
        {
            if (ThrowOnBind)
                throw new InvalidOperationException("Deliberate bind fault.");
            uniforms.Set("amount", (float)effect.Get("amount", 1.0));
        }
    }

    private static ResolvedEffect Aces(double exposure) =>
        new(EffectTypeIds.AcesFilmic, new Dictionary<string, double> { [EffectParamNames.Exposure] = exposure });

    private static ResolvedEffect ById(string id) =>
        new(id, new Dictionary<string, double> { ["amount"] = 1.0 });

    private static SKColor RenderCenter(SKColor source, IReadOnlyList<ResolvedEffect> effects) =>
        RenderLayerCenter(source, hasAlpha: false, effects);

    private static SKColor RenderLayerCenter(SKColor source, bool hasAlpha, IReadOnlyList<ResolvedEffect> effects)
    {
        using var pipeline = new SkiaEffectPipeline();
        var alphaType = hasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
        using var src = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, alphaType));
        src.Erase(source);
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(Size, Size), src.GetPixels(), src.RowBytes, Size, Size, effects, hasAlpha: hasAlpha);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(image);
        return readback.GetPixel(Size / 2, Size / 2);
    }
}
