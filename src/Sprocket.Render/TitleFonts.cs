using SkiaSharp;

namespace Sprocket.Render;

/// <summary>
/// The bundled title typefaces (PLAN.md step 40). Titles must rasterise <b>byte-identically across
/// Windows / Linux / macOS</b> so preview == export and the cross-OS golden-frame tests hold
/// (ARCHITECTURE.md §5); system-font substitution differs per OS, so title text always draws from this
/// embedded set (Liberation, SIL OFL 1.1 — <c>Fonts/LICENSE-Liberation.txt</c>), never a system font.
/// Typefaces are loaded once from the embedded resources via <see cref="SKTypeface.FromStream(Stream, int)"/>
/// and cached for the process lifetime — no per-frame work. The Inspector's family picker lists
/// <see cref="Families"/>; opt-in system fonts (non-delivery use) are a later add.
/// </summary>
public static class TitleFonts
{
    /// <summary>The default family used when a title names none (or an unknown one).</summary>
    public const string DefaultFamily = "Liberation Sans";

    /// <summary>The bundled family names, in picker display order.</summary>
    public static IReadOnlyList<string> Families { get; } =
        ["Liberation Sans", "Liberation Serif", "Liberation Mono"];

    private static readonly Dictionary<string, string> FileStems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Liberation Sans"] = "LiberationSans",
        ["Liberation Serif"] = "LiberationSerif",
        ["Liberation Mono"] = "LiberationMono",
    };

    private static readonly Dictionary<string, SKTypeface> Cache = new();
    private static readonly Lock Gate = new();

    /// <summary>
    /// The cached typeface for a family/style, falling back to <see cref="DefaultFamily"/> for an unknown
    /// family name (a project naming an uninstalled/renamed family still renders, §15). Callers must not
    /// dispose the returned typeface — it is shared for the process lifetime.
    /// </summary>
    public static SKTypeface Get(string? family, bool bold, bool italic)
    {
        string stem = FileStems.TryGetValue(family ?? "", out string? mapped)
            ? mapped
            : FileStems[DefaultFamily];
        string style = (bold, italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            (false, false) => "Regular",
        };
        string resource = $"{stem}-{style}.ttf";

        lock (Gate)
        {
            if (Cache.TryGetValue(resource, out SKTypeface? cached))
                return cached;

            using Stream? stream = typeof(TitleFonts).Assembly
                .GetManifestResourceStream($"Sprocket.Render.Fonts.{resource}");
            // FromStream takes ownership of a seekable copy; a missing resource (broken build) degrades to
            // Skia's default typeface rather than crashing the render.
            SKTypeface typeface = (stream is null ? null : SKTypeface.FromStream(stream)) ?? SKTypeface.Default;
            Cache[resource] = typeface;
            return typeface;
        }
    }
}
