using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sprocket.Plugins.Ladspa;

namespace Sprocket.Plugins.Tests.Ladspa;

/// <summary>
/// Builds a real <c>LADSPA_Descriptor</c> in unmanaged memory whose function pointers target
/// <c>[UnmanagedCallersOnly]</c> methods in this test — the native equivalent of how the managed-plugin tests
/// load a real <c>Sprocket.TestPlugin</c> assembly. It lets the LADSPA binding, the descriptor reader and the
/// <see cref="LadspaEffect"/> DSP plumbing be exercised end-to-end without shipping a platform-specific
/// <c>.so</c>/<c>.dll</c>/<c>.dylib</c>. The plugin is a <b>stateful</b> "gain + one-sample delay":
/// <c>out[i] = in[i-1] * gain</c>, carrying the previous block's last input across buffers, so both gain and
/// cross-buffer state continuity are directly assertable. Ports: 0 = control-in "Gain" (0…10, default 1),
/// 1 = audio-in, 2 = audio-out (a mono 1-in/1-out plugin — the dual-mono common case).
/// </summary>
internal sealed unsafe class FakeLadspaPlugin : IDisposable
{
    private readonly List<nint> _allocations = [];

    /// <param name="withDeactivate">When false the descriptor's <c>deactivate</c> pointer is null (the common
    /// LADSPA case), so tests can prove reset still works through <c>activate</c> alone.</param>
    public FakeLadspaPlugin(ulong uniqueId = 1234, string label = "test_gain_delay", bool withDeactivate = true)
    {
        int* ports = (int*)Alloc(sizeof(int) * 3);
        ports[0] = LadspaAbi.PortControl | LadspaAbi.PortInput;
        ports[1] = LadspaAbi.PortAudio | LadspaAbi.PortInput;
        ports[2] = LadspaAbi.PortAudio | LadspaAbi.PortOutput;

        nint* names = (nint*)Alloc(sizeof(nint) * 3);
        names[0] = AllocUtf8("Gain");
        names[1] = AllocUtf8("In");
        names[2] = AllocUtf8("Out");

        var hints = (LadspaPortRangeHint*)Alloc(sizeof(LadspaPortRangeHint) * 3);
        hints[0] = new LadspaPortRangeHint
        {
            HintDescriptor = LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove | LadspaAbi.HintDefault1,
            LowerBound = 0f,
            UpperBound = 10f,
        };
        hints[1] = default;
        hints[2] = default;

        var descriptor = new LadspaDescriptor
        {
            UniqueID = new CULong((nuint)uniqueId),
            Label = AllocUtf8(label),
            Properties = 0,
            Name = AllocUtf8("Test Gain Delay"),
            Maker = AllocUtf8("Sprocket Tests"),
            Copyright = AllocUtf8("MIT"),
            PortCount = new CULong(3),
            PortDescriptors = (nint)ports,
            PortNames = (nint)names,
            PortRangeHints = (nint)hints,
            ImplementationData = nint.Zero,
            Instantiate = (nint)(delegate* unmanaged<nint, CULong, nint>)&Instantiate,
            ConnectPort = (nint)(delegate* unmanaged<nint, CULong, float*, void>)&ConnectPort,
            Activate = (nint)(delegate* unmanaged<nint, void>)&Activate,
            Run = (nint)(delegate* unmanaged<nint, CULong, void>)&Run,
            RunAdding = nint.Zero,
            SetRunAddingGain = nint.Zero,
            Deactivate = withDeactivate ? (nint)(delegate* unmanaged<nint, void>)&Deactivate : nint.Zero,
            Cleanup = (nint)(delegate* unmanaged<nint, void>)&Cleanup,
        };

        nint descriptorPtr = Alloc(sizeof(LadspaDescriptor));
        *(LadspaDescriptor*)descriptorPtr = descriptor;
        DescriptorPtr = descriptorPtr;
    }

    /// <summary>The <c>LADSPA_Descriptor*</c> to feed into the reader / mapper / effect.</summary>
    public nint DescriptorPtr { get; }

    private nint Alloc(int bytes)
    {
        nint p = (nint)NativeMemory.AllocZeroed((nuint)bytes);
        _allocations.Add(p);
        return p;
    }

    private nint AllocUtf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        nint p = (nint)NativeMemory.AllocZeroed((nuint)(bytes.Length + 1));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        _allocations.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (nint p in _allocations)
            NativeMemory.Free((void*)p);
        _allocations.Clear();
    }

    // ── The native plugin implementation (one instance = one FakeState) ──

    [StructLayout(LayoutKind.Sequential)]
    private struct FakeState
    {
        public nint Gain; // float* (control-in)
        public nint In;   // float* (audio-in buffer)
        public nint Out;  // float* (audio-out buffer)
        public float Prev;
    }

    [UnmanagedCallersOnly]
    private static nint Instantiate(nint descriptor, CULong sampleRate)
        => (nint)NativeMemory.AllocZeroed((nuint)sizeof(FakeState));

    [UnmanagedCallersOnly]
    private static void ConnectPort(nint handle, CULong port, float* data)
    {
        var s = (FakeState*)handle;
        switch ((int)port.Value)
        {
            case 0: s->Gain = (nint)data; break;
            case 1: s->In = (nint)data; break;
            case 2: s->Out = (nint)data; break;
        }
    }

    [UnmanagedCallersOnly]
    private static void Activate(nint handle) => ((FakeState*)handle)->Prev = 0f;

    [UnmanagedCallersOnly]
    private static void Deactivate(nint handle) { /* no-op; Reset uses deactivate→activate */ }

    [UnmanagedCallersOnly]
    private static void Run(nint handle, CULong sampleCount)
    {
        var s = (FakeState*)handle;
        int n = (int)sampleCount.Value;
        float gain = *(float*)s->Gain;
        var input = (float*)s->In;
        var output = (float*)s->Out;
        float prev = s->Prev;
        for (int i = 0; i < n; i++)
        {
            float x = input[i];
            output[i] = prev * gain; // one-sample delay: this block's first out uses the carried prev
            prev = x;
        }
        s->Prev = prev;
    }

    [UnmanagedCallersOnly]
    private static void Cleanup(nint handle) => NativeMemory.Free((void*)handle);
}
