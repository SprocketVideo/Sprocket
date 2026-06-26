using System.Diagnostics;

namespace Sprocket.Playback.Tests;

/// <summary>
/// Generates (once) the deterministic fixture the engine integration tests decode: 320×240 at exactly
/// 30 fps for 3 s (90 frames), 12-frame GOP — so frame PTS are predictable and seeks land mid-GOP. Built
/// with the <c>ffmpeg</c> CLI into the test output directory; cached across runs.
/// </summary>
internal static class TestVideo
{
    public const int Width = 320;
    public const int Height = 240;
    public const int Fps = 30;
    public const int DurationSeconds = 3;
    public const int FrameCount = Fps * DurationSeconds; // 90

    private static readonly Lazy<string> LazyPath = new(Generate);

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
            "-c:v libx264 -g 12 -pix_fmt yuv420p " +
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
