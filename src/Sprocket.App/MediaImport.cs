using System;
using System.IO;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Media;

namespace Sprocket.App;

/// <summary>
/// Imports a source file into a project's <see cref="MediaPool"/> (PLAN.md step 16b): probes it via
/// <see cref="MediaSource"/> for format/duration and adds a <see cref="MediaRef"/> through the command stack
/// (<see cref="AddMediaCommand"/>), so the import is undoable and flips the dirty indicator (step 10). This is
/// the one place the App's file-import UI touches the Media layer; the bin / thumbnail / badge path (step 15)
/// then lights up for the imported source.
/// </summary>
internal static class MediaImport
{
    /// <summary>
    /// Probes <paramref name="path"/> and adds it to the project (deduplicating by absolute path — re-importing
    /// the same file returns the existing reference rather than a second copy). Returns the imported (or
    /// existing) <see cref="MediaRef"/>, or <see langword="null"/> if the file can't be opened/probed.
    /// </summary>
    public static MediaRef? TryImport(Project project, EditHistory history, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        // Already imported? Match on the stored absolute path so a file isn't added twice.
        foreach (MediaRef existing in project.MediaPool.Items)
            if (string.Equals(existing.AbsolutePath, path, StringComparison.OrdinalIgnoreCase))
                return existing;

        ProbedMediaInfo info;
        try
        {
            using MediaSource probe = MediaSource.Open(path);
            info = probe.Info;
        }
        catch
        {
            return null; // not a media file we can open (§15) — caller reports it
        }

        var media = new MediaRef(MediaRefId.New(), path, info);
        history.Execute(new AddMediaCommand(project.MediaPool, media));
        return media;
    }
}
