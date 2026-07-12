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
    /// Places a media-pool item on the timeline via the App's placement path (input color transform
    /// prepended for detected log media). Track indexes are into <c>Timeline.VideoTracks</c> /
    /// <c>AudioTracks</c>; <see langword="null"/> picks the first compatible track.
    /// <paramref name="includeVideo"/> / <paramref name="includeAudio"/> restrict which of the source's
    /// streams get a clip; when both land and <paramref name="linked"/> is on they share a fresh
    /// <see cref="Clip.LinkGroupId"/> and place as one undo entry (like a bin drop). Returns the primary
    /// placed clip.
    /// </summary>
    McpResult<Clip> PlaceClip(
        Guid mediaId, long startTicks, int? videoTrackIndex, int? audioTrackIndex,
        bool linked, bool includeVideo, bool includeAudio);

    /// <summary>Forces the paused preview to re-decode/recomposite after a structural edit
    /// (a seek to the current position; no-op while playing).</summary>
    void RefreshPreview();

    /// <summary>Whether the document has edits not yet written to <see cref="ProjectPath"/> (the App's
    /// title-bar dirty indicator).</summary>
    bool IsDirty { get; }

    /// <summary>Saves the project to its existing path. Returns <see langword="false"/> when the project is
    /// untitled (a save-as file dialog cannot be driven remotely — use <see cref="SaveProjectAs"/>).</summary>
    bool SaveProject();

    /// <summary>Saves the project to <paramref name="absolutePath"/> and re-points the document at that file
    /// (File ▸ Save As). Returns whether the write succeeded.</summary>
    bool SaveProjectAs(string absolutePath);

    /// <summary>
    /// Opens the project file at <paramref name="absolutePath"/>, replacing the current editing session (the
    /// App swaps the window/session and re-attaches a fresh MCP session — this <see cref="IEditorApi"/> goes
    /// stale after a success). The caller must have handled unsaved changes (<see cref="IsDirty"/>) first;
    /// no dialog is shown.
    /// </summary>
    McpResult<bool> OpenProject(string absolutePath);

    /// <summary>
    /// Closes the current project by swapping to a fresh empty one (File ▸ New — the App has no window-less
    /// "closed" state). Same session-swap semantics as <see cref="OpenProject"/>; the caller must have
    /// handled unsaved changes first.
    /// </summary>
    McpResult<bool> NewProject();

    /// <summary>
    /// Starts a background export of the active sequence to <paramref name="outputPath"/> in the default
    /// delivery format (MP4 / H.264 + AAC), returning immediately — poll <see cref="ExportStatus"/> for
    /// progress. Fails when an export is already running or the timeline is empty.
    /// <paramref name="rangeInTicks"/>/<paramref name="rangeOutTicks"/> select a half-open timeline slice
    /// (<see langword="null"/> = whole timeline). Rate control travels as primitives so this seam stays
    /// Export-assembly-free: <paramref name="rateControl"/> is <c>"quality"</c> (constant quality — the
    /// pre-validated tool default) or <c>"bitrate"</c>; <paramref name="crf"/> is an explicit 1–51
    /// constant-quality value (<see langword="null"/> = the app's High default); <paramref name="bitrateMbps"/>/
    /// <paramref name="maxBitrateMbps"/> are the bitrate-mode target and optional ceiling in Mbps
    /// (<see langword="null"/> = resolution-scaled default / uncapped); <paramref name="hardware"/> prefers the
    /// GPU encoder with automatic software fallback.
    /// </summary>
    McpResult<bool> StartExport(
        string outputPath, bool videoOnly, long? rangeInTicks, long? rangeOutTicks,
        string? rateControl = null, int? crf = null, double? bitrateMbps = null, double? maxBitrateMbps = null,
        bool hardware = false);

    /// <summary>
    /// Starts a background <b>audio-only</b> export of the active sequence's master mix to
    /// <paramref name="outputPath"/> (PLAN.md step 44), returning immediately — poll <see cref="ExportStatus"/> for
    /// progress. <paramref name="audioFormat"/> is one of <c>wav</c> / <c>flac</c> / <c>mp3</c> / <c>aac</c> /
    /// <c>opus</c> (case-insensitive). Fails when an export is already running, the timeline is empty, or the format
    /// is unrecognised. <paramref name="rangeInTicks"/>/<paramref name="rangeOutTicks"/> select a half-open slice.
    /// </summary>
    McpResult<bool> StartAudioExport(string outputPath, string audioFormat, long? rangeInTicks, long? rangeOutTicks);

    /// <summary>The state of the current (or most recently finished) export started via
    /// <see cref="StartExport"/> or <see cref="StartAudioExport"/>.</summary>
    McpExportStatus ExportStatus { get; }

    /// <summary>Requests cancellation of the running export (a no-op when none is running).</summary>
    void CancelExport();
}

/// <summary>
/// The observable state of an MCP-initiated export: <see cref="Running"/> with <see cref="Progress"/> in
/// [0, 1] while encoding; afterwards exactly one of <see cref="Completed"/> / <see cref="Cancelled"/> /
/// <see cref="Error"/> describes the outcome. <see cref="OutputPath"/> is <see langword="null"/> when no
/// export has been started this session.
/// </summary>
public readonly record struct McpExportStatus(
    bool Running, double Progress, string? OutputPath, bool Completed, bool Cancelled, string? Error);

/// <summary>A success-or-error result crossing the editor seam without exceptions.</summary>
public readonly record struct McpResult<T>(bool Ok, T? Value, string? Error)
{
    /// <summary>A success carrying <paramref name="value"/>.</summary>
    public static McpResult<T> Success(T value) => new(true, value, null);

    /// <summary>A failure carrying a human-actionable message.</summary>
    public static McpResult<T> Fail(string error) => new(false, default, error);
}
