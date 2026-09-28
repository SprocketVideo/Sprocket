using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Media;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Echo in the live preview (plan/features/toy-cassette-camera.md phase 6): the track player keeps a short history of
/// the frames it stepped past, and a seek on a clip with echoes starts the decode far enough back that the first
/// frame's echoes are already there — so the preview binds the same prior frames export does and draws the same
/// pixels, paused or playing.
/// </summary>
public sealed class EchoPreviewTests
{
    private static readonly Rational Fps = new(TestVideo.Fps, 1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static (Project Project, RingVideoFrameFeed Feed, Clip Clip) BuildSession(int echoes, int op = EchoOperators.Maximum)
    {
        MediaSource source = MediaSource.Open(TestVideo.Path, HardwareAccelMode.Disabled);
        ProbedMediaInfo info = source.Info;

        var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), 48000);
        var project = new Project(timeline);
        var id = MediaRefId.New();
        project.MediaPool.Add(new MediaRef(id, TestVideo.Path, info));
        var track = new VideoTrack { Name = "V1" };
        var clip = new Clip(id, Timecode.Zero, info.Duration, Timecode.Zero);
        if (echoes > 0)
            clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Echo)!.CreateInstance()
                .Set(EffectParamNames.EchoCount, echoes)
                .Set(EffectParamNames.EchoTime, -1.0 / 15)
                .Set(EffectParamNames.Decay, 0.6)
                .Set(EffectParamNames.EchoOperator, op));
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        return (project, new RingVideoFrameFeed(new VideoDecodeRing(source)), clip);
    }

    /// <summary>Draws the engine's presented layers exactly as the preview surface does (full-frame dest), returning
    /// the pixels and how many prior frames the media layer carried.</summary>
    private static (SKBitmap Bitmap, int PriorCount) DrawPreview(PlaybackEngine engine, int width, int height)
    {
        using var pipeline = new SkiaEffectPipeline();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        int priors = -1;
        engine.UseLayers(layers =>
        {
            surface.Canvas.Clear(SKColors.Black);
            PresentedVideoLayer layer = Assert.Single(layers);
            priors = layer.PriorFrames?.Count ?? 0;
            pipeline.DrawLayer(surface.Canvas, SKRect.Create(width, height), layer.Pixels, layer.RowBytes,
                layer.Width, layer.Height, layer.Effects, layer.Opacity, SKBlendMode.SrcOver, layer.HasAlpha, layer.PriorFrames);
            surface.Canvas.Flush();
        });
        using SKImage image = surface.Snapshot();
        return (SKBitmap.FromImage(image), priors);
    }

    private static int MaxChannelDifference(SKBitmap a, SKBitmap b)
    {
        int max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                max = Math.Max(max, Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue))));
            }
        return max;
    }

    [Fact]
    public async Task A_Paused_Seek_Shows_The_Same_Echo_Pixels_As_Export()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, RingVideoFrameFeed feed, _) = BuildSession(echoes: 3);
        await using var engine = new PlaybackEngine(project, feed, new SoftwareClock(() => TimeSpan.Zero));

        Timecode t = Timecode.FromFrames(40, Fps);
        feed.Start();
        engine.SeekTo(t);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        (SKBitmap preview, int priors) = DrawPreview(engine, TestVideo.Width, TestVideo.Height);
        using (preview)
        using (SKBitmap exported = VideoExporter.RenderFrameForTests(project, project.ActiveSequence, t, TestVideo.Width, TestVideo.Height))
        using (SKBitmap plain = VideoExporter.RenderFrameForTests(BuildSession(0).Project, project.ActiveSequence, t, TestVideo.Width, TestVideo.Height))
        {
            Assert.Equal(3, priors); // the seek pre-rolled far enough back for every echo
            Assert.True(MaxChannelDifference(preview, exported) <= 1, "preview and export must draw the same echo frame");
            Assert.True(MaxChannelDifference(exported, plain) > 20, "the echoes must actually show");
        }
    }

    [Fact]
    public async Task Playback_Keeps_The_Echoes_And_Matches_Export_Frame_For_Frame()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var elapsed = TimeSpan.Zero;
        (Project project, RingVideoFrameFeed feed, _) = BuildSession(echoes: 4, op: EchoOperators.Add);
        var clock = new SoftwareClock(() => elapsed);
        await using var engine = new PlaybackEngine(project, feed, clock);

        feed.Start();
        engine.SeekTo(Timecode.Zero);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);
        clock.Start();

        foreach (int frame in new[] { 10, 11, 12, 30, 31 })
        {
            elapsed = TimeSpan.FromSeconds(Timecode.FromFrames(frame, Fps).ToSeconds());
            await engine.PumpOnceAsync(forcePresent: false, cts.Token);
            Timecode t = Timecode.FromFrames(frame, Fps);
            Assert.Equal(t, engine.Position);

            (SKBitmap preview, int priors) = DrawPreview(engine, TestVideo.Width, TestVideo.Height);
            using (preview)
            using (SKBitmap exported = VideoExporter.RenderFrameForTests(project, project.ActiveSequence, t, TestVideo.Width, TestVideo.Height))
            {
                Assert.Equal(4, priors);
                Assert.True(MaxChannelDifference(preview, exported) <= 1, $"frame {frame}: preview differs from export");
            }
        }
    }

    [Fact]
    public async Task A_Clip_Without_Echo_Carries_No_Prior_Frames()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, RingVideoFrameFeed feed, _) = BuildSession(echoes: 0);
        await using var engine = new PlaybackEngine(project, feed, new SoftwareClock(() => TimeSpan.Zero));
        feed.Start();
        engine.SeekTo(Timecode.FromFrames(20, Fps));
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        (SKBitmap preview, int priors) = DrawPreview(engine, TestVideo.Width, TestVideo.Height);
        preview.Dispose();
        Assert.Equal(0, priors);
    }

    [Fact]
    public async Task Adding_Echo_While_Paused_Fills_The_History_On_The_Next_Pump()
    {
        using var cts = new CancellationTokenSource(Timeout);
        (Project project, RingVideoFrameFeed feed, Clip clip) = BuildSession(echoes: 0);
        await using var engine = new PlaybackEngine(project, feed, new SoftwareClock(() => TimeSpan.Zero));
        Timecode t = Timecode.FromFrames(45, Fps);
        feed.Start();
        engine.SeekTo(t);
        await engine.PumpOnceAsync(forcePresent: true, cts.Token);

        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Echo)!.CreateInstance()
            .Set(EffectParamNames.EchoCount, 2).Set(EffectParamNames.EchoTime, -0.1));
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        (SKBitmap preview, int priors) = DrawPreview(engine, TestVideo.Width, TestVideo.Height);
        using (preview)
        using (SKBitmap exported = VideoExporter.RenderFrameForTests(project, project.ActiveSequence, t, TestVideo.Width, TestVideo.Height))
        {
            Assert.Equal(2, priors);
            Assert.True(MaxChannelDifference(preview, exported) <= 1);
        }
    }
}
