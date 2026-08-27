using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Ladspa;

/// <summary>
/// The LADSPA 1.1 C ABI, hand-bound the same way <c>Sprocket.Media</c> binds FFmpeg (ARCHITECTURE.md §1, no
/// C++/CLI): plain P/Invoke against a C ABI so one managed build serves every OS. LADSPA is the simplest audio
/// plugin standard — a single <c>ladspa.h</c> declaring a descriptor of float32 control/audio ports plus a
/// <c>run(instance, sampleCount)</c> — so there is no NuGet and no shim; the plugin's own functions are reached
/// through the function pointers carried in its descriptor, and only the module entry point
/// (<c>ladspa_descriptor</c>) is a named export.
/// </summary>
/// <remarks>
/// <para><b><c>unsigned long</c> is bound as <see cref="CULong"/></b>, the purpose-built interop type that is
/// 32-bit on Windows (LLP64) and 64-bit on Linux/macOS (LP64) — matching the C <c>unsigned long</c> LADSPA uses
/// for ids, port counts, port indices, the sample rate and the block sample count on every platform. (LADSPA's
/// native ecosystem is Unix, but binding it correctly costs nothing.)</para>
/// <para>Function pointers use the platform-default unmanaged calling convention (C / cdecl on the x64 ABIs),
/// which is what LADSPA declares.</para>
/// </remarks>
internal static class LadspaAbi
{
    /// <summary>The C entry point every LADSPA library exports.</summary>
    public const string DescriptorFunction = "ladspa_descriptor";

    // ── LADSPA_PortDescriptor bits (a port's direction and kind) ──
    public const int PortInput = 0x1;
    public const int PortOutput = 0x2;
    public const int PortControl = 0x4;
    public const int PortAudio = 0x8;

    // ── LADSPA_PortRangeHintDescriptor bits ──
    public const int HintBoundedBelow = 0x1;
    public const int HintBoundedAbove = 0x2;
    public const int HintToggled = 0x4;
    public const int HintSampleRate = 0x8;
    public const int HintLogarithmic = 0x10;
    public const int HintInteger = 0x20;

    public const int HintDefaultMask = 0x3C0;
    public const int HintDefaultNone = 0x0;
    public const int HintDefaultMinimum = 0x40;
    public const int HintDefaultLow = 0x80;
    public const int HintDefaultMiddle = 0xC0;
    public const int HintDefaultHigh = 0x100;
    public const int HintDefaultMaximum = 0x140;
    public const int HintDefault0 = 0x200;
    public const int HintDefault1 = 0x240;
    public const int HintDefault100 = 0x280;
    public const int HintDefault440 = 0x2C0;

    public static bool IsInput(int port) => (port & PortInput) != 0;
    public static bool IsOutput(int port) => (port & PortOutput) != 0;
    public static bool IsControl(int port) => (port & PortControl) != 0;
    public static bool IsAudio(int port) => (port & PortAudio) != 0;
}

/// <summary>The <c>LADSPA_PortRangeHint</c> struct: a port's hint bits and its (optionally unbounded) range.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LadspaPortRangeHint
{
    public int HintDescriptor;
    public float LowerBound;
    public float UpperBound;
}

/// <summary>
/// The <c>LADSPA_Descriptor</c> struct laid out for the current platform (natural alignment matches the C
/// compiler's). Pointer and function-pointer fields are held as <see cref="nint"/> and cast to
/// <c>delegate* unmanaged</c> at the call site (<see cref="LadspaInstanceSet"/>); the <c>unsigned long</c>
/// fields are <see cref="CULong"/>. Read once per plugin via a blittable pointer dereference.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LadspaDescriptor
{
    public CULong UniqueID;
    public nint Label;         // const char*
    public int Properties;
    public nint Name;          // const char*
    public nint Maker;         // const char*
    public nint Copyright;     // const char*
    public CULong PortCount;
    public nint PortDescriptors; // const LADSPA_PortDescriptor* (int*)
    public nint PortNames;       // const char* const*
    public nint PortRangeHints;  // const LADSPA_PortRangeHint*
    public nint ImplementationData;
    public nint Instantiate;     // LADSPA_Handle (*)(const LADSPA_Descriptor*, unsigned long SampleRate)
    public nint ConnectPort;     // void (*)(LADSPA_Handle, unsigned long Port, LADSPA_Data*)
    public nint Activate;        // void (*)(LADSPA_Handle)              — may be null
    public nint Run;             // void (*)(LADSPA_Handle, unsigned long SampleCount)
    public nint RunAdding;       // may be null (unused — we always overwrite)
    public nint SetRunAddingGain;// may be null (unused)
    public nint Deactivate;      // void (*)(LADSPA_Handle)              — may be null
    public nint Cleanup;         // void (*)(LADSPA_Handle)
}
