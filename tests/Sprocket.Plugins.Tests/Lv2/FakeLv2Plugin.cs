using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sprocket.Plugins.Lv2;

namespace Sprocket.Plugins.Tests.Lv2;

/// <summary>
/// Builds a real <c>LV2_Descriptor</c> in unmanaged memory whose function pointers target
/// <c>[UnmanagedCallersOnly]</c> methods — the LV2 twin of <see cref="Ladspa.FakeLadspaPlugin"/>. The plugin is a
/// stateful "gain + one-sample delay" (<c>out[i] = in[i-1] * gain</c>) whose <c>instantiate</c> also walks the host
/// feature array, looks up <c>urid:map</c>, and maps a URI (recorded per instance) so the tests can prove the
/// feature plumbing end to end. Ports: 0 control-in "gain", 1 audio-in, 2 audio-out, 3 control-out "latency"
/// (written each run), 4 a connection-optional atom port (must be connected to NULL).
/// </summary>
internal sealed unsafe class FakeLv2Plugin : IDisposable
{
    public const string Uri = "http://sprocket.test/lv2/gain_delay";
    public const string MappedUri = "http://sprocket.test/lv2/some#urid";

    private readonly List<nint> _allocations = [];

    /// <summary>The URID the most recently instantiated instance obtained from the host's <c>urid:map</c> (0 = none).</summary>
    public static uint LastMappedUrid => _lastMappedUrid;
    private static uint _lastMappedUrid;

    /// <summary>The bundle path the most recent instance received.</summary>
    public static string? LastBundlePath => _lastBundlePath;
    private static string? _lastBundlePath;

    public FakeLv2Plugin(bool withDeactivate = true)
    {
        var descriptor = new Lv2Descriptor
        {
            URI = AllocUtf8(Uri),
            Instantiate = (nint)(delegate* unmanaged<nint, double, byte*, nint*, nint>)&Instantiate,
            ConnectPort = (nint)(delegate* unmanaged<nint, uint, void*, void>)&ConnectPort,
            Activate = (nint)(delegate* unmanaged<nint, void>)&Activate,
            Run = (nint)(delegate* unmanaged<nint, uint, void>)&Run,
            Deactivate = withDeactivate ? (nint)(delegate* unmanaged<nint, void>)&Deactivate : nint.Zero,
            Cleanup = (nint)(delegate* unmanaged<nint, void>)&Cleanup,
            ExtensionData = nint.Zero,
        };
        nint p = Alloc(sizeof(Lv2Descriptor));
        *(Lv2Descriptor*)p = descriptor;
        DescriptorPtr = p;
    }

    public nint DescriptorPtr { get; }

    /// <summary>The bundle metadata a reader would have produced for this plugin.</summary>
    public static Lv2PluginInfo Info(string bundlePath = "/tmp/fake.lv2") => new(
        Uri: Uri, Name: "Fake Gain Delay", Maker: "Sprocket Tests", License: "", Comment: "Test plugin.",
        BinaryPath: "/tmp/fake.lv2/fake.so", BundlePath: bundlePath, MinorVersion: 1, MicroVersion: 2,
        RequiredFeatures: [Lv2Ns.UridMap],
        Ports:
        [
            new Lv2PortInfo(0, "gain", "Gain", Types(Lv2Ns.InputPort, Lv2Ns.ControlPort), Props(), 1.0, 0.0, 10.0, null, []),
            new Lv2PortInfo(1, "in", "In", Types(Lv2Ns.InputPort, Lv2Ns.AudioPort), Props(), null, null, null, null, []),
            new Lv2PortInfo(2, "out", "Out", Types(Lv2Ns.OutputPort, Lv2Ns.AudioPort), Props(), null, null, null, null, []),
            new Lv2PortInfo(3, "latency", "Latency", Types(Lv2Ns.OutputPort, Lv2Ns.ControlPort), Props(), null, null, null, null, []),
            new Lv2PortInfo(4, "control", "Control", Types(Lv2Ns.InputPort, "http://lv2plug.in/ns/ext/atom#AtomPort"),
                Props(Lv2Ns.ConnectionOptional), null, null, null, null, []),
        ]);

    private static HashSet<string> Types(params string[] t) => new(t);
    private static HashSet<string> Props(params string[] p) => new(p);

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

    // ── the native plugin ──

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public nint Gain, In, Out, Latency, Atom;
        public float Prev;
        public int AtomConnected; // 1 once connect_port(4, …) was called (even with NULL)
    }

    [UnmanagedCallersOnly]
    private static nint Instantiate(nint descriptor, double sampleRate, byte* bundlePath, nint* features)
    {
        _lastBundlePath = bundlePath is null ? null : Marshal.PtrToStringUTF8((nint)bundlePath);
        _lastMappedUrid = 0;
        if (features is not null)
        {
            for (nint* f = features; *f != 0; f++)
            {
                var feature = (Lv2Feature*)*f;
                string? uri = Marshal.PtrToStringUTF8(feature->URI);
                if (uri == Lv2Ns.UridMap)
                {
                    var map = (Lv2UridMapFeature*)feature->Data;
                    var mapFn = (delegate* unmanaged<nint, byte*, uint>)map->Map;
                    byte[] bytes = Encoding.UTF8.GetBytes(MappedUri + "\0");
                    fixed (byte* b = bytes)
                        _lastMappedUrid = mapFn(map->Handle, b);
                }
            }
        }
        return (nint)NativeMemory.AllocZeroed((nuint)sizeof(State));
    }

    [UnmanagedCallersOnly]
    private static void ConnectPort(nint handle, uint port, void* data)
    {
        var s = (State*)handle;
        switch (port)
        {
            case 0: s->Gain = (nint)data; break;
            case 1: s->In = (nint)data; break;
            case 2: s->Out = (nint)data; break;
            case 3: s->Latency = (nint)data; break;
            case 4: s->Atom = (nint)data; s->AtomConnected = 1; break;
        }
    }

    [UnmanagedCallersOnly]
    private static void Activate(nint handle) => ((State*)handle)->Prev = 0f;

    [UnmanagedCallersOnly]
    private static void Deactivate(nint handle) { }

    [UnmanagedCallersOnly]
    private static void Run(nint handle, uint sampleCount)
    {
        var s = (State*)handle;
        float gain = *(float*)s->Gain;
        var input = (float*)s->In;
        var output = (float*)s->Out;
        float prev = s->Prev;
        for (int i = 0; i < (int)sampleCount; i++)
        {
            float x = input[i];
            output[i] = prev * gain;
            prev = x;
        }
        s->Prev = prev;
        if (s->Latency != 0)
            *(float*)s->Latency = 1f; // one sample of latency, reported through the control output
    }

    [UnmanagedCallersOnly]
    private static void Cleanup(nint handle) => NativeMemory.Free((void*)handle);
}
