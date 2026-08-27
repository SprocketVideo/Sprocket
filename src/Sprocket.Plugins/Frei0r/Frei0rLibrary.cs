using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Frei0r;

/// <summary>
/// One loaded frei0r library file (PLAN.md step 59): its resolved entry points, its plugin info, and — when it is
/// a filter — the <see cref="Frei0rCpuEffect"/> it contributes. Sources and mixers are recognised but not hosted
/// (they need the generator / transition seams, not the effect chain) and are reported in <see cref="Skipped"/>.
/// </summary>
/// <remarks>
/// <b>The module is never unmapped within a session</b> (same reasoning as the audio hosts): a render thread may be
/// inside <c>f0r_update</c>, and frei0r's <c>f0r_init</c>/<c>f0r_deinit</c> are library-global. Disabling a frei0r
/// plugin only unregisters its descriptor; the CPU stage drops its instance on the next draw.
/// </remarks>
internal sealed unsafe class Frei0rLibrary
{
    // frei0r.h: f0r_init / f0r_deinit "must not be called more than once". The module is never unmapped within a
    // session, so a disable → enable or a rescan must hand back the already-initialised library, not re-init it.
    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, Frei0rLibrary> Cache = new(StringComparer.Ordinal);

    private Frei0rLibrary(string path, in Frei0rFunctions functions, Frei0rPluginInfo info, Frei0rCpuEffect? effect, IReadOnlyList<string> skipped)
    {
        Path = path;
        Functions = functions;
        Info = info;
        Effect = effect;
        Skipped = skipped;
    }

    public string Path { get; }
    public Frei0rFunctions Functions { get; }
    public Frei0rPluginInfo Info { get; }

    /// <summary>The hosted filter, or null when the library is a source/mixer or an unsupported API version.</summary>
    public Frei0rCpuEffect? Effect { get; }

    public IReadOnlyList<string> Skipped { get; }

    /// <summary>
    /// Loads a frei0r library, resolves its entry points, initialises it and reads its info. Throws
    /// <see cref="DllNotFoundException"/> / <see cref="BadImageFormatException"/> if the file is not a native module
    /// and <see cref="EntryPointNotFoundException"/> if it is not a frei0r library.
    /// </summary>
    public static Frei0rLibrary Load(string path)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(path, out Frei0rLibrary? cached))
                return cached;
        }

        nint module = NativeLibrary.Load(path);
        var functions = new Frei0rFunctions
        {
            Init = NativeLibrary.GetExport(module, "f0r_init"),
            Deinit = TryExport(module, "f0r_deinit"),
            GetPluginInfo = NativeLibrary.GetExport(module, "f0r_get_plugin_info"),
            GetParamInfo = NativeLibrary.GetExport(module, "f0r_get_param_info"),
            Construct = NativeLibrary.GetExport(module, "f0r_construct"),
            Destruct = NativeLibrary.GetExport(module, "f0r_destruct"),
            SetParamValue = NativeLibrary.GetExport(module, "f0r_set_param_value"),
            GetParamValue = TryExport(module, "f0r_get_param_value"),
            Update = NativeLibrary.GetExport(module, "f0r_update"),
            Update2 = TryExport(module, "f0r_update2"),
        };

        string key = System.IO.Path.GetFileNameWithoutExtension(path);
        Frei0rLibrary library = FromFunctions(path, functions, key);
        lock (CacheGate)
            Cache.TryAdd(path, library);
        return library;
    }

    /// <summary>Initialises and describes a plugin from an already-resolved function table (the test fixture's path).</summary>
    internal static Frei0rLibrary FromFunctions(string path, in Frei0rFunctions functions, string key)
    {
        if (!functions.HasMandatory)
            throw new EntryPointNotFoundException("Not a frei0r library: a mandatory f0r_* entry point is missing.");

        int initResult = ((delegate* unmanaged<int>)functions.Init)();
        if (initResult == 0)
            throw new InvalidOperationException("f0r_init() failed.");

        Frei0rPluginInfo info = Frei0rInfoReader.Read(functions, key);
        if (info.IsFilter && info.Frei0rVersion == Frei0rAbi.MajorVersion)
        {
            try { info = Frei0rInfoReader.WithPluginDefaults(functions, info); }
            catch { /* generic defaults are the documented fallback */ }
        }
        var skipped = new List<string>();
        Frei0rCpuEffect? effect = null;
        if (info.Frei0rVersion != Frei0rAbi.MajorVersion)
            skipped.Add($"{info.Name} — built for frei0r API version {info.Frei0rVersion} (host supports {Frei0rAbi.MajorVersion}).");
        else if (!info.IsFilter)
            skipped.Add($"{info.Name} — a frei0r {Frei0rAbi.TypeName(info.PluginType)}; only filters are hosted as clip effects.");
        else
            effect = new Frei0rCpuEffect(functions, info);

        return new Frei0rLibrary(path, functions, info, effect, skipped);
    }

    private static nint TryExport(nint module, string name) =>
        NativeLibrary.TryGetExport(module, name, out nint address) ? address : nint.Zero;
}
