using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Frei0r;

/// <summary>
/// The frei0r 1.x C ABI (<c>frei0r.h</c>), hand-bound like the LADSPA/LV2/FFmpeg bindings (ARCHITECTURE.md §1, no
/// C++/CLI). frei0r is a minimal video-effect standard: a library exports a fixed set of named functions
/// (<c>f0r_init</c>, <c>f0r_get_plugin_info</c>, <c>f0r_get_param_info</c>, <c>f0r_construct</c>,
/// <c>f0r_update</c>, …) and processes packed 32-bit-per-pixel <b>CPU</b> frames of a fixed size chosen at
/// construction. That is why it rides the CPU-effect readback seam (<c>ICpuVideoEffect</c>) rather than the shader
/// chain. Unlike LADSPA/LV2 the entry points are named exports, so the host resolves them once into a
/// <see cref="Frei0rFunctions"/> table (which the tests can also fill from in-process function pointers).
/// </summary>
internal static class Frei0rAbi
{
    /// <summary>The frei0r major version the binding implements (<c>FREI0R_MAJOR_VERSION</c>).</summary>
    public const int MajorVersion = 1;

    // ── f0r_plugin_info_t.plugin_type ──
    public const int TypeFilter = 0;
    public const int TypeSource = 1;
    public const int TypeMixer2 = 2;
    public const int TypeMixer3 = 3;

    // ── f0r_plugin_info_t.color_model ──
    public const int ColorBgra8888 = 0;
    public const int ColorRgba8888 = 1;
    public const int ColorPacked32 = 2;

    // ── f0r_param_info_t.type ──
    public const int ParamBool = 0;
    public const int ParamDouble = 1;
    public const int ParamColor = 2;
    public const int ParamPosition = 3;
    public const int ParamString = 4;

    /// <summary>An upper bound on a plugin's declared parameter count (no real plugin approaches it; a larger value is
    /// treated as malformed so a bogus <c>num_params</c> can't drive the reader into unmapped memory).</summary>
    public const int MaxParams = 1024;

    public static string TypeName(int type) => type switch
    {
        TypeFilter => "filter",
        TypeSource => "source",
        TypeMixer2 => "two-input mixer",
        TypeMixer3 => "three-input mixer",
        _ => $"type {type}",
    };
}

/// <summary>The <c>f0r_plugin_info_t</c> struct (natural alignment).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct F0rPluginInfo
{
    public nint Name;          // const char*
    public nint Author;        // const char*
    public int PluginType;
    public int ColorModel;
    public int Frei0rVersion;
    public int MajorVersion;
    public int MinorVersion;
    public int NumParams;
    public nint Explanation;   // const char*
}

/// <summary>The <c>f0r_param_info_t</c> struct.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct F0rParamInfo
{
    public nint Name;        // const char*
    public int Type;
    public nint Explanation; // const char*
}

/// <summary>The <c>f0r_param_color_t</c> value struct (three floats, 0…1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct F0rColor
{
    public float R, G, B;
}

/// <summary>The <c>f0r_param_position_t</c> value struct (two doubles, 0…1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct F0rPosition
{
    public double X, Y;
}

/// <summary>
/// The resolved frei0r entry points of one library, as raw function pointers (cast to <c>delegate* unmanaged</c>
/// at the call sites). <see cref="Update2"/> is optional (mixers only); the rest are mandatory.
/// </summary>
internal struct Frei0rFunctions
{
    public nint Init;          // int  f0r_init(void)
    public nint Deinit;        // void f0r_deinit(void)
    public nint GetPluginInfo; // void f0r_get_plugin_info(f0r_plugin_info_t*)
    public nint GetParamInfo;  // void f0r_get_param_info(f0r_param_info_t*, int index)
    public nint Construct;     // f0r_instance_t f0r_construct(unsigned int width, unsigned int height)
    public nint Destruct;      // void f0r_destruct(f0r_instance_t)
    public nint SetParamValue; // void f0r_set_param_value(f0r_instance_t, f0r_param_t param, int index)
    public nint GetParamValue; // void f0r_get_param_value(f0r_instance_t, f0r_param_t param, int index)
    public nint Update;        // void f0r_update(f0r_instance_t, double time, const uint32_t* in, uint32_t* out)
    public nint Update2;       // void f0r_update2(f0r_instance_t, double time, const uint32_t* in1, in2, in3, uint32_t* out) — may be 0

    public readonly bool HasMandatory =>
        Init != 0 && GetPluginInfo != 0 && GetParamInfo != 0 && Construct != 0 && Destruct != 0
        && SetParamValue != 0 && Update != 0;
}
