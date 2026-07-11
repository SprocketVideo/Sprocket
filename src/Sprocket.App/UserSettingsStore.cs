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
/// <param name="UpdateCheckEnabled">Whether the startup update check runs (PLAN.md steps 36 + 45).
/// Only Velopack-installed builds can also download/apply; a portable build never checks.</param>
/// <param name="UpdateDismissedTag">The release version the user dismissed ("Skip This Version") — the
/// badge stays hidden for exactly that version, so a result never nags across startups.</param>
/// <param name="StillImageDefaultSeconds">Default on-timeline duration for a newly imported still image, in
/// seconds (PLAN.md step 42; the industry-convention default is 5 s). A still's media headroom is unbounded, so this is only
/// the initial drop length.</param>
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
    string UpdateDismissedTag = "",
    double StillImageDefaultSeconds = 5);

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

    /// <summary>Still-image default-duration bounds in seconds (PLAN.md step 42).</summary>
    public const double MinStillSeconds = 0.1;

    /// <inheritdoc cref="MinStillSeconds"/>
    public const double MaxStillSeconds = 3600;

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
        UpdateDismissedTag = settings.UpdateDismissedTag ?? "",
        StillImageDefaultSeconds = Math.Clamp(settings.StillImageDefaultSeconds, MinStillSeconds, MaxStillSeconds),
    };

    /// <summary>Generates a fresh MCP bearer token: 32 random bytes, base64url (no padding, URL/header safe).</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
