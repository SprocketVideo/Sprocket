using System.Runtime.InteropServices;

namespace Sprocket.Media;

/// <summary>
/// A Linux-only pre-flight that decides whether it is safe to open a <b>VAAPI</b> hardware device
/// (ARCHITECTURE.md §11).
/// <para>
/// The bundled FFmpeg (BtbN <c>*-gpl-shared</c>) links <c>libva</c> through generated lazy-loading
/// trampolines. When FFmpeg's VAAPI init needs a symbol the <b>system</b> <c>libva.so.2</c> does not
/// export (older distros lack <c>vaMapBuffer2</c>, added in libva 2.17), the trampoline does not return
/// an error — it hits an <c>assert(0)</c> and <c>abort()</c>s the whole process, natively, below any
/// managed handler. That kills the app on launch and defeats the software fallback that
/// <see cref="MediaSource"/> otherwise guarantees.
/// </para>
/// <para>
/// We detect that class of too-old / absent <c>libva</c> up front by <c>dlopen</c>ing the system library
/// ourselves and probing for the sentinel symbol. This never goes through FFmpeg's trampolines (we load
/// the system <c>libva</c> directly), so it cannot itself trip the abort. When the probe fails we skip
/// VAAPI and let the platform-preferred list fall through to the next device / software decode. Users on a
/// modern <c>libva</c> keep hardware decode; users on an old one degrade silently instead of crashing.
/// The manual <c>SPROCKET_HWACCEL=off</c> override (<see cref="HardwareAccelSettings"/>) remains as a
/// blanket escape hatch.
/// </para>
/// </summary>
internal static class LibVaPreflight
{
    // The system libva shared object and a symbol present only in libva >= 2.17 — the version FFmpeg 8's
    // VAAPI backend needs. Its presence is the proxy for "new enough not to abort in the trampoline".
    private const string LibVaSoName = "libva.so.2";
    private const string SentinelSymbol = "vaMapBuffer2";

    // Probe once per process: the answer is stable (the system libva does not change under a running app),
    // and the dlopen/dlsym is cheap but needn't repeat per opened file.
    private static readonly Lazy<bool> UsableLazy = new(Probe);

    /// <summary>
    /// Whether a VAAPI device may be opened on this machine. Always <c>true</c> off Linux (VAAPI is not in
    /// the platform-preferred probe list there, so the value is moot); on Linux, <c>true</c> only when the
    /// system <c>libva</c> is present and new enough that FFmpeg's VAAPI init will not abort the process.
    /// </summary>
    public static bool VaapiUsable => UsableLazy.Value;

    private static bool Probe()
    {
        // Only Linux routes VAAPI through the bundled FFmpeg's trampolines; other OSes never ask.
        if (!OperatingSystem.IsLinux())
            return true;

        if (!NativeLibrary.TryLoad(LibVaSoName, out IntPtr handle))
            return false; // no libva at all → VAAPI cannot work; degrade to software

        try
        {
            return NativeLibrary.TryGetExport(handle, SentinelSymbol, out _);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}
