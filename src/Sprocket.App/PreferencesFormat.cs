using System;
using System.Globalization;

namespace Sprocket.App;

/// <summary>
/// The Preferences dialog's pure decision/formatting logic (PLAN.md step 38), split out of the code-built
/// dialog so it is headlessly testable — the <see cref="StatusBarFormat"/> pattern.
/// </summary>
public static class PreferencesFormat
{
    /// <summary>Human cache size: "1.2 GB" / "340 MB" / "12 KB" / "0 bytes".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 0 => "0 bytes",
        0 => "0 bytes",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024L * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };

    /// <summary>
    /// The paste-ready Claude Code command that connects a client to the running MCP server — the
    /// Preferences dialog's "Copy setup command" payload. Includes the Authorization header only when
    /// token auth is required.
    /// </summary>
    public static string McpSetupCommand(int port, bool requireToken, string token)
    {
        string url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/mcp");
        return requireToken && !string.IsNullOrEmpty(token)
            ? $"claude mcp add --transport http sprocket {url} --header \"Authorization: Bearer {token}\""
            : $"claude mcp add --transport http sprocket {url}";
    }

    /// <summary>Parses a port text-box value; null when not an integer (the dialog then keeps the old value).</summary>
    public static int? ParsePort(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ? port : null;

    /// <summary>Parses the autosave-interval text-box value; null when not an integer.</summary>
    public static int? ParseSeconds(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : null;

    /// <summary>
    /// Resolves the token to persist given the require-token toggle: keeps an existing token, generates one
    /// the first time the toggle is enabled, and preserves (not clears) the token when disabled so
    /// re-enabling doesn't invalidate already-configured clients.
    /// </summary>
    public static string ResolveToken(bool requireToken, string existingToken, Func<string> newToken) =>
        requireToken && string.IsNullOrEmpty(existingToken) ? newToken() : existingToken;
}
