using System.Runtime.InteropServices;
using SkiaSharp;
using Sprocket.Core.Rendering;

namespace Sprocket.Render;

/// <summary>
/// The CPU-effect execution point of <see cref="SkiaEffectPipeline"/> (PLAN.md step 59, ARCHITECTURE.md §13/§17):
/// materialises the shader chain built so far into an offscreen surface at the layer's <em>source</em>
/// resolution, reads it back into a pooled <b>native</b> buffer, runs an <see cref="ICpuVideoEffectInstance"/>
/// over it into a second pooled native buffer, and re-uploads the result as a copy-on-write snapshot the remaining
/// chain continues from. This is the one deliberate GPU→CPU→GPU round-trip in the pipeline — the seam frei0r
/// (and later CPU-only OFX) plugins land on — and it honours §1's rule: pixels live in native memory only
/// (<see cref="NativeMemory"/>), reused frame to frame; nothing per-frame lands on the managed heap.
/// </summary>
/// <remarks>
/// <para>Owned by one pipeline instance (so one render thread). Per effect type it keeps one plugin instance
/// for the current frame size (recreated on a size change or when a re-registered effect replaces the source
/// object) and one offscreen surface for the current (context, size). Any exception from the effect — or a
/// failed readback — makes the stage report "pass through" (<see langword="null"/>) so a misbehaving plugin
/// degrades rather than killing the frame (§15). A finalizer reclaims the native buffers if the owner is
/// abandoned without <see cref="Dispose"/>.</para>
/// <para>Straight alpha: the readback converts the premultiplied composite to un-premultiplied
/// <see cref="CpuPixelFormat"/> order and the re-upload declares the output un-premultiplied, so a plugin
/// sees and produces ordinary RGBA (frei0r's model) and Skia re-premultiplies on the way back.</para>
/// </remarks>
internal sealed unsafe class CpuEffectStage : IDisposable
{
    private sealed record LiveInstance(ICpuVideoEffect Source, ICpuVideoEffectInstance Instance);

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear);

    private readonly Dictionary<string, LiveInstance> _instances = new(StringComparer.Ordinal);
    private readonly SKPaint _paint = new();

    private nint _input;
    private nint _output;
    private nuint _bufferBytes;

    private SKSurface? _surface;
    private nint _surfaceContext;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private bool _disposed;

    ~CpuEffectStage() => FreeNative();

    /// <summary>The number of times the pooled pixel buffers were (re)allocated — steady-state rendering at one
    /// frame size must hold this constant (the §1 allocation contract, asserted by the tests).</summary>
    public int BufferAllocations { get; private set; }

    /// <summary>
    /// Runs <paramref name="effect"/> over the chain-so-far <paramref name="source"/> shader. <paramref name="dest"/>
    /// is the layer's canvas rectangle and <paramref name="localMatrix"/> the image→dest mapping every chain shader
    /// is expressed in; the stage draws the shader through the inverse so the readback is at the layer's own
    /// <paramref name="width"/>×<paramref name="height"/> resolution (padded up to the effect's
    /// <see cref="ICpuVideoEffect.SizeGranularity"/> with a transparent border, cropped again on the way out).
    /// Returns the processed frame as an independent copy-on-write snapshot — safe to hold across further
    /// <see cref="Run"/> calls in the same draw (a transition with a CPU effect on both sides) — or
    /// <see langword="null"/> to pass the stage through. The caller disposes it after the draw.
    /// </summary>
    public SKImage? Run(
        GRRecordingContext? context,
        int width, int height,
        SKShader source, SKRect dest, SKMatrix localMatrix,
        ICpuVideoEffect effect, ResolvedEffect parameters, double timeSeconds)
    {
        if (_disposed || width <= 0 || height <= 0)
            return null;

        try
        {
            // frei0r (and friends) only guarantee frame sizes that are multiples of 8; pad the working frame up and
            // let the plugin see a transparent border rather than an out-of-contract size.
            int granularity = Math.Max(1, effect.SizeGranularity);
            int paddedWidth = RoundUp(width, granularity);
            int paddedHeight = RoundUp(height, granularity);

            ICpuVideoEffectInstance? instance = GetInstance(effect, parameters.EffectTypeId, paddedWidth, paddedHeight);
            if (instance is null)
                return null;

            SKSurface? surface = GetSurface(context, paddedWidth, paddedHeight);
            if (surface is null)
                return null;

            // Materialise the chain so far at source resolution: undo the image→dest mapping so the layer's
            // pixels land 1:1 in the offscreen surface.
            if (!localMatrix.TryInvert(out SKMatrix inverse))
                return null;
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Save();
            canvas.Concat(in inverse);
            _paint.Shader = source;
            canvas.DrawRect(dest, _paint);
            _paint.Shader = null;
            canvas.Restore();

            int rowBytes = paddedWidth * 4;
            EnsureBuffers((nuint)rowBytes * (nuint)paddedHeight);
            SKColorType colorType = effect.PixelFormat == CpuPixelFormat.Bgra8888 ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
            var cpuInfo = new SKImageInfo(paddedWidth, paddedHeight, colorType, SKAlphaType.Unpremul);

            using (SKImage snapshot = surface.Snapshot())
            {
                if (snapshot is null || !snapshot.ReadPixels(cpuInfo, _input, rowBytes, 0, 0))
                    return null; // readback failed (e.g. lost context) — pass through
            }

            instance.Process(_input, _output, timeSeconds, parameters);

            // Upload the result back into the offscreen surface and hand out a snapshot. Snapshots are copy-on-write
            // on both backends, so a later Run in the same draw (the other side of a transition) reusing this
            // surface and the pooled buffers cannot disturb it — and nothing outside the stage ever references
            // the pooled memory, so reallocating it can't race a deferred GPU upload.
            using (SKImage wrapped = SKImage.FromPixels(cpuInfo, _output, rowBytes))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(wrapped, 0, 0);
                surface.Flush(); // the GPU upload reads the pooled buffer now, not at some later flush
            }
            SKImage processed = surface.Snapshot();
            if (processed is null)
                return null;
            if (paddedWidth == width && paddedHeight == height)
                return processed;
            // Crop the padding. (Skia returns the *same* image for a full-bounds subset — SkiaSharp then hands back
            // the same managed object — so only subset, and only dispose the parent, when the crop is real.)
            using (processed)
                return processed.Subset(SKRectI.Create(width, height));
        }
        catch
        {
            _paint.Shader = null;
            return null; // a faulting plugin passes through rather than killing the frame (§15)
        }
    }

    /// <summary>Drops the live instance for an effect type that is no longer registered.</summary>
    public void Forget(string effectTypeId)
    {
        if (_instances.Remove(effectTypeId, out LiveInstance? live))
            live.Instance.Dispose();
    }

    private static int RoundUp(int value, int granularity) =>
        granularity <= 1 ? value : (value + granularity - 1) / granularity * granularity;

    private ICpuVideoEffectInstance? GetInstance(ICpuVideoEffect effect, string effectTypeId, int width, int height)
    {
        if (_instances.TryGetValue(effectTypeId, out LiveInstance? live))
        {
            if (ReferenceEquals(live.Source, effect) && live.Instance.Width == width && live.Instance.Height == height)
                return live.Instance;
            _instances.Remove(effectTypeId);
            live.Instance.Dispose(); // size changed, or the plugin was re-registered (reloaded)
        }

        ICpuVideoEffectInstance created = effect.CreateInstance(width, height);
        _instances[effectTypeId] = new LiveInstance(effect, created);
        return created;
    }

    private SKSurface? GetSurface(GRRecordingContext? context, int width, int height)
    {
        nint contextHandle = context?.Handle ?? nint.Zero;
        if (_surface is not null && _surfaceContext == contextHandle && _surfaceWidth == width && _surfaceHeight == height)
            return _surface;

        _surface?.Dispose();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _surface = context is not null ? SKSurface.Create(context, budgeted: true, info) : SKSurface.Create(info);
        _surfaceContext = contextHandle;
        _surfaceWidth = width;
        _surfaceHeight = height;
        return _surface;
    }

    private void EnsureBuffers(nuint bytes)
    {
        if (bytes <= _bufferBytes && _input != nint.Zero)
            return;
        if (_input != nint.Zero) NativeMemory.Free((void*)_input);
        if (_output != nint.Zero) NativeMemory.Free((void*)_output);
        _input = (nint)NativeMemory.Alloc(bytes);
        _output = (nint)NativeMemory.AllocZeroed(bytes);
        _bufferBytes = bytes;
        BufferAllocations++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (LiveInstance live in _instances.Values)
        {
            try { live.Instance.Dispose(); } catch { /* a plugin's teardown fault must not stop ours */ }
        }
        _instances.Clear();
        _surface?.Dispose();
        _surface = null;
        _paint.Dispose();
        FreeNative();
        GC.SuppressFinalize(this);
    }

    private void FreeNative()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_input != nint.Zero) { NativeMemory.Free((void*)_input); _input = nint.Zero; }
        if (_output != nint.Zero) { NativeMemory.Free((void*)_output); _output = nint.Zero; }
        _bufferBytes = 0;
    }
}
