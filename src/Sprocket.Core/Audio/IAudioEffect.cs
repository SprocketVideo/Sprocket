using Sprocket.Core.Rendering;

namespace Sprocket.Core.Audio;

/// <summary>
/// The audio analogue of the video effect seam (ARCHITECTURE.md §19, PLAN.md step 31): one stage of an audio
/// effect chain, processing interleaved float32 PCM <b>in place</b>, block by block, at the project
/// rate/layout. Core only describes <em>which</em> effects run in <em>what</em> order with <em>what</em>
/// (possibly keyframed) parameters — the DSP lives behind this seam (built-ins in Sprocket.Audio; native
/// VST3/AU plugins later reach it through a C-ABI bridge, §19).
/// </summary>
/// <remarks>
/// <para>Implementations are <b>stateful</b> (filter memory, envelopes, delay lines) and are driven
/// sequentially by a single mixing thread, so they need no locking. The executor keys one instance per
/// chain position (see <see cref="ResolvedAudioChain.StateKey"/>) so state carries across buffers.</para>
/// <para><see cref="Process"/> must be <b>allocation-free in steady state</b> (§1): allocate delay lines and
/// scratch on first use / format change only. Parameters arrive per block, already evaluated at the block's
/// time; a parameter change mid-automation therefore steps per block (a deliberate first cut, §19).</para>
/// </remarks>
public interface IAudioEffect
{
    /// <summary>
    /// Processes one block of interleaved float32 PCM in place. <paramref name="parameters"/> is the effect's
    /// parameter set evaluated at the block's start time; unknown/missing parameters use the effect's defaults.
    /// </summary>
    /// <param name="interleaved">The block, exactly <paramref name="frames"/> × <paramref name="channels"/> floats.</param>
    /// <param name="frames">Sample-frame count of the block.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Interleaved channel count.</param>
    /// <param name="parameters">The effect's parameters, evaluated for this block.</param>
    void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters);

    /// <summary>Clears internal state (filter memory, envelopes, tails), e.g. after a large seek.</summary>
    void Reset();
}
