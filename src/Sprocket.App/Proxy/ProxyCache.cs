using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Sprocket.App.Proxy;

/// <summary>
/// Locates and names files in the local proxy cache (PLAN.md step 18). Proxies are a <b>local, regenerable</b>
/// artifact — kept in a per-user cache dir, never in the project file, and always safely discardable (the same
/// store family as the render cache, §20). The cache key is a pure function of the source's identity (path +
/// size + modified time) and the proxy's target dimensions, so a changed source or a different tier produces a
/// different file and a stale proxy is never reused.
/// </summary>
public static class ProxyCache
{
    /// <summary>The per-user proxy cache directory (created on demand by the caller). Honours a
    /// <c>SPROCKET_PROXY_DIR</c> override (used by tests / portable installs).</summary>
    public static string Directory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SPROCKET_PROXY_DIR");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(AppContext.BaseDirectory, "cache");
        return Path.Combine(baseDir, "Sprocket", "proxies");
    }

    /// <summary>Total size in bytes of everything in the proxy cache (0 when the directory is absent).</summary>
    public static long SizeBytes()
    {
        try
        {
            var dir = new DirectoryInfo(Directory());
            if (!dir.Exists)
                return 0;
            long total = 0;
            foreach (FileInfo file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                total += file.Length;
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Best-effort delete of every file in the proxy cache (the Preferences "Clear proxy cache" action,
    /// PLAN.md step 38). Files that are in use (e.g. a proxy the current session is playing) are skipped;
    /// the directory itself is kept. Proxies regenerate on demand, so clearing is always safe. Returns the
    /// number of files deleted.
    /// </summary>
    public static int DeleteAll()
    {
        var dir = new DirectoryInfo(Directory());
        if (!dir.Exists)
            return 0;

        int deleted = 0;
        foreach (FileInfo file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or locked — leave it; it will be reused or aged out naturally.
            }
        }
        return deleted;
    }

    /// <summary>
    /// A stable file name for the proxy of the source at <paramref name="absolutePath"/> (whose on-disk identity
    /// is <paramref name="length"/> bytes / <paramref name="lastWriteUtcTicks"/>) at the given target dimensions.
    /// Pure: the same inputs always yield the same name, and any change to the source bytes or the target size
    /// changes it — so a re-imported / edited source rebuilds rather than reusing a stale proxy.
    /// </summary>
    public static string KeyFileName(string absolutePath, long length, long lastWriteUtcTicks, int targetWidth, int targetHeight)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);

        // Normalise the path so case/separator differences on the same file don't fork the cache.
        string normalized = absolutePath.Replace('\\', '/').ToLowerInvariant();
        string material = string.Create(CultureInfo.InvariantCulture,
            $"{normalized}|{length}|{lastWriteUtcTicks}|{targetWidth}x{targetHeight}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var sb = new StringBuilder(hash.Length * 2 + 4);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        sb.Append(".mp4");
        return sb.ToString();
    }
}
