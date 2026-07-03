using System;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sprocket.App;

/// <summary>
/// Application-level (user-scoped) preferences, persisted separately from any project document
/// (PLAN.md step 38). Additive by construction: every member has a constructor default, so an old
/// settings file deserializes cleanly after new fields are added and unknown fields are ignored.
/// </summary>
/// <param name="ExportTitle">Default value for the export dialog's Title metadata field ("" = none).</param>
/// <param name="ExportAuthor">Default value for the export dialog's Author metadata field ("" = none).</param>
/// <param name="ExportCopyright">Default value for the export dialog's Copyright metadata field ("" = none).</param>
/// <param name="ExportComment">Default value for the export dialog's Comment metadata field ("" = none).</param>
/// <param name="AutosaveIntervalSeconds">Autosave debounce interval in seconds (step 20's fixed 5 s made tunable).</param>
/// <param name="McpEnabled">Whether the loopback MCP server runs. Off by default — enabling it makes the
/// open project inspectable and editable by local AI clients.</param>
/// <param name="McpPort">Loopback TCP port the MCP server listens on.</param>
/// <param name="McpRequireToken">Whether MCP requests must carry the bearer token. Off by default —
/// the server is loopback-only either way; the token additionally shuts out other local processes.</param>
/// <param name="McpToken">The bearer token, generated once when token auth is first enabled ("" = none yet).</param>
/// <param name="UpdateCheckEnabled">Whether the startup update check runs (PLAN.md step 45). Notify-only —
/// it never downloads or installs anything.</param>
/// <param name="UpdateChannelPolicy">Which releases the check may notify about — an
/// <see cref="Sprocket.App.UpdateChannelPolicy"/> name; unknown values fall back to MatchCurrentChannel.</param>
/// <param name="UpdateLastCheckedUtc">Round-trip UTC timestamp of the last successful check, for the
/// cross-launch throttle ("" = never checked).</param>
/// <param name="UpdateAvailableTag">Cached tag of the newest acceptable release found ("" = up to date).</param>
/// <param name="UpdateAvailableUrl">Cached release-page URL for <paramref name="UpdateAvailableTag"/>.</param>
/// <param name="UpdateAvailableAssetName">Cached name of this platform's download asset ("" = none matched).</param>
/// <param name="UpdateAvailableAssetUrl">Cached direct download URL for that asset ("" = none matched).</param>
/// <param name="UpdateDismissedTag">The release tag the user dismissed ("Skip This Version") — the badge
/// stays hidden for exactly that version, so a result never nags across startups.</param>
public sealed record UserSettings(
    string ExportTitle = "",
    string ExportAuthor = "",
    string ExportCopyright = "",
    string ExportComment = "",
    int AutosaveIntervalSeconds = 5,
    bool McpEnabled = false,
    int McpPort = UserSettingsStore.DefaultMcpPort,
    bool McpRequireToken = false,
    string McpToken = "",
    bool UpdateCheckEnabled = true,
    string UpdateChannelPolicy = nameof(Sprocket.App.UpdateChannelPolicy.MatchCurrentChannel),
    string UpdateLastCheckedUtc = "",
    string UpdateAvailableTag = "",
    string UpdateAvailableUrl = "",
    string UpdateAvailableAssetName = "",
    string UpdateAvailableAssetUrl = "",
    string UpdateDismissedTag = "");

/// <summary>
/// The pure, headlessly-tested (de)serialization and validation logic for <see cref="UserSettings"/> —
/// the file location itself is owned by <see cref="UserSettingsFile"/>, mirroring the
/// <c>ExportPresetStore</c> / <c>UserExportPresets</c> split.
/// </summary>
public static class UserSettingsStore
{
    /// <summary>Default MCP listen port (unregistered, above the well-known/ephemeral hot zones).</summary>
    public const int DefaultMcpPort = 41008;

    /// <summary>Autosave interval bounds in seconds.</summary>
    public const int MinAutosaveSeconds = 1;

    /// <inheritdoc cref="MinAutosaveSeconds"/>
    public const int MaxAutosaveSeconds = 600;

    /// <summary>MCP port bounds (non-privileged TCP range).</summary>
    public const int MinPort = 1024;

    /// <inheritdoc cref="MinPort"/>
    public const int MaxPort = 65535;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Serializes settings to indented JSON.</summary>
    public static string Serialize(UserSettings settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>
    /// Deserializes settings, falling back to defaults on missing/garbage input, and always returns a
    /// <see cref="Clamp"/>ed value (a hand-edited file can't smuggle an out-of-range port or interval in).
    /// </summary>
    public static UserSettings Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new UserSettings();
        try
        {
            return Clamp(JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings());
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
    }

    /// <summary>Clamps numeric fields into their valid ranges and normalizes null strings to "".</summary>
    public static UserSettings Clamp(UserSettings settings) => settings with
    {
        ExportTitle = settings.ExportTitle ?? "",
        ExportAuthor = settings.ExportAuthor ?? "",
        ExportCopyright = settings.ExportCopyright ?? "",
        ExportComment = settings.ExportComment ?? "",
        AutosaveIntervalSeconds = Math.Clamp(settings.AutosaveIntervalSeconds, MinAutosaveSeconds, MaxAutosaveSeconds),
        McpPort = Math.Clamp(settings.McpPort, MinPort, MaxPort),
        McpToken = settings.McpToken ?? "",
        // Normalize the channel policy to a known enum name — a hand-edited value degrades to the default.
        UpdateChannelPolicy = UpdateCheck.ParsePolicy(settings.UpdateChannelPolicy).ToString(),
        UpdateLastCheckedUtc = settings.UpdateLastCheckedUtc ?? "",
        UpdateAvailableTag = settings.UpdateAvailableTag ?? "",
        UpdateAvailableUrl = settings.UpdateAvailableUrl ?? "",
        UpdateAvailableAssetName = settings.UpdateAvailableAssetName ?? "",
        UpdateAvailableAssetUrl = settings.UpdateAvailableAssetUrl ?? "",
        UpdateDismissedTag = settings.UpdateDismissedTag ?? "",
    };

    /// <summary>Generates a fresh MCP bearer token: 32 random bytes, base64url (no padding, URL/header safe).</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
