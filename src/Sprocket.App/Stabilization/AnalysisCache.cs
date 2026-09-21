using System;
using System.IO;
using Sprocket.Core.Stabilization;

namespace Sprocket.App.Stabilization;

/// <summary>
/// Locates, reads, and writes files in the local motion-analysis cache (plan/features/stabilization.md phase 5).
/// A recovered <see cref="MotionTrack"/> is a <b>local, regenerable</b> artifact — kept in a per-user cache dir,
/// never in the project file, and always safely discardable (the same store family as the proxy and render
/// caches, ARCHITECTURE.md §20). The cache key is the content hash of an <see cref="AnalysisKey"/> (source
/// identity + Detailed flag + bucketed source range), so the same footage analysed in two projects shares one
/// entry, Resolve/FCP style, and a changed source or a different analysis range produces a different file — a
/// stale track is never reused.
/// </summary>
public static class AnalysisCache
{
    /// <summary>The per-user analysis cache directory (created on demand by the caller). Honours a
    /// <c>SPROCKET_ANALYSIS_DIR</c> override (used by tests / portable installs).</summary>
    public static string Directory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SPROCKET_ANALYSIS_DIR");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(AppContext.BaseDirectory, "cache");
        return Path.Combine(baseDir, "Sprocket", "analysis");
    }

    /// <summary>The full cache path for <paramref name="key"/> (whose file name is a content hash).</summary>
    public static string PathFor(AnalysisKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Path.Combine(Directory(), key.CacheFileName);
    }

    /// <summary>
    /// Reads the cached track for <paramref name="key"/>, or <see langword="null"/> when there is none (or the file
    /// is unreadable / from an incompatible build — <see cref="MotionTrack.Read"/> throws, which is treated as a
    /// miss so the track is re-analysed rather than replayed wrongly). Never throws.
    /// </summary>
    public static MotionTrack? TryRead(AnalysisKey key)
    {
        string path = PathFor(key);
        try
        {
            if (!File.Exists(path))
                return null;
            using FileStream stream = File.OpenRead(path);
            return MotionTrack.Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="track"/> as the cache entry for <paramref name="key"/>, atomically (temp file +
    /// move) so a crash or a concurrent read never sees a half-written track. Best-effort: an I/O failure is
    /// swallowed — the analysis simply isn't cached and re-runs next time.
    /// </summary>
    public static void Write(AnalysisKey key, MotionTrack track)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(track);

        string path = PathFor(key);
        try
        {
            System.IO.Directory.CreateDirectory(Directory());
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream stream = File.Create(temp))
                track.Write(stream);
            // Move is atomic on the same volume; overwrite a prior entry (a re-analyse of the same key).
            File.Move(temp, path, overwrite: true);
            TryDeleteFile(temp); // no-op after a successful move; cleans up a leftover if Move threw post-write
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not cacheable right now (disk full, locked dir) — leave it; the track is still returned to the caller.
        }
    }

    /// <summary>Total size in bytes of everything in the analysis cache (0 when the directory is absent).</summary>
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
    /// Best-effort delete of every file in the analysis cache (the Preferences "Clear analysis cache" action,
    /// phase 6). Files in use are skipped; the directory itself is kept. Tracks regenerate on demand, so clearing
    /// is always safe. Returns the number of files deleted.
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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }
}
