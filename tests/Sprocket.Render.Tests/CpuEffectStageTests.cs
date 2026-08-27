using System.Runtime.InteropServices;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The CPU-effect readback stage of <see cref="SkiaEffectPipeline"/> (PLAN.md step 59): a registered
/// <see cref="ICpuVideoEffect"/> materialises the chain-so-far, runs in native memory, and feeds the rest of the
/// chain — on the offscreen raster backend here, the same code path the GPU takes in preview/export. Also proves the
/// §1 contract: pooled native buffers, no per-frame reallocation, and pass-through on any fault.
/// </summary>
public sealed unsafe class CpuEffectStageTests : IDisposable
{
    private const int Size = 8;
    private readonly List<string> _registered = [];

    public void Dispose()
    {
        foreach (string id in _registered)
            SkiaEffectPipeline.UnregisterEffect(id);
    }

    /// <summary>A managed CPU effect that inverts RGB (alpha untouched) in whichever byte order it declares, and
    /// records what it saw. Instances count constructions so recreation-on-resize is observable.</summary>
    private sealed class InvertEffect(string id, CpuPixelFormat format, bool throwOnCreate = false, int granularity = 1) : ICpuVideoEffect
    {
        public int Created;
        public int Processed;
        public double LastTime;
        public int LastWidth, LastHeight;
        public byte[] LastInputPixel0 = new byte[4];
        public byte[] LastInputLastPixel = new byte[4];

        public EffectDescriptor Descriptor { get; } = new(id, "Invert (CPU)", EffectCategory.Video, "test", []);
        public CpuPixelFormat PixelFormat => format;
        public int SizeGranularity => granularity;

        public ICpuVideoEffectInstance CreateInstance(int width, int height)
        {
            if (throwOnCreate)
                throw new InvalidOperationException("boom");
            Created++;
            return new Instance(this, width, height);
        }

        private sealed class Instance(InvertEffect owner, int width, int height) : ICpuVideoEffectInstance
        {
            public int Width => width;
            public int Height => height;
            public void Process(nint input, nint output, double timeSeconds, ResolvedEffect parameters)
            {
                owner.Processed++;
                owner.LastTime = timeSeconds;
                owner.LastWidth = width;
                owner.LastHeight = height;
                var src = (byte*)input;
                var dst = (byte*)output;
                for (int k = 0; k < 4; k++) owner.LastInputPixel0[k] = src[k];
                for (int k = 0; k < 4; k++) owner.LastInputLastPixel[k] = src[(width * height - 1) * 4 + k];
                int n = width * height;
                for (int i = 0; i < n; i++)
                {
                    dst[4 * i] = (byte)(255 - src[4 * i]);
                    dst[4 * i + 1] = (byte)(255 - src[4 * i + 1]);
                    dst[4 * i + 2] = (byte)(255 - src[4 * i + 2]);
                    dst[4 * i + 3] = src[4 * i + 3];
                }
            }
            public void Dispose() { }
        }
    }

    private InvertEffect Register(string id, CpuPixelFormat format = CpuPixelFormat.Rgba8888, bool throwOnCreate = false, int granularity = 1)
    {
        var effect = new InvertEffect(id, format, throwOnCreate, granularity);
        SkiaEffectPipeline.RegisterCpuEffect(effect);
        _registered.Add(id);
        return effect;
    }

    private static ResolvedEffect Use(string id) => new(id, new Dictionary<string, double>());

    private static ResolvedEffect Brightness(double amount) =>
        new(EffectTypeIds.Brightness, new Dictionary<string, double> { [EffectParamNames.Amount] = amount });

    private static SKColor Render(SkiaEffectPipeline pipeline, SKColor source, IReadOnlyList<ResolvedEffect> effects, int size = Size)
    {
        using var src = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        src.Erase(source);
        using SKSurface surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(size, size), src.GetPixels(), src.RowBytes, size, size, effects);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(image);
        return readback.GetPixel(size / 2, size / 2);
    }

    [Fact]
    public void Cpu_effect_inverts_the_frame_between_gpu_stages()
    {
        InvertEffect effect = Register("plugin.test.cpu.invert");
        using var pipeline = new SkiaEffectPipeline();

        // Brightness 0.5 (GPU) → invert (CPU) → brightness 0.5 (GPU): 200 → 100 → 155 → ~77.
        SKColor c = Render(pipeline, new SKColor(200, 200, 200, 255), [Brightness(0.5), Use(effect.Descriptor.Id), Brightness(0.5)]);

        Assert.InRange(c.Red, 75, 80);
        Assert.Equal(255, c.Alpha);
        Assert.Equal(1, effect.Processed);
        Assert.Equal(Size, effect.LastWidth);
        Assert.InRange(effect.LastInputPixel0[0], 98, 102); // the CPU stage saw the GPU-brightened value
    }

    [Fact]
    public void Bgra_effect_receives_and_returns_bgra_byte_order()
    {
        InvertEffect effect = Register("plugin.test.cpu.bgra", CpuPixelFormat.Bgra8888);
        using var pipeline = new SkiaEffectPipeline();

        SKColor c = Render(pipeline, new SKColor(10, 20, 30, 255), [Use(effect.Descriptor.Id)]);

        Assert.Equal(30, effect.LastInputPixel0[0]); // B first
        Assert.Equal(20, effect.LastInputPixel0[1]);
        Assert.Equal(10, effect.LastInputPixel0[2]);
        Assert.Equal(new SKColor(245, 235, 225, 255), c); // inverted, re-uploaded in the right channel order
    }

    [Fact]
    public void Frame_time_reaches_the_effect()
    {
        InvertEffect effect = Register("plugin.test.cpu.time");
        using var pipeline = new SkiaEffectPipeline { FrameTimeSeconds = 12.5 };
        Render(pipeline, SKColors.Gray, [Use(effect.Descriptor.Id)]);
        Assert.Equal(12.5, effect.LastTime);
    }

    [Fact]
    public void Steady_state_frames_do_not_reallocate_the_pooled_buffers_and_reuse_the_instance()
    {
        InvertEffect effect = Register("plugin.test.cpu.pool");
        using var pipeline = new SkiaEffectPipeline();

        for (int i = 0; i < 5; i++)
            Render(pipeline, SKColors.Gray, [Use(effect.Descriptor.Id)]);

        Assert.Equal(1, pipeline.CpuStageBufferAllocations);
        Assert.Equal(1, effect.Created);
        Assert.Equal(5, effect.Processed);
    }

    [Fact]
    public void Size_change_recreates_the_instance_and_grows_the_buffers_once()
    {
        InvertEffect effect = Register("plugin.test.cpu.resize");
        using var pipeline = new SkiaEffectPipeline();

        Render(pipeline, SKColors.Gray, [Use(effect.Descriptor.Id)], size: 8);
        Render(pipeline, SKColors.Gray, [Use(effect.Descriptor.Id)], size: 16);
        Render(pipeline, SKColors.Gray, [Use(effect.Descriptor.Id)], size: 16);

        Assert.Equal(2, effect.Created);
        Assert.Equal(16, effect.LastWidth);
        Assert.Equal(2, pipeline.CpuStageBufferAllocations); // grew once; the smaller size never reallocated after
    }

    [Fact]
    public void Faulting_instantiation_passes_the_stage_through()
    {
        InvertEffect effect = Register("plugin.test.cpu.fault", throwOnCreate: true);
        using var pipeline = new SkiaEffectPipeline();

        SKColor c = Render(pipeline, new SKColor(200, 200, 200, 255), [Use(effect.Descriptor.Id), Brightness(0.5)]);

        Assert.InRange(c.Red, 98, 102); // only the brightness applied
        Assert.Equal(0, effect.Processed);
    }

    [Fact]
    public void Unregistered_cpu_effect_passes_through_and_IsCpuEffect_tracks_registration()
    {
        InvertEffect effect = Register("plugin.test.cpu.gone");
        Assert.True(SkiaEffectPipeline.IsCpuEffect(effect.Descriptor.Id));
        using var pipeline = new SkiaEffectPipeline();
        Render(pipeline, SKColors.Gray, [Use(effect.Descriptor.Id)]);

        Assert.True(SkiaEffectPipeline.UnregisterEffect(effect.Descriptor.Id));
        Assert.False(SkiaEffectPipeline.IsCpuEffect(effect.Descriptor.Id));
        SKColor c = Render(pipeline, new SKColor(200, 200, 200, 255), [Use(effect.Descriptor.Id)]);

        Assert.Equal(200, c.Red);
        Assert.Equal(1, effect.Processed);
    }

    [Fact]
    public void Transition_with_a_cpu_effect_on_both_sides_keeps_each_side_independent()
    {
        // Both chains are built before anything is drawn; the first side's result must survive the second side's
        // run through the same pooled buffers (it did not when the stage handed out a wrapper over the pool).
        InvertEffect effect = Register("plugin.test.cpu.transition");
        using var pipeline = new SkiaEffectPipeline();
        using var from = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        from.Erase(new SKColor(255, 0, 0, 255)); // red → inverted cyan
        using var to = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        to.Erase(new SKColor(0, 0, 255, 255));   // blue → inverted yellow
        using SKImage fromImage = SKImage.FromBitmap(from);
        using SKImage toImage = SKImage.FromBitmap(to);
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);

        var dummy = new VideoLayer(default, default, [], 1.0, BlendMode.Normal);
        var dissolveAtStart = new ResolvedTransition(TransitionTypeIds.CrossDissolve, 0.0, new Dictionary<string, double>(), dummy, dummy);
        pipeline.DrawTransition(surface.Canvas, SKRect.Create(Size, Size),
            fromImage, [Use(effect.Descriptor.Id)], toImage, [Use(effect.Descriptor.Id)], dissolveAtStart);
        surface.Canvas.Flush();

        using SKImage result = surface.Snapshot();
        using SKBitmap readback = SKBitmap.FromImage(result);
        SKColor c = readback.GetPixel(Size / 2, Size / 2);
        Assert.Equal(new SKColor(0, 255, 255, 255), c); // progress 0 = the FROM side: inverted red, not inverted blue
        Assert.Equal(2, effect.Processed);
    }

    [Fact]
    public void Size_granularity_pads_the_working_frame_and_crops_the_result()
    {
        InvertEffect effect = Register("plugin.test.cpu.granular", granularity: 8);
        using var pipeline = new SkiaEffectPipeline();

        SKColor c = Render(pipeline, new SKColor(10, 20, 30, 255), [Use(effect.Descriptor.Id)], size: 12);

        Assert.Equal(16, effect.LastWidth);  // 12 rounded up to a multiple of 8
        Assert.Equal(16, effect.LastHeight);
        Assert.Equal(0, effect.LastInputLastPixel[3]); // the padding is transparent
        Assert.Equal(new SKColor(245, 235, 225, 255), c); // the visible frame is still the inverted source
    }

    [Fact]
    public void Builtin_ids_are_refused_for_cpu_effects() =>
        Assert.Throws<ArgumentException>(() => SkiaEffectPipeline.RegisterCpuEffect(new InvertEffect("builtin.cpu.nope", CpuPixelFormat.Rgba8888)));
}
