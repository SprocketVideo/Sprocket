using System.Diagnostics;

namespace Sprocket.Media.Tests;

/// <summary>
/// Generates (once) a small, fully-deterministic fixture clip the decode/seek tests run against:
/// 320×240 video at exactly 30 fps for 3 s (90 frames) with a 12-frame GOP, plus a 48 kHz audio track —
/// so seek targets land mid-GOP (exercising decode-to-target discard) and audio probing has something to
/// read. Built with the <c>ffmpeg</c> CLI into the test output directory; cached across runs.
/// </summary>
internal static class TestVideo
{
    public const int Width = 320;
    public const int Height = 240;
    public const int Fps = 30;
    public const int DurationSeconds = 3;
    public const int FrameCount = Fps * DurationSeconds; // 90
    public const int SampleRate = 48000;

    private static readonly Lazy<string> LazyPath = new(Generate);

    /// <summary>Absolute path to the fixture clip, generating it on first use.</summary>
    public static string Path => LazyPath.Value;

    private static string Generate()
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "fixture.mp4");
        if (File.Exists(path))
            return path;

        string args =
            "-y " +
            $"-f lavfi -i testsrc2=size={Width}x{Height}:rate={Fps}:duration={DurationSeconds} " +
            $"-f lavfi -i sine=frequency=440:sample_rate={SampleRate}:duration={DurationSeconds} " +
            "-c:v libx264 -g 12 -pix_fmt yuv420p -c:a aac -shortest " +
            $"\"{path}\"";

        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg CLI not found on PATH to generate the test fixture.");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!File.Exists(path))
            throw new InvalidOperationException($"ffmpeg failed to generate the fixture.\n{stderr}");
        return path;
    }
}
