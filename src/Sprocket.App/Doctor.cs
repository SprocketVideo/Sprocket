using System;
using System.IO;
using System.Runtime.InteropServices;
using Sprocket.Media;

namespace Sprocket.App;

/// <summary>
/// The <c>--doctor</c> headless self-check: reports the host environment and probes every dependency a
/// shipped Sprocket build relies on, so a user on a minimal or unusual Linux install gets an actionable
/// diagnosis ("run <c>apt install libva2</c>") instead of an opaque native loader failure.
/// <para>
/// It is the honest bound on the alpha's <b>experimental, modern-glibc-desktop</b> Linux support: it
/// declares the supported baseline (<see cref="GlibcBaseline"/>), detects when the host is below it or on
/// an unsupported libc (musl / Alpine), and turns the runtime-dependency gap — the bundled BtbN FFmpeg
/// <c>dlopen</c>s several host libraries that <c>install.sh</c> does not install — into a checklist.
/// </para>
/// <para>
/// Exit code: <c>0</c> when the core (FFmpeg natives + a supported libc) is sound, even if optional
/// hardware-accel or GUI libraries are missing; <c>1</c> on a hard failure (unsupported libc, or the
/// bundled FFmpeg natives do not load). Missing optional libraries and a below-baseline (but still
/// glibc) host are warnings, not failures — Sprocket may still run, just unsupported.
/// </para>
/// </summary>
internal static class Doctor
{
    /// <summary>The oldest glibc Sprocket declares support for: glibc 2.35 (Ubuntu 22.04 LTS). The prior
    /// 2.31 (20.04) floor was dropped once the bundled OpenAL Soft failed to load there (newer libstdc++
    /// ABI). Below this the host is out of the supported baseline (reported, not blocked).</summary>
    private static readonly Version GlibcBaseline = new(2, 35);

    // Host shared libraries the bundled build reaches for at runtime (the BtbN gpl-shared FFmpeg dlopen's
    // the media/HW ones lazily; Avalonia needs the GUI ones). None is asserted here as hard-required —
    // the FFmpeg native load (RunFFmpegCheck) is the source of truth for "can it decode at all"; these
    // probes tell the user which optional/GUI paths their host currently satisfies. Package names are the
    // providing package on each major family.
    private static readonly HostLib[] HostLibs =
    [
        new("libxml2.so.2",       "FFmpeg XML / DASH demuxers",        Gui: false, Apt: "libxml2",        Dnf: "libxml2",   Pacman: "libxml2",   Zypper: "libxml2-2"),
        new("libdrm.so.2",        "DRM (VAAPI / KMS render paths)",    Gui: false, Apt: "libdrm2",        Dnf: "libdrm",    Pacman: "libdrm",    Zypper: "libdrm2"),
        new("libva.so.2",         "VAAPI hardware accel (optional)",   Gui: false, Apt: "libva2",         Dnf: "libva",     Pacman: "libva",     Zypper: "libva2"),
        new("libva-drm.so.2",     "VAAPI DRM backend (optional)",      Gui: false, Apt: "libva-drm2",     Dnf: "libva",     Pacman: "libva",     Zypper: "libva-drm2"),
        new("libvdpau.so.1",      "VDPAU hardware accel (optional)",   Gui: false, Apt: "libvdpau1",      Dnf: "libvdpau",  Pacman: "libvdpau",  Zypper: "libvdpau1"),
        new("libfontconfig.so.1", "GUI text rendering",                Gui: true,  Apt: "libfontconfig1", Dnf: "fontconfig", Pacman: "fontconfig", Zypper: "fontconfig"),
        new("libX11.so.6",        "GUI X11 windowing",                 Gui: true,  Apt: "libx11-6",       Dnf: "libX11",    Pacman: "libx11",    Zypper: "libX11-6"),
    ];

    public static int Run()
    {
        bool fail = false;

        Line("== Sprocket doctor ==");
        Line($"version : {Program.AppVersion}");
        Line($"os      : {RuntimeInformation.OSDescription}");
        Line($"arch    : {RuntimeInformation.OSArchitecture} (process {RuntimeInformation.ProcessArchitecture})");
        Line($"rid     : {RuntimeInformation.RuntimeIdentifier}");
        Line($"appdir  : {AppContext.BaseDirectory}");
        Line("");

        if (OperatingSystem.IsLinux())
            fail |= CheckLinuxLibc();

        // FFmpeg natives — the one hard requirement. A throw here means the bundled .so/.dll/.dylib set
        // did not resolve (missing, wrong version, or an unmet transitive host dep).
        try
        {
            Ok($"ffmpeg  : {FFmpegDiagnostics.ProbeVersion()}");
        }
        catch (Exception ex)
        {
            Fail($"ffmpeg  : FAILED to load bundled natives — {ex.GetType().Name}: {ex.Message}");
            fail = true;
        }

        // Audio — important (it is the master clock) but not fatal to a diagnosis: a user with no sound
        // device can still edit/export, so a failure here is a warning.
        try
        {
            using var output = new Sprocket.Audio.OpenAlAudioOutput();
            output.Configure(48000, 2);
            Ok("audio   : OpenAL device opened (48 kHz stereo)");
        }
        catch (Exception ex)
        {
            Warn($"audio   : OpenAL device did NOT open — {ex.GetType().Name}: {ex.Message}");
        }

        if (OperatingSystem.IsLinux())
        {
            Line("");
            CheckHostLibs();
            Line("");
            Line($"vaapi   : {FFmpegDiagnostics.VaapiStatus()}");
        }

        Line("");
        Line(fail ? "RESULT: FAIL" : "RESULT: OK");
        return fail ? 1 : 0;
    }

    // Returns true on a HARD failure (unsupported libc). A below-baseline glibc is only a warning.
    private static bool CheckLinuxLibc()
    {
        string? pretty = ReadOsReleasePrettyName();
        if (pretty is not null) Line($"distro  : {pretty}");

        if (File.Exists("/etc/alpine-release") || !NativeLibrary.TryLoad("libc.so.6", out IntPtr libc))
        {
            Fail("libc    : glibc not found (musl / Alpine?) — these glibc builds are unsupported on this host.");
            return true;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(libc, "gnu_get_libc_version", out IntPtr p))
            {
                Fail("libc    : libc.so.6 present but not glibc (no gnu_get_libc_version) — unsupported libc.");
                return true;
            }

            string? raw = Marshal.PtrToStringUTF8(Marshal.GetDelegateForFunctionPointer<GnuGetLibcVersion>(p)());
            if (raw is null || !Version.TryParse(raw, out Version? v))
            {
                Warn($"libc    : glibc {raw ?? "unknown"} (could not parse version; baseline is {GlibcBaseline}).");
                return false;
            }

            if (v < GlibcBaseline)
                Warn($"libc    : glibc {v} is BELOW the supported baseline {GlibcBaseline} (Ubuntu 22.04) — Sprocket may run but is unsupported here.");
            else
                Ok($"libc    : glibc {v} (baseline {GlibcBaseline}+)");
            return false;
        }
        finally
        {
            NativeLibrary.Free(libc);
        }
    }

    private static void CheckHostLibs()
    {
        Line("host libraries (bundled FFmpeg / Avalonia dlopen these from the system):");
        foreach (HostLib lib in HostLibs)
        {
            if (NativeLibrary.TryLoad(lib.SoName, out IntPtr h))
            {
                NativeLibrary.Free(h);
                Ok($"  {lib.SoName,-20} present  — {lib.Purpose}");
            }
            else
            {
                string kind = lib.Gui ? "needed for the GUI (ignore if running headless)" : "optional";
                Warn($"  {lib.SoName,-20} MISSING  — {lib.Purpose} [{kind}]");
                Line($"      install: apt {lib.Apt} · dnf {lib.Dnf} · pacman {lib.Pacman} · zypper {lib.Zypper}");
            }
        }
    }

    private static string? ReadOsReleasePrettyName()
    {
        try
        {
            foreach (string line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                    return line["PRETTY_NAME=".Length..].Trim().Trim('"');
            }
        }
        catch { /* /etc/os-release absent or unreadable — non-fatal */ }
        return null;
    }

    private static void Line(string s) => Console.WriteLine(s);
    private static void Ok(string s) => Console.WriteLine($"[ok]   {s}");
    private static void Warn(string s) => Console.WriteLine($"[warn] {s}");
    private static void Fail(string s) => Console.Error.WriteLine($"[fail] {s}");

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GnuGetLibcVersion();

    private readonly record struct HostLib(
        string SoName, string Purpose, bool Gui, string Apt, string Dnf, string Pacman, string Zypper);
}
