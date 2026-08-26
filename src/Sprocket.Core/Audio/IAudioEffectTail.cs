namespace Sprocket.Core.Audio;

/// <summary>
/// Optional per-<em>instance</em> latency/tail metadata beside <see cref="IAudioEffect"/> (PLAN.md steps 41/49):
/// the small surface heavy effects expose so a host can report how far their output lags the input and how
/// long they keep ringing after it stops. Complements the static per-<em>type</em> hints in
/// <see cref="AudioEffectTraits"/> — an instance knows things the type cannot (a convolution reverb's tail
/// is exactly its loaded impulse response's length). Implement on the DSP class; callers test with
/// <c>effect is IAudioEffectTail tail</c>.
/// </summary>
public interface IAudioEffectTail
{
    /// <summary>Samples of processing latency the effect adds (0 = output sample <c>n</c> corresponds to input
    /// sample <c>n</c>). A host may compensate by advancing the effect's input.</summary>
    int LatencySamples { get; }

    /// <summary>Samples the effect keeps producing output after its input falls silent (a reverb's full decay);
    /// 0 for memoryless / not-yet-configured effects. Intended for freeze/export renderers to extend their
    /// range by (not yet consumed — today's freeze renders the clip range as-is).</summary>
    int TailSamples { get; }
}
