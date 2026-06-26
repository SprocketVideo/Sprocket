using System.Numerics;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;

namespace Sprocket.Audio;

/// <summary>
/// Fills one interleaved float32 output buffer for a timeline range by executing the render graph's
/// <see cref="AudioBufferPlan"/> (ARCHITECTURE.md §6): for each audible audio layer it pulls PCM from that
/// source through the <see cref="IPcmReader"/> seam, applies the layer's gain envelope (a linear ramp across
/// the buffer, which is how fades work), and sums into the mix; then it applies master gain and clamps.
/// </summary>
/// <remarks>
/// <para>The mixer keeps each source's reader positioned for sequential playback and only issues a
/// <see cref="IPcmReader.SeekTo"/> when the requested source time jumps (a scrub), so steady playback never
/// re-seeks. It is driven by a single feeder thread (the audio engine), so it holds no locks.</para>
/// <para>Readers are resolved lazily by <see cref="MediaRefId"/> and owned by the mixer (disposed with it).</para>
/// </remarks>
public sealed class AudioMixer : IDisposable
{
    // Re-seek a reader only when the requested source time drifts beyond this; sequential playback stays within
    // sub-sample rounding, so this avoids needless seeks while still catching real scrubs.
    private static readonly long SeekToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    private sealed class SourceState
    {
        public required IPcmReader Reader;
        public Timecode NextSourceTime;
        public bool Positioned;
    }

    private readonly Func<MediaRefId, IPcmReader?> _resolve;
    private readonly Dictionary<MediaRefId, SourceState> _states = new();
    private float[] _layerScratch = [];
    private bool _disposed;

    /// <summary>Creates a mixer for the given output format. <paramref name="resolveReader"/> maps a media id to
    /// its PCM reader (returning null for an unavailable/offline source, which mixes as silence).</summary>
    public AudioMixer(int sampleRate, int channels, Func<MediaRefId, IPcmReader?> resolveReader)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        ArgumentNullException.ThrowIfNull(resolveReader);
        SampleRate = sampleRate;
        Channels = channels;
        _resolve = resolveReader;
    }

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Output channel count.</summary>
    public int Channels { get; }

    /// <summary>
    /// Mixes the timeline audio for the buffer that starts at <paramref name="timelineStart"/> and spans
    /// <c><paramref name="destinationInterleaved"/>.Length / Channels</c> sample-frames into the destination
    /// (fully overwritten — silence where no clip plays).
    /// </summary>
    public void MixInto(Span<float> destinationInterleaved, Timecode timelineStart, Project project)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(project);

        destinationInterleaved.Clear();

        int frames = destinationInterleaved.Length / Channels;
        if (frames == 0)
            return;

        Timecode duration = Timecode.FromSamples(frames, SampleRate);
        AudioBufferPlan plan = RenderGraph.PlanAudioBuffer(project, timelineStart, duration);

        EnsureScratch(frames * Channels);
        Span<float> layer = _layerScratch.AsSpan(0, frames * Channels);

        foreach (AudioLayer al in plan.Layers)
        {
            IPcmReader? reader = ResolvePositioned(al.MediaRefId, al.SourceStart);
            if (reader is null)
                continue;

            layer.Clear();
            int got = reader.Read(layer);
            AdvanceReader(al.MediaRefId, got);

            SumWithRamp(destinationInterleaved, layer, frames, al.GainStartLinear, al.GainEndLinear);
        }

        ApplyMasterGainAndClamp(destinationInterleaved, plan.MasterGainLinear);
    }

    /// <summary>Resolves the reader for a media id and seeks it if the requested source time has jumped.</summary>
    private IPcmReader? ResolvePositioned(MediaRefId id, Timecode sourceStart)
    {
        if (!_states.TryGetValue(id, out SourceState? state))
        {
            IPcmReader? reader = _resolve(id);
            if (reader is null)
                return null;
            state = new SourceState { Reader = reader };
            _states[id] = state;
        }

        if (!state.Positioned || Math.Abs(state.NextSourceTime.Ticks - sourceStart.Ticks) > SeekToleranceTicks)
        {
            state.Reader.SeekTo(sourceStart);
            state.NextSourceTime = sourceStart;
            state.Positioned = true;
        }
        return state.Reader;
    }

    private void AdvanceReader(MediaRefId id, int framesRead)
    {
        SourceState state = _states[id];
        state.NextSourceTime += Timecode.FromSamples(framesRead, SampleRate);
    }

    /// <summary>Sums <paramref name="layer"/> into <paramref name="mix"/>, scaling by a per-frame gain that
    /// ramps linearly from <paramref name="gainStart"/> to <paramref name="gainEnd"/> across the buffer.</summary>
    private void SumWithRamp(Span<float> mix, ReadOnlySpan<float> layer, int frames, double gainStart, double gainEnd)
    {
        double step = frames > 1 ? (gainEnd - gainStart) / frames : 0.0;
        int c = Channels;
        for (int f = 0; f < frames; f++)
        {
            float gain = (float)(gainStart + step * f);
            int baseIndex = f * c;
            for (int ch = 0; ch < c; ch++)
                mix[baseIndex + ch] += layer[baseIndex + ch] * gain;
        }
    }

    /// <summary>Applies master gain and hard-limits to [-1, 1], vectorised over the whole buffer (§6 SIMD).</summary>
    private static void ApplyMasterGainAndClamp(Span<float> mix, double masterGainLinear)
    {
        var gain = (float)masterGainLinear;
        int width = Vector<float>.Count;
        var gainVec = new Vector<float>(gain);
        var lo = new Vector<float>(-1f);
        var hi = new Vector<float>(1f);

        int i = 0;
        for (; i <= mix.Length - width; i += width)
        {
            var v = new Vector<float>(mix.Slice(i, width)) * gainVec;
            v = Vector.Max(lo, Vector.Min(hi, v));
            v.CopyTo(mix.Slice(i, width));
        }
        for (; i < mix.Length; i++)
            mix[i] = Math.Clamp(mix[i] * gain, -1f, 1f);
    }

    private void EnsureScratch(int floats)
    {
        if (_layerScratch.Length < floats)
            _layerScratch = new float[floats];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (SourceState state in _states.Values)
            state.Reader.Dispose();
        _states.Clear();
    }
}
