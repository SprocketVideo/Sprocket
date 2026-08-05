using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Sprocket.App;

/// <summary>
/// User-level desktop integration for the Linux <b>AppImage</b> build (PLAN.md step 36): registers an
/// application launcher and installs the app icon into the freedesktop hicolor theme, so Sprocket shows
/// up in the applications menu / dock with its proper icon — the thing an AppImage does <i>not</i> do on
/// its own (it is portable-by-design and never "installs"). This is the in-app equivalent of the portable
/// zip's <c>packaging/linux/install.sh</c> / <c>uninstall.sh</c>, and writes the same files, but resolves
/// the launcher's <c>Exec=</c> from the AppImage's own path so it keeps working wherever the file lives.
///
/// <para>Everything is per-user (no root, under <c>$XDG_DATA_HOME</c>/<c>~/.local/share</c>) and best-effort:
/// a failure to write a file or refresh a cache is logged and surfaced to the caller, never thrown up the
/// UI. Only meaningful when <see cref="IsAvailable"/> — i.e. the process is a running AppImage (the AppImage
/// runtime sets <c>$APPIMAGE</c> to the file's absolute path); a dev/portable run leaves it unset, so the
/// feature quietly disables itself.</para>
/// </summary>
internal static class LinuxDesktopIntegration
{
    // The launcher/icon basename, matched to packaging/linux/sprocket.desktop so a user who moves between the
    // AppImage and the zip installer never ends up with two competing entries.
    private const string ResourceName = "sprocket";

    /// <summary>The absolute path of the running AppImage (from <c>$APPIMAGE</c>), or <see langword="null"/>
    /// when this is not an AppImage run (dev/portable, or a non-Linux OS).</summary>
    public static string? AppImagePath =>
        OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("APPIMAGE") : null;

    /// <summary>Whether in-app menu integration applies to this process: a real AppImage run whose file still
    /// exists. The one gate the UI checks before offering the prompt or the Help-menu items.</summary>
    public static bool IsAvailable => AppImagePath is { Length: > 0 } path && File.Exists(path);

    /// <summary>Whether the launcher entry is currently installed for this user.</summary>
    public static bool IsInstalled => File.Exists(DesktopFilePath);

    // Freedesktop per-user locations. The icon ships at 1024x1024 (see install.sh); install into the matching
    // hicolor bucket (the theme downscales as needed) and also into pixmaps as a broad fallback for panels /
    // file managers that ignore icon-theme sizing.
    private static string DataHome
    {
        get
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
        }
    }

    private static string AppsDir => Path.Combine(DataHome, "applications");
    private static string DesktopFilePath => Path.Combine(AppsDir, ResourceName + ".desktop");
    private static string HicolorRoot => Path.Combine(DataHome, "icons", "hicolor");
    private static string PixmapsDir => Path.Combine(DataHome, "pixmaps");

    // The standard hicolor buckets the icon is installed into. A single 1024x1024 file is invisible to GTK /
    // Cinnamon icon lookup: 1024x1024 is not a size declared in the hicolor index.theme, and the menu only
    // searches the indexed buckets — so we render one PNG per size below (see RenderIcons).
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256, 512 };

    private static string IconAppsDir(int size) => Path.Combine(HicolorRoot, $"{size}x{size}", "apps");

    /// <summary>
    /// Writes the launcher <c>.desktop</c> and installs the app icon for the current user, then refreshes the
    /// desktop/icon caches so the entry appears without a re-login. Returns <see langword="false"/> (and logs)
    /// if anything went wrong or this is not an AppImage run.
    /// </summary>
    public static bool Install(IReadOnlyDictionary<int, byte[]> icons)
    {
        if (AppImagePath is not { Length: > 0 } appImage || icons.Count == 0)
            return false;

        try
        {
            Directory.CreateDirectory(AppsDir);
            Directory.CreateDirectory(PixmapsDir);

            // One PNG per indexed hicolor bucket, so the menu / panel / window-switcher each find a crisp icon
            // at the size they ask for. The bytes are pre-rendered by RenderIcons (which needs the UI thread).
            foreach ((int size, byte[] png) in icons)
            {
                string dir = IconAppsDir(size);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, ResourceName + ".png"), png);
            }

            // A broad legacy fallback for panels / file managers that bypass the icon theme entirely.
            byte[] pixmap = icons.TryGetValue(256, out byte[]? p) ? p : icons.Values.Last();
            File.WriteAllBytes(Path.Combine(PixmapsDir, ResourceName + ".png"), pixmap);

            File.WriteAllText(DesktopFilePath, BuildDesktopEntry(appImage));

            RefreshCaches();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Linux desktop integration install failed", ex);
            return false;
        }
    }

    /// <summary>Removes the launcher and icon this user installed (idempotent — succeeds even if absent), then
    /// refreshes the caches. Returns <see langword="false"/> (and logs) only on an actual IO failure.</summary>
    public static bool Uninstall()
    {
        try
        {
            File.Delete(DesktopFilePath);
            foreach (int size in IconSizes)
                File.Delete(Path.Combine(IconAppsDir(size), ResourceName + ".png"));
            // Also sweep the pre-fix single 1024x1024 icon, so upgrading users don't leave one behind.
            File.Delete(Path.Combine(HicolorRoot, "1024x1024", "apps", ResourceName + ".png"));
            File.Delete(Path.Combine(PixmapsDir, ResourceName + ".png"));
            RefreshCaches();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Linux desktop integration uninstall failed", ex);
            return false;
        }
    }

    // Mirrors packaging/linux/sprocket.desktop, but with Exec resolved to the running AppImage's own path
    // (quoted per the freedesktop spec so a path with spaces still launches). %f lets the launcher open a file
    // passed by a file manager, and StartupWMClass groups the window under this launcher's icon in the dock.
    internal static string BuildDesktopEntry(string appImagePath) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Version=1.0",
        "Name=Sprocket",
        "GenericName=Video Editor",
        "Comment=Cross-platform, non-destructive video editor",
        $"Exec={QuoteExec(appImagePath)} %f",
        $"Icon={ResourceName}",
        "Terminal=false",
        "Categories=AudioVideo;AudioVideoEditing;Video;",
        "Keywords=video;editor;timeline;cut;clip;",
        "StartupWMClass=Sprocket",
        "");

    // Freedesktop Exec quoting: wrap in double quotes and backslash-escape the reserved characters that keep
    // their meaning inside quotes ("`$\). AppImage paths almost never contain these, but a user's home dir can.
    private static string QuoteExec(string path)
    {
        string escaped = path
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$");
        return "\"" + escaped + "\"";
    }

    /// <summary>
    /// Renders the embedded app icon (the same <c>avares://Sprocket/Assets/sprocket.png</c> the window uses)
    /// to a PNG for each <see cref="IconSizes"/> bucket, high-quality-downscaled from the 1024px source. Uses
    /// Avalonia bitmap APIs, so <b>call it on the UI thread</b>; the resulting bytes are then written by the
    /// thread-agnostic <see cref="Install(IReadOnlyDictionary{int, byte[]})"/> off the UI thread.
    /// </summary>
    public static Dictionary<int, byte[]> RenderIcons()
    {
        using var source = new Bitmap(AssetLoader.Open(new Uri("avares://Sprocket/Assets/sprocket.png")));
        var result = new Dictionary<int, byte[]>(IconSizes.Length);
        foreach (int size in IconSizes)
        {
            using Bitmap scaled = source.CreateScaledBitmap(new PixelSize(size, size), BitmapInterpolationMode.HighQuality);
            using var ms = new MemoryStream();
            scaled.Save(ms, PngBitmapEncoderOptions.Default);
            result[size] = ms.ToArray();
        }
        return result;
    }

    // Best-effort cache refresh so the entry/icon appear without a re-login; both tools are optional and a
    // missing one (or a slow shell) must not stall or fail the install.
    private static void RefreshCaches()
    {
        RunQuiet("update-desktop-database", AppsDir);
        RunQuiet("gtk-update-icon-cache", "-f", "-t", HicolorRoot);
    }

    private static void RunQuiet(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch
        {
            // The tool may be absent (headless / minimal desktop) — the entry still lands; the cache just
            // refreshes on next login. Never let this break integration.
        }
    }
}
