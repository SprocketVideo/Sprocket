using System.Runtime.InteropServices;
using Sprocket.Core.Audio;
using Sprocket.Core.Rendering;

namespace Sprocket.Plugins.Ladspa;

/// <summary>
/// One instantiated LADSPA plugin, laid out to process the mixer's interleaved float32 PCM (PLAN.md step 59).
/// LADSPA runs <b>mono, non-interleaved</b> buffers through one instance per audio port, so this owns the
/// per-port native buffers, connects them once, and de-interleaves / re-interleaves around
/// <c>run(instance, frames)</c>. A mono (1-in/1-out) plugin — the common case — is instantiated once per
/// channel (dual-mono); a matched multi-channel plugin runs a single instance with a port per channel; any
/// remaining stream channels pass through untouched.
/// </summary>
/// <remarks>
/// <para>All PCM stays in pinned <b>native</b> memory (<see cref="NativeMemory"/>); nothing per-buffer touches
/// the managed heap (ARCHITECTURE.md §1). This is a RAII handle in the style of <c>Sprocket.Media/Native/Handles</c>:
/// a finalizer is the safety net because the mixer replaces (never disposes) an <see cref="IAudioEffect"/> when a
/// chain is re-built, so an abandoned instance's native memory and the plugin's <c>cleanup()</c> are still
/// reclaimed on collection. The owning library module is never unloaded within a session (see
/// <c>LadspaLibrary</c>), so the descriptor's function pointers stay valid for the finalizer.</para>
/// </remarks>
internal sealed unsafe class LadspaInstanceSet : IDisposable
{
    private readonly nint _descriptorPtr;
    private readonly LadspaDescriptor _desc;

    private nint[] _instances = [];
    private nint _controlIn;   // ControlInputCount contiguous floats (shared across instances)
    private nint _controlOut;  // control-output scratch (written by the plugin, ignored)
    private readonly List<nint> _audioBuffers = [];
    private (int Channel, nint Buffer)[] _inCopies = [];
    private (int Channel, nint Buffer)[] _outCopies = [];
    private bool _activated;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int CapacityFrames { get; }
    public int ControlInputCount { get; }

    public LadspaInstanceSet(nint descriptorPtr, LadspaPluginInfo info, int sampleRate, int channels, int capacityFrames)
    {
        _descriptorPtr = descriptorPtr;
        _desc = *(LadspaDescriptor*)descriptorPtr;
        SampleRate = sampleRate;
        Channels = channels;
        CapacityFrames = capacityFrames;

        List<LadspaPortInfo> ctrlIn = info.Ports.Where(p => p.IsControlInput).ToList();
        List<LadspaPortInfo> ctrlOut = info.Ports.Where(p => p.IsControlOutput).ToList();
        List<LadspaPortInfo> audioIn = info.Ports.Where(p => p.IsAudioInput).ToList();
        List<LadspaPortInfo> audioOut = info.Ports.Where(p => p.IsAudioOutput).ToList();
        ControlInputCount = ctrlIn.Count;

        try
        {
            Build(ctrlIn, ctrlOut, audioIn, audioOut);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    ~LadspaInstanceSet() => FreeNative();

    private void Build(
        List<LadspaPortInfo> ctrlIn, List<LadspaPortInfo> ctrlOut,
        List<LadspaPortInfo> audioIn, List<LadspaPortInfo> audioOut)
    {
        // instantiate/connect_port/run/cleanup are all mandatory in LADSPA; a plugin missing cleanup would leak
        // the memory its instantiate() allocated on every teardown, so refuse it up front.
        if (_desc.Instantiate == nint.Zero || _desc.ConnectPort == nint.Zero
            || _desc.Run == nint.Zero || _desc.Cleanup == nint.Zero)
            throw new InvalidOperationException("LADSPA descriptor is missing instantiate/connect_port/run/cleanup.");

        _controlIn = ctrlIn.Count > 0 ? (nint)NativeMemory.AllocZeroed((nuint)ctrlIn.Count, sizeof(float)) : nint.Zero;
        _controlOut = ctrlOut.Count > 0 ? (nint)NativeMemory.AllocZeroed((nuint)ctrlOut.Count, sizeof(float)) : nint.Zero;

        bool mono = audioIn.Count == 1 && audioOut.Count == 1;
        int instanceCount = mono ? Channels : 1;
        _instances = new nint[instanceCount];

        var instantiate = (delegate* unmanaged<nint, CULong, nint>)_desc.Instantiate;
        var connect = (delegate* unmanaged<nint, CULong, float*, void>)_desc.ConnectPort;
        var inCopies = new List<(int, nint)>();
        var outCopies = new List<(int, nint)>();

        for (int i = 0; i < instanceCount; i++)
        {
            nint handle = instantiate(_descriptorPtr, new CULong((nuint)SampleRate));
            if (handle == nint.Zero)
                throw new InvalidOperationException("LADSPA instantiate() returned null.");
            _instances[i] = handle;

            for (int k = 0; k < ctrlIn.Count; k++)
                connect(handle, new CULong((nuint)ctrlIn[k].Index), (float*)_controlIn + k);
            for (int k = 0; k < ctrlOut.Count; k++)
                connect(handle, new CULong((nuint)ctrlOut[k].Index), (float*)_controlOut + k);

            if (mono)
            {
                nint inBuf = AllocAudio();
                nint outBuf = AllocAudio();
                connect(handle, new CULong((nuint)audioIn[0].Index), (float*)inBuf);
                connect(handle, new CULong((nuint)audioOut[0].Index), (float*)outBuf);
                inCopies.Add((i, inBuf));
                outCopies.Add((i, outBuf));
            }
            else
            {
                for (int j = 0; j < audioIn.Count; j++)
                {
                    nint buf = AllocAudio();
                    connect(handle, new CULong((nuint)audioIn[j].Index), (float*)buf);
                    if (j < Channels)
                        inCopies.Add((j, buf)); // ports past the channel count stay silent
                }
                for (int j = 0; j < audioOut.Count; j++)
                {
                    nint buf = AllocAudio();
                    connect(handle, new CULong((nuint)audioOut[j].Index), (float*)buf);
                    if (j < Channels)
                        outCopies.Add((j, buf)); // ports past the channel count are discarded
                }
            }
        }

        _inCopies = [.. inCopies];
        _outCopies = [.. outCopies];

        if (_desc.Activate != nint.Zero)
        {
            var activate = (delegate* unmanaged<nint, void>)_desc.Activate;
            foreach (nint handle in _instances)
                activate(handle);
        }
        _activated = true;
    }

    private nint AllocAudio()
    {
        nint buf = (nint)NativeMemory.AllocZeroed((nuint)CapacityFrames, sizeof(float));
        _audioBuffers.Add(buf);
        return buf;
    }

    /// <summary>Processes one block in place. <paramref name="controlValues"/> holds the control-input values in
    /// control-input port order (length <see cref="ControlInputCount"/>).</summary>
    public void Process(Span<float> interleaved, int frames, ReadOnlySpan<float> controlValues)
    {
        if (_disposed || frames <= 0 || frames > CapacityFrames)
            return;

        var controls = (float*)_controlIn;
        for (int k = 0; k < ControlInputCount; k++)
            controls[k] = controlValues[k];

        int channels = Channels;
        foreach ((int channel, nint buffer) in _inCopies)
        {
            var b = (float*)buffer;
            for (int f = 0; f < frames; f++)
                b[f] = interleaved[f * channels + channel];
        }

        var run = (delegate* unmanaged<nint, CULong, void>)_desc.Run;
        foreach (nint handle in _instances)
            run(handle, new CULong((nuint)frames));

        foreach ((int channel, nint buffer) in _outCopies)
        {
            var b = (float*)buffer;
            for (int f = 0; f < frames; f++)
                interleaved[f * channels + channel] = b[f];
        }
    }

    /// <summary>Clears the plugin's internal state (deactivate → activate, the LADSPA reset idiom) and zeroes the
    /// port buffers, e.g. after a transport jump.</summary>
    public void Reset()
    {
        if (_disposed)
            return;
        // LADSPA resets state via activate(); deactivate() is optional (frequently null). The idiom is
        // "deactivate if present, then activate" — gating on both would skip the reset for the many plugins that
        // ship activate() alone, leaving stale delay/reverb/filter memory after a seek.
        if (_activated && _desc.Activate != nint.Zero)
        {
            var activate = (delegate* unmanaged<nint, void>)_desc.Activate;
            delegate* unmanaged<nint, void> deactivate =
                _desc.Deactivate != nint.Zero ? (delegate* unmanaged<nint, void>)_desc.Deactivate : null;
            foreach (nint handle in _instances)
            {
                if (deactivate != null)
                    deactivate(handle);
                activate(handle);
            }
        }
        foreach (nint buffer in _audioBuffers)
            NativeMemory.Clear((void*)buffer, (nuint)CapacityFrames * sizeof(float));
    }

    public void Dispose()
    {
        FreeNative();
        GC.SuppressFinalize(this);
    }

    private void FreeNative()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_desc.Cleanup != nint.Zero)
        {
            var cleanup = (delegate* unmanaged<nint, void>)_desc.Cleanup;
            foreach (nint handle in _instances)
                if (handle != nint.Zero)
                    cleanup(handle);
        }
        _instances = [];

        foreach (nint buffer in _audioBuffers)
            NativeMemory.Free((void*)buffer);
        _audioBuffers.Clear();

        if (_controlIn != nint.Zero) { NativeMemory.Free((void*)_controlIn); _controlIn = nint.Zero; }
        if (_controlOut != nint.Zero) { NativeMemory.Free((void*)_controlOut); _controlOut = nint.Zero; }
    }
}

/// <summary>
/// The <see cref="IAudioEffect"/> adapter for a LADSPA plugin (PLAN.md step 59): a stateful mixer-chain stage
/// that (re)instantiates a <see cref="LadspaInstanceSet"/> on the first buffer and whenever the sample rate,
/// channel count, or block size grows (the format-change idiom the built-ins use), then hands each block through
/// it. Control-input port values come from the resolved parameters by their <c>port&lt;index&gt;</c> keys.
/// </summary>
/// <remarks>
/// Per ARCHITECTURE.md §15 a plugin fault never takes the editor down: if instantiation throws, the effect
/// latches into pass-through and the block flows untouched. Not thread-safe — the mixer drives one instance from
/// a single thread (see <see cref="IAudioEffect"/>).
/// </remarks>
internal sealed class LadspaEffect : IAudioEffect, IDisposable
{
    private readonly nint _descriptorPtr;
    private readonly LadspaPluginInfo _info;
    private readonly int _controlInputCount;
    private readonly string[] _controlParamNames;
    private readonly float[] _controlDefaults;
    private readonly float[] _controlScratch;

    private LadspaInstanceSet? _set;
    private bool _failed;

    public LadspaEffect(nint descriptorPtr, LadspaPluginInfo info)
    {
        _descriptorPtr = descriptorPtr;
        _info = info;
        LadspaPortInfo[] controlInputs = info.ControlInputs.ToArray();
        _controlInputCount = controlInputs.Length;
        // Precompute the per-port key and default once — Process runs per buffer and must not allocate (§1).
        _controlParamNames = new string[_controlInputCount];
        _controlDefaults = new float[_controlInputCount];
        for (int k = 0; k < _controlInputCount; k++)
        {
            _controlParamNames[k] = LadspaParameterMapping.ParameterName(controlInputs[k].Index);
            _controlDefaults[k] = (float)LadspaParameterMapping.ToParameter(controlInputs[k]).Default;
        }
        _controlScratch = new float[_controlInputCount];
    }

    /// <inheritdoc />
    public void Process(Span<float> interleaved, int frames, int sampleRate, int channels, ResolvedEffect parameters)
    {
        if (_failed || frames <= 0 || channels <= 0)
            return;

        if (_set is null || _set.SampleRate != sampleRate || _set.Channels != channels || frames > _set.CapacityFrames)
        {
            try
            {
                _set?.Dispose();
                _set = new LadspaInstanceSet(_descriptorPtr, _info, sampleRate, channels, frames);
            }
            catch
            {
                _set = null;
                _failed = true; // a broken plugin degrades to pass-through rather than throwing on the audio thread
                return;
            }
        }

        for (int k = 0; k < _controlInputCount; k++)
            _controlScratch[k] = (float)parameters.Get(_controlParamNames[k], _controlDefaults[k]);

        try
        {
            _set.Process(interleaved, frames, _controlScratch);
        }
        catch
        {
            // A misbehaving plugin must never throw out of the audio thread (§15); latch to pass-through.
            _failed = true;
        }
    }

    /// <inheritdoc />
    public void Reset() => _set?.Reset();

    public void Dispose()
    {
        _set?.Dispose();
        _set = null;
    }
}
