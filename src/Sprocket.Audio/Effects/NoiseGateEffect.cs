using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Noise Gate (PLAN.md step 47): the standard DAW gate design (Ableton Gate, Logic Noise Gate, Pro Tools
/// Dyn3 Expander/Gate). A per-frame peak detector (max |sample| across channels, instant rise, fixed fast
/// fall — the same detector shape as <see cref="CompressorEffect"/>) drives a stereo-linked gain that opens
/// when the envelope rises above <see cref="EffectParamNames.ThresholdDb"/> and closes only after it has
/// stayed below the close threshold (threshold − <see cref="EffectParamNames.HysteresisDb"/>, so
/// threshold-straddling input can't chatter) for <see cref="EffectParamNames.HoldMs"/> (so brief dips and
/// decaying tails aren't clipped off). The gain ramps between unity and the
/// <see cref="EffectParamNames.RangeDb"/> floor (partial attenuation rather than hard on/off — the
/// Pro Tools / Logic "Range" convention) through one-pole <see cref="EffectParamNames.AttackMs"/> /
/// <see cref="EffectParamNames.ReleaseMs"/> smoothing. Causal (no look-ahead), real-time-safe, allocation
/// free; all state carries across buffers for seamless block processing. A 0 dB range is an exact
/// pass-through that advances no state.
/// </summary>
public sealed class NoiseGateEffect : IAudioEffect
{
    /// <summary>Fixed detector fall time: fast enough to track speech pauses, slow enough that the envelope
    /// rides a low-frequency cycle's peaks instead of collapsing between them.</summary>
    internal const double DetectorReleaseMs = 10.0;

    private double _envelope;   // linear peak envelope, carried across buffers
    private double _gain;       // smoothed gate gain; starts (and Resets) fully closed
    private bool _open;
    private int _holdRemaining; // samples of hold left once the signal falls below the close threshold

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double rangeDb = Math.Clamp(parameters.Get(EffectParamNames.RangeDb, -80), -80, 0);
        if (rangeDb >= 0)
            return; // a 0 dB floor never attenuates — exact pass-through, no state to advance

        double thresholdDb = parameters.Get(EffectParamNames.ThresholdDb, -40);
        double attackMs = Math.Max(0.01, parameters.Get(EffectParamNames.AttackMs, 1));
        double holdMs = Math.Max(0.0, parameters.Get(EffectParamNames.HoldMs, 50));
        double releaseMs = Math.Max(1.0, parameters.Get(EffectParamNames.ReleaseMs, 100));
        double hysteresisDb = Math.Max(0.0, parameters.Get(EffectParamNames.HysteresisDb, 3));

        double attack = Math.Exp(-1.0 / (attackMs / 1000.0 * sampleRate));
        double release = Math.Exp(-1.0 / (releaseMs / 1000.0 * sampleRate));
        double detectorFall = Math.Exp(-1.0 / (DetectorReleaseMs / 1000.0 * sampleRate));
        int holdSamples = (int)(holdMs / 1000.0 * sampleRate);
        double closeDb = thresholdDb - hysteresisDb;
        double floor = Math.Pow(10, rangeDb / 20.0);

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            float peak = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                float abs = Math.Abs(interleaved[baseIndex + ch]);
                if (abs > peak)
                    peak = abs;
            }

            // Peak detector: instant rise, fixed fast fall toward the current peak.
            _envelope = peak > _envelope ? peak : detectorFall * _envelope + (1 - detectorFall) * peak;
            double envDb = _envelope > 1e-9 ? 20 * Math.Log10(_envelope) : -180.0;

            if (envDb >= thresholdDb)
            {
                _open = true;
                _holdRemaining = holdSamples;
            }
            else if (_open)
            {
                if (envDb >= closeDb)
                    _holdRemaining = holdSamples; // inside the hysteresis band: stay open, hold re-armed
                else if (_holdRemaining > 0)
                    _holdRemaining--;             // a brief dip: hold keeps the gate open
                else
                    _open = false;
            }

            double target = _open ? 1.0 : floor;
            double coeff = target > _gain ? attack : release;
            _gain = coeff * _gain + (1 - coeff) * target;

            var gain = (float)_gain;
            for (int ch = 0; ch < channels; ch++)
                interleaved[baseIndex + ch] *= gain;
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        _envelope = 0;
        _gain = 0;
        _open = false;
        _holdRemaining = 0;
    }
}
