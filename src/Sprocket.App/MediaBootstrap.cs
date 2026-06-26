using System;
using System.Diagnostics;
using System.IO;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Playback;

namespace Sprocket.App;

/// <summary>
/// Wires up the slice's playback session: opens a media file, builds a one-video-track
/// <see cref="Project"/> over it, and hands back a started <see cref="PlaybackEngine"/>. Real media import
/// (a bin, drag-drop, dialogs) is a later milestone (PLAN steps 11/15); for the slice the app opens the
/// path given on the command line, or generates a sample clip.
/// </summary>
internal static class MediaBootstrap
{
    /// <summary>The created engine and a human-readable status line, or a null engine and an error message.</summary>
    public readonly record struct Result(PlaybackEngine? Engine, string Status);

    /// <summary>Opens <c>args[0]</c> (if it is an existing file) or a generated sample, and builds the engine.</summary>
    public static Result Create(string[] args)
    {
        try
        {
            string path = args.Length > 0 && File.Exists(args[0]) ? args[0] : SampleClip.EnsureExists();

            MediaSource source = MediaSource.Open(path);
            ProbedMediaInfo info = source.Info;

            int sampleRate = info.SampleRate > 0 ? info.SampleRate : 48000;
            var timeline = new Timeline(info.FrameRate, new Resolution(info.Width, info.Height), sampleRate);
            var project = new Project(timeline);

            var mediaId = MediaRefId.New();
            project.MediaPool.Add(new MediaRef(mediaId, path, info));

            var track = new VideoTrack { Name = "V1" };
            track.Clips.Add(new Clip(mediaId, Timecode.Zero, info.Duration, Timecode.Zero));
            timeline.Tracks.Add(track);

            var feed = new RingVideoFrameFeed(new VideoDecodeRing(source));
            var engine = new PlaybackEngine(project, feed);
            engine.Start();

            string status =
                $"{Path.GetFileName(path)}  ·  {info.Width}×{info.Height}  ·  " +
                $"{Fps(info.FrameRate):0.##} fps  ·  {info.Duration.ToSeconds():0.0}s";
            return new Result(engine, status);
        }
        catch (Exception ex)
        {
            return new Result(null, $"Could not open media: {ex.Message}");
        }
    }

    private static double Fps(Rational r) => r.Den > 0 ? (double)r.Num / r.Den : 0;
}

/// <summary>
/// Ensures a 1080p sample clip exists to play when no file is given on the command line. Generated once with
/// the <c>ffmpeg</c> CLI into the app's output directory and cached (mirrors the spike's test asset).
/// </summary>
internal static class SampleClip
{
    public static string EnsureExists()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "assets");
        string path = Path.Combine(dir, "sample.mp4");
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("ffmpeg",
            "-y -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=6 " +
            "-f lavfi -i sine=frequency=440:sample_rate=48000:duration=6 " +
            $"-c:v libx264 -g 30 -pix_fmt yuv420p -c:a aac -shortest \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using Process? p = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "No media path given and the ffmpeg CLI was not found to generate a sample clip. " +
                "Pass a video file path as the first argument.");
        p.WaitForExit();

        if (!File.Exists(path))
            throw new InvalidOperationException("ffmpeg failed to generate the sample clip.");
        return path;
    }
}
