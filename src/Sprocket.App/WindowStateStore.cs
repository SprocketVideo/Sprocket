using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace Sprocket.App;

/// <summary>
/// The live geometry of a shell window, handed to its replacement across a session swap (File ▸ New / Open /
/// Open Sample rebuild <see cref="MainWindow"/> — see <c>App.OnSessionRequested</c>). Without it the new window
/// would fall back to <see cref="WindowStateStore.Load"/> plus the XAML default size, so a window the user
/// maximized, resized or moved *since launch* visibly snaps back to 1280×800 centred the moment a project is
/// opened. The convention in leading editors is that opening a project never moves the window.
/// </summary>
/// <param name="State">The state to reopen in — <see cref="WindowState.Minimized"/> is never carried.</param>
/// <param name="StateBeforeFullScreen">Where View ▸ Full Screen should return to (Normal / Maximized).</param>
/// <param name="Position">Frame position, carried in every state so the window stays on its monitor.</param>
/// <param name="ClientSize">Client size, applied only when <paramref name="State"/> is
/// <see cref="WindowState.Normal"/> — while zoomed it describes the screen, not a restore rectangle.</param>
internal readonly record struct WindowPlacement(
    WindowState State, WindowState StateBeforeFullScreen, PixelPoint Position, Size ClientSize)
{
    /// <summary>The state a window in <paramref name="live"/> should hand on: a shell minimized at the moment of
    /// the swap reopens at the state it was minimized from, never as an already-minimized new window.</summary>
    public static WindowState StateToCarry(WindowState live, WindowState lastNonMinimized) =>
        live == WindowState.Minimized ? lastNonMinimized : live;

    /// <summary>The maximized-or-not the receiving window should persist on close. Full screen is transient and
    /// never persisted (<see cref="WindowStateStore"/>), so it resolves to its pre-fullscreen state.</summary>
    public WindowState PersistableState =>
        State == WindowState.FullScreen ? StateBeforeFullScreen : State;

    /// <summary>Whether <see cref="ClientSize"/> is a real restore rectangle worth applying — true only while
    /// Normal; maximized / full-screen sizes describe the screen.</summary>
    public bool CarriesSize => State == WindowState.Normal;

    /// <summary>Applies this placement to a not-yet-shown window. Position goes on first so the OS maximizes /
    /// full-screens onto the monitor the outgoing window was on; while zoomed the size is left at the XAML
    /// default, which then serves as the restore rectangle.</summary>
    public void ApplyTo(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual; // honour Position over the XAML CenterScreen
        window.Position = Position;
        if (CarriesSize)
        {
            window.Width = ClientSize.Width;
            window.Height = ClientSize.Height;
        }
        window.WindowState = State;
    }
}

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
