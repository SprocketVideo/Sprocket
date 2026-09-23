using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sprocket.App;

/// <summary>
/// The per-project, per-user view state a project reopens with: pane splitter sizes and visibility, the timeline
/// zoom and the active monitor tab. Deliberately <b>not</b> part of the <c>.sprocket.json</c> document — a
/// project file may be shared, and one user's panel arrangement is not project content (Premiere/Resolve keep
/// workspaces per user too). Every parameter has a default, so the format is additive like
/// <see cref="UserSettings"/>: a file written before a field existed loads with that field's default.
/// </summary>
/// <param name="ProjectWidth">Width (px) of the Project (bin) column — its last visible width while hidden.</param>
/// <param name="InspectorWidth">Width (px) of the Inspector column — its last visible width while hidden.</param>
/// <param name="TimelineHeight">Height (px) of the timeline row.</param>
/// <param name="ProjectVisible">View ▸ Show Project.</param>
/// <param name="InspectorVisible">View ▸ Show Inspector.</param>
/// <param name="TimelinePxPerSecond">Timeline zoom, in pixels per second of timeline.</param>
/// <param name="SourceMonitorActive">Whether the Source tab (rather than Program) was showing.</param>
/// <param name="SourceMediaPath">The absolute path of the media loaded in the Source monitor, if any; restored
/// only when it still matches a bin item.</param>
/// <param name="ProjectPath">The project the layout belongs to (diagnostic only — the file name is the key).</param>
internal sealed record ProjectLayout(
    double ProjectWidth = ProjectLayoutStore.DefaultProjectWidth,
    double InspectorWidth = ProjectLayoutStore.DefaultInspectorWidth,
    double TimelineHeight = ProjectLayoutStore.DefaultTimelineHeight,
    bool ProjectVisible = true,
    bool InspectorVisible = true,
    double TimelinePxPerSecond = ProjectLayoutStore.DefaultPxPerSecond,
    bool SourceMonitorActive = false,
    string? SourceMediaPath = null,
    string? ProjectPath = null);

/// <summary>
/// (De)serialization, validation and on-disk location for <see cref="ProjectLayout"/>. One small JSON file per
/// project under <c>%LocalAppData%/Sprocket/layouts</c> (local, not roaming: pixel sizes are machine-specific),
/// named by a hash of the project's path. Losing a layout is harmless, so IO failures are swallowed.
/// </summary>
internal static class ProjectLayoutStore
{
    public const double DefaultProjectWidth = 240;
    public const double DefaultInspectorWidth = 300;
    public const double DefaultTimelineHeight = 240;
    public const double DefaultPxPerSecond = TimelineControl.DefaultPxPerSecond;

    /// <summary>Pane size bounds — the floor keeps a restored pane grabbable, the ceiling stops a layout saved
    /// on a large monitor from pushing the other panes off a small one.</summary>
    public const double MinPaneSize = 120;

    /// <inheritdoc cref="MinPaneSize"/>
    public const double MaxPaneSize = 2000;

    /// <summary>The Inspector column's XAML <c>MinWidth</c>.</summary>
    public const double MinInspectorWidth = 192;

    /// <summary>How many layout files are kept; the least recently written beyond this are pruned on save.</summary>
    public const int MaxLayouts = 100;

    private const int FormatVersion = 1;
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The per-user layouts directory. Honours a <c>SPROCKET_LAYOUTS_DIR</c> override (tests / portable
    /// installs), mirroring <c>SPROCKET_THUMBS_DIR</c>.</summary>
    public static string DefaultDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SPROCKET_LAYOUTS_DIR");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(AppContext.BaseDirectory, "cache");
        return Path.Combine(baseDir, "Sprocket", "layouts");
    }

    /// <summary>A stable file key (lower-case SHA-256 hex) for a project path. The path is made absolute and
    /// case/separator-normalized so the same file always maps to the same layout.</summary>
    public static string KeyFor(string projectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        string normalized = Path.GetFullPath(projectPath).Replace('\\', '/').ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"v{FormatVersion}|{normalized}")));
    }

    /// <summary>Serializes a layout to indented JSON.</summary>
    public static string Serialize(ProjectLayout layout) => JsonSerializer.Serialize(layout, JsonOptions);

    /// <summary>Deserializes and <see cref="Clamp"/>s a layout; <see langword="null"/> for missing/garbage input.</summary>
    public static ProjectLayout? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ProjectLayout>(json) is { } layout ? Clamp(layout) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Clamps sizes and zoom into range; non-finite values fall back to the defaults.</summary>
    public static ProjectLayout Clamp(ProjectLayout layout) => layout with
    {
        ProjectWidth = ClampOr(layout.ProjectWidth, MinPaneSize, MaxPaneSize, DefaultProjectWidth),
        InspectorWidth = ClampOr(layout.InspectorWidth, MinInspectorWidth, MaxPaneSize, DefaultInspectorWidth),
        TimelineHeight = ClampOr(layout.TimelineHeight, MinPaneSize, MaxPaneSize, DefaultTimelineHeight),
        TimelinePxPerSecond = ClampOr(layout.TimelinePxPerSecond,
            TimelineControl.MinPxPerSecond, TimelineControl.MaxPxPerSecond, DefaultPxPerSecond),
        SourceMediaPath = string.IsNullOrWhiteSpace(layout.SourceMediaPath) ? null : layout.SourceMediaPath,
    };

    private static double ClampOr(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>Loads the stored layout for <paramref name="projectPath"/>, or <see langword="null"/> if none.</summary>
    public static ProjectLayout? Load(string projectPath, string? directory = null)
    {
        try
        {
            string path = PathFor(directory ?? DefaultDirectory(), projectPath);
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    /// <summary>Persists the layout for <paramref name="projectPath"/> (atomically, best-effort), then prunes the
    /// oldest layouts beyond <see cref="MaxLayouts"/>.</summary>
    public static void Save(string projectPath, ProjectLayout layout, string? directory = null)
    {
        try
        {
            string dir = directory ?? DefaultDirectory();
            Directory.CreateDirectory(dir);
            string path = PathFor(dir, projectPath);
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serialize(Clamp(layout with { ProjectPath = Path.GetFullPath(projectPath) })));
            File.Move(temp, path, overwrite: true);
            Prune(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { }
    }

    private static string PathFor(string directory, string projectPath) =>
        Path.Combine(directory, KeyFor(projectPath) + Extension);

    private static void Prune(string directory)
    {
        var stale = new DirectoryInfo(directory).GetFiles("*" + Extension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxLayouts);
        foreach (FileInfo f in stale)
        {
            try { f.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
