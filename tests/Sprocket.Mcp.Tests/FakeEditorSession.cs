using Sprocket.Core.Commands;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Mcp;

namespace Sprocket.Mcp.Tests;

/// <summary>
/// A synchronous, in-memory <see cref="IEditorSession"/>/<see cref="IEditorApi"/> over a real
/// <see cref="Project"/> + <see cref="EditHistory"/> — the tool layer under test drives real commands;
/// only the App-side seams (import probe, placement, transport, save) are simulated.
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
            HasAudio: false, 0, 0));
        History.Execute(new AddMediaCommand(Project.MediaPool, media));
        return McpResult<MediaRef>.Success(media);
    }

    public McpResult<Clip> PlaceClip(Guid mediaId, long startTicks, int? videoTrackIndex, int? audioTrackIndex)
    {
        MediaRef? media = Project.MediaPool.Get(new MediaRefId(mediaId));
        if (media is null)
            return McpResult<Clip>.Fail($"media {mediaId} not found");
        VideoTrack track = Project.Timeline.VideoTracks.First();
        var clip = new Clip(media.Id, Timecode.Zero, media.Info.Duration, new Timecode(startTicks));
        History.Execute(new AddClipCommand(track, clip));
        return McpResult<Clip>.Success(clip);
    }

    public void RefreshPreview() => RefreshCount++;

    public bool SaveProject()
    {
        if (ProjectPath is null)
            return false;
        SaveCount++;
        return true;
    }
}
