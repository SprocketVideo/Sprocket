using Sprocket.Core.Commands;
using Sprocket.Core.Model;

namespace Sprocket.Mcp;

/// <summary>
/// The seam between the MCP tool layer and the live editor session (PLAN.md step 38). The App implements
/// this over its <c>MainWindow</c>-owned project / history / engine; Sprocket.Mcp never sees Avalonia.
/// </summary>
public interface IEditorSession
{
    /// <summary>
    /// Runs <paramref name="fn"/> on the thread that owns the model and <see cref="EditHistory"/>
    /// (ARCHITECTURE.md §8 — the UI thread in the App) and returns its result. The whole callback executes
    /// atomically with respect to user edits, so a tool's resolve-ids → build-command → execute sequence
    /// can never interleave with a concurrent mutation.
    /// </summary>
    Task<T> OnModelThreadAsync<T>(Func<IEditorApi, T> fn);
}

/// <summary>
/// Editor operations available inside an <see cref="IEditorSession.OnModelThreadAsync{T}"/> callback.
/// Everything here is model-thread-only by construction. State-changing members route through
/// <see cref="History"/> (never mutate the model directly — §4 / PLAN.md step 10).
/// </summary>
public interface IEditorApi
{
    /// <summary>The open project.</summary>
    Project Project { get; }

    /// <summary>The session's single undo/redo command stack.</summary>
    EditHistory History { get; }

    /// <summary>Absolute path of the project file, or <see langword="null"/> while untitled.</summary>
    string? ProjectPath { get; }

    /// <summary>The program playhead position in ticks (240000/s).</summary>
    long PlayheadTicks { get; }

    /// <summary>The active sequence's duration in ticks.</summary>
    long DurationTicks { get; }

    /// <summary>Whether the transport is currently playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Seeks the program monitor to the given tick position (clamped to the timeline).</summary>
    void Seek(long ticks);

    /// <summary>Starts playback.</summary>
    void Play();

    /// <summary>Pauses playback.</summary>
    void Pause();

    /// <summary>
    /// Imports a media file into the project's media pool via the App's import path (probe +
    /// <c>AddMediaCommand</c>, deduplicated by absolute path). Returns the pool item (existing or new),
    /// or an error for an unreadable file.
    /// </summary>
    McpResult<MediaRef> ImportMedia(string absolutePath);

    /// <summary>
    /// Places a media-pool item on the timeline via the App's placement path (linked audio+video like a
    /// bin drop, input color transform prepended for detected log media). Track indexes are into
    /// <c>Timeline.VideoTracks</c> / <c>AudioTracks</c>; <see langword="null"/> picks the first compatible
    /// track. Returns the primary placed clip (its linked partner shares <see cref="Clip.LinkGroupId"/>).
    /// </summary>
    McpResult<Clip> PlaceClip(Guid mediaId, long startTicks, int? videoTrackIndex, int? audioTrackIndex);

    /// <summary>Forces the paused preview to re-decode/recomposite after a structural edit
    /// (a seek to the current position; no-op while playing).</summary>
    void RefreshPreview();

    /// <summary>Saves the project to its existing path. Returns <see langword="false"/> when the project is
    /// untitled (a save-as file dialog cannot be driven remotely).</summary>
    bool SaveProject();
}

/// <summary>A success-or-error result crossing the editor seam without exceptions.</summary>
public readonly record struct McpResult<T>(bool Ok, T? Value, string? Error)
{
    /// <summary>A success carrying <paramref name="value"/>.</summary>
    public static McpResult<T> Success(T value) => new(true, value, null);

    /// <summary>A failure carrying a human-actionable message.</summary>
    public static McpResult<T> Fail(string error) => new(false, default, error);
}
