using Sprocket.Core.Model;

namespace Sprocket.Core.Rendering;

/// <summary>The byte order of a 32-bit-per-pixel frame handed to a <see cref="ICpuVideoEffectInstance"/>.</summary>
public enum CpuPixelFormat
{
    /// <summary>R, G, B, A bytes in memory order (the frei0r <c>RGBA8888</c> colour model).</summary>
    Rgba8888,

    /// <summary>B, G, R, A bytes in memory order (the frei0r <c>BGRA8888</c> colour model).</summary>
    Bgra8888,
}

/// <summary>
/// A <b>CPU</b> video effect (PLAN.md step 59, ARCHITECTURE.md §13/§17): the contract for effects that must
/// process pixels in host memory rather than as a GPU shader — the seam native C-ABI plugin standards such as
/// frei0r (and CPU-only OFX plugins later) land on. It is the deliberate, bounded exception to the GPU-only
/// pipeline (§1): the Render layer materialises the chain-so-far into a <em>pooled native</em> buffer (a GPU
/// readback — no managed pixel arrays), hands the effect an input and an output pointer, and re-uploads the
/// result as the root of the remaining shader chain. Per-frame readback is expensive, so a CPU effect is
/// classified <em>heavy</em>: the Inspector points users at the render cache for chains that carry one.
/// </summary>
/// <remarks>
/// Like <see cref="IVideoEffect"/> the effect type is declarative (the Inspector builds its controls from
/// <see cref="Descriptor"/>) and the same instance serves every render pipeline; the per-size processing
/// state lives in the <see cref="ICpuVideoEffectInstance"/>s it creates, which each pipeline owns on its own
/// thread. Implementations must not throw out of <see cref="ICpuVideoEffectInstance.Process"/> for ordinary
/// plugin misbehaviour; the Render layer treats any exception as "pass this stage through" (§15).
/// </remarks>
public interface ICpuVideoEffect
{
    /// <summary>The effect's catalog entry (id, display name, category, typed parameters).</summary>
    EffectDescriptor Descriptor { get; }

    /// <summary>The byte order the effect expects in its input and produces in its output.</summary>
    CpuPixelFormat PixelFormat { get; }

    /// <summary>
    /// The frame-size granularity the effect requires: the Render layer rounds the working width and height up to a
    /// multiple of this (padding with transparent pixels, cropped again afterwards) before
    /// <see cref="CreateInstance"/>. 1 (the default) means any size; frei0r's specification requires multiples of 8.
    /// </summary>
    int SizeGranularity => 1;

    /// <summary>
    /// Creates the processing state for frames of exactly <paramref name="width"/>×<paramref name="height"/>
    /// pixels. The Render layer keeps one instance per pipeline per effect type and recreates it when the frame
    /// size changes. May throw if the plugin cannot be instantiated (the stage then passes through).
    /// </summary>
    ICpuVideoEffectInstance CreateInstance(int width, int height);
}

/// <summary>
/// One CPU effect's processing state for a fixed frame size (see <see cref="ICpuVideoEffect"/>). Not
/// thread-safe: a single render pipeline drives an instance from one thread.
/// </summary>
public interface ICpuVideoEffectInstance : IDisposable
{
    /// <summary>The frame width the instance was created for.</summary>
    int Width { get; }

    /// <summary>The frame height the instance was created for.</summary>
    int Height { get; }

    /// <summary>
    /// Processes one frame. <paramref name="input"/> and <paramref name="output"/> each point at
    /// <c>Width × Height × 4</c> bytes of tightly packed (row stride = <c>Width × 4</c>) 8-bit pixels in the
    /// effect's <see cref="ICpuVideoEffect.PixelFormat"/> with <b>straight (un-premultiplied) alpha</b>. The
    /// two buffers never alias. <paramref name="timeSeconds"/> is the frame's timeline position (0 when the
    /// caller has no time, e.g. a thumbnail); <paramref name="parameters"/> are the keyframe-evaluated values
    /// keyed by the descriptor's parameter names.
    /// </summary>
    void Process(nint input, nint output, double timeSeconds, ResolvedEffect parameters);
}
