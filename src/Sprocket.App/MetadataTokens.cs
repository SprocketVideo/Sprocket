using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sprocket.App;

/// <summary>
/// The values a <see cref="MetadataTokens"/> template is resolved against (PLAN.md step 38). Held as a
/// readonly struct so callers pass live values (<see cref="Environment.UserName"/>, the current year, the
/// project name) in — the resolver itself stays a pure, deterministic function of its inputs.
/// </summary>
/// <param name="Username">Replaces <c>{username}</c> — typically <see cref="Environment.UserName"/>.</param>
/// <param name="Year">Replaces <c>{year}</c> — typically <c>DateTime.Now.Year</c>.</param>
/// <param name="Project">Replaces <c>{project}</c> — the current document/project name ("" when untitled).</param>
/// <param name="Date">Replaces <c>{date}</c> — an ISO <c>yyyy-MM-dd</c> string, typically today's date.</param>
public readonly record struct MetadataTokenContext(string Username, int Year, string Project, string Date);

/// <summary>
/// Resolves <c>{token}</c> placeholders in the export-metadata default templates (PLAN.md step 38) — so a
/// stored default like <c>© {year} {username}</c> becomes <c>© 2026 Jane</c> when the Export dialog prefills
/// its boxes. Kept pure/headlessly-tested alongside <see cref="PreferencesFormat"/> and
/// <see cref="UserSettingsStore"/>; the time/user/project values are supplied by the caller via
/// <see cref="MetadataTokenContext"/>.
/// </summary>
public static partial class MetadataTokens
{
    // A brace-delimited token; unknown names are left as-is so an unrecognized {tag} survives verbatim.
    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    // Runs of whitespace left behind after a token resolves to "" are collapsed so "© {year} {username}"
    // with an empty username doesn't leave a double space or trailing separator.
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ExtraWhitespace();

    /// <summary>
    /// Replaces the known tokens (<c>{username}</c>, <c>{year}</c>, <c>{project}</c>, <c>{date}</c>,
    /// case-insensitive) in <paramref name="template"/>, leaving any unrecognized <c>{tag}</c> intact. When a
    /// token resolves to an empty string the surrounding whitespace is collapsed and the result trimmed, so a
    /// template never yields a dangling separator or leading/trailing space. Never throws.
    /// </summary>
    public static string Resolve(string? template, in MetadataTokenContext ctx)
    {
        if (string.IsNullOrEmpty(template))
            return "";

        MetadataTokenContext local = ctx;
        string replaced = TokenPattern().Replace(template, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "username" => local.Username ?? "",
            "year" => local.Year.ToString(CultureInfo.InvariantCulture),
            "project" => local.Project ?? "",
            "date" => local.Date ?? "",
            _ => match.Value, // unknown token: keep verbatim
        });

        return ExtraWhitespace().Replace(replaced, " ").Trim();
    }
}
