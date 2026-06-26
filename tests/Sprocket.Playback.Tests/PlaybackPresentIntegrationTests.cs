using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// End-to-end coverage of the slice's real playback path (PLAN.md step 4): <see cref="PlaybackEngine.Start"/>
/// spins the live background pump, which decodes a real fixture and presents frames, and
/// <see cref="FramePresenter"/> draws the presented native frame to an offscreen Skia raster surface — the
/// same call the Avalonia preview makes inside its GPU lease. This exercises the live pump + transport +
/// <see cref="PlaybackEngine.DisposeAsync"/> teardown that the deterministic <see cref="PlaybackEngineTests"/>
/// step manually. All waits are bounded so a regression fails fast instead of hanging the suite.
/// </summary>
public class PlaybackPresentIntegrationTests
{
    private static readonly SKColor Background = new(0x0E, 0x0E, 0x12);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static (Project project, RingVideoFrameFeed feed) BuildSession()
    {
        MediaSource source = MediaSource.Open(TestVideo.Path);
        ProbedMediaInfo info = source.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));
        var track = new VideoTrack { Name = "V1" };
        track.Clips.Add(new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero));
        timeline.Tracks.Add(track);

        return (project, new RingVideoFrameFeed(new VideoDecodeRing(source)));
    }

    /// <summary>Presents the engine's current frame to an offscreen surface; returns (pts, centre-pixel-non-blank).</summary>
    private static (long pts, bool nonBlank) RenderToOffscreen(PlaybackEngine engine, int w, int h)
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        long pts = -1;
        engine.UseCurrentFrame(f =>
        {
            if (f is not { } frame)
                return;
            pts = frame.Pts.Ticks;
            FramePresenter.Present(surface.Canvas, SKRect.Create(0, 0, w, h),
                frame.Pixels, frame.RowBytes, frame.Width, frame.Height, Background);
        });
        surface.Canvas.Flush();

        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        SKColor centre = bitmap.GetPixel(w / 2, h / 2);
        return (pts, pts >= 0 && centre != Background);
    }

    [Fact]
    public async Task Live_Pump_Presents_A_Real_Frame_That_Renders_Non_Blank()
    {
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        var engine = new PlaybackEngine(project, feed);
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FramePresented += () => firstFrame.TrySetResult();

        try
        {
            engine.Start();
            await firstFrame.Task.WaitAsync(Timeout); // the live pump decodes + presents frame 0

            (long pts, bool nonBlank) = RenderToOffscreen(engine, 320, 180);
            Assert.True(pts >= 0, "a frame should be presented after Start()");
            Assert.True(nonBlank, "the presented frame should render as non-blank pixels");
        }
        finally
        {
            // Teardown must complete promptly: a stuck pump/worker would hang here, so bound it.
            await engine.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Live_Seek_While_Paused_Presents_A_Different_Frame()
    {
        (Project project, RingVideoFrameFeed feed) = BuildSession();
        var engine = new PlaybackEngine(project, feed);

        try
        {
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.FramePresented += () => firstFrame.TrySetResult();
            engine.Start();
            await firstFrame.Task.WaitAsync(Timeout);
            long startPts = RenderToOffscreen(engine, 320, 180).pts;

            // Scrub ~1s in; the pump must present the post-seek frame.
            var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.FramePresented += () =>
            {
                long now = -1;
                engine.UseCurrentFrame(f => { if (f is { } fr) now = fr.Pts.Ticks; });
                if (now != startPts)
                    moved.TrySetResult();
            };
            engine.SeekTo(Timecode.FromSeconds(1.0));
            await moved.Task.WaitAsync(Timeout);

            (long seekPts, bool nonBlank) = RenderToOffscreen(engine, 320, 180);
            Assert.NotEqual(startPts, seekPts);
            Assert.True(nonBlank);
        }
        finally
        {
            await engine.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }
}
