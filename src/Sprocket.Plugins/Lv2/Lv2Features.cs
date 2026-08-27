using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Sprocket.Plugins.Lv2;

/// <summary>
/// The host features Sprocket offers LV2 plugins (PLAN.md step 59, core-subset host) and the process-wide
/// <c>urid:map</c> / <c>urid:unmap</c> implementation behind them. URIDs are small integers a plugin uses in
/// place of URI strings; the mapping is global and stable for the process lifetime (the spec requires a URID to
/// map back to the same URI for as long as the host runs), so the UTF-8 strings live in native memory forever.
/// </summary>
/// <remarks>
/// <para>The map/unmap callbacks are <c>[UnmanagedCallersOnly]</c> so their addresses can be handed to native code
/// directly. They are lock-protected: plugins are only supposed to call <c>map</c> from <c>instantiate</c>, but
/// a defensive host assumes some will call it from <c>run()</c> too.</para>
/// <para>A plugin whose <c>lv2:requiredFeature</c>s are not all in <see cref="Supported"/> is <b>not</b> hosted
/// (the LV2 spec forbids instantiating it). <c>lv2:inPlaceBroken</c> and <c>lv2:hardRTCapable</c> are accepted
/// because the host always uses distinct input/output buffers and asks nothing extra of an RT-capable plugin;
/// atoms, worker threads, options and plugin UIs are deliberately out of the core subset.</para>
/// </remarks>
internal static unsafe class Lv2Features
{
    /// <summary>The feature URIs this host implements or accepts as required features.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Lv2Ns.UridMap,
        Lv2Ns.UridUnmap,
        Lv2Ns.InPlaceBroken,
        Lv2Ns.HardRtCapable,
    };

    /// <summary>Cap on distinct URIs mapped in a process; past it <c>map</c> returns 0 (the spec's "could not map")
    /// rather than letting a misbehaving plugin grow host memory without bound from <c>run()</c>.</summary>
    private const int MaxUrids = 1_000_000;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, uint> Ids = new(StringComparer.Ordinal);
    private static readonly List<nint> Uris = []; // index = urid - 1 → native UTF-8 string (never freed)
    private static nint _features; // LV2_Feature** (null-terminated), built once

    /// <summary>The null-terminated <c>LV2_Feature* const*</c> array to pass to <c>instantiate</c>.</summary>
    public static nint FeaturesArray
    {
        get
        {
            lock (Gate)
            {
                if (_features == nint.Zero)
                    _features = BuildFeatures();
                return _features;
            }
        }
    }

    /// <summary>Returns the unsupported entries of a plugin's required-feature list (empty = hostable).</summary>
    public static IReadOnlyList<string> Unsupported(IEnumerable<string> requiredFeatures) =>
        requiredFeatures.Where(f => !Supported.Contains(f)).ToArray();

    /// <summary>Maps a URI to its process-wide URID (allocating one on first sight). Returns 0 only once
    /// <see cref="MaxUrids"/> distinct URIs have been mapped.</summary>
    public static uint MapUri(string uri)
    {
        lock (Gate)
        {
            if (Ids.TryGetValue(uri, out uint existing))
                return existing;
            if (Uris.Count >= MaxUrids)
                return 0;
            byte[] bytes = Encoding.UTF8.GetBytes(uri);
            var p = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(p, bytes.Length));
            Uris.Add((nint)p);
            uint id = (uint)Uris.Count;
            Ids[uri] = id;
            return id;
        }
    }

    /// <summary>The URI for a URID previously returned by <see cref="MapUri"/>, or null.</summary>
    public static string? UnmapUri(uint urid)
    {
        lock (Gate)
        {
            if (urid == 0 || urid > Uris.Count)
                return null;
            return Marshal.PtrToStringUTF8(Uris[(int)urid - 1]);
        }
    }

    [UnmanagedCallersOnly]
    private static uint Map(nint handle, byte* uri)
    {
        if (uri is null)
            return 0;
        try
        {
            return MapUri(Marshal.PtrToStringUTF8((nint)uri) ?? "");
        }
        catch
        {
            return 0; // never let an exception cross into native code
        }
    }

    [UnmanagedCallersOnly]
    private static byte* Unmap(nint handle, uint urid)
    {
        lock (Gate)
        {
            if (urid == 0 || urid > Uris.Count)
                return null;
            return (byte*)Uris[(int)urid - 1];
        }
    }

    private static nint BuildFeatures()
    {
        // urid:map
        var map = (Lv2UridMapFeature*)NativeMemory.AllocZeroed((nuint)sizeof(Lv2UridMapFeature));
        map->Handle = nint.Zero;
        map->Map = (nint)(delegate* unmanaged<nint, byte*, uint>)&Map;
        var mapFeature = (Lv2Feature*)NativeMemory.AllocZeroed((nuint)sizeof(Lv2Feature));
        mapFeature->URI = Utf8(Lv2Ns.UridMap);
        mapFeature->Data = (nint)map;

        // urid:unmap
        var unmap = (Lv2UridUnmapFeature*)NativeMemory.AllocZeroed((nuint)sizeof(Lv2UridUnmapFeature));
        unmap->Handle = nint.Zero;
        unmap->Unmap = (nint)(delegate* unmanaged<nint, uint, byte*>)&Unmap;
        var unmapFeature = (Lv2Feature*)NativeMemory.AllocZeroed((nuint)sizeof(Lv2Feature));
        unmapFeature->URI = Utf8(Lv2Ns.UridUnmap);
        unmapFeature->Data = (nint)unmap;

        var array = (nint*)NativeMemory.AllocZeroed(3, (nuint)sizeof(nint));
        array[0] = (nint)mapFeature;
        array[1] = (nint)unmapFeature;
        array[2] = nint.Zero;
        return (nint)array;
    }

    private static nint Utf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        var p = (byte*)NativeMemory.AllocZeroed((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        return (nint)p;
    }
}
