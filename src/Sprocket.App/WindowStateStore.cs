using System;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;

namespace Sprocket.App;

/// <summary>
/// Persists a sliver of window UI state — currently just whether the shell was last maximized — to a small JSON
/// file under the user's per-platform application-data folder, so a relaunch reopens the way the user left it.
/// This is presentation chrome, intentionally kept out of <c>Sprocket.Persistence</c> (which owns the project
/// document); losing it is harmless, so every read/write swallows IO errors and falls back to the default.
/// </summary>
internal static class WindowStateStore
{
    private static readonly string SettingsPath = BuildSettingsPath();

    private static string BuildSettingsPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sprocket");
        return Path.Combine(dir, "window.json");
    }

    private sealed record Settings(bool Maximized);

    /// <summary>Reads the remembered window state; returns <see cref="WindowState.Normal"/> if none is stored.</summary>
    public static WindowState Load()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) is { } s)
                return s.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        return WindowState.Normal;
    }

    /// <summary>Records whether the window is maximized (minimized is treated as not-maximized).</summary>
    public static void Save(WindowState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new Settings(state == WindowState.Maximized)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
}
