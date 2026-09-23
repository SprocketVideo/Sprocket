using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sprocket.Core.Model;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// The persistent, per-user store behind <see cref="ThumbnailService"/> (PLAN.md step 15, UI.md §3.3): encoded
/// poster / waveform / filmstrip PNGs keyed by source identity, so re-opening a project shows its bin thumbnails
/// without re-decoding every source. Like the proxy cache (<see cref="Proxy.ProxyCache"/>) it is a <b>local,
/// regenerable</b> artifact — never in the project file, always safe to discard — and lives beside it under the
/// per-user cache root (<c>…/Sprocket/thumbs</c>).
/// </summary>
/// <remarks>
/// The key is a pure function of (format version, kind, source path / sequence pattern, file size, last-write
/// time, output dimensions, frame count), so an edited source or a different tile size forks to a new file and a
/// stale thumbnail is never reused. All IO is best-effort: any failure degrades to "not cached" rather than
/// surfacing an error, since the thumbnail can always be regenerated.
/// </remarks>
public sealed class ThumbnailDiskCache
{
    /// <summary>Bumped whenever the rendered output changes (colours, layout, encoding), invalidating every entry.</summary>
    public const int FormatVersion = 1;

    /// <summary>Over this total size the background prune trims the cache…</summary>
    public const long PruneHighWaterBytes = 200L * 1024 * 1024;

    /// <summary>…deleting oldest-first until it is under this size.</summary>
    public const long PruneLowWaterBytes = 150L * 1024 * 1024;

    private const string Extension = ".png";

    private static readonly Lazy<ThumbnailDiskCache> DefaultInstance = new(() => new ThumbnailDiskCache(DefaultDirectory()));
    private int _pruneStarted;

    /// <summary>Creates a cache rooted at <paramref name="directory"/> (created on first write). Tests inject a
    /// temp directory; the app uses <see cref="Default"/>.</summary>
    public ThumbnailDiskCache(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    /// <summary>The process-wide cache at <see cref="DefaultDirectory"/>. Shared so the once-per-process prune
    /// runs once however many windows / projects open.</summary>
    public static ThumbnailDiskCache Default => DefaultInstance.Value;

    /// <summary>The directory this cache reads and writes.</summary>
    public string Directory { get; }

    /// <summary>The per-user thumbnail cache directory — a <c>thumbs</c> sibling of the proxy cache, resolved the
    /// same way (<see cref="Proxy.ProxyCache.Directory"/>). Honours a <c>SPROCKET_THUMBS_DIR</c> override (tests /
    /// portable installs).</summary>
    public static string DefaultDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SPROCKET_THUMBS_DIR");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(AppContext.BaseDirectory, "cache");
        return Path.Combine(baseDir, "Sprocket", "thumbs");
    }

    /// <summary>
    /// A stable cache key (lower-case SHA-256 hex) for one thumbnail. <paramref name="source"/> is the source's
    /// path (or, for an image sequence, its pattern + run); <paramref name="length"/> / <paramref name="lastWriteUtcTicks"/>
    /// are its on-disk identity. Pure: identical inputs always yield the same key, and any change to one of them
    /// changes it. The path is normalised so case / separator differences on the same file don't fork the cache.
    /// </summary>
    public static string Key(string kind, string source, long length, long lastWriteUtcTicks, int width, int height, int frames)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(source);

        string normalized = source.Replace('\\', '/').ToLowerInvariant();
        string material = string.Create(CultureInfo.InvariantCulture,
            $"v{FormatVersion}|{kind}|{normalized}|{length}|{lastWriteUtcTicks}|{width}x{height}x{frames}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// The cache key for <paramref name="media"/>'s <paramref name="kind"/> thumbnail, or <see langword="null"/>
    /// when the source can't be stat'd (offline / inaccessible) — the caller then skips the disk cache. An image
    /// sequence is identified by its first frame (<see cref="MediaRef.AbsolutePath"/>) plus its pattern and run,
    /// so a re-numbered / extended run forks the key even when the first frame is untouched.
    /// </summary>
    public static string? KeyFor(MediaRef media, string kind, int width, int height, int frames = 0)
    {
        ArgumentNullException.ThrowIfNull(media);
        try
        {
            var fi = new FileInfo(media.AbsolutePath);
            if (!fi.Exists)
                return null;
            string source = media.Kind == MediaKind.ImageSequence && media.SequencePattern is { } pattern
                ? string.Create(CultureInfo.InvariantCulture, $"{pattern}|{media.SequenceStartNumber}|{media.SequenceFrameCount}")
                : media.AbsolutePath;
            return Key(kind, source, fi.Length, fi.LastWriteTimeUtc.Ticks, width, height, frames);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The file path a key maps to (whether or not it exists).</summary>
    public string PathFor(string key) => Path.Combine(Directory, key + Extension);

    /// <summary>Reads the cached PNG bytes for <paramref name="key"/>, or <see langword="null"/> on a miss / IO
    /// failure. Read fully into memory so no file handle outlives the call (the prune can delete freely).</summary>
    public byte[]? TryRead(string key)
    {
        try
        {
            string path = PathFor(key);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="png"/> under <paramref name="key"/> atomically (temp file + move), so a
    /// crash or a concurrent reader never sees a torn file. Best-effort: IO failures are swallowed.</summary>
    public void TryWrite(string key, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        string? temp = null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = PathFor(key);
            temp = Path.Combine(Directory, $"{key}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
            temp = null;
        }
        catch
        {
            // Cache is optional — the thumbnail was still produced; it just regenerates next time.
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Starts the size-bounding prune on a background thread the first time it is called on this
    /// instance (so once per process for <see cref="Default"/>); later calls are no-ops.</summary>
    public void EnsurePruneStarted()
    {
        if (Interlocked.Exchange(ref _pruneStarted, 1) != 0)
            return;
        _ = Task.Run(() => Prune(PruneHighWaterBytes, PruneLowWaterBytes));
    }

    /// <summary>
    /// When the cache exceeds <paramref name="highWaterBytes"/>, deletes files oldest-first (by last-write time)
    /// until it is under <paramref name="lowWaterBytes"/>. Files that can't be deleted are skipped. Returns the
    /// number of files deleted. Never throws.
    /// </summary>
    public int Prune(long highWaterBytes, long lowWaterBytes)
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (!dir.Exists)
                return 0;

            FileInfo[] files = dir.GetFiles();
            long total = files.Sum(f => f.Length);
            if (total <= highWaterBytes)
                return 0;

            int deleted = 0;
            foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total < lowWaterBytes)
                    break;
                try
                {
                    long size = file.Length;
                    file.Delete();
                    total -= size;
                    deleted++;
                }
                catch
                {
                    // In use or locked — leave it.
                }
            }
            return deleted;
        }
        catch
        {
            return 0;
        }
    }
}
