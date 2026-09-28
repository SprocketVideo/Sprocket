using Sprocket.Core.Timing;

namespace Sprocket.Render;

/// <summary>
/// One earlier decoded frame of a layer's source, handed to <see cref="SkiaEffectPipeline.DrawLayer"/> for a temporal
/// effect such as Echo (plan/features/toy-cassette-camera.md, phase 6). Like the layer's own frame it is a pointer to
/// native RGBA8888 pixels (a pooled FFmpeg buffer) that the pipeline wraps, never copies (ARCHITECTURE.md §1), and it
/// must stay valid for the draw call. <see cref="SourceTime"/> is the source time the plan <em>asked for</em>
/// (<see cref="Core.Rendering.TemporalInput.SourceTime"/>) — the key the pipeline matches echoes by — not the frame's
/// own presentation time.
/// </summary>
/// <param name="SourceTime">The requested source time this frame answers (a <see cref="Core.Rendering.VideoLayer.PriorSourceTimes"/> entry).</param>
/// <param name="Pixels">Pointer to the RGBA8888 pixels.</param>
/// <param name="RowBytes">Stride in bytes.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="HasAlpha">Whether the pixels carry straight alpha (as <see cref="SkiaEffectPipeline.DrawLayer"/>).</param>
public readonly record struct PriorFrame(
    Timecode SourceTime,
    nint Pixels,
    int RowBytes,
    int Width,
    int Height,
    bool HasAlpha = false);
