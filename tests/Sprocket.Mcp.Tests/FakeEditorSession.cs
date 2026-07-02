using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Mcp;

namespace Sprocket.Mcp.Tests;

/// <summary>
/// A synchronous, in-memory <see cref="IEditorSession"/>/<see cref="IEditorApi"/> over a real
/// <see cref="Project"/> + <see cref="EditHistory"/> — the tool layer under test drives real commands;
/// only the App-side seams (import probe, placement, transport, save/lifecycle/export) are simulated.
/// Imported media reports both video and audio streams so the linked-placement paths are exercised.
/// </summary>
internal sealed class FakeEditorSession : IEditorSession, IEditorApi
{
    public FakeEditorSession(Project? project = null)
    {
        if (project is null)
        {
            var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
            timeline.Tracks.Add(new VideoTrack { Name = "V1" });
            timeline.Tracks.Add(new AudioTrack { Name = "A1" });
            project = new Project(timeline);
        }
        Project = project;
    }

    public Project Project { get; }
    public EditHistory History { get; } = new();
    public string? ProjectPath { get; set; }
    public long PlayheadTicks { get; private set; }
    public long DurationTicks => Project.Timeline.Duration.Ticks;
    public bool IsPlaying { get; private set; }

    public int RefreshCount { get; private set; }
    public int SaveCount { get; private set; }
    public string? SavedAsPath { get; private set; }
    public string? OpenedPath { get; private set; }
    public int NewProjectCount { get; private set; }
    public string? ExportedPath { get; private set; }
    public bool ExportVideoOnly { get; private set; }
    public (long? In, long? Out) ExportRange { get; private set; }
    public bool ExportCancelRequested { get; private set; }

    private int _savedUndoCount;

    public Task<T> OnModelThreadAsync<T>(Func<IEditorApi, T> fn) => Task.FromResult(fn(this));

    public void Seek(long ticks) => PlayheadTicks = Math.Clamp(ticks, 0, DurationTicks);
    public void Play() => IsPlaying = true;
    public void Pause() => IsPlaying = false;

    public McpResult<MediaRef> ImportMedia(string absolutePath)
    {
        if (absolutePath.Length == 0)
            return McpResult<MediaRef>.Fail("empty path");
        var media = new MediaRef(MediaRefId.New(), absolutePath, new ProbedMediaInfo(
            Timecode.FromSeconds(2), HasVideo: true, new Rational(30, 1), 1920, 1080,
            HasAudio: true, 48000, 2));
        History.Execute(new AddMediaCommand(Project.MediaPool, media));
        return McpResult<MediaRef>.Success(media);
    }

    public McpResult<Clip> PlaceClip(
        Guid mediaId, long startTicks, int? videoTrackIndex, int? audioTrackIndex,
        bool linked, bool includeVideo, bool includeAudio)
    {
        MediaRef? media = Project.MediaPool.Get(new MediaRefId(mediaId));
        if (media is null)
            return McpResult<Clip>.Fail($"media {mediaId} not found");

        bool wantVideo = includeVideo && media.Info.HasVideo;
        bool wantAudio = includeAudio && media.Info.HasAudio;
        if (!wantVideo && !wantAudio)
            return McpResult<Clip>.Fail("no matching stream");

        var videoTracks = Project.Timeline.VideoTracks.ToList();
        var audioTracks = Project.Timeline.AudioTracks.ToList();
        VideoTrack? video = wantVideo ? videoTracks.ElementAtOrDefault(videoTrackIndex ?? 0) : null;
        AudioTrack? audio = wantAudio ? audioTracks.ElementAtOrDefault(audioTrackIndex ?? 0) : null;
        if (wantVideo && video is null)
            return McpResult<Clip>.Fail("video track index out of range");
        if (wantAudio && audio is null)
            return McpResult<Clip>.Fail("audio track index out of range");

        Guid? group = (linked && video is not null && audio is not null) ? Guid.NewGuid() : null;
        var start = new Timecode(startTicks);
        Clip? videoClip = video is null
            ? null
            : new Clip(media.Id, Timecode.Zero, media.Info.Duration, start) { LinkGroupId = group };
        Clip? audioClip = audio is null
            ? null
            : new Clip(media.Id, Timecode.Zero, media.Info.Duration, start) { LinkGroupId = group };

        var commands = new List<IEditCommand>();
        if (videoClip is not null)
            commands.Add(new AddClipCommand(video!, videoClip));
        if (audioClip is not null)
            commands.Add(new AddClipCommand(audio!, audioClip));
        History.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Place linked clips", commands));
        return McpResult<Clip>.Success(videoClip ?? audioClip!);
    }

    public void RefreshPreview() => RefreshCount++;

    public bool IsDirty => History.UndoCount != _savedUndoCount;

    public bool SaveProject()
    {
        if (ProjectPath is null)
            return false;
        SaveCount++;
        _savedUndoCount = History.UndoCount;
        return true;
    }

    public bool SaveProjectAs(string absolutePath)
    {
        ProjectPath = absolutePath;
        SavedAsPath = absolutePath;
        SaveCount++;
        _savedUndoCount = History.UndoCount;
        return true;
    }

    public McpResult<bool> OpenProject(string absolutePath)
    {
        if (!absolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return McpResult<bool>.Fail("open failed: not a project file");
        OpenedPath = absolutePath;
        return McpResult<bool>.Success(true);
    }

    public McpResult<bool> NewProject()
    {
        NewProjectCount++;
        return McpResult<bool>.Success(true);
    }

    public McpResult<bool> StartExport(string outputPath, bool videoOnly, long? rangeInTicks, long? rangeOutTicks)
    {
        if (DurationTicks <= 0)
            return McpResult<bool>.Fail("the timeline is empty — nothing to export.");
        ExportedPath = outputPath;
        ExportVideoOnly = videoOnly;
        ExportRange = (rangeInTicks, rangeOutTicks);
        // The fake completes instantly — the real App runs VideoExporter on a background thread.
        ExportStatus = new McpExportStatus(false, 1.0, outputPath, true, false, null);
        return McpResult<bool>.Success(true);
    }

    public McpExportStatus ExportStatus { get; private set; }

    public void CancelExport() => ExportCancelRequested = true;
}
