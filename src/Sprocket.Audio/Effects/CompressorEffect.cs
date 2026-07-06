using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>A snapshot of a <see cref="CompressorEffect"/>'s live readout — how many dB it is currently
/// attenuating (always ≥ 0, before make-up gain) and the input/output peak level of the most recently processed
/// block, all in dB(FS). <see cref="double.NegativeInfinity"/> peaks mean silence (or nothing processed yet).
/// For UI metering only (PLAN.md step 31); not part of the DSP contract.</summary>
public readonly record struct CompressorMeterSnapshot(double GainReductionDb, double InputPeakDb, double OutputPeakDb);

/// <summary>
/// A feed-forward dynamic-range compressor (PLAN.md step 31): a per-frame peak detector (max |sample| across
/// channels) smoothed by one-pole attack/release envelopes, a hard-knee gain computer
/// (<see cref="EffectParamNames.ThresholdDb"/> / <see cref="EffectParamNames.Ratio"/>), and static make-up
/// gain — the textbook design every DAW ships. All channels receive the same gain (stereo-linked), so the
/// image never shifts. The envelope carries across buffers for seamless block processing.
/// </summary>
/// <remarks>
/// <para><b>Metering.</b> Each <see cref="Process"/> call also tracks the block's peak gain reduction (before
/// make-up — the standard GR-meter reading) and its input/output peak level, published under a small lock for
/// <see cref="TakeSnapshot"/> to read from the UI thread — the same publish discipline
/// <see cref="Loudness.LoudnessMeter"/> uses. Purely a read-out: it costs a few extra flops per sample and
/// never allocates, so it doesn't affect the DSP or the allocation-free steady-state guarantee (§1, §19).</para>
/// </remarks>
public sealed class CompressorEffect : IAudioEffect
{
    private double _envelope; // linear peak envelope, carried across buffers

    private readonly object _meterGate = new();
    private double _pubGainReductionDb;
    private double _pubInputPeakDb = double.NegativeInfinity;
    private double _pubOutputPeakDb = double.NegativeInfinity;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double thresholdDb = parameters.Get(EffectParamNames.ThresholdDb, -18);
        double ratio = Math.Max(1.0, parameters.Get(EffectParamNames.Ratio, 4));
        double attackMs = Math.Max(0.01, parameters.Get(EffectParamNames.AttackMs, 10));
        double releaseMs = Math.Max(1.0, parameters.Get(EffectParamNames.ReleaseMs, 100));
        double makeupDb = parameters.Get(EffectParamNames.MakeupDb);

        double attack = Math.Exp(-1.0 / (attackMs / 1000.0 * sampleRate));
        double release = Math.Exp(-1.0 / (releaseMs / 1000.0 * sampleRate));
        var makeup = (float)Math.Pow(10, makeupDb / 20.0);
        double slope = 1.0 - 1.0 / ratio; // dB of reduction per dB over threshold

        float blockInPeak = 0f, blockOutPeak = 0f;
        double blockReductionDb = 0;

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
            if (peak > blockInPeak)
                blockInPeak = peak;

            // One-pole smoothing: fast rise (attack), slow fall (release).
            double coeff = peak > _envelope ? attack : release;
            _envelope = coeff * _envelope + (1 - coeff) * peak;

            float gain = makeup;
            if (_envelope > 1e-9)
            {
                double envDb = 20 * Math.Log10(_envelope);
                double overDb = envDb - thresholdDb;
                if (overDb > 0)
                {
                    double reductionDb = overDb * slope;
                    if (reductionDb > blockReductionDb)
                        blockReductionDb = reductionDb;
                    gain = (float)Math.Pow(10, (makeupDb - reductionDb) / 20.0);
                }
            }

            for (int ch = 0; ch < channels; ch++)
            {
                float outSample = interleaved[baseIndex + ch] * gain;
                interleaved[baseIndex + ch] = outSample;
                float absOut = Math.Abs(outSample);
                if (absOut > blockOutPeak)
                    blockOutPeak = absOut;
            }
        }

        lock (_meterGate)
        {
            _pubGainReductionDb = blockReductionDb;
            _pubInputPeakDb = ToDb(blockInPeak);
            _pubOutputPeakDb = ToDb(blockOutPeak);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        _envelope = 0;
        lock (_meterGate)
        {
            _pubGainReductionDb = 0;
            _pubInputPeakDb = double.NegativeInfinity;
            _pubOutputPeakDb = double.NegativeInfinity;
        }
    }

    /// <summary>Reads the current published meter values. Safe to call from any thread.</summary>
    public CompressorMeterSnapshot TakeSnapshot()
    {
        lock (_meterGate)
            return new CompressorMeterSnapshot(_pubGainReductionDb, _pubInputPeakDb, _pubOutputPeakDb);
    }

    private static double ToDb(double linear) => linear > 0.0 ? 20.0 * Math.Log10(linear) : double.NegativeInfinity;
}
