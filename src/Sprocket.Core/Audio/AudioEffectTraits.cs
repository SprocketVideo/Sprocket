using Sprocket.Core.Model;

namespace Sprocket.Core.Audio;

/// <summary>
/// Static cost/tail metadata about audio effect types (PLAN.md step 41) — the small optional surface beside
/// <see cref="IAudioEffect"/> the step called for, kept in Core so the UI can warn about likely-CPU-heavy
/// chains (and steer users toward Sequence ▸ Freeze Clip Audio, which pre-renders the range through the
/// step-32 audio render cache) without instantiating any DSP. Traits describe the effect <em>type</em>, not
/// an instance; hosted plugins (steps 31/33) will report their own latency/tail through their bridge and can
/// be folded in here later.
/// </summary>
public static class AudioEffectTraits
{
    /// <summary>
    /// Whether chains containing this effect are worth pre-rendering ("freezing") rather than recomputing
    /// every playback pass — long-tailed or CPU-expensive DSP (the Studio Reverb tier and the step-50
    /// Shimmer Reverb today; the convolution reverb, step 49, will join them).
    /// </summary>
    public static bool IsHeavy(string effectTypeId) =>
        effectTypeId is EffectTypeIds.AudioStudioReverb or EffectTypeIds.AudioShimmerReverb;

    /// <summary>Whether <paramref name="chain"/> contains any enabled heavy effect (see <see cref="IsHeavy"/>).</summary>
    public static bool HasHeavyEffect(IEnumerable<EffectInstance> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        foreach (EffectInstance effect in chain)
        {
            if (effect.Enabled && IsHeavy(effect.EffectTypeId))
                return true;
        }
        return false;
    }
}
