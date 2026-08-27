using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Lv2;

/// <summary>
/// The LV2 core C ABI (<c>lv2core/lv2.h</c>), hand-bound like the LADSPA and FFmpeg bindings (ARCHITECTURE.md §1,
/// no C++/CLI). LV2 is LADSPA's successor: the same "descriptor of function pointers + float ports" shape, with
/// explicit <c>uint32_t</c> port indices / sample counts, a <c>double</c> sample rate, the bundle path, and a
/// null-terminated array of host <c>LV2_Feature</c>s handed to <c>instantiate</c>. All metadata (ports, ranges,
/// names) lives in the bundle's Turtle files, not in the binary — see <see cref="Lv2BundleReader"/>.
/// </summary>
internal static class Lv2Abi
{
    /// <summary>The classic C entry point: <c>const LV2_Descriptor* lv2_descriptor(uint32_t index)</c>.</summary>
    public const string DescriptorFunction = "lv2_descriptor";

    /// <summary>The newer library-level entry point (LV2 1.2+): <c>const LV2_Lib_Descriptor* lv2_lib_descriptor(const char*
    /// bundle_path, const LV2_Feature* const* features)</c>. Used only when a binary lacks <see cref="DescriptorFunction"/>.</summary>
    public const string LibDescriptorFunction = "lv2_lib_descriptor";
}

/// <summary>The <c>LV2_Lib_Descriptor</c> struct returned by <c>lv2_lib_descriptor</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Lv2LibDescriptor
{
    public nint Handle;    // LV2_Lib_Handle
    public uint Size;      // sizeof(LV2_Lib_Descriptor) — for forward compatibility
    public nint Cleanup;   // void (*)(LV2_Lib_Handle)                          — never called: the library stays resident
    public nint GetPlugin; // const LV2_Descriptor* (*)(LV2_Lib_Handle, uint32_t index)
}

/// <summary>Well-known LV2 namespace IRIs used by the bundle reader and the feature set.</summary>
internal static class Lv2Ns
{
    public const string Core = "http://lv2plug.in/ns/lv2core#";
    public const string Plugin = Core + "Plugin";
    public const string Binary = Core + "binary";
    public const string Port = Core + "port";
    public const string Index = Core + "index";
    public const string Symbol = Core + "symbol";
    public const string Name = Core + "name";
    public const string Default = Core + "default";
    public const string Minimum = Core + "minimum";
    public const string Maximum = Core + "maximum";
    public const string PortProperty = Core + "portProperty";
    public const string ScalePoint = Core + "scalePoint";
    public const string RequiredFeature = Core + "requiredFeature";
    public const string OptionalFeature = Core + "optionalFeature";
    public const string MinorVersion = Core + "minorVersion";
    public const string MicroVersion = Core + "microVersion";

    public const string InputPort = Core + "InputPort";
    public const string OutputPort = Core + "OutputPort";
    public const string AudioPort = Core + "AudioPort";
    public const string ControlPort = Core + "ControlPort";
    public const string CvPort = Core + "CVPort";

    public const string Toggled = Core + "toggled";
    public const string Integer = Core + "integer";
    public const string Enumeration = Core + "enumeration";
    public const string SampleRate = Core + "sampleRate";
    public const string ConnectionOptional = Core + "connectionOptional";

    // Features the host implements / accepts (see Lv2Features).
    public const string InPlaceBroken = Core + "inPlaceBroken";
    public const string HardRtCapable = Core + "hardRTCapable";
    public const string UridMap = "http://lv2plug.in/ns/ext/urid#map";
    public const string UridUnmap = "http://lv2plug.in/ns/ext/urid#unmap";

    public const string PortPropsLogarithmic = "http://lv2plug.in/ns/ext/port-props#logarithmic";
    public const string PortPropsNotOnGui = "http://lv2plug.in/ns/ext/port-props#notOnGUI";

    public const string Units = "http://lv2plug.in/ns/extensions/units#";
    public const string UnitsUnit = Units + "unit";

    public const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    public const string RdfsSeeAlso = Rdfs + "seeAlso";
    public const string RdfsLabel = Rdfs + "label";
    public const string RdfsComment = Rdfs + "comment";
    public const string RdfValue = TurtleReader.Rdf + "value";

    public const string Doap = "http://usefulinc.com/ns/doap#";
    public const string DoapName = Doap + "name";
    public const string DoapMaintainer = Doap + "maintainer";
    public const string DoapLicense = Doap + "license";
    public const string Foaf = "http://xmlns.com/foaf/0.1/";
    public const string FoafName = Foaf + "name";
}

/// <summary>The <c>LV2_Descriptor</c> struct: the plugin URI plus its function pointers (natural alignment).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Lv2Descriptor
{
    public nint URI;           // const char*
    public nint Instantiate;   // LV2_Handle (*)(const LV2_Descriptor*, double sample_rate, const char* bundle_path, const LV2_Feature* const* features)
    public nint ConnectPort;   // void (*)(LV2_Handle, uint32_t port, void* data_location)
    public nint Activate;      // void (*)(LV2_Handle)                  — may be null
    public nint Run;           // void (*)(LV2_Handle, uint32_t sample_count)
    public nint Deactivate;    // void (*)(LV2_Handle)                  — may be null
    public nint Cleanup;       // void (*)(LV2_Handle)
    public nint ExtensionData; // const void* (*)(const char* uri)      — may be null (unused)
}

/// <summary>The <c>LV2_Feature</c> struct: a feature URI and its opaque data pointer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Lv2Feature
{
    public nint URI;  // const char*
    public nint Data; // void*
}

/// <summary>The <c>LV2_URID_Map</c> struct (<c>urid.h</c>): an opaque handle plus <c>LV2_URID (*map)(handle, const char* uri)</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Lv2UridMapFeature
{
    public nint Handle;
    public nint Map;
}

/// <summary>The <c>LV2_URID_Unmap</c> struct: an opaque handle plus <c>const char* (*unmap)(handle, LV2_URID urid)</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Lv2UridUnmapFeature
{
    public nint Handle;
    public nint Unmap;
}
