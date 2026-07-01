using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Sprocket.Media;

namespace Sprocket.App;

internal static class Program
{
    /// <summary>
    /// Entry point for the slice's editor app: opens a media file (first CLI arg, or a generated sample
    /// clip), builds a one-video-track project, and plays it in the Skia preview with a transport
    /// (PLAN.md step 4).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Install the global crash logger first, before anything that can fault, so even an early failure
        // (e.g. FFmpeg natives missing) is written to a discoverable log file rather than vanishing with the
        // window. The log directory is surfaced in Help ▸ About (see CrashLog).
        CrashLog.Install();

        // Pre-load any FFmpeg natives bundled next to the executable, in dependency order, before
        // anything touches FFmpeg (ARCHITECTURE.md §11). No-op on Windows / local dev.
        FFmpegLoader.EnsureBundledNativesLoaded();

        if (args.Contains("--version"))
        {
            Console.WriteLine($"Sprocket {AppVersion}");
            return 0;
        }

        // Headless release smoke check (scripts/linux-smoke.sh): load the bundled FFmpeg natives and
        // exit, without starting the UI. Proves the per-RID native bundling actually resolves.
        if (args.Contains("--ffmpeg-check"))
            return RunFFmpegCheck();

        // Diagnostic: open a media file through the real MediaSource path and report what happened
        // (or the full failure), without starting the UI.  --probe "C:\path\to\file.mp4"
        int probeIdx = Array.IndexOf(args, "--probe");
        if (probeIdx >= 0)
            return RunProbe(probeIdx + 1 < args.Length ? args[probeIdx + 1] : "");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            // An exception that escaped the Avalonia message loop. AppDomain.UnhandledException usually also
            // fires, but capture it here too so a startup/UI-thread crash is logged before the process exits.
            CrashLog.Write("Fatal exception in main loop", ex);
            throw;
        }
    }

    private static int RunProbe(string path)
    {
        Console.WriteLine($"[probe] path: '{path}'");
        Console.WriteLine($"[probe] File.Exists: {System.IO.File.Exists(path)}");
        try
        {
            using MediaSource source = MediaSource.Open(path);
            var i = source.Info;
            Console.WriteLine($"[probe] OK: {i.Width}x{i.Height} {i.FrameRate} hasVideo={i.HasVideo} hasAudio={i.HasAudio} dur={i.Duration.ToSeconds():0.0}s hw={source.HardwareDeviceName ?? "software"}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe] FAIL: {ex.GetType().FullName}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int RunFFmpegCheck()
    {
        try
        {
            Console.WriteLine($"[ffmpeg-check] OK: {FFmpegDiagnostics.ProbeVersion()}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ffmpeg-check] FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The build version stamped from Directory.Build.props (informational version, falling back to file version).</summary>
    public static string AppVersion
    {
        get
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string? informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // Strip any source-revision suffix (e.g. "0.1.1+abcdef") for a clean display string.
            if (!string.IsNullOrEmpty(informational))
                return informational.Split('+')[0];
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
