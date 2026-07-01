using Sprocket.Core.Audio;
using Sprocket.Core.Model;

namespace Sprocket.Audio.Effects;

/// <summary>
/// The registry of built-in managed audio DSP effects (PLAN.md step 31, ARCHITECTURE.md §19): pure C#,
/// cross-platform, deterministic (golden-PCM testable). The mixer creates one instance per chain position via
/// <see cref="Create"/> and drives it block-by-block; hosted native plugins (VST3/AU) later arrive through the
/// same <see cref="IAudioEffect"/> seam via a C-ABI bridge.
/// </summary>
public static class BuiltInAudioEffects
{
    /// <summary>
    /// Creates a fresh (stateful) instance of the built-in effect named by <paramref name="effectTypeId"/>, or
    /// <see langword="null"/> for an unknown id — which the mixer treats as a pass-through, mirroring how the
    /// video pipeline skips unknown shader ids (§15).
    /// </summary>
    public static IAudioEffect? Create(string effectTypeId) => effectTypeId switch
    {
        EffectTypeIds.AudioGain => new GainPanEffect(),
        EffectTypeIds.AudioEq => new ParametricEqEffect(),
        EffectTypeIds.AudioCompressor => new CompressorEffect(),
        EffectTypeIds.AudioReverb => new ReverbEffect(),
        _ => null,
    };
}
