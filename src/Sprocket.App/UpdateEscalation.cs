using System;
using System.Globalization;

namespace Sprocket.App;

/// <summary>
/// How prominent the update badge should be, based on how long a known update has stayed uninstalled.
/// The badge deepens color the longer you defer (Chrome's approach) — signalling age without ever
/// interrupting. Ordered least → most urgent.
/// </summary>
internal enum UpdateTier
{
    /// <summary>Just appeared (&lt; 2 days): quiet muted dot, "Update {version}".</summary>
    Fresh,

    /// <summary>Been available a while (2–7 days): accent dot, "Update {version}".</summary>
    Aging,

    /// <summary>Overdue (≥ 7 days): amber dot, "Update recommended".</summary>
    Stale,
}

/// <summary>
/// The pure, headlessly-tested age → <see cref="UpdateTier"/> mapping for the status-bar update badge
/// (and the mirrored Help-menu dot). Kept a pure function of two timestamps so escalation is testable
/// without a clock; the caller supplies "now".
/// </summary>
internal static class UpdateEscalation
{
    /// <summary>Days available before the badge steps up from <see cref="UpdateTier.Fresh"/> to
    /// <see cref="UpdateTier.Aging"/> (Chrome uses ~2 days for its first step).</summary>
    public const double AgingAfterDays = 2;

    /// <summary>Days available before the badge steps up to <see cref="UpdateTier.Stale"/>.</summary>
    public const double StaleAfterDays = 7;

    /// <summary>
    /// The tier for an update first seen at <paramref name="firstSeen"/>, evaluated at
    /// <paramref name="now"/>. A future <paramref name="firstSeen"/> (clock skew) clamps to
    /// <see cref="UpdateTier.Fresh"/>.
    /// </summary>
    public static UpdateTier TierFor(DateTimeOffset firstSeen, DateTimeOffset now)
    {
        double days = (now - firstSeen).TotalDays;
        if (days >= StaleAfterDays)
            return UpdateTier.Stale;
        if (days >= AgingAfterDays)
            return UpdateTier.Aging;
        return UpdateTier.Fresh;
    }

    /// <summary>
    /// The tier from a persisted <see cref="UserSettings.UpdateFirstSeenUtc"/> string, evaluated at
    /// <paramref name="now"/>. A missing or unparseable timestamp degrades to <see cref="UpdateTier.Fresh"/>
    /// (treated as just-seen) so a hand-edited/garbage settings file can never throw.
    /// </summary>
    public static UpdateTier TierFor(string? firstSeenUtc, DateTimeOffset now) =>
        TryParse(firstSeenUtc, out DateTimeOffset firstSeen) ? TierFor(firstSeen, now) : UpdateTier.Fresh;

    /// <summary>Formats a timestamp for <see cref="UserSettings.UpdateFirstSeenUtc"/> (round-trip "O").</summary>
    public static string Format(DateTimeOffset when) => when.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the one-time first-run toast should fire for <paramref name="version"/>. Gated per-version
    /// (not per-session) so future launches stay quiet: fires only for a self-updatable install, for a real
    /// available version that isn't the skipped one, and only if it hasn't already been shown for that
    /// version. The caller persists <paramref name="version"/> as the shown tag when this returns true.
    /// </summary>
    public static bool ShouldToast(string? version, string? toastShownTag, string? dismissedTag, bool isInstalled)
    {
        if (!isInstalled || string.IsNullOrEmpty(version))
            return false;
        if (string.Equals(version, dismissedTag, StringComparison.Ordinal))
            return false;
        return !string.Equals(version, toastShownTag, StringComparison.Ordinal);
    }

    private static bool TryParse(string? s, out DateTimeOffset value)
    {
        if (!string.IsNullOrWhiteSpace(s) &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out value))
            return true;
        value = default;
        return false;
    }
}
