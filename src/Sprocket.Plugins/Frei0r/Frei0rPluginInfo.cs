using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Frei0r;

/// <summary>One frei0r parameter as declared by <c>f0r_get_param_info</c>: its index, name, value type, explanation,
/// and — when the plugin exports <c>f0r_get_param_value</c> — the default value(s) a freshly constructed instance
/// carries (one component for BOOL/DOUBLE, r/g/b for COLOR, x/y for POSITION; null when unknown).</summary>
public sealed record Frei0rParamInfo(int Index, string Name, int Type, string Explanation, IReadOnlyList<double>? Defaults = null);

/// <summary>
/// A frei0r plugin's <c>f0r_plugin_info_t</c> plus its parameter table, as pure managed data. Produced by
/// <see cref="Frei0rInfoReader"/>; consumed by <see cref="Frei0rParameterMapping"/> and <see cref="Frei0rCpuEffect"/>.
/// </summary>
/// <param name="Key">The plugin's stable identity — the library file name without extension (what MLT / Kdenlive /
/// Shotcut also key frei0r effects by, e.g. <c>glow</c>), since frei0r carries no unique id.</param>
/// <param name="Name">The plugin's display name.</param>
/// <param name="Author">The author string (may be empty).</param>
/// <param name="PluginType">One of <see cref="Frei0rAbi.TypeFilter"/> … (only filters are hosted).</param>
/// <param name="ColorModel">One of <see cref="Frei0rAbi.ColorBgra8888"/> ….</param>
/// <param name="Frei0rVersion">The frei0r API version the plugin was built against.</param>
/// <param name="MajorVersion">The plugin's own major version.</param>
/// <param name="MinorVersion">The plugin's own minor version.</param>
/// <param name="Explanation">The plugin's one-line explanation (may be empty).</param>
/// <param name="Params">Its parameters, in index order.</param>
public sealed record Frei0rPluginInfo(
    string Key,
    string Name,
    string Author,
    int PluginType,
    int ColorModel,
    int Frei0rVersion,
    int MajorVersion,
    int MinorVersion,
    string Explanation,
    IReadOnlyList<Frei0rParamInfo> Params)
{
    public bool IsFilter => PluginType == Frei0rAbi.TypeFilter;

    /// <summary>"major.minor" for the Plugin Manager row.</summary>
    public string Version => $"{MajorVersion}.{MinorVersion}";
}

/// <summary>Reads a frei0r library's info + parameter table through its resolved function table. Kept apart from
/// loading so the tests can drive it with in-process function pointers (no native library needed).</summary>
internal static unsafe class Frei0rInfoReader
{
    /// <summary>Reads the plugin info. The caller must already have called <c>f0r_init</c>. Throws
    /// <see cref="BadImageFormatException"/> for an implausible parameter count.</summary>
    public static Frei0rPluginInfo Read(in Frei0rFunctions functions, string key)
    {
        F0rPluginInfo info = default;
        ((delegate* unmanaged<F0rPluginInfo*, void>)functions.GetPluginInfo)(&info);

        if (info.NumParams < 0 || info.NumParams > Frei0rAbi.MaxParams)
            throw new BadImageFormatException($"frei0r plugin declares {info.NumParams} parameters (max {Frei0rAbi.MaxParams}).");

        var getParam = (delegate* unmanaged<F0rParamInfo*, int, void>)functions.GetParamInfo;
        var parameters = new List<Frei0rParamInfo>(info.NumParams);
        for (int i = 0; i < info.NumParams; i++)
        {
            F0rParamInfo p = default;
            getParam(&p, i);
            parameters.Add(new Frei0rParamInfo(i, Utf8(p.Name) ?? $"Parameter {i}", p.Type, Utf8(p.Explanation) ?? ""));
        }

        return new Frei0rPluginInfo(
            Key: key,
            Name: Utf8(info.Name) ?? key,
            Author: Utf8(info.Author) ?? "",
            PluginType: info.PluginType,
            ColorModel: info.ColorModel,
            Frei0rVersion: info.Frei0rVersion,
            MajorVersion: info.MajorVersion,
            MinorVersion: info.MinorVersion,
            Explanation: Utf8(info.Explanation) ?? "",
            Params: parameters);
    }

    /// <summary>
    /// Fills in each parameter's <see cref="Frei0rParamInfo.Defaults"/> by constructing a small throwaway instance
    /// and reading its values back through <c>f0r_get_param_value</c> — what MLT/Kdenlive do, so a freshly inserted
    /// frei0r effect starts where every other host starts it. Returns <paramref name="info"/> unchanged when the
    /// plugin exports no getter or construction fails (the mapping then falls back to generic defaults).
    /// </summary>
    public static Frei0rPluginInfo WithPluginDefaults(in Frei0rFunctions functions, Frei0rPluginInfo info)
    {
        if (functions.GetParamValue == nint.Zero || functions.Construct == nint.Zero || functions.Destruct == nint.Zero)
            return info;

        nint instance = ((delegate* unmanaged<uint, uint, nint>)functions.Construct)(8, 8);
        if (instance == nint.Zero)
            return info;

        var get = (delegate* unmanaged<nint, void*, int, void>)functions.GetParamValue;
        var scratch = (byte*)NativeMemory.AllocZeroed(16);
        try
        {
            var withDefaults = new List<Frei0rParamInfo>(info.Params.Count);
            foreach (Frei0rParamInfo p in info.Params)
            {
                IReadOnlyList<double>? defaults = null;
                switch (p.Type)
                {
                    case Frei0rAbi.ParamBool:
                    case Frei0rAbi.ParamDouble:
                        NativeMemory.Clear(scratch, 16);
                        get(instance, scratch, p.Index);
                        defaults = [Sane(*(double*)scratch)];
                        break;
                    case Frei0rAbi.ParamColor:
                        NativeMemory.Clear(scratch, 16);
                        get(instance, scratch, p.Index);
                        F0rColor c = *(F0rColor*)scratch;
                        defaults = [Sane(c.R), Sane(c.G), Sane(c.B)];
                        break;
                    case Frei0rAbi.ParamPosition:
                        NativeMemory.Clear(scratch, 16);
                        get(instance, scratch, p.Index);
                        F0rPosition pos = *(F0rPosition*)scratch;
                        defaults = [Sane(pos.X), Sane(pos.Y)];
                        break;
                }
                withDefaults.Add(p with { Defaults = defaults });
            }
            return info with { Params = withDefaults };
        }
        finally
        {
            NativeMemory.Free(scratch);
            ((delegate* unmanaged<nint, void>)functions.Destruct)(instance);
        }
    }

    /// <summary>frei0r values are normalised 0…1; anything else (NaN, garbage) is clamped so the Inspector never sees it.</summary>
    private static double Sane(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : 0.0;

    private static string? Utf8(nint p) => p == nint.Zero ? null : Marshal.PtrToStringUTF8(p);
}
