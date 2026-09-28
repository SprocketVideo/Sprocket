using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Echo on the export path (plan/features/toy-cassette-camera.md phase 6): the frame provider serves a layer's prior
/// frames from the history of frames its sequential walk already passed, so a forward clip's echoes cost one seek at
/// the clip's start and no re-decode; every frame it hands out stays valid through the draw; a reversed walk serves
/// the (later) prior frames correctly; and a full export with Echo renders the trail.
/// </summary>
public sealed class EchoExportTests
{
    private const long FrameTicks = Timecode.TicksPerSecond / ExportFixture.Fps; // 8000
    private const long EchoTicks = Timecode.TicksPerSecond / 15;                  // one echo = two source frames
    private const int Frames = ExportFixture.Fps;                                 // the fixture is 1 s

    private static ExportFrameProvider OpenProvider() =>
        new(MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled));

    /// <summary>The distinct, ascending prior times a forward clip at 1× with <paramref name="echoes"/> echoes of −1/15 s
    /// asks for at source time <paramref name="t"/> (clamped to the first frame, as the planner does).</summary>
    private static List<Timecode> ForwardPriors(long t, int echoes) =>
        [.. Enumerable.Range(1, echoes).Select(k => new Timecode(Math.Max(0, t - k * EchoTicks))).Distinct().Order()];

    [Fact]
    public void Forward_Echoes_Seek_Once_Per_Clip_And_Are_Served_From_The_History()
    {
        using ExportFrameProvider provider = OpenProvider();
        var shown = new Dictionary<long, nint>(); // pts → the native buffer that frame was served in
        var priors = new List<PriorFrame>();

        for (int n = 0; n < Frames; n++)
        {
            long t = n * FrameTicks;
            List<Timecode> times = ForwardPriors(t, echoes: 4);
            priors.Clear();
            VideoFrame current = provider.GetFrames(new Timecode(t), reverse: false, times, priors)!;

            Assert.Equal(t, current.Pts.Ticks);
            Assert.Equal(times, priors.Select(p => p.SourceTime));
            // Each prior frame is the very buffer that frame was shown in earlier (at the clip's first frame: the
            // current one) — kept, not re-decoded or recycled.
            shown[t] = current.Pixels;
            foreach (PriorFrame prior in priors)
                Assert.Equal(shown[prior.SourceTime.Ticks], prior.Pixels);
            Assert.True(provider.HistoryCount <= 9, $"history holds {provider.HistoryCount} frames");
        }

        Assert.Equal(1, provider.SeekCount); // the clip's start only
    }

    [Fact]
    public unsafe void Prior_Frames_Carry_The_Right_Pixels_And_Stay_Valid_Through_The_Call()
    {
        List<byte[]> reference = DecodeAllFrames();
        using ExportFrameProvider provider = OpenProvider();
        var priors = new List<PriorFrame>();

        for (int n = 0; n < Frames; n++)
        {
            long t = n * FrameTicks;
            priors.Clear();
            VideoFrame current = provider.GetFrames(new Timecode(t), false, ForwardPriors(t, echoes: 3), priors)!;
            Assert.Equal(reference[n], Copy(current.Pixels, current.RowBytes, current.Width, current.Height));
            foreach (PriorFrame prior in priors)
                Assert.Equal(reference[(int)(prior.SourceTime.Ticks / FrameTicks)],
                    Copy(prior.Pixels, prior.RowBytes, prior.Width, prior.Height));
        }
    }

    [Fact]
    public void A_Cut_Back_Into_The_Same_Source_Seeks_Once_More()
    {
        using ExportFrameProvider provider = OpenProvider();
        var priors = new List<PriorFrame>();
        for (int n = 20; n < 26; n++)
        {
            priors.Clear();
            provider.GetFrames(new Timecode(n * FrameTicks), false, ForwardPriors(n * FrameTicks, 2), priors);
        }
        // A second clip of the same media starting earlier: one re-seek, then the history serves it again.
        for (int n = 5; n < 12; n++)
        {
            priors.Clear();
            VideoFrame current = provider.GetFrames(new Timecode(n * FrameTicks), false, ForwardPriors(n * FrameTicks, 2), priors)!;
            Assert.Equal(n * FrameTicks, current.Pts.Ticks);
            Assert.Equal(2, priors.Count);
        }
        Assert.Equal(2, provider.SeekCount);
    }

    [Fact]
    public unsafe void Reverse_Echoes_Are_The_Later_Frames_Strictly_Before_Each_Bound()
    {
        List<byte[]> reference = DecodeAllFrames();
        long end = Frames * FrameTicks;
        using ExportFrameProvider provider = OpenProvider();
        var priors = new List<PriorFrame>();

        for (int n = 0; n < Frames; n++)
        {
            long bound = end - n * FrameTicks; // a reversed clip's exclusive upper bound
            List<Timecode> times =
                [.. Enumerable.Range(1, 3).Select(k => new Timecode(Math.Min(end, bound + k * EchoTicks))).Distinct().Order()];
            priors.Clear();
            VideoFrame current = provider.GetFrames(new Timecode(bound), reverse: true, times, priors)!;

            Assert.Equal(bound - FrameTicks, current.Pts.Ticks);
            Assert.Equal(times, priors.Select(p => p.SourceTime).Order());
            foreach (PriorFrame prior in priors)
            {
                int index = (int)(prior.SourceTime.Ticks / FrameTicks) - 1; // latest frame strictly before the bound
                Assert.Equal(reference[index], Copy(prior.Pixels, prior.RowBytes, prior.Width, prior.Height));
            }
            Assert.Equal(reference[(int)(bound / FrameTicks) - 1], Copy(current.Pixels, current.RowBytes, current.Width, current.Height));
        }
    }

    [Fact]
    public void Export_With_Echo_Renders_The_Trail()
    {
        Project plain = ExportFixture.BuildProject(withAudio: false);
        Project echoed = ExportFixture.BuildProject(withAudio: false);
        Clip clip = echoed.Timeline.VideoTracks.Single().Clips.Single();
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Echo)!.CreateInstance()
            .Set(EffectParamNames.EchoCount, 3)
            .Set(EffectParamNames.EchoTime, -1.0 / 15)
            .Set(EffectParamNames.Decay, 0.7)
            .Set(EffectParamNames.EchoOperator, EchoOperators.Add));

        using var plainOut = new TempFile();
        using var echoOut = new TempFile();
        VideoExporter.Export(plain, plainOut.Path);
        VideoExporter.Export(echoed, echoOut.Path);

        Assert.Equal(Frames, ExportProbe.CountVideoFrames(echoOut.Path));
        // Add piles the earlier frames onto each one: the echoed picture is clearly brighter mid-clip.
        Assert.True(ExportProbe.FrameMeanRgbAt(echoOut.Path, 15) > ExportProbe.FrameMeanRgbAt(plainOut.Path, 15) + 10);
    }

    [Fact]
    public void Rendered_Echo_Frame_Matches_A_Reference_Built_From_The_Decoded_Frames()
    {
        // The export composite of one Echo frame equals drawing the current frame with the prior frames decoded
        // independently — i.e. the provider served exactly the frames the plan asked for.
        Project project = ExportFixture.BuildProject(withAudio: false);
        Clip clip = project.Timeline.VideoTracks.Single().Clips.Single();
        clip.Effects.Add(EffectCatalog.Find(EffectTypeIds.Echo)!.CreateInstance()
            .Set(EffectParamNames.EchoCount, 3)
            .Set(EffectParamNames.EchoTime, -1.0 / 15)
            .Set(EffectParamNames.Decay, 0.6)
            .Set(EffectParamNames.EchoOperator, EchoOperators.Maximum));
        Timecode t = new(20 * FrameTicks);

        using SKBitmap exported = VideoExporter.RenderFrameForTests(project, project.ActiveSequence, t,
            ExportFixture.Width, ExportFixture.Height);
        using SKBitmap reference = ReferenceRender(project, t);
        Assert.True(MaxChannelDifference(exported, reference) <= 1, "export must bind the planned prior frames");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Draws the planned layer with frames decoded one by one (a fresh seek per frame).</summary>
    private static SKBitmap ReferenceRender(Project project, Timecode t)
    {
        var layer = Core.Rendering.RenderGraph.PlanVideoFrame(project, t).Layers.Single();
        var decoded = new List<(Timecode Time, VideoFrame Frame)>();
        using MediaSource source = MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        try
        {
            foreach (Timecode time in layer.PriorSourceTimes!.Append(layer.SourceTime))
                decoded.Add((time, DecodeAt(source, pool, time)));
            VideoFrame current = decoded[^1].Frame;
            List<PriorFrame> priors = [.. decoded.SkipLast(1).Select(d =>
                new PriorFrame(d.Time, d.Frame.Pixels, d.Frame.RowBytes, d.Frame.Width, d.Frame.Height))];

            using var pipeline = new SkiaEffectPipeline();
            using SKSurface surface = SKSurface.Create(
                new SKImageInfo(ExportFixture.Width, ExportFixture.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Black);
            pipeline.DrawLayer(surface.Canvas, SKRect.Create(ExportFixture.Width, ExportFixture.Height), current.Pixels,
                current.RowBytes, current.Width, current.Height, layer.Effects, priorFrames: priors);
            surface.Canvas.Flush();
            using SKImage image = surface.Snapshot();
            return SKBitmap.FromImage(image);
        }
        finally
        {
            foreach ((_, VideoFrame frame) in decoded)
                frame.Dispose();
        }
    }

    /// <summary>The latest frame at/before <paramref name="time"/>, decoded from the start (no seek subtleties).</summary>
    private static VideoFrame DecodeAt(MediaSource source, VideoFramePool pool, Timecode time)
    {
        source.SeekTo(Timecode.Zero);
        VideoFrame? best = null;
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
        {
            if (frame.Pts > time)
            {
                frame.Dispose();
                break;
            }
            best?.Dispose();
            best = frame;
        }
        return best!;
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

    private static List<byte[]> DecodeAllFrames()
    {
        using MediaSource source = MediaSource.Open(ExportFixture.SourcePath, HardwareAccelMode.Disabled);
        using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        var frames = new List<byte[]>();
        while (source.TryDecodeNextFrame(pool, out VideoFrame? frame))
            using (frame)
                frames.Add(Copy(frame.Pixels, frame.RowBytes, frame.Width, frame.Height));
        return frames;
    }

    private static unsafe byte[] Copy(nint pixels, int rowBytes, int width, int height)
    {
        var bytes = new byte[width * height * 4];
        var p = (byte*)pixels;
        for (int y = 0; y < height; y++)
            new ReadOnlySpan<byte>(p + (long)y * rowBytes, width * 4).CopyTo(bytes.AsSpan(y * width * 4));
        return bytes;
    }
}
