using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Export;
using Sprocket.Media;
using Sprocket.Persistence;

namespace Sprocket.App;

/// <summary>
/// The <c>--export-check</c> headless release smoke (code-signing ticket 04): loads the bundled sample project
/// (the one File ▸ Open Sample Project opens), exports it end-to-end through the real
/// <see cref="VideoExporter"/> with the default MP4 / H.264 / AAC settings, then re-opens the output and checks it
/// has the expected duration, resolution, and streams and that its first frame decodes. Proves a shipped build can
/// actually <i>export</i> — HEVC decode, the effect shaders on the raster surface, the audio mix, and the
/// libx264/AAC encode + mux — not merely load its natives. No Avalonia, no audio device.
/// </summary>
internal static class ExportCheck
{
    /// <summary>How far the re-probed container duration may drift from the timeline's — covers the AAC
    /// priming / final-packet padding and container rounding, far below any "export truncated" failure.</summary>
    internal static readonly Timecode DurationTolerance = Timecode.FromSeconds(0.2);

    /// <summary>What a correct default export of a timeline looks like when re-probed.</summary>
    /// <param name="VideoCodec">The decoder codec name the probe reports (not the encoder name).</param>
    /// <param name="AudioCodec">The decoder codec name the probe reports.</param>
    internal sealed record Expectation(Timecode Duration, int Width, int Height, string VideoCodec, string AudioCodec);

    /// <summary>The expectation for a whole-timeline <c>default(ExportOptions)</c> export (MP4 / H.264 / AAC at the
    /// sequence's own resolution, capped and evened by <see cref="VideoExporter.ComputeExportResolution"/>).</summary>
    internal static Expectation ExpectFor(Timeline timeline)
    {
        (int width, int height) = VideoExporter.ComputeExportResolution(timeline.Resolution.Width, timeline.Resolution.Height);
        return new Expectation(timeline.Duration, width, height, VideoCodec: "h264", AudioCodec: "aac");
    }

    /// <summary>Every way <paramref name="actual"/> falls short of <paramref name="expected"/>; empty means it passed.</summary>
    internal static IReadOnlyList<string> Verify(Expectation expected, ProbedMediaInfo actual)
    {
        var failures = new List<string>();

        if (!actual.HasVideo)
            failures.Add("output has no video stream");
        else
        {
            if (actual.Width != expected.Width || actual.Height != expected.Height)
                failures.Add($"resolution {actual.Width}x{actual.Height}, expected {expected.Width}x{expected.Height}");
            if (!string.Equals(actual.VideoCodec, expected.VideoCodec, StringComparison.OrdinalIgnoreCase))
                failures.Add($"video codec {actual.VideoCodec}, expected {expected.VideoCodec}");
        }

        if (!actual.HasAudio)
            failures.Add("output has no audio stream");
        else if (!string.Equals(actual.AudioCodec, expected.AudioCodec, StringComparison.OrdinalIgnoreCase))
            failures.Add($"audio codec {actual.AudioCodec}, expected {expected.AudioCodec}");

        long drift = Math.Abs(actual.Duration.Ticks - expected.Duration.Ticks);
        if (drift > DurationTolerance.Ticks)
            failures.Add($"duration {actual.Duration.ToSeconds():0.###}s, expected {expected.Duration.ToSeconds():0.###}s " +
                         $"± {DurationTolerance.ToSeconds():0.###}s");

        return failures;
    }

    public static int Run()
    {
        Console.WriteLine("== Sprocket export-check ==");
        string outDir = Path.Combine(Path.GetTempPath(), $"sprocket-export-check-{Guid.NewGuid():N}");
        try
        {
            if (MediaBootstrap.SampleProjectPath() is not { } projectPath)
                return Fail("bundled sample project not found under Samples/");
            Project project = ProjectSerializer.Load(projectPath);

            // An offline source would export as black/silence (§15) and still "pass" — the sample must resolve.
            MediaRef? offline = project.MediaPool.Items.FirstOrDefault(m => !File.Exists(m.AbsolutePath));
            if (offline is not null)
                return Fail($"sample media is offline: {offline.AbsolutePath}");

            Expectation expected = ExpectFor(project.Timeline);
            Console.WriteLine($"sample:   {projectPath}");
            Console.WriteLine($"expected: {expected.Width}x{expected.Height} {expected.VideoCodec}/{expected.AudioCodec} " +
                              $"{expected.Duration.ToSeconds():0.###}s");

            Directory.CreateDirectory(outDir);
            string outputPath = Path.Combine(outDir, "sample-export.mp4");
            ExportRunSummary summary = VideoExporter.ExportWithSummary(
                project, outputPath, default, sequenceId: null, range: null);
            Console.WriteLine($"[ok]   exported {summary.VideoFrames} frames + {summary.AudioSampleFrames} audio samples " +
                              $"with {summary.ActualVideoEncoder} in {summary.Timings.Total.TotalSeconds:0.0}s " +
                              $"({new FileInfo(outputPath).Length / 1024} KiB)");
            if (summary.VideoFrames == 0 || summary.AudioSampleFrames == 0)
                return Fail("exporter wrote no video frames or no audio samples");

            ProbedMediaInfo actual = MediaSource.ProbeInfo(outputPath);
            Console.WriteLine($"probed:   {actual.Width}x{actual.Height} {actual.VideoCodec}/{actual.AudioCodec} " +
                              $"{actual.Duration.ToSeconds():0.###}s");
            IReadOnlyList<string> failures = Verify(expected, actual);
            if (failures.Count > 0)
                return Fail(string.Join("; ", failures));

            // The output must genuinely decode, not just carry plausible headers — at both ends: the container
            // duration is the longer of the streams, so only a frame near the end proves the video isn't short.
            using (MediaSource decoded = MediaSource.Open(outputPath, HardwareAccelMode.Disabled))
            using (var pool = new VideoFramePool(actual.Width, actual.Height))
            {
                if (!decoded.TryDecodeNextFrame(pool, out VideoFrame? first))
                    return Fail("output opened but no video frame decoded");
                first.Dispose();

                Timecode nearEnd = expected.Duration - Timecode.FromSeconds(0.5);
                decoded.SeekTo(nearEnd);
                if (!decoded.TryDecodeNextFrame(pool, out VideoFrame? last))
                    return Fail($"video stream ends before {nearEnd.ToSeconds():0.###}s");
                Timecode lastPts = last.Pts;
                last.Dispose();
                if (lastPts < nearEnd - DurationTolerance)
                    return Fail($"frame near the end decoded at {lastPts.ToSeconds():0.###}s, expected ≥ {nearEnd.ToSeconds():0.###}s");
            }
            Console.WriteLine("[ok]   output re-opened; first frame and a frame near the end decoded");

            Console.WriteLine("[export-check] RESULT: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(outDir))
                    Directory.Delete(outDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort temp cleanup; never turn a pass into a failure.
            }
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[export-check] FAIL: {message}");
        return 1;
    }
}
