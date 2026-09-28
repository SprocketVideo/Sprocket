using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// Echo (plan/features/toy-cassette-camera.md, phase 6): the pipeline binds each prior native frame as an extra
/// shader child (<c>echo0…</c>), folds the effects below Echo over it, and the Echo stage combines the current frame
/// with them per the operator. Rendered on the offscreen CPU backend (the same SkSL the GPU runs) over a "moving dot":
/// the current frame has a white dot at one spot and each earlier frame has it further left, so a trail is visible
/// exactly where the prior dots were.
/// </summary>
public sealed class EchoEffectTests
{
    private const int Size = 32;
    private const int Y = Size / 2;

    // The dot is at x = 24 now and 4 px further left in each earlier frame (x = 20, 16, 12, …).
    private static int DotX(int k) => 24 - 4 * k;

    private static Timecode PriorTime(int k) => new(1000L * k); // distinct plan keys; the pixels are what matter

    [Fact]
    public void Maximum_Leaves_A_Trail_Where_The_Dot_Was()
    {
        using Frames frames = new(3);
        using SKBitmap echoed = frames.Render(Echo(frames.Count, EchoOperators.Maximum, decay: 1.0));

        Assert.True(echoed.GetPixel(DotX(0), Y).Red > 250, "the current dot stays");
        for (int k = 1; k <= 3; k++)
            Assert.True(echoed.GetPixel(DotX(k), Y).Red > 250, $"echo {k} should light x = {DotX(k)}");
        Assert.True(echoed.GetPixel(DotX(4), Y).Red < 3, "nothing past the last echo");
        Assert.True(echoed.GetPixel(Size / 2, 4).Red < 3, "the background stays black");
    }

    [Fact]
    public void Decay_Fades_Each_Echo_By_The_Ratio()
    {
        using Frames frames = new(3);
        using SKBitmap echoed = frames.Render(Echo(frames.Count, EchoOperators.Maximum, decay: 0.5));
        Assert.InRange(echoed.GetPixel(DotX(1), Y).Red, 124, 131); // 0.5
        Assert.InRange(echoed.GetPixel(DotX(2), Y).Red, 61, 66);   // 0.25
        Assert.InRange(echoed.GetPixel(DotX(3), Y).Red, 29, 34);   // 0.125
    }

    [Fact]
    public void No_Echoes_Is_A_Pass_Through()
    {
        using Frames frames = new(3);
        // Number of Echoes 0: the planner resolves no temporal inputs, even with prior frames offered.
        ResolvedEffect none = Echo(0, EchoOperators.Maximum, decay: 1.0) with { TemporalInputs = null };
        using SKBitmap plain = frames.Render();
        using SKBitmap echoed = frames.Render(none);
        AssertSame(plain, echoed);
    }

    [Fact]
    public void Highlight_Key_100_Percent_Leaves_No_Trail()
    {
        using Frames frames = new(3);
        using SKBitmap plain = frames.Render();
        using SKBitmap echoed = frames.Render(Echo(frames.Count, EchoOperators.Maximum, decay: 1.0, key: 1.0));
        AssertSame(plain, echoed);
    }

    [Fact]
    public void Highlight_Key_Only_Echoes_Bright_Pixels()
    {
        // Prior frames carry a mid-grey dot: an 80 % key ignores it, a 0 % key (AE behaviour) echoes it.
        using Frames frames = new(2, priorLevel: 128);
        using SKBitmap keyed = frames.Render(Echo(frames.Count, EchoOperators.Maximum, decay: 1.0, key: 0.8));
        using SKBitmap unkeyed = frames.Render(Echo(frames.Count, EchoOperators.Maximum, decay: 1.0, key: 0.0));
        Assert.True(keyed.GetPixel(DotX(1), Y).Red < 3);
        Assert.InRange(unkeyed.GetPixel(DotX(1), Y).Red, 124, 131);
    }

    [Fact]
    public void A_Missing_Prior_Frame_Contributes_Nothing()
    {
        using Frames frames = new(3, supplied: [1, 3]); // echo 2's frame is not available
        using SKBitmap echoed = frames.Render(Echo(3, EchoOperators.Add, decay: 1.0));
        Assert.True(echoed.GetPixel(DotX(1), Y).Red > 250);
        Assert.True(echoed.GetPixel(DotX(2), Y).Red < 3, "an unavailable echo must not be faked");
        Assert.True(echoed.GetPixel(DotX(3), Y).Red > 250);
    }

    [Theory]
    [InlineData(EchoOperators.Add)]
    [InlineData(EchoOperators.Screen)]
    public void Brightening_Operators_Show_The_Trail(int op)
    {
        using Frames frames = new(2);
        using SKBitmap echoed = frames.Render(Echo(frames.Count, op, decay: 1.0));
        Assert.True(echoed.GetPixel(DotX(0), Y).Red > 250, $"operator {EchoOperators.Names[op]}");
        Assert.True(echoed.GetPixel(DotX(1), Y).Red > 250, $"operator {EchoOperators.Names[op]}");
        Assert.True(echoed.GetPixel(DotX(2), Y).Red > 250, $"operator {EchoOperators.Names[op]}");
    }

    [Fact]
    public void Composite_Operators_Stack_Opaque_Frames_By_Alpha()
    {
        // Opaque frames hide what they are composited over: in back, the current frame covers every echo; in front,
        // each echo covers the stack so far, so the farthest-back echo ends up on top.
        using Frames frames = new(2);
        using SKBitmap plain = frames.Render();
        using SKBitmap back = frames.Render(Echo(frames.Count, EchoOperators.CompositeInBack, decay: 1.0));
        AssertSame(plain, back);

        using SKBitmap front = frames.Render(Echo(frames.Count, EchoOperators.CompositeInFront, decay: 1.0));
        Assert.True(front.GetPixel(DotX(2), Y).Red > 250);
        Assert.True(front.GetPixel(DotX(0), Y).Red < 3);
        Assert.True(front.GetPixel(DotX(1), Y).Red < 3);
    }

    [Fact]
    public void Minimum_Keeps_Only_What_Every_Frame_Shares()
    {
        using Frames frames = new(2);
        using SKBitmap echoed = frames.Render(Echo(frames.Count, EchoOperators.Minimum, decay: 1.0));
        Assert.True(echoed.GetPixel(DotX(0), Y).Red < 3, "the dot is black in the earlier frames there");
    }

    [Fact]
    public void Blend_Averages_The_Images()
    {
        using Frames frames = new(3);
        using SKBitmap echoed = frames.Render(Echo(frames.Count, EchoOperators.Blend, decay: 1.0));
        // Four images, the dot in one of them at each position → a quarter of white.
        Assert.InRange(echoed.GetPixel(DotX(0), Y).Red, 60, 68);
        Assert.InRange(echoed.GetPixel(DotX(2), Y).Red, 60, 68);
    }

    [Fact]
    public void Effects_Below_Echo_Are_Re_Applied_To_Each_Prior_Frame()
    {
        using Frames frames = new(2);
        // The chain is [Brightness 0.5, Echo]: the current frame reaches Echo already dimmed, and so must each echo —
        // with the Brightness as resolved for that prior frame (here: 0.5 and 0.25).
        ResolvedEffect dim = Brightness(0.5);
        ResolvedEffect echo = Echo(2, EchoOperators.Maximum, decay: 1.0, upstream: k => [Brightness(k == 1 ? 0.5 : 0.25)]);
        using SKBitmap echoed = frames.Render(dim, echo);
        Assert.InRange(echoed.GetPixel(DotX(0), Y).Red, 124, 131);
        Assert.InRange(echoed.GetPixel(DotX(1), Y).Red, 124, 131);
        Assert.InRange(echoed.GetPixel(DotX(2), Y).Red, 61, 66);
    }

    [Fact]
    public void Rendering_Is_A_Pure_Function_Of_The_Inputs()
    {
        using Frames frames = new(3);
        using SKBitmap a = frames.Render(Echo(3, EchoOperators.Maximum, decay: 0.7, key: 0.5));
        using SKBitmap b = frames.Render(Echo(3, EchoOperators.Maximum, decay: 0.7, key: 0.5));
        AssertSame(a, b);
    }

    [Fact]
    public void Prior_Frames_Add_No_Pixel_Sized_Managed_Allocation()
    {
        // §1: prior frames are wrapped, never copied onto the managed heap. The managed cost of an echo draw is the
        // handful of shader/image wrapper objects — independent of the frame size — so it is measured at two sizes.
        long small = BytesPerDraw(64);
        long large = BytesPerDraw(512); // a 512×512 RGBA frame is 1 MiB of pixels
        Assert.True(large < 64 * 1024, $"an 8-echo draw allocated {large} managed bytes");
        Assert.True(Math.Abs(large - small) < 8 * 1024, $"managed allocation grew with frame size ({small} → {large} bytes)");

        static long BytesPerDraw(int size)
        {
            using var frames = new Frames(TemporalFootprint.MaxPriorFrames, size: size);
            ResolvedEffect echo = Echo(TemporalFootprint.MaxPriorFrames, EchoOperators.Maximum, decay: 0.8);
            using var pipeline = new SkiaEffectPipeline();
            using SKSurface surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            for (int i = 0; i < 3; i++)
                frames.Draw(pipeline, surface, [echo]); // warm-up: compile + caches
            const int draws = 20;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < draws; i++)
                frames.Draw(pipeline, surface, [echo]);
            return (GC.GetAllocatedBytesForCurrentThread() - before) / draws;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static ResolvedEffect Brightness(double amount) =>
        new(EffectTypeIds.Brightness, new Dictionary<string, double> { [EffectParamNames.Amount] = amount });

    private static ResolvedEffect Echo(
        int echoes, int op, double decay, double key = 0.0, Func<int, IReadOnlyList<ResolvedEffect>>? upstream = null) =>
        new(EffectTypeIds.Echo,
            new Dictionary<string, double>
            {
                [EffectParamNames.EchoCount] = echoes,
                [EffectParamNames.EchoTime] = -1.0 / 15,
                [EffectParamNames.StartingIntensity] = 1.0,
                [EffectParamNames.Decay] = decay,
                [EffectParamNames.EchoOperator] = op,
                [EffectParamNames.HighlightKey] = key,
            },
            TemporalInputs: echoes == 0
                ? null
                : [.. Enumerable.Range(1, echoes).Select(k => new TemporalInput(PriorTime(k), upstream?.Invoke(k) ?? []))]);

    private static void AssertSame(SKBitmap a, SKBitmap b)
    {
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
    }

    /// <summary>The current frame plus <c>count</c> prior frames of a dot moving right — native-style pixel buffers
    /// (SKBitmap memory, handed to the pipeline by pointer exactly like decoded frames).</summary>
    private sealed class Frames : IDisposable
    {
        private readonly SKBitmap _current;
        private readonly List<SKBitmap> _priors = [];
        private readonly List<PriorFrame> _supplied = [];
        private readonly int _size;

        public Frames(int count, byte priorLevel = 255, int[]? supplied = null, int size = Size)
        {
            _size = size;
            Count = count;
            _current = Dot(size, DotX(0) * size / Size, 255);
            for (int k = 1; k <= count; k++)
            {
                SKBitmap prior = Dot(size, Math.Max(0, DotX(k)) * size / Size, priorLevel);
                _priors.Add(prior);
                if (supplied is null || supplied.Contains(k))
                    _supplied.Add(new PriorFrame(PriorTime(k), prior.GetPixels(), prior.RowBytes, prior.Width, prior.Height));
            }
        }

        public int Count { get; }

        public SKBitmap Render(params ResolvedEffect[] effects)
        {
            using var pipeline = new SkiaEffectPipeline();
            using SKSurface surface = SKSurface.Create(new SKImageInfo(_size, _size, SKColorType.Rgba8888, SKAlphaType.Premul));
            Draw(pipeline, surface, effects);
            using SKImage image = surface.Snapshot();
            return SKBitmap.FromImage(image);
        }

        public void Draw(SkiaEffectPipeline pipeline, SKSurface surface, IReadOnlyList<ResolvedEffect> effects)
        {
            surface.Canvas.Clear(SKColors.Transparent);
            pipeline.DrawLayer(surface.Canvas, SKRect.Create(_size, _size), _current.GetPixels(), _current.RowBytes,
                _current.Width, _current.Height, effects, priorFrames: _supplied);
            surface.Canvas.Flush();
        }

        private static SKBitmap Dot(int size, int x, byte level)
        {
            var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));
            bitmap.Erase(SKColors.Black);
            int y = size / 2, r = Math.Max(1, size / Size); // a 1-px dot at 32 px, scaled with the frame
            for (int dy = -r + 1; dy < r; dy++)
                for (int dx = -r + 1; dx < r; dx++)
                    bitmap.SetPixel(Math.Clamp(x + dx, 0, size - 1), y + dy, new SKColor(level, level, level, 255));
            return bitmap;
        }

        public void Dispose()
        {
            _current.Dispose();
            foreach (SKBitmap b in _priors)
                b.Dispose();
        }
    }
}
