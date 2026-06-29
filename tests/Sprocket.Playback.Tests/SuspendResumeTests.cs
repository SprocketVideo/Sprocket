using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Covers <see cref="PlaybackEngine.SuspendAsync"/>/<see cref="PlaybackEngine.Resume"/> — the quiescence the app
/// uses before an in-process export muxer runs. The bug: <see cref="PlaybackEngine.Pause"/> only pauses the clock,
/// so the pump and the per-track decode-ring workers keep decoding; running the export's in-process FFmpeg muxer
/// alongside that live decode crashed the muxer with a native access violation (reproduced; the same hazard
/// <c>ProxyTranscoder</c> documents). Suspend must fully tear the decode pipeline down so nothing decodes in-process
/// during the export, and Resume must bring it back. Driven through the live background pump with bounded waits.
/// </summary>
public class SuspendResumeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) BuildSession()
    {
        ProbedMediaInfo info;
        using (MediaSource probe = MediaSource.Open(TestVideo.Path))
            info = probe.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero));
        timeline.Tracks.Add(track);

        Func<MediaRefId, IVideoFrameFeed?> factory = mediaId =>
        {
            MediaRef? media = project.MediaPool.Get(mediaId);
            return media is { Info.HasVideo: true }
                ? new RingVideoFrameFeed(new VideoDecodeRing(MediaSource.Open(media.AbsolutePath)))
                : null;
        };
        return (project, factory);
    }

    private static int LayerCount(PlaybackEngine engine)
    {
        int count = -1;
        engine.UseLayers(layers => count = layers.Count);
        return count;
    }

    [Fact]
    public async Task SuspendAsync_TearsDownTheDecodePipeline_AndResumeRestoresIt()
    {
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession();
        var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));
        try
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.FramePresented += () => first.TrySetResult();
            engine.Start();
            await first.Task.WaitAsync(Timeout); // the live pump decodes + presents frame 0
            Assert.Equal(1, LayerCount(engine));

            // Suspend: the pump stops and the decode-ring worker is torn down, so no in-process FFmpeg decode
            // remains to collide with an export muxer. With the players gone there is nothing to composite.
            await engine.SuspendAsync().WaitAsync(Timeout);
            Assert.Equal(0, LayerCount(engine));

            // Resume: the pump restarts, rebuilds the feed for the track, and re-presents the current frame.
            var again = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.FramePresented += () => again.TrySetResult();
            engine.Resume();
            await again.Task.WaitAsync(Timeout);
            Assert.Equal(1, LayerCount(engine));
        }
        finally
        {
            await engine.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Suspend_And_Resume_Are_Idempotent()
    {
        (Project project, Func<MediaRefId, IVideoFrameFeed?> factory) = BuildSession();
        await using var engine = new PlaybackEngine(project, factory, new SoftwareClock(() => TimeSpan.Zero));

        engine.Resume(); // not suspended → no-op (must not start a second pump)
        engine.Start();

        await engine.SuspendAsync().WaitAsync(Timeout);
        await engine.SuspendAsync().WaitAsync(Timeout); // idempotent

        engine.Resume();
        engine.Resume(); // idempotent
    }
}
