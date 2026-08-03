using System.Runtime.InteropServices;

namespace Sprocket.Media;

/// <summary>
/// A Linux-only pre-flight that decides whether it is safe to open a <b>VAAPI</b> hardware device
/// (ARCHITECTURE.md §11).
/// <para>
/// The bundled FFmpeg (BtbN <c>*-gpl-shared</c>) does not link <c>libva</c> normally — it carries no
/// <c>DT_NEEDED</c> entry and no undefined <c>va*</c> symbols. Instead it <c>dlopen</c>s the system
/// <c>libva</c> lazily through <c>implib-gen</c> trampolines. A trampoline whose symbol the system library
/// does not export does <b>not</b> return an error: it <c>abort()</c>s the whole process, natively, below
/// any managed handler, with <c>implib-gen: libva.so.2: failed to resolve symbol '…' via dlsym</c>. That
/// kills the app and defeats the software fallback <see cref="MediaSource"/> otherwise guarantees.
/// </para>
/// <para>
/// So before any VAAPI device is opened we <c>dlopen</c> the system <c>libva</c> family <i>ourselves</i> —
/// never through FFmpeg's trampolines, so the probe cannot itself trip the abort — and <c>dlsym</c> every
/// entry point the bundled FFmpeg references. A single miss skips VAAPI and lets the platform-preferred
/// list fall through to the next device / software decode. <see cref="RequiredSymbols"/> is the trampoline
/// table enumerated straight out of the bundled <c>libavutil</c> (a contiguous run of NUL-separated strings),
/// so it covers every symbol any FFmpeg code path could reach rather than a guess at which ones matter —
/// and regenerating it is a mechanical step on an FFmpeg <b>major</b> bump, exactly like the struct offsets
/// in <c>Native/AvStructs.cs</c> (procedure in <c>Native/SPIKE_RESULTS.md</c>).
/// </para>
/// <para>
/// <b>Two classes of referenced symbol are deliberately excluded</b>, because requiring them would reject a
/// perfectly good stack:
/// <list type="bullet">
/// <item><description><c>va_</c>-prefixed names (<c>va_TraceStatus</c>, <c>va_newDisplayContext</c>,
/// <c>va_TracePutSurface</c>, <c>va_newDriverContext</c>) are private to libva and exported by no released
/// version at all.</description></item>
/// <item><description>The display getters live in sibling libraries, one per windowing system
/// (<see cref="DisplayBackends"/>). FFmpeg needs whichever matches the session, so <b>any one</b> resolving
/// is enough — demanding <c>vaGetDisplayDRM</c> would fail an X11-only box and vice versa.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Measured on an Intel N100 / iHD box with libva 2.20 (Ubuntu 24.04 LTS):</b> exactly one referenced
/// symbol fails to resolve — <c>vaMapBuffer2</c>, which libva added in <b>2.21</b> (Mar 2024) — and that is
/// enough to abort. Note that <c>av_hwdevice_ctx_create(VAAPI)</c> succeeds there (<c>rc=0</c>) and
/// <c>vainfo</c> advertises HEVC Main/Main10 VLD: device creation never reaches the symbol, the decode /
/// <c>av_hwframe_transfer_data</c> path does. Probing by "can a device be opened" therefore reports usable
/// and then dies a frame later — which is why this check is a symbol sweep and not a device open. The
/// <c>SPROCKET_HWACCEL=off</c> override remains the blanket escape hatch for other unstable stacks.
/// </para>
/// </summary>
internal static class LibVaPreflight
{
    private const string LibVaSoName = "libva.so.2";

    /// <summary>
    /// Every <c>libva</c> entry point the bundled FFmpeg 8.1 <c>libavutil</c> carries a trampoline for, less the
    /// private <c>va_</c> names and the per-windowing-system display getters (see the type remarks). All must
    /// resolve or VAAPI is skipped. <b>Regenerate on an FFmpeg major bump:</b>
    /// <c>strings -t d libavutil.so.&lt;major&gt; | grep -E '^ *[0-9]+ va[A-Z][A-Za-z0-9]*$'</c> — the table is the
    /// contiguous offset run (a stray fragment elsewhere in <c>.rodata</c> is not part of it).
    /// </summary>
    internal static readonly string[] RequiredSymbols =
    [
        "vaAcquireBufferHandle", "vaAssociateSubpicture", "vaAttachProtectedSession", "vaBeginPicture",
        "vaBufferInfo", "vaBufferSetNumElements", "vaBufferTypeStr", "vaConfigAttribTypeStr", "vaCopy",
        "vaCreateBuffer", "vaCreateBuffer2", "vaCreateConfig", "vaCreateContext", "vaCreateImage",
        "vaCreateMFContext", "vaCreateProtectedSession", "vaCreateSubpicture", "vaCreateSurfaces",
        "vaDeassociateSubpicture", "vaDeriveImage", "vaDestroyBuffer", "vaDestroyConfig", "vaDestroyContext",
        "vaDestroyImage", "vaDestroyProtectedSession", "vaDestroySubpicture", "vaDestroySurfaces",
        "vaDetachProtectedSession", "vaDisplayIsValid", "vaEndPicture", "vaEntrypointStr", "vaErrorStr",
        "vaExportSurfaceHandle", "vaGetConfigAttributes", "vaGetDisplayAttributes", "vaGetImage", "vaGetLibFunc",
        "vaInitialize", "vaLockSurface", "vaMapBuffer", "vaMapBuffer2", "vaMaxNumConfigAttributes",
        "vaMaxNumDisplayAttributes", "vaMaxNumEntrypoints", "vaMaxNumImageFormats", "vaMaxNumProfiles",
        "vaMaxNumSubpictureFormats", "vaMFAddContext", "vaMFReleaseContext", "vaMFSubmit", "vaProfileStr",
        "vaProtectedSessionExecute", "vaPutImage", "vaQueryConfigAttributes", "vaQueryConfigEntrypoints",
        "vaQueryConfigProfiles", "vaQueryDisplayAttributes", "vaQueryImageFormats", "vaQueryProcessingRate",
        "vaQuerySubpictureFormats", "vaQuerySurfaceAttributes", "vaQuerySurfaceError", "vaQuerySurfaceStatus",
        "vaQueryVendorString", "vaQueryVideoProcFilterCaps", "vaQueryVideoProcFilters",
        "vaQueryVideoProcPipelineCaps", "vaReleaseBufferHandle", "vaRenderPicture", "vaSetDisplayAttributes",
        "vaSetDriverName", "vaSetErrorCallback", "vaSetImagePalette", "vaSetInfoCallback",
        "vaSetSubpictureChromakey", "vaSetSubpictureGlobalAlpha", "vaSetSubpictureImage", "vaStatusStr",
        "vaSyncBuffer", "vaSyncSurface", "vaSyncSurface2", "vaTerminate", "vaUnlockSurface", "vaUnmapBuffer",
    ];

    /// <summary>
    /// The display backends FFmpeg opens a VAAPI device through, as (library, entry point) pairs — one per
    /// windowing system, so <b>any one</b> resolving is enough. None of them means there is no way to reach a
    /// device at all.
    /// </summary>
    internal static readonly (string Library, string Symbol)[] DisplayBackends =
    [
        ("libva-drm.so.2", "vaGetDisplayDRM"),
        ("libva-x11.so.2", "vaGetDisplay"),
        ("libva-wayland.so.2", "vaGetDisplayWl"),
    ];

    // Probe once per process: the answer is stable (the system libva does not change under a running app),
    // and the dlopen/dlsym is cheap but needn't repeat per opened file.
    private static readonly Lazy<bool> UsableLazy = new(Probe);

    /// <summary>
    /// Whether a VAAPI device may be opened on this machine. Always <c>true</c> off Linux (VAAPI is not in
    /// the platform-preferred probe list there, so the value is moot); on Linux, <c>true</c> only when the
    /// system <c>libva</c> resolves every symbol the bundled FFmpeg could call through a trampoline.
    /// </summary>
    public static bool VaapiUsable => UsableLazy.Value;

    /// <summary>The first referenced symbol the system <c>libva</c> does not export, or <c>null</c> when it
    /// exports them all — the reason a machine is on software decode, for tests and diagnostics.</summary>
    internal static string? FirstMissingSymbol()
    {
        if (!NativeLibrary.TryLoad(LibVaSoName, out IntPtr handle))
            return LibVaSoName;

        try
        {
            foreach (string symbol in RequiredSymbols)
                if (!NativeLibrary.TryGetExport(handle, symbol, out _))
                    return symbol;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
        return null;
    }

    private static bool Probe()
    {
        // Only Linux routes VAAPI through the bundled FFmpeg's trampolines; other OSes never ask.
        if (!OperatingSystem.IsLinux())
            return true;

        return FirstMissingSymbol() is null && HasDisplayBackend();
    }

    internal static bool HasDisplayBackend()
    {
        foreach ((string library, string symbol) in DisplayBackends)
        {
            if (!NativeLibrary.TryLoad(library, out IntPtr handle))
                continue;
            try
            {
                if (NativeLibrary.TryGetExport(handle, symbol, out _))
                    return true;
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        return false;
    }
}
