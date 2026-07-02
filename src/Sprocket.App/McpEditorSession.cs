using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Mcp;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// The App's implementation of the MCP editor seam (PLAN.md step 38), bridging <c>Sprocket.Mcp</c> to one
/// window session's project / history / transport. <see cref="OnModelThreadAsync{T}"/> is the single marshal
/// point: every tool callback runs on the Avalonia UI thread — the thread that owns the model and
/// <see cref="EditHistory"/> (ARCHITECTURE.md §8) — atomically with respect to user edits. Import, placement,
/// save, project lifecycle, and export all reuse the exact window flows the UI uses (minus their dialogs), so
/// AI edits get link groups, log-color-transform prepends, clamping, and export quiescing identically to the
/// user's own actions.
/// </summary>
internal sealed class McpEditorSession(
    Project project,
    EditHistory history,
    MainWindow window) : IEditorSession, IEditorApi
{
    public Task<T> OnModelThreadAsync<T>(Func<IEditorApi, T> fn) =>
        Dispatcher.UIThread.InvokeAsync(() => fn(this)).GetTask();

    public Project Project => project;
    public EditHistory History => history;
    public string? ProjectPath => window.McpProjectPath;
    public long PlayheadTicks => window.McpProgramMonitor?.Position.Ticks ?? 0;
    public long DurationTicks => project.Timeline.Duration.Ticks;
    public bool IsPlaying => window.McpProgramMonitor?.State == PlaybackState.Playing;

    public void Seek(long ticks) => window.McpProgramMonitor?.SeekTo(new Timecode(Math.Max(0, ticks)));
    public void Play() => window.McpProgramMonitor?.Play();
    public void Pause() => window.McpProgramMonitor?.Pause();

    public McpResult<MediaRef> ImportMedia(string absolutePath)
    {
        MediaImport.Result result = MediaImport.TryImport(project, history, absolutePath);
        return result.Succeeded
            ? McpResult<MediaRef>.Success(result.Media!)
            : McpResult<MediaRef>.Fail(result.Error ?? "import failed.");
    }

    public McpResult<Clip> PlaceClip(
        Guid mediaId, long startTicks, int? videoTrackIndex, int? audioTrackIndex,
        bool linked, bool includeVideo, bool includeAudio)
    {
        MediaRef? media = project.MediaPool.Get(new MediaRefId(mediaId));
        if (media is null)
            return McpResult<Clip>.Fail($"media {mediaId} is not in the pool — call list_media.");

        bool wantVideo = includeVideo && media.Info.HasVideo;
        bool wantAudio = includeAudio && media.Info.HasAudio;
        if (!wantVideo && !wantAudio)
            return McpResult<Clip>.Fail(includeVideo == includeAudio
                ? "the media has no placeable streams."
                : $"the media has no {(includeVideo ? "video" : "audio")} stream.");

        var videoTracks = project.Timeline.VideoTracks.ToList();
        var audioTracks = project.Timeline.AudioTracks.ToList();
        VideoTrack? video = null;
        AudioTrack? audio = null;
        if (wantVideo)
        {
            int index = videoTrackIndex ?? 0;
            if (index < 0 || index >= videoTracks.Count)
                return McpResult<Clip>.Fail($"video track index {index} is out of range (the sequence has {videoTracks.Count}).");
            video = videoTracks[index];
        }
        if (wantAudio)
        {
            int index = audioTrackIndex ?? 0;
            if (index < 0 || index >= audioTracks.Count)
                return McpResult<Clip>.Fail($"audio track index {index} is out of range (the sequence has {audioTracks.Count}).");
            audio = audioTracks[index];
        }

        ClipPlacement.PlacementResult? placement = ClipPlacement.BuildPlaceCommand(
            media, video, audio, startTicks, linked, primaryIsVideo: wantVideo);
        if (placement is not { } result)
            return McpResult<Clip>.Fail("the sequence has no compatible track for this media.");

        history.Execute(result.Command);
        return McpResult<Clip>.Success(result.PrimaryClip);
    }

    public void RefreshPreview()
    {
        // A paused preview holds its last composite; re-seeking the current position forces a fresh
        // decode/composite so the edit is visible immediately. While playing, the pump picks it up itself.
        if (window.McpProgramMonitor is { State: not PlaybackState.Playing } monitor)
            monitor.SeekTo(monitor.Position);
    }

    public bool IsDirty => window.McpIsDirty;

    public bool SaveProject() => window.McpSave();

    public bool SaveProjectAs(string absolutePath) => window.McpSaveAs(absolutePath);

    public McpResult<bool> OpenProject(string absolutePath) =>
        window.McpOpenProject(absolutePath) is { } error
            ? McpResult<bool>.Fail(error)
            : McpResult<bool>.Success(true);

    public McpResult<bool> NewProject() =>
        window.McpNewProject() is { } error
            ? McpResult<bool>.Fail(error)
            : McpResult<bool>.Success(true);

    public McpResult<bool> StartExport(string outputPath, bool videoOnly, long? rangeInTicks, long? rangeOutTicks) =>
        window.McpStartExport(outputPath, videoOnly, rangeInTicks, rangeOutTicks) is { } error
            ? McpResult<bool>.Fail(error)
            : McpResult<bool>.Success(true);

    public McpExportStatus ExportStatus => window.McpExportStatus;

    public void CancelExport() => window.McpCancelExport();
}
