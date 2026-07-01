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
    private static readonly Lazy<string> LazyAlphaPath = new(GenerateAlpha);

    /// <summary>Absolute path to the fixture clip, generating it on first use.</summary>
    public static string Path => LazyPath.Value;

    /// <summary>Absolute path to a short alpha-channel fixture clip (64×64 QuickTime Animation / <c>qtrle</c>,
    /// which stores <c>argb</c> — an alpha pixel format), generating it on first use. The whole frame is at 50%
    /// alpha so both alpha detection and alpha-byte preservation through swscale are verifiable (PLAN.md step 26).</summary>
    public static string AlphaPath => LazyAlphaPath.Value;

    /// <summary>The uniform alpha the alpha fixture is authored at (255 × 0.5 → 128), as it lands in the decoded
    /// RGBA buffer.</summary>
    public const int AlphaFixtureAlpha = 128;

    private static string Generate() => RunFfmpeg(
        "fixture.mp4",
        "-y " +
        $"-f lavfi -i testsrc2=size={Width}x{Height}:rate={Fps}:duration={DurationSeconds} " +
        $"-f lavfi -i sine=frequency=440:sample_rate={SampleRate}:duration={DurationSeconds} " +
        "-c:v libx264 -g 12 -pix_fmt yuv420p -c:a aac -shortest ");

    private static string GenerateAlpha() => RunFfmpeg(
        "fixture-alpha.mov",
        "-y " +
        "-f lavfi -i testsrc2=size=64x64:rate=30:duration=1 " +
        "-vf \"format=rgba,colorchannelmixer=aa=0.5\" -c:v qtrle ");

    /// <summary>Runs the <c>ffmpeg</c> CLI to produce <paramref name="fileName"/> in the test <c>fixtures</c> dir
    /// with the given <paramref name="args"/> (the output path is appended), cached across runs.</summary>
    private static string RunFfmpeg(string fileName, string args)
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, fileName);
        if (File.Exists(path))
            return path;

        var psi = new ProcessStartInfo("ffmpeg", args + $"\"{path}\"")
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
