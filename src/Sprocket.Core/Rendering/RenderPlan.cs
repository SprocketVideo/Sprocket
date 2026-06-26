using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Core.Rendering;

/// <summary>
/// An effect with its parameters already evaluated to concrete numbers at a specific time. The Render
/// layer turns this into a shader; Core never sees the shader (ARCHITECTURE.md §5, §7).
/// </summary>
/// <param name="EffectTypeId">The effect type, e.g. <see cref="EffectTypeIds.Brightness"/>.</param>
/// <param name="Parameters">Parameter values, evaluated at the frame's time.</param>
public sealed record ResolvedEffect(string EffectTypeId, IReadOnlyDictionary<string, double> Parameters)
{
    /// <summary>Gets a parameter value, or <paramref name="fallback"/> if it is not set.</summary>
    public double Get(string name, double fallback = 0) =>
        Parameters.TryGetValue(name, out double value) ? value : fallback;
}

/// <summary>
/// One resolved video layer: the source frame to fetch, the effect chain to apply (bottom→top), and
/// how to composite it onto the layers beneath.
/// </summary>
/// <param name="MediaRefId">Source to fetch from.</param>
/// <param name="SourceTime">Time within the source for the frame to fetch.</param>
/// <param name="Effects">Effect chain, evaluated at the frame's time, applied in order.</param>
/// <param name="Opacity">Track opacity for the composite step.</param>
/// <param name="BlendMode">Track blend mode for the composite step.</param>
public sealed record VideoLayer(
    MediaRefId MediaRefId,
    Timecode SourceTime,
    IReadOnlyList<ResolvedEffect> Effects,
    double Opacity,
    BlendMode BlendMode);

/// <summary>
/// A pure description of how to render one composited frame at a given time: the target size and the
/// ordered layers (bottom→top, disabled tracks already removed). This is the output of the render
/// graph's <em>resolution</em> step and the input to its execution; it is fully serializable and
/// trivially unit-testable headlessly (ARCHITECTURE.md §5).
/// </summary>
/// <param name="Resolution">Target canvas size.</param>
/// <param name="Time">The timeline time this plan was resolved for.</param>
/// <param name="Layers">Layers to composite, bottom→top.</param>
public sealed record VideoFramePlan(Resolution Resolution, Timecode Time, IReadOnlyList<VideoLayer> Layers);

/// <summary>
/// One resolved audio layer for a buffer. The gain is given at both ends of the buffer so the mixer
/// can apply a linear ramp across it (fades, ARCHITECTURE.md §6); for a constant gain the two values
/// are equal.
/// </summary>
/// <param name="MediaRefId">Source to pull PCM from.</param>
/// <param name="SourceStart">Time within the source corresponding to the start of the buffer.</param>
/// <param name="GainStartLinear">Linear gain at the start of the buffer.</param>
/// <param name="GainEndLinear">Linear gain at the end of the buffer.</param>
public sealed record AudioLayer(
    MediaRefId MediaRefId,
    Timecode SourceStart,
    double GainStartLinear,
    double GainEndLinear);

/// <summary>
/// A pure description of how to fill one audio output buffer: which source spans to sum and at what
/// gain. The mixer (Sprocket.Audio) executes it; Core only resolves it.
/// </summary>
/// <param name="BufferStart">Timeline time at the start of the buffer.</param>
/// <param name="BufferDuration">Length of the buffer.</param>
/// <param name="Layers">Audio layers to sum.</param>
/// <param name="MasterGainLinear">Master output gain (linear) to apply after summing.</param>
public sealed record AudioBufferPlan(
    Timecode BufferStart,
    Timecode BufferDuration,
    IReadOnlyList<AudioLayer> Layers,
    double MasterGainLinear);
