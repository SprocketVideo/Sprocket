using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// The simplest audio chain stage (PLAN.md step 31): a static gain (<see cref="EffectParamNames.GainDb"/>,
/// 0 dB = unity) plus stereo balance (<see cref="EffectParamNames.Pan"/>, same <see cref="PanLaw"/> as the
/// track pan). Stateless; pan applies only to a stereo buffer (other layouts get gain only).
/// </summary>
public sealed class GainPanEffect : IAudioEffect
{
    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double gainDb = parameters.Get(EffectParamNames.GainDb);
        double pan = parameters.Get(EffectParamNames.Pan);
        (double panL, double panR) = PanLaw.Balance(pan);
        var gain = (float)Math.Pow(10, gainDb / 20.0);

        if (channels == 2 && (panL != 1.0 || panR != 1.0))
        {
            float gl = gain * (float)panL;
            float gr = gain * (float)panR;
            for (int f = 0; f < frames; f++)
            {
                interleaved[f * 2] *= gl;
                interleaved[f * 2 + 1] *= gr;
            }
            return;
        }

        if (gain == 1f)
            return;
        for (int i = 0; i < frames * channels; i++)
            interleaved[i] *= gain;
    }

    /// <inheritdoc />
    public void Reset()
    {
        // Stateless.
    }
}
