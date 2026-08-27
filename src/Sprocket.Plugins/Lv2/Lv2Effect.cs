using System.Runtime.InteropServices;
using System.Text;
using Sprocket.Core.Audio;
using Sprocket.Core.Rendering;

namespace Sprocket.Plugins.Lv2;

/// <summary>
/// One instantiated LV2 plugin laid out to process the mixer's interleaved float32 PCM (PLAN.md step 59) — the
/// LV2 counterpart of <c>LadspaInstanceSet</c>, with the same topology rules: a mono (1-in/1-out) plugin is
/// instantiated once per channel (dual-mono), a matched multi-channel plugin runs one instance with a port per
/// channel, extra stream channels pass through. All PCM lives in pinned native memory (ARCHITECTURE.md §1); a
/// finalizer backs <see cref="Dispose"/> because the mixer replaces rather than disposes chain effects.
/// </summary>
/// <remarks>
/// LV2 requires <em>every</em> port to be connected before <c>run</c>: control outputs go to a scratch block,
/// and ports of kinds the core-subset host doesn't drive (which must be <c>lv2:connectionOptional</c> for the
/// plugin to have been admitted) are connected to <c>NULL</c> as the spec allows. The owning binary is never
/// unmapped within a session (see <see cref="Lv2Library"/>), so the descriptor's function pointers stay valid.
/// </remarks>
internal sealed unsafe class Lv2InstanceSet : IDisposable
{
    private readonly nint _descriptorPtr;
    private readonly Lv2Descriptor _desc;

    private nint[] _instances = [];
    private nint _controlIn;
    private nint _controlOut;
    private nint _bundlePath;
    private readonly List<nint> _audioBuffers = [];
    private (int Channel, nint Buffer)[] _inCopies = [];
    private (int Channel, nint Buffer)[] _outCopies = [];
    private bool _activated;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int CapacityFrames { get; }
    public int ControlInputCount { get; }

    public Lv2InstanceSet(nint descriptorPtr, Lv2PluginInfo info, int sampleRate, int channels, int capacityFrames)
    {
        _descriptorPtr = descriptorPtr;
        _desc = *(Lv2Descriptor*)descriptorPtr;
        SampleRate = sampleRate;
        Channels = channels;
        CapacityFrames = capacityFrames;
        ControlInputCount = info.ControlInputs.Count();

        try
        {
            Build(info);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    ~Lv2InstanceSet() => FreeNative();

    private void Build(Lv2PluginInfo info)
    {
        if (_desc.Instantiate == nint.Zero || _desc.ConnectPort == nint.Zero
            || _desc.Run == nint.Zero || _desc.Cleanup == nint.Zero)
            throw new InvalidOperationException("LV2 descriptor is missing instantiate/connect_port/run/cleanup.");

        List<Lv2PortInfo> ctrlIn = info.Ports.Where(p => p.IsControlInput).ToList();
        List<Lv2PortInfo> ctrlOut = info.Ports.Where(p => p.IsControlOutput).ToList();
        List<Lv2PortInfo> audioIn = info.Ports.Where(p => p.IsAudioInput).ToList();
        List<Lv2PortInfo> audioOut = info.Ports.Where(p => p.IsAudioOutput).ToList();
        List<Lv2PortInfo> optionalOther = info.Ports.Where(p => !p.IsSupportedKind).ToList();

        _controlIn = ctrlIn.Count > 0 ? (nint)NativeMemory.AllocZeroed((nuint)ctrlIn.Count, sizeof(float)) : nint.Zero;
        _controlOut = ctrlOut.Count > 0 ? (nint)NativeMemory.AllocZeroed((nuint)ctrlOut.Count, sizeof(float)) : nint.Zero;

        // The spec requires the bundle path to end with a directory separator.
        string bundle = info.BundlePath.EndsWith(Path.DirectorySeparatorChar) ? info.BundlePath : info.BundlePath + Path.DirectorySeparatorChar;
        byte[] bundleBytes = Encoding.UTF8.GetBytes(bundle);
        _bundlePath = (nint)NativeMemory.AllocZeroed((nuint)bundleBytes.Length + 1);
        bundleBytes.CopyTo(new Span<byte>((void*)_bundlePath, bundleBytes.Length));

        bool mono = audioIn.Count == 1 && audioOut.Count == 1;
        int instanceCount = mono ? Channels : 1;
        _instances = new nint[instanceCount];

        var instantiate = (delegate* unmanaged<nint, double, byte*, nint, nint>)_desc.Instantiate;
        var connect = (delegate* unmanaged<nint, uint, void*, void>)_desc.ConnectPort;
        var inCopies = new List<(int, nint)>();
        var outCopies = new List<(int, nint)>();
        nint features = Lv2Features.FeaturesArray;

        for (int i = 0; i < instanceCount; i++)
        {
            nint handle = instantiate(_descriptorPtr, SampleRate, (byte*)_bundlePath, features);
            if (handle == nint.Zero)
                throw new InvalidOperationException("LV2 instantiate() returned null.");
            _instances[i] = handle;

            for (int k = 0; k < ctrlIn.Count; k++)
                connect(handle, (uint)ctrlIn[k].Index, (float*)_controlIn + k);
            for (int k = 0; k < ctrlOut.Count; k++)
                connect(handle, (uint)ctrlOut[k].Index, (float*)_controlOut + k);
            foreach (Lv2PortInfo other in optionalOther)
                connect(handle, (uint)other.Index, null); // connectionOptional: NULL is a legal connection

            if (mono)
            {
                nint inBuf = AllocAudio();
                nint outBuf = AllocAudio();
                connect(handle, (uint)audioIn[0].Index, (float*)inBuf);
                connect(handle, (uint)audioOut[0].Index, (float*)outBuf);
                inCopies.Add((i, inBuf));
                outCopies.Add((i, outBuf));
            }
            else
            {
                for (int j = 0; j < audioIn.Count; j++)
                {
                    nint buf = AllocAudio();
                    connect(handle, (uint)audioIn[j].Index, (float*)buf);
                    if (j < Channels)
                        inCopies.Add((j, buf));
                }
                for (int j = 0; j < audioOut.Count; j++)
                {
                    nint buf = AllocAudio();
                    connect(handle, (uint)audioOut[j].Index, (float*)buf);
                    if (j < Channels)
                        outCopies.Add((j, buf));
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

    /// <summary>Processes one block in place; <paramref name="controlValues"/> is in control-input port order.</summary>
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

        var run = (delegate* unmanaged<nint, uint, void>)_desc.Run;
        foreach (nint handle in _instances)
            run(handle, (uint)frames);

        foreach ((int channel, nint buffer) in _outCopies)
        {
            var b = (float*)buffer;
            for (int f = 0; f < frames; f++)
                interleaved[f * channels + channel] = b[f];
        }
    }

    /// <summary>Resets plugin state (deactivate → activate) and zeroes the port buffers, e.g. after a seek.</summary>
    public void Reset()
    {
        if (_disposed)
            return;
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
            // The spec says deactivate (if present) before cleanup for an activated instance.
            delegate* unmanaged<nint, void> deactivate =
                _activated && _desc.Deactivate != nint.Zero ? (delegate* unmanaged<nint, void>)_desc.Deactivate : null;
            var cleanup = (delegate* unmanaged<nint, void>)_desc.Cleanup;
            foreach (nint handle in _instances)
            {
                if (handle == nint.Zero)
                    continue;
                if (deactivate != null)
                    deactivate(handle);
                cleanup(handle);
            }
        }
        _instances = [];

        foreach (nint buffer in _audioBuffers)
            NativeMemory.Free((void*)buffer);
        _audioBuffers.Clear();

        if (_controlIn != nint.Zero) { NativeMemory.Free((void*)_controlIn); _controlIn = nint.Zero; }
        if (_controlOut != nint.Zero) { NativeMemory.Free((void*)_controlOut); _controlOut = nint.Zero; }
        if (_bundlePath != nint.Zero) { NativeMemory.Free((void*)_bundlePath); _bundlePath = nint.Zero; }
    }
}

/// <summary>
/// The <see cref="IAudioEffect"/> adapter for an LV2 plugin (PLAN.md step 59): (re)instantiates an
/// <see cref="Lv2InstanceSet"/> on the first buffer and whenever the sample rate / channel count changes or the
/// block size grows, then runs each block through it. Control values come from the resolved parameters by port
/// symbol. A plugin fault latches into pass-through rather than throwing on the audio thread (§15).
/// </summary>
internal sealed class Lv2Effect : IAudioEffect, IDisposable
{
    private readonly nint _descriptorPtr;
    private readonly Lv2PluginInfo _info;
    private readonly int _controlInputCount;
    private readonly string[] _controlParamNames;
    private readonly float[] _controlDefaults;
    private readonly float[] _controlScratch;

    private Lv2InstanceSet? _set;
    private bool _failed;

    public Lv2Effect(nint descriptorPtr, Lv2PluginInfo info)
    {
        _descriptorPtr = descriptorPtr;
        _info = info;
        Lv2PortInfo[] controlInputs = info.ControlInputs.ToArray();
        _controlInputCount = controlInputs.Length;
        _controlParamNames = new string[_controlInputCount];
        _controlDefaults = new float[_controlInputCount];
        for (int k = 0; k < _controlInputCount; k++)
        {
            _controlParamNames[k] = Lv2ParameterMapping.ParameterName(controlInputs[k]);
            _controlDefaults[k] = (float)Lv2ParameterMapping.ToParameter(controlInputs[k]).Default;
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
                _set = new Lv2InstanceSet(_descriptorPtr, _info, sampleRate, channels, frames);
            }
            catch
            {
                _set = null;
                _failed = true;
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
