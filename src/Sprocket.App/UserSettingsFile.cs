using System;
using System.IO;
using System.Text.Json;

namespace Sprocket.App;

/// <summary>
/// Owns the on-disk location of the user-scoped application settings —
/// <c>%AppData%/Sprocket/settings.json</c> (roaming, beside <c>window.json</c>), mirroring
/// <see cref="WindowStateStore"/>. All (de)serialization/validation logic lives in the headlessly-tested
/// <see cref="UserSettingsStore"/>; losing this file is harmless (defaults apply), so reads and writes
/// swallow IO errors.
/// </summary>
internal static class UserSettingsFile
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sprocket", "settings.json");

    /// <summary>Loads the stored settings, or defaults when absent/unreadable.</summary>
    public static UserSettings Load()
    {
        try
        {
            return UserSettingsStore.Deserialize(File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    /// <summary>Persists the settings, best-effort.</summary>
    public static void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, UserSettingsStore.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
}
