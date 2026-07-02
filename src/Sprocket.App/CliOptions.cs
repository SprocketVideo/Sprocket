using System;

namespace Sprocket.App;

/// <summary>
/// The parsed command line for a normal (UI) launch: an optional media file to open, plus the
/// scripting flags that start the loopback MCP server for this session (PLAN.md step 38 follow-on).
/// Pure and headlessly tested (<c>CliOptionsTests</c>); the diagnostic flags that never reach the UI
/// (<c>--version</c> / <c>--ffmpeg-check</c> / <c>--probe</c>) are handled earlier, in <c>Program.Main</c>.
/// </summary>
/// <param name="MediaPath">The first non-flag argument — the media file to open (existence is checked
/// by <c>MediaBootstrap</c>, which degrades to an empty project), or <see langword="null"/>.</param>
/// <param name="McpRequested">Whether <c>--mcp</c> (or <c>--mcp-port</c>, which implies it) was given —
/// start the MCP server this session regardless of the persisted Preferences toggle. Session-only:
/// never written back to the settings file.</param>
/// <param name="McpPort">The <c>--mcp-port</c> override for this session, or <see langword="null"/> to
/// use the persisted port setting.</param>
/// <param name="Error">A human-readable parse failure (bad/missing <c>--mcp-port</c> value), or
/// <see langword="null"/>. On error the MCP flags are not honoured; the app still launches.</param>
public sealed record CliOptions(
    string? MediaPath = null,
    bool McpRequested = false,
    int? McpPort = null,
    string? Error = null)
{
    /// <summary>Starts the MCP server this session, using the persisted port/token settings.</summary>
    public const string McpFlag = "--mcp";

    /// <summary>Overrides the MCP listen port for this session (implies <see cref="McpFlag"/>).</summary>
    public const string McpPortFlag = "--mcp-port";

    /// <summary>Name of the env var that supplies a session-only MCP bearer token for scripted launches —
    /// tokens must never appear on the command line (argv is visible to other local processes).</summary>
    public const string McpTokenEnvVar = "SPROCKET_MCP_TOKEN";

    /// <summary>
    /// The MCP settings actually applied at launch: <paramref name="persisted"/> overlaid with this
    /// command line's session-only scripting flags and the <paramref name="envToken"/> bearer token
    /// (from <see cref="McpTokenEnvVar"/>). Nothing here is written back to disk, and a later
    /// Preferences apply supersedes the overlay.
    /// </summary>
    public UserSettings ApplyTo(UserSettings persisted, string? envToken = null)
    {
        UserSettings settings = persisted;
        if (McpRequested && Error is null)
            settings = settings with { McpEnabled = true, McpPort = McpPort ?? persisted.McpPort };
        if (!string.IsNullOrEmpty(envToken))
            settings = settings with { McpRequireToken = true, McpToken = envToken };
        return settings;
    }

    /// <summary>
    /// Parses the launch arguments. Unknown <c>--flags</c> are ignored (never mistaken for a media
    /// path) so future flags stay backward-compatible; the first bare argument is the media path.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        string? mediaPath = null;
        bool mcp = false;
        int? port = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, McpFlag, StringComparison.Ordinal))
            {
                mcp = true;
            }
            else if (string.Equals(arg, McpPortFlag, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int p))
                    return new CliOptions(Error: $"{McpPortFlag} requires a port number");
                if (p is < UserSettingsStore.MinPort or > UserSettingsStore.MaxPort)
                    return new CliOptions(
                        Error: $"{McpPortFlag} must be {UserSettingsStore.MinPort}-{UserSettingsStore.MaxPort} (got {p})");
                port = p;
                mcp = true;
                i++; // consume the value
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // Unknown flag: ignore rather than treat as a media path (or fail a future-version launch).
            }
            else
            {
                mediaPath ??= arg;
            }
        }

        return new CliOptions(mediaPath, mcp, port);
    }
}
