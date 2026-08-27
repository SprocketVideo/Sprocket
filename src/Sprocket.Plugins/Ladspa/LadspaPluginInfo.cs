using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Ladspa;

/// <summary>One LADSPA port, read from the plugin descriptor: its direction/kind flags, its display name and,
/// for a port that carries a range hint, the hint bits and bounds. Pure managed data — no native pointers, so
/// the parameter-mapping and the tests work against it without touching the plugin.</summary>
/// <param name="Index">The port's index in the descriptor (stable per plugin version — the key parameter
/// names are derived from it).</param>
/// <param name="Flags">The <c>LADSPA_PortDescriptor</c> bits (<see cref="LadspaAbi.PortInput"/> …).</param>
/// <param name="Name">The port's human-readable name.</param>
/// <param name="HintDescriptor">The <c>LADSPA_PortRangeHintDescriptor</c> bits.</param>
/// <param name="LowerBound">The hint's lower bound (meaningful only with <see cref="LadspaAbi.HintBoundedBelow"/>).</param>
/// <param name="UpperBound">The hint's upper bound (meaningful only with <see cref="LadspaAbi.HintBoundedAbove"/>).</param>
public sealed record LadspaPortInfo(
    int Index,
    int Flags,
    string Name,
    int HintDescriptor,
    float LowerBound,
    float UpperBound)
{
    /// <summary>True for a control input port (a plugin parameter the host drives).</summary>
    public bool IsControlInput => LadspaAbi.IsControl(Flags) && LadspaAbi.IsInput(Flags);

    /// <summary>True for a control output port (a plugin readout — allocated and ignored by the host in v1).</summary>
    public bool IsControlOutput => LadspaAbi.IsControl(Flags) && LadspaAbi.IsOutput(Flags);

    /// <summary>True for an audio input port (a signal channel fed into the plugin).</summary>
    public bool IsAudioInput => LadspaAbi.IsAudio(Flags) && LadspaAbi.IsInput(Flags);

    /// <summary>True for an audio output port (a signal channel read back from the plugin).</summary>
    public bool IsAudioOutput => LadspaAbi.IsAudio(Flags) && LadspaAbi.IsOutput(Flags);
}

/// <summary>
/// A LADSPA plugin's descriptor as pure managed data: its identity (unique id, label, name, maker, copyright)
/// and its ports. Produced by <see cref="LadspaDescriptorReader"/>; consumed by
/// <see cref="LadspaParameterMapping"/> to build the catalog descriptor and by <see cref="LadspaEffect"/> to
/// lay out its port connections.
/// </summary>
/// <param name="UniqueId">The LADSPA-registry unique id (0 for an unregistered/dev plugin).</param>
/// <param name="Label">The plugin's stable label within its library.</param>
/// <param name="Name">The plugin's human-readable name.</param>
/// <param name="Maker">The plugin author (may be empty).</param>
/// <param name="Copyright">The plugin copyright/licence string (may be empty).</param>
/// <param name="Ports">Every port, in descriptor order.</param>
public sealed record LadspaPluginInfo(
    ulong UniqueId,
    string Label,
    string Name,
    string Maker,
    string Copyright,
    IReadOnlyList<LadspaPortInfo> Ports)
{
    /// <summary>The control input ports, in order — the plugin's parameters.</summary>
    public IEnumerable<LadspaPortInfo> ControlInputs => Ports.Where(p => p.IsControlInput);

    /// <summary>The number of audio input ports (signal channels the plugin consumes).</summary>
    public int AudioInputCount => Ports.Count(p => p.IsAudioInput);

    /// <summary>The number of audio output ports (signal channels the plugin produces).</summary>
    public int AudioOutputCount => Ports.Count(p => p.IsAudioOutput);
}

/// <summary>
/// Reads a native <c>LADSPA_Descriptor*</c> into a pure-managed <see cref="LadspaPluginInfo"/>. Kept apart from
/// the loading/running code so it can be exercised directly against a descriptor built in-process (the test
/// fixture supplies one via <c>[UnmanagedCallersOnly]</c> function pointers rather than shipping a native .so).
/// </summary>
internal static unsafe class LadspaDescriptorReader
{
    /// <summary>An upper bound on a plugin's port count. No real LADSPA plugin approaches this; a descriptor
    /// declaring more is treated as malformed/hostile so a bogus <c>PortCount</c> can't drive the reader to walk
    /// its port arrays off the end into unmapped memory (an uncatchable access violation would crash the whole
    /// editor at startup discovery). Mirrors <c>LadspaLibrary</c>'s <c>MaxDescriptors</c> runaway guard.</summary>
    private const int MaxPorts = 4096;

    /// <summary>Reads the descriptor at <paramref name="descriptorPtr"/>. Throws
    /// <see cref="ArgumentException"/> for a null pointer, or <see cref="BadImageFormatException"/> for a
    /// descriptor declaring an implausible port count (recorded as a per-file load error, never thrown to the UI).</summary>
    public static LadspaPluginInfo Read(nint descriptorPtr)
    {
        if (descriptorPtr == nint.Zero)
            throw new ArgumentException("Null LADSPA descriptor pointer.", nameof(descriptorPtr));

        LadspaDescriptor d = *(LadspaDescriptor*)descriptorPtr;
        if (d.PortCount.Value > (nuint)MaxPorts)
            throw new BadImageFormatException($"LADSPA descriptor declares {d.PortCount.Value} ports (max {MaxPorts}).");
        int portCount = (int)d.PortCount.Value;

        var ports = new List<LadspaPortInfo>(portCount);
        var flags = (int*)d.PortDescriptors;
        var names = (nint*)d.PortNames;
        var hints = (LadspaPortRangeHint*)d.PortRangeHints;
        for (int i = 0; i < portCount; i++)
        {
            LadspaPortRangeHint hint = hints is null ? default : hints[i];
            ports.Add(new LadspaPortInfo(
                Index: i,
                Flags: flags is null ? 0 : flags[i],
                Name: names is null ? $"Port {i}" : Utf8(names[i]) ?? $"Port {i}",
                HintDescriptor: hint.HintDescriptor,
                LowerBound: hint.LowerBound,
                UpperBound: hint.UpperBound));
        }

        return new LadspaPluginInfo(
            UniqueId: d.UniqueID.Value,
            Label: Utf8(d.Label) ?? "",
            Name: Utf8(d.Name) ?? "",
            Maker: Utf8(d.Maker) ?? "",
            Copyright: Utf8(d.Copyright) ?? "",
            Ports: ports);
    }

    private static string? Utf8(nint p) => p == nint.Zero ? null : Marshal.PtrToStringUTF8(p);
}
