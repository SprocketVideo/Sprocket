using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Persistence;

namespace Sprocket.App;

/// <summary>
/// Periodic, debounced autosave (PLAN.md step 20). Subscribes to <see cref="EditHistory.Changed"/> to learn the
/// document is dirty, then a timer writes the project to an atomic sidecar at most once per interval — so editing
/// never triggers a write per keystroke and the UI thread is never blocked on disk (the project is snapshotted on
/// the UI thread, where the model lives per ARCHITECTURE.md §8, and the file write happens on a background
/// thread). On a clean Save the host calls <see cref="ClearDirty"/> + deletes the sidecar so a stale autosave
/// can't keep offering recovery. A saved project autosaves beside its file; an untitled one autosaves to a
/// per-user slot so a crash before the first manual save is still recoverable.
/// </summary>
public sealed class AutosaveService : IDisposable
{
    private readonly Project _project;
    private readonly EditHistory _history;
    private readonly Func<string?> _projectPath; // the current saved file (null = untitled)
    private readonly DispatcherTimer _timer;
    private bool _dirty;
    private bool _disposed;

    /// <summary>The per-user autosave slot for an untitled (never-saved) project.</summary>
    public static string UntitledAutosavePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sprocket", "untitled" + Autosave.Suffix);

    public AutosaveService(Project project, EditHistory history, Func<string?> projectPath, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(projectPath);
        _project = project;
        _history = history;
        _projectPath = projectPath;

        _history.Changed += OnHistoryChanged;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    /// <summary>The autosave sidecar path for the current document (beside the project file, or the untitled slot).</summary>
    public string CurrentAutosavePath()
    {
        string? path = _projectPath();
        return path is null ? UntitledAutosavePath : Autosave.SidecarPath(path);
    }

    // A change only flags dirtiness; the timer coalesces a burst of edits into one write.
    private void OnHistoryChanged() => _dirty = true;

    /// <summary>Marks the document clean (call after a manual save) so the next tick doesn't re-write.</summary>
    public void ClearDirty() => _dirty = false;

    /// <summary>Writes the current autosave now if the document is dirty (e.g. a manual flush). No-op when clean.</summary>
    public void Flush()
    {
        if (_disposed || !_dirty)
            return;

        string autosavePath = CurrentAutosavePath();
        string? projectPath = _projectPath();

        string json;
        try
        {
            // Snapshot on the UI thread so the serialized text can't tear against a concurrent edit.
            json = ProjectSerializer.Serialize(_project, projectPath);
        }
        catch
        {
            return; // autosave is best-effort; never let it disrupt editing
        }
        _dirty = false;

        // Push the disk write off the UI thread; ensure the directory exists (the untitled slot lives under AppData).
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(autosavePath)!);
                Autosave.WriteText(json, autosavePath);
            }
            catch
            {
                // Swallow: a failed autosave must not crash the app; the user can still save manually.
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _history.Changed -= OnHistoryChanged;
    }
}
