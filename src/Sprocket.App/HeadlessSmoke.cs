using System;
using System.Threading;
using SkiaSharp;
using Sprocket.Core.Timing;
using Sprocket.Playback;
using Sprocket.Render;

namespace Sprocket.App;

/// <summary>
/// A no-GUI smoke check (<c>--smoke</c>) that exercises the whole slice playback path —
/// decode (Media) → engine pump (Playback) → present (Render) — and renders to an offscreen raster surface,
/// asserting a real, non-blank frame comes out. Mirrors the spike's headless check (PLAN.md step 1) so the
/// playback path can be validated in CI / on a box with no display or GPU.
/// </summary>
internal static class HeadlessSmoke
{
    private static readonly SKColor Background = new(0x0E, 0x0E, 0x12);
    private const int Width = 640;
    private const int Height = 360;

    public static int Run(string[] args)
    {
        MediaBootstrap.Result result = MediaBootstrap.Create(args);
        if (result.Engine is not { } engine)
        {
            Console.Error.WriteLine(result.Status);
            return 1;
        }
        Console.WriteLine($"[smoke] {result.Status}");

        try
        {
            // engine.Start() (in MediaBootstrap) seeked to 0 and the pump is loading frame 0.
            if (!WaitForFrame(engine, TimeSpan.FromSeconds(15)))
            {
                Console.Error.WriteLine("[smoke] FAIL: no frame was presented at the start.");
                return 1;
            }
            long startPts = RenderAndProbe(engine, out bool startNonBlank);
            Console.WriteLine($"[smoke] first frame: pts={startPts} ticks, non-blank={startNonBlank}");

            // Seek to ~1s in and confirm a different frame is presented (seek wiring works end to end).
            engine.SeekTo(Timecode.FromSeconds(1.0));
            bool moved = WaitForCondition(engine, () => CurrentPts(engine) != startPts, TimeSpan.FromSeconds(15));
            long seekPts = RenderAndProbe(engine, out bool seekNonBlank);
            Console.WriteLine($"[smoke] after seek to 1.0s: pts={seekPts} ticks, moved={moved}, non-blank={seekNonBlank}");

            bool ok = startNonBlank && seekNonBlank && moved;
            Console.WriteLine(ok ? "[smoke] PASS" : "[smoke] FAIL");
            return ok ? 0 : 1;
        }
        finally
        {
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Presents the current frame to an offscreen surface and reports its PTS, setting
    /// <paramref name="nonBlank"/> if the centre pixel differs from the letterbox background.</summary>
    private static long RenderAndProbe(PlaybackEngine engine, out bool nonBlank)
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        long pts = -1;
        engine.UseCurrentFrame(f =>
        {
            if (f is not { } frame)
                return;
            pts = frame.Pts.Ticks;
            FramePresenter.Present(surface.Canvas, SKRect.Create(0, 0, Width, Height),
                frame.Pixels, frame.RowBytes, frame.Width, frame.Height, Background);
        });
        surface.Canvas.Flush();

        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);
        SKColor centre = bitmap.GetPixel(Width / 2, Height / 2);
        nonBlank = pts >= 0 && centre != Background;
        return pts;
    }

    private static long CurrentPts(PlaybackEngine engine)
    {
        long pts = -1;
        engine.UseCurrentFrame(f => { if (f is { } frame) pts = frame.Pts.Ticks; });
        return pts;
    }

    private static bool WaitForFrame(PlaybackEngine engine, TimeSpan timeout) =>
        WaitForCondition(engine, () => CurrentPts(engine) >= 0, timeout);

    private static bool WaitForCondition(PlaybackEngine engine, Func<bool> condition, TimeSpan timeout)
    {
        using var signal = new ManualResetEventSlim(false);
        void OnFrame() => signal.Set();
        engine.FramePresented += OnFrame;
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                signal.Wait(TimeSpan.FromMilliseconds(100));
                signal.Reset();
            }
            return condition();
        }
        finally
        {
            engine.FramePresented -= OnFrame;
        }
    }
}
