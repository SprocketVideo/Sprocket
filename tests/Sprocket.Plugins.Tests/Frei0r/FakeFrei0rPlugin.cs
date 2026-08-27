using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sprocket.Plugins.Frei0r;

namespace Sprocket.Plugins.Tests.Frei0r;

/// <summary>
/// An in-process frei0r filter whose entry points are <c>[UnmanagedCallersOnly]</c> methods handed to the binding
/// as a <see cref="Frei0rFunctions"/> table — the exact shape <c>Frei0rLibrary.Load</c> resolves from a real
/// library's named exports, so the info reader, parameter mapping, instance lifecycle and <c>f0r_update</c> path
/// run for real without shipping a platform-specific binary. The filter is a "tinted invert": every RGB byte is
/// inverted, then multiplied by the colour parameter; the bool parameter bypasses the invert; the position and
/// string parameters are recorded but don't affect pixels. Colour model is configurable (RGBA / BGRA) so the byte
/// order contract can be asserted.
/// </summary>
internal static unsafe class FakeFrei0rPlugin
{
    private static int _colorModel = Frei0rAbi.ColorRgba8888;
    private static int _pluginType = Frei0rAbi.TypeFilter;
    private static int _apiVersion = Frei0rAbi.MajorVersion;
    private static readonly nint Name = Utf8("Fake Tinted Invert");
    private static readonly nint Author = Utf8("Sprocket Tests");
    private static readonly nint Explanation = Utf8("Inverts and tints.");
    private static readonly nint[] ParamNames = [Utf8("Amount"), Utf8("Bypass"), Utf8("Tint"), Utf8("Center"), Utf8("Label")];
    private static readonly nint[] ParamExplanations = [Utf8("How much."), Utf8("Skip the invert."), Utf8("Tint colour."), Utf8("Centre point."), Utf8("A text.")];

    /// <summary>The number of live (constructed, not yet destructed) instances — proves RAII teardown.</summary>
    public static int LiveInstances;

    /// <summary>The number of <c>f0r_set_param_value</c> calls received since the last reset — proves change-only pushes.</summary>
    public static int SetParamCalls;

    /// <summary>The time passed to the most recent <c>f0r_update</c>.</summary>
    public static double LastTime;

    /// <summary>The last position parameter received.</summary>
    public static F0rPosition LastPosition;

    /// <summary>Builds the function table, configuring the plugin's reported colour model / type / API version.</summary>
    public static Frei0rFunctions Functions(int colorModel = Frei0rAbi.ColorRgba8888, int pluginType = Frei0rAbi.TypeFilter, int apiVersion = Frei0rAbi.MajorVersion)
    {
        _colorModel = colorModel;
        _pluginType = pluginType;
        _apiVersion = apiVersion;
        return new Frei0rFunctions
        {
            Init = (nint)(delegate* unmanaged<int>)&Init,
            Deinit = (nint)(delegate* unmanaged<void>)&Deinit,
            GetPluginInfo = (nint)(delegate* unmanaged<F0rPluginInfo*, void>)&GetPluginInfo,
            GetParamInfo = (nint)(delegate* unmanaged<F0rParamInfo*, int, void>)&GetParamInfo,
            Construct = (nint)(delegate* unmanaged<uint, uint, nint>)&Construct,
            Destruct = (nint)(delegate* unmanaged<nint, void>)&Destruct,
            SetParamValue = (nint)(delegate* unmanaged<nint, void*, int, void>)&SetParamValue,
            GetParamValue = (nint)(delegate* unmanaged<nint, void*, int, void>)&GetParamValue,
            Update = (nint)(delegate* unmanaged<nint, double, uint*, uint*, void>)&Update,
            Update2 = nint.Zero,
        };
    }

    private static nint Utf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        nint p = (nint)NativeMemory.AllocZeroed((nuint)(bytes.Length + 1));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p; // static lifetime, like a real plugin's string constants
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public uint Width, Height;
        public double Amount;
        public double Bypass;
        public F0rColor Tint;
        public F0rPosition Center;
    }

    [UnmanagedCallersOnly]
    private static int Init() => 1;

    [UnmanagedCallersOnly]
    private static void Deinit() { }

    [UnmanagedCallersOnly]
    private static void GetPluginInfo(F0rPluginInfo* info)
    {
        info->Name = Name;
        info->Author = Author;
        info->PluginType = _pluginType;
        info->ColorModel = _colorModel;
        info->Frei0rVersion = _apiVersion;
        info->MajorVersion = 2;
        info->MinorVersion = 3;
        info->NumParams = ParamNames.Length;
        info->Explanation = Explanation;
    }

    [UnmanagedCallersOnly]
    private static void GetParamInfo(F0rParamInfo* info, int index)
    {
        info->Name = ParamNames[index];
        info->Type = index switch
        {
            0 => Frei0rAbi.ParamDouble,
            1 => Frei0rAbi.ParamBool,
            2 => Frei0rAbi.ParamColor,
            3 => Frei0rAbi.ParamPosition,
            _ => Frei0rAbi.ParamString,
        };
        info->Explanation = ParamExplanations[index];
    }

    [UnmanagedCallersOnly]
    private static nint Construct(uint width, uint height)
    {
        var inst = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
        inst->Width = width;
        inst->Height = height;
        inst->Amount = 1.0;
        inst->Tint = new F0rColor { R = 1f, G = 1f, B = 1f };
        Interlocked.Increment(ref LiveInstances);
        return (nint)inst;
    }

    [UnmanagedCallersOnly]
    private static void Destruct(nint instance)
    {
        NativeMemory.Free((void*)instance);
        Interlocked.Decrement(ref LiveInstances);
    }

    [UnmanagedCallersOnly]
    private static void SetParamValue(nint instance, void* param, int index)
    {
        var inst = (Instance*)instance;
        SetParamCalls++;
        switch (index)
        {
            case 0: inst->Amount = *(double*)param; break;
            case 1: inst->Bypass = *(double*)param; break;
            case 2: inst->Tint = *(F0rColor*)param; break;
            case 3: inst->Center = *(F0rPosition*)param; LastPosition = inst->Center; break;
        }
    }

    [UnmanagedCallersOnly]
    private static void GetParamValue(nint instance, void* param, int index)
    {
        var inst = (Instance*)instance;
        switch (index)
        {
            case 0: *(double*)param = inst->Amount; break;
            case 1: *(double*)param = inst->Bypass; break;
            case 2: *(F0rColor*)param = inst->Tint; break;
            case 3: *(F0rPosition*)param = inst->Center; break;
        }
    }

    [UnmanagedCallersOnly]
    private static void Update(nint instance, double time, uint* input, uint* output)
    {
        var inst = (Instance*)instance;
        LastTime = time;
        int n = (int)(inst->Width * inst->Height);
        var src = (byte*)input;
        var dst = (byte*)output;
        bool invert = inst->Bypass < 0.5;
        // Channel order follows the declared colour model: RGBA → tint.R applies to byte 0; BGRA → to byte 2.
        float c0 = _colorModel == Frei0rAbi.ColorBgra8888 ? inst->Tint.B : inst->Tint.R;
        float c1 = inst->Tint.G;
        float c2 = _colorModel == Frei0rAbi.ColorBgra8888 ? inst->Tint.R : inst->Tint.B;
        for (int i = 0; i < n; i++)
        {
            byte r = src[4 * i], g = src[4 * i + 1], b = src[4 * i + 2], a = src[4 * i + 3];
            if (invert) { r = (byte)(255 - r); g = (byte)(255 - g); b = (byte)(255 - b); }
            dst[4 * i] = (byte)(r * c0);
            dst[4 * i + 1] = (byte)(g * c1);
            dst[4 * i + 2] = (byte)(b * c2);
            dst[4 * i + 3] = a;
        }
    }
}
