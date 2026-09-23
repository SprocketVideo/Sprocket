using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace Sprocket.Render;

/// <summary>
/// The user-file <c>.cube</c> LUTs behind the Creative LUT effect (plan/features/looks-browser.md) — the file-aware
/// sibling of <see cref="ColorLuts"/>. A LUT is read, parsed (<see cref="CubeLut.Parse"/>) and packed into its GPU
/// texture (<see cref="CubeLut.ToPackedImage"/>) once per path and shared by every draw and export worker; never
/// per frame. Loading is synchronous on first use so preview and export always render the same pixels (a
/// background load would let an export's first frames miss the look); <see cref="Preload"/> lets the UI warm a
/// path off the render thread as soon as it is chosen. A missing, oversized or malformed file is remembered as
/// failed — the effect passes through (§15) — until <see cref="Invalidate"/> (the user re-picks it).
/// </summary>
public static class CreativeLuts
{
    /// <summary>Largest accepted lattice (<c>LUT_3D_SIZE</c>). Creative LUTs ship at 17/33/65; 129 leaves room
    /// for high-precision tables while bounding the parse to ~25 MB.</summary>
    public const int MaxLatticeSize = 129;

    /// <summary>Largest accepted file. A 129³ table is ~55 MB of text at typical six-decimal precision.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>Retained-entry bound (a 65³ LUT is ~2 MB of texture). Evicted images are not disposed — a worker
    /// may still be drawing with one — they are released by the GC once unreferenced.</summary>
    public const int MaxEntries = 16;

    private sealed record Loaded(SKImage? Image, int Size, string? Error);

    private static readonly ConcurrentDictionary<string, Lazy<Loaded>> Entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The packed LUT texture + lattice size for <paramref name="path"/>, loading it on first use. Returns
    /// <see langword="false"/> for an empty path or a file that cannot be loaded. Callers must not dispose the
    /// image — it is shared.
    /// </summary>
    public static bool TryGet(string path, out SKImage image, out int size)
    {
        image = null!;
        size = 0;
        if (string.IsNullOrEmpty(path))
            return false;

        Lazy<Loaded> entry = Entries.GetOrAdd(path, p => new Lazy<Loaded>(() => Load(p), LazyThreadSafetyMode.ExecutionAndPublication));
        Loaded loaded = entry.Value;
        Trim(path);
        if (loaded.Image is null)
            return false;
        (image, size) = (loaded.Image, loaded.Size);
        return true;
    }

    /// <summary>Starts loading <paramref name="path"/> on the thread pool so the first frame that needs it does
    /// not pay for the parse. Safe to call repeatedly.</summary>
    public static void Preload(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _ = Task.Run(() => TryGet(path, out _, out _));
    }

    /// <summary>Whether <paramref name="path"/> was loaded and failed (for the Inspector's "unreadable" flag).</summary>
    public static bool HasFailed(string path) => Error(path) is not null;

    /// <summary>The load failure message for <paramref name="path"/>, or <see langword="null"/> if it loaded, has
    /// not been tried yet, or is still loading.</summary>
    public static string? Error(string path) =>
        Entries.TryGetValue(path, out Lazy<Loaded>? entry) && entry.IsValueCreated ? entry.Value.Error : null;

    /// <summary>Forgets <paramref name="path"/> so the next request reloads it (after a re-pick / replaced file).</summary>
    public static void Invalidate(string path) => Entries.TryRemove(path, out _);

    /// <summary>Drops every cached LUT (tests).</summary>
    public static void Clear() => Entries.Clear();

    private static void Trim(string keep)
    {
        if (Entries.Count <= MaxEntries)
            return;
        foreach (string key in Entries.Keys)
        {
            if (Entries.Count <= MaxEntries)
                break;
            if (!string.Equals(key, keep, StringComparison.Ordinal))
                Entries.TryRemove(key, out _);
        }
    }

    private static Loaded Load(string path)
    {
        try
        {
            // An asset path is stored absolute (EffectInstance.Assets); a relative one would resolve against
            // whatever the working directory happens to be, so it is refused rather than guessed at.
            if (!Path.IsPathFullyQualified(path))
                return new Loaded(null, 0, "The LUT path is not absolute.");
            CubeLut lut = CubeLut.Parse(ReadBounded(path), MaxLatticeSize);
            return new Loaded(lut.ToPackedImage(), lut.Size, null);
        }
        catch (Exception ex)
        {
            // Every failure — missing file, access, a non-.cube file, an absurd size — degrades to pass-through.
            return new Loaded(null, 0, ex.Message);
        }
    }

    /// <summary>Reads the file as text, refusing anything over <see cref="MaxFileBytes"/> — checked while reading,
    /// not just from the reported length, so a device or pipe path that reports 0 bytes cannot stream forever.</summary>
    private static string ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.CanSeek && stream.Length > MaxFileBytes)
            throw new InvalidDataException($"The LUT file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        char[] buffer = new char[64 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxFileBytes)
                throw new InvalidDataException($"The LUT file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        }
        return text.ToString();
    }
}
