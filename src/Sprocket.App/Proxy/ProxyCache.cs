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
