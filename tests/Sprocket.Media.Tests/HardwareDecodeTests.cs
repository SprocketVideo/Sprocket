using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Media.Tests;

/// <summary>
/// Hardware-accelerated decode (PLAN step 6). The GPU path is device-dependent, so these tests assert the
/// behaviour that must hold <em>regardless</em> of whether a device is present: software mode always decodes
/// with no device; auto mode decodes whether it engages hardware or falls back; and — crucially — the
/// hardware and software paths produce the <b>same frame timing</b>, so enabling hardware never breaks
/// frame-accuracy. (On a machine with a GPU, auto exercises the real hardware download path here.)
/// </summary>
public class HardwareDecodeTests
{
    private const long FrameTicks = Timecode.TicksPerSecond / TestVideo.Fps;

    private static long[] DecodePtsSequence(HardwareAccelMode mode, int count)
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, mode);
        var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        var pts = new List<long>(count);
        while (pts.Count < count && source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            pts.Add(frame.Pts.Ticks);
            frame.Dispose();
        }
        pool.Dispose();
        return [.. pts];
    }

    [Fact]
    public void Software_Mode_Uses_No_Hardware_Device()
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled);
        Assert.Null(source.HardwareDeviceName);
    }

    [Fact]
    public void Software_Mode_Decodes_Frames_In_Order()
    {
        long[] pts = DecodePtsSequence(HardwareAccelMode.Disabled, 10);
        Assert.Equal(10, pts.Length);
        for (int i = 0; i < pts.Length; i++)
            Assert.Equal(i * FrameTicks, pts[i]);
    }

    [Fact]
    public void Auto_Mode_Decodes_Whether_Or_Not_Hardware_Engages()
    {
        using MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Auto);
        // HardwareDeviceName is a device name when the GPU path engaged, or null on software fallback —
        // both are valid; what must hold is that decoding works either way.
        var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        bool decoded = source.TryDecodeNextFrame(pool, out VideoFrame? frame);
        frame?.Dispose();
        pool.Dispose();
        Assert.True(decoded);
    }

    [Fact]
    public void Hardware_And_Software_Paths_Produce_Identical_Frame_Timing()
    {
        // Frame-accuracy must not depend on the decode backend. Auto may run on the GPU (then this compares
        // the hardware-download path against software) or fall back (software vs software) — either way the
        // PTS sequence must match exactly.
        long[] software = DecodePtsSequence(HardwareAccelMode.Disabled, 20);
        long[] auto = DecodePtsSequence(HardwareAccelMode.Auto, 20);
        Assert.Equal(software, auto);
    }

    [Fact]
    public void Reports_The_Compiled_Hardware_Types()
    {
        // The bundled FFmpeg is built with hardware support; the list is non-empty on a desktop build.
        Assert.NotEmpty(HardwareDevice.CompiledTypes());
    }

    [Fact]
    public void Platform_Preferred_Types_Are_Defined_For_This_Os()
    {
        IReadOnlyList<HardwareDeviceType> preferred = HardwareDevice.PlatformPreferredTypes();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.NotEmpty(preferred);
    }

    [Fact]
    public void LibVa_Preflight_Never_Blocks_Vaapi_Off_Linux()
    {
        // The pre-flight guards only the Linux VAAPI-via-bundled-FFmpeg path; on Windows/macOS VAAPI is not in
        // the probe list and the pre-flight must be an unconditional pass so it never masks another OS's devices.
        if (!OperatingSystem.IsLinux())
            Assert.True(LibVaPreflight.VaapiUsable);
    }

    [Fact]
    public void TryCreate_Vaapi_Is_Null_When_Libva_Is_Unusable()
    {
        // The core safety property: when the system libva is too old/absent, opening a VAAPI device returns null
        // (degrade to software) instead of reaching FFmpeg's VAAPI init, which would abort the process natively.
        // When libva IS usable, TryCreate may still return null (no GPU/driver present) — both are non-crashing,
        // so we only assert the guaranteed direction.
        if (!LibVaPreflight.VaapiUsable)
            Assert.Null(HardwareDevice.TryCreate(HardwareDeviceType.Vaapi));
    }

    [Fact]
    public void LibVa_Preflight_Gates_On_vaMapBuffer2()
    {
        // vaMapBuffer2 (libva 2.21+) is the symbol that actually aborts the process on an older libva, and it is
        // reached from the decode / av_hwframe_transfer_data path — NOT from av_hwdevice_ctx_create, which returns
        // rc=0 on a libva 2.20 box that then dies a frame later. It was dropped from the gate once on the strength
        // of that misleading device-open probe, and the whole Media/Playback/Export suites crashed with
        // "implib-gen: libva.so.2: failed to resolve symbol 'vaMapBuffer2' via dlsym". It must stay gated.
        Assert.Contains("vaMapBuffer2", LibVaPreflight.RequiredSymbols);
    }

    [Fact]
    public void LibVa_Preflight_Does_Not_Gate_On_Symbols_No_Libva_Exports()
    {
        // The gate is the bundled FFmpeg's whole trampoline table, so it must exclude the two classes of referenced
        // name that never resolve even on a fully working stack: libva-private va_* symbols, and the display getters
        // that live one-per-windowing-system in sibling libraries (any one of which is enough — see DisplayBackends).
        Assert.DoesNotContain(LibVaPreflight.RequiredSymbols, s => s.StartsWith("va_", StringComparison.Ordinal));
        Assert.Empty(LibVaPreflight.RequiredSymbols.Intersect(
            LibVaPreflight.DisplayBackends.Select(b => b.Symbol)));
        Assert.All(LibVaPreflight.RequiredSymbols, s => Assert.StartsWith("va", s, StringComparison.Ordinal));
    }

    [Fact]
    public void LibVa_Preflight_Verdict_Matches_What_The_System_Libva_Actually_Exports()
    {
        // Both directions of the safety property, against the real system libva: a missing symbol must be reported
        // (so TryCreate skips VAAPI before a trampoline can abort), and a libva that resolves the whole table with a
        // display backend present must be accepted (so hardware decode is not silently lost on a capable machine).
        if (!OperatingSystem.IsLinux())
            return;

        string? missing = LibVaPreflight.FirstMissingSymbol();
        if (missing is null && LibVaPreflight.HasDisplayBackend())
            Assert.True(LibVaPreflight.VaapiUsable, "every referenced symbol resolves, so VAAPI must be allowed");
        else
            Assert.False(LibVaPreflight.VaapiUsable, $"'{missing}' is unresolvable, so VAAPI must be skipped");
    }
}
