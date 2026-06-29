using Sprocket.Core.Model;

namespace Sprocket.Persistence;

/// <summary>
/// Autosave + crash-recovery support (PLAN.md step 20). The project is plain data and already serializes
/// (<see cref="ProjectSerializer"/>), so autosave is a periodic, atomic sidecar write driven off the dirty
/// signal; recovery on launch compares the autosave against the last clean save. Writes are atomic (temp file →
/// promote), like the proxy / render-cache stores, so a crash mid-write never corrupts the recovery file.
/// </summary>
public static class Autosave
{
    /// <summary>The autosave sidecar suffix appended to a project file path.</summary>
    public const string Suffix = ".autosave.json";

    /// <summary>The autosave sidecar path for a saved project (e.g. <c>foo.sprocket.json.autosave.json</c>).</summary>
    public static string SidecarPath(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectFilePath);
        return projectFilePath + Suffix;
    }

    /// <summary>
    /// Atomically writes <paramref name="project"/> to <paramref name="autosavePath"/>: serialize to a sibling
    /// temp file, then promote it over the target in one move. <paramref name="projectFilePath"/> (the real
    /// project file, when known) lets media paths stay relative so a recovered file relinks like a normal save.
    /// </summary>
    public static void Write(Project project, string autosavePath, string? projectFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        WriteText(ProjectSerializer.Serialize(project, projectFilePath), autosavePath);
    }

    /// <summary>
    /// Atomically writes already-serialized project JSON to <paramref name="autosavePath"/>. Lets a caller
    /// snapshot the project on the UI thread (where the model lives, ARCHITECTURE.md §8) and then push the slow
    /// disk write to a background thread without racing a concurrent edit.
    /// </summary>
    public static void WriteText(string json, string autosavePath)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(autosavePath);

        string tempPath = autosavePath + ".tmp";
        File.WriteAllText(tempPath, json);
        // Move with overwrite is atomic on the same volume — a reader sees either the old file or the new, never
        // a half-written one.
        File.Move(tempPath, autosavePath, overwrite: true);
    }

    /// <summary>Deletes the autosave sidecar if it exists (e.g. after a clean save / on accepting a recovery).
    /// Silently ignores a missing file.</summary>
    public static void Delete(string autosavePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(autosavePath);
        try
        {
            File.Delete(autosavePath);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to delete.
        }
    }
}

/// <summary>
/// The pure decision of whether to offer crash recovery on launch (PLAN.md step 20), separated from file I/O so
/// it is unit-testable headlessly. Recovery is offered when an autosave exists and it is newer than the clean
/// save — or there is no clean save at all (a crash before the first manual save).
/// </summary>
public static class AutosaveRecovery
{
    /// <summary>The filesystem facts the decision depends on. Timestamps are last-write times (UTC).</summary>
    /// <param name="AutosaveExists">Whether an autosave sidecar is present.</param>
    /// <param name="AutosaveTimeUtc">The autosave's last-write time.</param>
    /// <param name="SavedExists">Whether the clean project file is present.</param>
    /// <param name="SavedTimeUtc">The clean project file's last-write time.</param>
    public readonly record struct State(
        bool AutosaveExists, DateTime AutosaveTimeUtc, bool SavedExists, DateTime SavedTimeUtc);

    /// <summary>Whether to offer recovery from the autosave: it exists and is strictly newer than the clean
    /// save (or there is no clean save).</summary>
    public static bool ShouldOffer(State state) =>
        state.AutosaveExists && (!state.SavedExists || state.AutosaveTimeUtc > state.SavedTimeUtc);
}
