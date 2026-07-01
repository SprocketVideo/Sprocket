using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Sprocket.App;

/// <summary>
/// Process-wide, last-resort crash logging. Installs handlers for unhandled exceptions on any thread
/// (<see cref="AppDomain.UnhandledException"/>) and for faulted tasks nobody awaited
/// (<see cref="TaskScheduler.UnobservedTaskException"/>), and appends each to a per-user log file — so a
/// crash that closes the window still leaves a diagnosable trail even when the app was launched by
/// double-click with no attached terminal. The log location is surfaced in Help ▸ About so users can find
/// it without knowing the convention.
/// </summary>
/// <remarks>
/// This captures <b>managed</b> exceptions only. A native crash — a segmentation fault inside an FFmpeg,
/// Skia or OpenAL call (e.g. an unstable Linux VAAPI driver) — terminates the process without raising any
/// managed event and will not appear here; set <c>DOTNET_DbgEnableMiniDump=1</c> to have the .NET runtime
/// write a crash dump for those, and see the <c>SPROCKET_HWACCEL=off</c> override for the common Linux
/// GPU-decode case.
/// </remarks>
internal static class CrashLog
{
    private static readonly object Gate = new();
    private static bool _installed;

    /// <summary>The directory exception logs are written to — <c>…/Sprocket/logs</c> under the per-user
    /// local application-data folder (the same root autosave and the proxy cache use). Created on first
    /// write; shown in the About dialog.</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sprocket", "logs");

    /// <summary>The current log file: one per day, so the folder neither fills with a file per crash nor
    /// grows without bound within a single session.</summary>
    public static string LogFilePath =>
        Path.Combine(LogDirectory, $"sprocket-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Installs the global handlers. Idempotent; call once at startup, before anything that can
    /// fault, so even an early failure is captured.</summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed)
                return;
            _installed = true;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unhandled exception" + (e.IsTerminating ? " (terminating)" : ""), e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task exception", e.Exception);
            e.SetObserved(); // it is now logged; don't let it escalate to a process-terminating rethrow
        };
    }

    /// <summary>
    /// Appends an exception (with a timestamp, the app version, and OS context) to <see cref="LogFilePath"/>.
    /// Best-effort and self-contained: it never throws, so it is safe to call from a crash handler.
    /// Also echoes a one-line summary + the log path to stderr for anyone running from a terminal.
    /// </summary>
    public static void Write(string context, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var sb = new StringBuilder();
            sb.Append("\n===== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append("  ·  ").Append(context).Append(" =====\n");
            sb.Append("Sprocket ").Append(Program.AppVersion)
              .Append("  ·  ").Append(RuntimeInformation.OSDescription)
              .Append("  ·  ").Append(RuntimeInformation.OSArchitecture)
              .Append("  ·  .NET ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
            sb.Append(ex?.ToString() ?? "(no exception object)").Append('\n');
            File.AppendAllText(LogFilePath, sb.ToString());

            Console.Error.WriteLine($"[sprocket] {context}: {ex?.Message}");
            Console.Error.WriteLine($"[sprocket] logged to {LogFilePath}");
        }
        catch
        {
            // Logging must never itself crash the crash handler; swallow any I/O failure.
        }
    }
}
