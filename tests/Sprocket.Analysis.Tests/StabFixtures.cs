using System.Diagnostics;
using System.Globalization;

namespace Sprocket.Analysis.Tests;

/// <summary>
/// Generates (once) the deterministic fixture clips the <see cref="Sprocket.Analysis.Motion.MotionTrackAnalyzer"/>
/// integration tests run against, built with the <c>ffmpeg</c> CLI into the test output directory and cached
/// across runs (like <c>Sprocket.Media.Tests.TestVideo</c>). Each clip loops a single richly-textured
/// <c>testsrc2</c> still so the <em>only</em> motion is the one the filter graph applies — a known translation
/// (a moving crop window) or a known scale wobble (a sinusoidal zoom) — which the analyzer must recover.
/// </summary>
internal static class StabFixtures
{
    public const int Fps = 30;
    public const int DurationSeconds = 2;
    public const int FrameCount = Fps * DurationSeconds; // 60

    /// <summary>Output clip size (the moving-crop and zoom clips both produce this).</summary>
    public const int Width = 320;
    public const int Height = 240;

    /// <summary>The moving-crop shake amplitudes, in output pixels (from the <c>crop</c> expression below).</summary>
    public const int ShakeAmplitudeX = 24;
    public const int ShakeAmplitudeY = 16;

    /// <summary>The zoom wobble amplitude (fraction) of the pumping-zoom clip.</summary>
    public const double ZoomAmplitude = 0.03;

    private static readonly Lazy<string> LazyStill320 = new(() => Still(Width, Height, "stab-still-320.png"));
    // The shake base is 64 px larger each side so crop=iw-64:ih-64 yields a 320x240 window with room to move.
    private static readonly Lazy<string> LazyStill384 = new(() => Still(Width + 64, Height + 64, "stab-still-384.png"));

    private static readonly Lazy<string> LazyStatic = new(GenerateStatic);
    private static readonly Lazy<string> LazyShaking = new(GenerateShaking);
    private static readonly Lazy<string> LazyZoom = new(GenerateZoom);

    /// <summary>A static clip: a held textured still, so recovered motion is ~identity.</summary>
    public static string StaticPath => LazyStatic.Value;

    /// <summary>A clip whose 320×240 crop window pans by <c>x=32+24·sin(2πt)</c>, <c>y=32+16·cos(2πt)</c> over a
    /// static base — a known sinusoidal translation.</summary>
    public static string ShakingPath => LazyShaking.Value;

    /// <summary>A clip that sinusoidally zooms a static still by ±3 % — a known scale wobble (focus-breathing model).</summary>
    public static string ZoomPath => LazyZoom.Value;

    private static string GenerateStatic() => RunFfmpeg(
        "stab-static.mp4",
        $"-y -loop 1 -i \"{LazyStill320.Value}\" -t {DurationSeconds} -r {Fps} " +
        "-c:v libx264 -g 12 -pix_fmt yuv420p ");

    private static string GenerateShaking() => RunFfmpeg(
        "stab-shaking.mp4",
        $"-y -loop 1 -i \"{LazyStill384.Value}\" -t {DurationSeconds} -r {Fps} " +
        "-vf \"crop=iw-64:ih-64:x='32+24*sin(2*PI*t)':y='32+16*cos(2*PI*t)'\" " +
        "-c:v libx264 -g 12 -pix_fmt yuv420p ");

    private static string GenerateZoom()
    {
        // zoompan zooms only >= 1, so oscillate around 1.03 with a ±0.03 amplitude; centred so only scale changes.
        string z = string.Create(CultureInfo.InvariantCulture, $"1.03+{ZoomAmplitude}*sin(2*PI*on/{Fps})");
        return RunFfmpeg(
            "stab-zoom.mp4",
            $"-y -loop 1 -i \"{LazyStill320.Value}\" -t {DurationSeconds} " +
            $"-vf \"zoompan=z='{z}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s={Width}x{Height}:fps={Fps}\" " +
            "-c:v libx264 -g 12 -pix_fmt yuv420p ");
    }

    /// <summary>Renders one <c>testsrc2</c> frame to a still PNG (cached), the static base the clips loop.</summary>
    private static string Still(int w, int h, string fileName) => RunFfmpeg(
        fileName,
        $"-y -f lavfi -i testsrc2=size={w}x{h}:rate=1:duration=1 -frames:v 1 ");

    private static string RunFfmpeg(string fileName, string args)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
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
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed to generate '{fileName}'.\n{stderr}");
        return path;
    }
}
