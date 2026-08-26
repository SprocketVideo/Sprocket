using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Audio.Effects;

/// <summary>
/// Acoustic Space / Convolution Reverb (PLAN.md step 49): emulates a real captured space by convolving the
/// signal with an impulse response, shipped as its own dedicated effect beside the algorithmic
/// <see cref="StudioReverbEffect"/> / <see cref="ShimmerReverbEffect"/> (the DAW convention — Logic Space
/// Designer next to ChromaVerb, Ableton Convolution Reverb next to Reverb). The IR arrives as an <em>asset</em>
/// reference (<see cref="EffectParamNames.ImpulseResponse"/>, a WAV path) resolved through
/// <see cref="ImpulseResponseCache"/>, which loads, resamples and partitions it in the background; until it
/// lands — or if the file is missing / unreadable — the effect passes the dry signal through unchanged (the
/// graceful-degradation convention for unavailable effects, §15), never blocking or failing the audio thread.
/// </summary>
/// <remarks>
/// <para><b>DSP.</b> One <see cref="PartitionedConvolver"/> per output channel (a mono IR feeds every channel; a
/// stereo IR maps L→L, R→R) — zero-latency uniformly partitioned convolution, so a multi-second hall costs
/// bounded per-buffer work instead of the O(IR) per sample of direct convolution. Around it, in signal order:
/// <see cref="EffectParamNames.PreDelayMs"/> delays the wet feed; <see cref="EffectParamNames.IrLength"/>
/// trims the tail by scaling the later partitions' gains with a cosine fade (no re-transform, so it is a live
/// control); <see cref="EffectParamNames.HighDamp"/> / <see cref="EffectParamNames.LowDamp"/> shape the wet
/// output with one-pole low-pass / high-pass filters (exact bypass at 0); <see cref="EffectParamNames.Width"/>
/// is a mid/side scale on a stereo wet pair; <see cref="EffectParamNames.Mix"/> blends (0 = exact pass-through
/// that advances no state, like the other reverbs).</para>
/// <para><b>Real-time safety.</b> Steady state allocates nothing; buffers are (re)allocated only when the
/// sample rate / channel count / IR identity changes. This is the CPU-heaviest built-in
/// (<see cref="AudioEffectTraits.IsHeavy"/>), so the Inspector steers clip-scope users toward Sequence ▸
/// Freeze Clip Audio for long IRs. Deterministic — freeze/export replay matches preview bit for bit.
/// <see cref="IAudioEffectTail"/> reports zero latency and a tail equal to the loaded IR's length.</para>
/// </remarks>
public sealed class ConvolutionReverbEffect : IAudioEffect, IAudioEffectTail
{
    private const double MaxPreDelaySeconds = 0.2;
    private const double TrimFadeFraction = 0.25; // the trimmed tail's last quarter fades out (no hard cut)
    private const double HighDampMinHz = 1000.0;  // full HighDamp low-passes the tail here (from 20 kHz open)
    private const double LowDampMaxHz = 500.0;    // full LowDamp high-passes the tail here (from 20 Hz open)

    private readonly Func<string, int, ImpulseResponse?> _resolve;
    private ImpulseResponse? _ir;
    private string _irPath = ""; // the asset path _ir was resolved for, so a steady buffer skips re-resolution
    private PartitionedConvolver[] _convolvers = [];
    private DelayLine[] _preDelay = [];
    private float[] _wet = [];        // per-channel wet scratch for the width stage
    private float[] _highState = [];  // HighDamp one-pole low-pass per channel
    private float[] _lowState = [];   // LowDamp one-pole low tracker per channel
    private int _rate, _channels;

    /// <summary>Creates the effect resolving IR assets through the shared <see cref="ImpulseResponseCache"/>.</summary>
    public ConvolutionReverbEffect() : this(ImpulseResponseCache.TryGet)
    {
    }

    /// <summary>Creates the effect with a custom (path, sample rate) → IR resolver — tests inject synthetic
    /// IRs synchronously this way, bypassing file IO and the background loader.</summary>
    internal ConvolutionReverbEffect(Func<string, int, ImpulseResponse?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <inheritdoc />
    public int LatencySamples => 0;

    /// <inheritdoc />
    public int TailSamples => _ir?.Length ?? 0;

    /// <summary>Whether an impulse response is currently loaded and convolving (false = passing through).</summary>
    public bool IsImpulseResponseLoaded => _ir is not null;

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        double mix = Math.Clamp(parameters.Get(EffectParamNames.Mix, 0.3), 0, 1);
        if (mix == 0)
            return; // fully dry — exact pass-through, no state to advance

        string path = parameters.GetAsset(EffectParamNames.ImpulseResponse);
        if (path.Length == 0)
        {
            _ir = null;
            _irPath = ""; // no IR: exact dry pass-through
            return;
        }
        // Resolve through the shared cache only until we hold this path's IR at the current format. Once loaded we
        // keep our own reference and stop polling the cache, so an eviction of an in-use IR (ImpulseResponseCache
        // drops arbitrary entries beyond MaxEntries when many IRs are auditioned) never forces a reload-induced
        // dry gap mid-playback — this is the "a live effect keeps its own reference" invariant the cache assumes.
        if (_ir is null || !string.Equals(path, _irPath, StringComparison.Ordinal)
            || sampleRate != _rate || channels != _channels)
        {
            ImpulseResponse? ir = _resolve(path, sampleRate);
            if (ir is null)
            {
                _ir = null; // not-yet-loaded / failed IR: dry pass-through (the next buffer re-checks the cache)
                return;
            }
            if (!ReferenceEquals(ir, _ir) || sampleRate != _rate || channels != _channels)
                Allocate(ir, sampleRate, channels);
            _irPath = path;
        }

        int preDelaySamples = (int)(Math.Clamp(parameters.Get(EffectParamNames.PreDelayMs, 0.0), 0, MaxPreDelaySeconds * 1000)
                                    / 1000.0 * sampleRate);
        double length = Math.Clamp(parameters.Get(EffectParamNames.IrLength, 1.0), 0.01, 1);
        UpdatePartitionGains(length);

        double highDamp = Math.Clamp(parameters.Get(EffectParamNames.HighDamp, 0.0), 0, 1);
        double lowDamp = Math.Clamp(parameters.Get(EffectParamNames.LowDamp, 0.0), 0, 1);
        // Exponential sweeps: HighDamp 20 kHz → 1 kHz low-pass, LowDamp 20 Hz → 500 Hz high-pass.
        var highPole = (float)(1 - Math.Exp(-2 * Math.PI * (20000.0 * Math.Pow(HighDampMinHz / 20000.0, highDamp)) / sampleRate));
        var lowPole = (float)(1 - Math.Exp(-2 * Math.PI * (20.0 * Math.Pow(LowDampMaxHz / 20.0, lowDamp)) / sampleRate));
        bool applyHigh = highDamp > 0, applyLow = lowDamp > 0;
        // A bypassed filter forgets its state so re-engaging it later starts clean rather than with a step.
        if (!applyHigh)
            _highState.AsSpan().Clear();
        if (!applyLow)
            _lowState.AsSpan().Clear();

        double width = Math.Clamp(parameters.Get(EffectParamNames.Width, 1.0), 0, 1);
        var side = (float)width;
        bool applyWidth = channels == 2 && width < 1;
        var wet = (float)mix;
        float dry = 1 - wet;

        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                float input = interleaved[baseIndex + ch];
                float feed = input;
                if (preDelaySamples > 0)
                {
                    DelayLine line = _preDelay[ch];
                    feed = line.Tap(preDelaySamples);
                    line.Push(input);
                }
                float w = _convolvers[ch].Process(feed);
                if (applyHigh)
                {
                    _highState[ch] += highPole * (w - _highState[ch]);
                    w = _highState[ch];
                }
                if (applyLow)
                {
                    _lowState[ch] += lowPole * (w - _lowState[ch]);
                    w -= _lowState[ch];
                }
                _wet[ch] = w;
            }

            if (applyWidth)
            {
                // Width as mid/side on the wet pair (0 = mono, 1 = as captured), like the Studio Reverb.
                float mid = 0.5f * (_wet[0] + _wet[1]);
                float sideAmt = 0.5f * (_wet[0] - _wet[1]) * side;
                _wet[0] = mid + sideAmt;
                _wet[1] = mid - sideAmt;
            }

            for (int ch = 0; ch < channels; ch++)
                interleaved[baseIndex + ch] = dry * interleaved[baseIndex + ch] + wet * _wet[ch];
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        foreach (PartitionedConvolver c in _convolvers)
            c.Reset();
        foreach (DelayLine d in _preDelay)
            d.Clear();
        _highState.AsSpan().Clear();
        _lowState.AsSpan().Clear();
    }

    /// <summary>Maps the IR-length trim onto per-partition gains: unity up to the trim point minus a fade
    /// region, a raised-cosine fall to zero at the trim point, zero beyond. Length = 1 is exactly unity
    /// everywhere (the whole IR, bit-exact).</summary>
    private void UpdatePartitionGains(double length)
    {
        int partitions = _ir!.PartitionCount;
        float[] gains = _convolvers[0].PartitionGains;
        if (length >= 1)
        {
            if (gains[^1] != 1f)
                Array.Fill(gains, 1f);
        }
        else
        {
            double fadeStart = length * (1 - TrimFadeFraction);
            for (int k = 0; k < partitions; k++)
            {
                double position = (k + 0.5) / partitions; // partition centre as a fraction of the IR
                float g;
                if (position <= fadeStart)
                    g = 1f;
                else if (position >= length)
                    g = 0f;
                else
                    g = (float)(0.5 * (1 + Math.Cos(Math.PI * (position - fadeStart) / (length - fadeStart))));
                gains[k] = g;
            }
        }
        // Every channel's convolver reads the same trim (they share the IR).
        for (int ch = 1; ch < _convolvers.Length; ch++)
            gains.CopyTo(_convolvers[ch].PartitionGains, 0);
    }

    /// <summary>(Re)builds the per-channel convolvers for a new IR / format. This is a state change, not steady
    /// state: a 10 s stereo IR allocates ~15 MB of frequency-domain delay line here, on the thread that first
    /// sees the IR (the audio thread when a pick lands mid-playback) — a one-off cost per IR swap. If it ever
    /// proves audible, pool the FDL buffers by partition count.</summary>
    private void Allocate(ImpulseResponse ir, int sampleRate, int channels)
    {
        _ir = ir;
        _rate = sampleRate;
        _channels = channels;
        _convolvers = new PartitionedConvolver[channels];
        _preDelay = new DelayLine[channels];
        int preDelayCapacity = (int)(MaxPreDelaySeconds * sampleRate) + 2;
        for (int ch = 0; ch < channels; ch++)
        {
            _convolvers[ch] = new PartitionedConvolver(ir, ch);
            _preDelay[ch] = new DelayLine(preDelayCapacity);
        }
        _wet = new float[channels];
        _highState = new float[channels];
        _lowState = new float[channels];
    }
}
