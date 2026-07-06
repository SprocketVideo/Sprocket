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
    private static readonly Lazy<string> LazyAudioOnlyPath = new(GenerateAudioOnly);
    private static readonly Lazy<string> LazyDLogMPath = new(GenerateDLogM);
    private static readonly Lazy<string> LazySLog3Path = new(GenerateSLog3);
    private static readonly Lazy<string> LazySequenceDir = new(GenerateSequence);
    private static readonly Lazy<string> LazyStillPath = new(GenerateStill);

    /// <summary>Frames in the numbered-PNG image-sequence fixture (PLAN.md step 42).</summary>
    public const int SequenceFrameCount = 12;

    /// <summary>Pixel size of the image-sequence / still fixtures.</summary>
    public const int ImageWidth = 64;
    public const int ImageHeight = 48;

    /// <summary>Absolute path to the fixture clip, generating it on first use.</summary>
    public static string Path => LazyPath.Value;

    /// <summary>Absolute path to an audio-only fixture (<c>.m4a</c>, AAC 48 kHz sine — no video stream),
    /// generating it on first use. Exercises the audio-only import probe (PLAN.md step 16b).</summary>
    public static string AudioOnlyPath => LazyAudioOnlyPath.Value;

    /// <summary>Absolute path to a short alpha-channel fixture clip (64×64 QuickTime Animation / <c>qtrle</c>,
    /// which stores <c>argb</c> — an alpha pixel format), generating it on first use. The whole frame is at 50%
    /// alpha so both alpha detection and alpha-byte preservation through swscale are verifiable (PLAN.md step 26).</summary>
    public static string AlphaPath => LazyAlphaPath.Value;

    /// <summary>The uniform alpha the alpha fixture is authored at (255 × 0.5 → 128), as it lands in the decoded
    /// RGBA buffer.</summary>
    public const int AlphaFixtureAlpha = 128;

    /// <summary>Absolute path to a fixture tagged like DJI log footage (PLAN.md step 37): declared bt709
    /// color metadata on the stream plus a container comment naming <c>D-Log M</c>, generating it on first
    /// use. Exercises the color-metadata probe + log-profile detection.</summary>
    public static string DLogMPath => LazyDLogMPath.Value;

    /// <summary>Absolute path to a fixture tagged like Sony S-Log3 footage (PLAN.md step 52): a container
    /// comment naming <c>S-Log3</c>, generating it on first use. Exercises the generalized
    /// <c>ColorProfiles.DetectLogProfile</c> dispatcher end-to-end through the Media probe, proving the
    /// non-DJI vendor heuristics are wired in (the dispatcher's own string-matching logic is unit-tested
    /// directly, without FFmpeg, in <c>Sprocket.Core.Tests</c>).</summary>
    public static string SLog3Path => LazySLog3Path.Value;

    /// <summary>Directory holding a numbered PNG run (<c>frame_0001.png</c>…) for the image-sequence tests
    /// (PLAN.md step 42), generating it on first use.</summary>
    public static string SequenceDir => LazySequenceDir.Value;

    /// <summary>The printf pattern the <c>image2</c> demuxer expands for <see cref="SequenceDir"/>.</summary>
    public static string SequencePattern => System.IO.Path.Combine(SequenceDir, "frame_%04d.png");

    /// <summary>Absolute path to a single still PNG with a 50%-alpha channel (PLAN.md step 42), generating it on
    /// first use — exercises the still probe (alpha intact, one held frame).</summary>
    public static string StillPath => LazyStillPath.Value;

    private static string Generate() => RunFfmpeg(
        "fixture.mp4",
        "-y " +
        $"-f lavfi -i testsrc2=size={Width}x{Height}:rate={Fps}:duration={DurationSeconds} " +
        $"-f lavfi -i sine=frequency=440:sample_rate={SampleRate}:duration={DurationSeconds} " +
        "-c:v libx264 -g 12 -pix_fmt yuv420p -c:a aac -shortest ");

    private static string GenerateAudioOnly() => RunFfmpeg(
        "fixture.m4a",
        "-y " +
        $"-f lavfi -i sine=frequency=440:sample_rate={SampleRate}:duration={DurationSeconds} " +
        "-c:a aac ");

    // The setparams filter (not the -color_* output flags, which FFmpeg 8's libx264 path drops for
    // primaries/transfer) is what reliably lands all four declarations in the encoded stream.
    private static string GenerateDLogM() => RunFfmpeg(
        "fixture-dlogm.mp4",
        "-y " +
        "-f lavfi -i \"testsrc2=size=64x64:rate=30:duration=1," +
        "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv\" " +
        "-c:v libx264 -pix_fmt yuv420p " +
        "-metadata comment=\"Shot in D-Log M\" ");

    private static string GenerateSLog3() => RunFfmpeg(
        "fixture-slog3.mp4",
        "-y " +
        "-f lavfi -i testsrc2=size=64x64:rate=30:duration=1 " +
        "-c:v libx264 -pix_fmt yuv420p " +
        "-metadata comment=\"Shot in S-Log3\" ");

    private static string GenerateAlpha() => RunFfmpeg(
        "fixture-alpha.mov",
        "-y " +
        "-f lavfi -i testsrc2=size=64x64:rate=30:duration=1 " +
        "-vf \"format=rgba,colorchannelmixer=aa=0.5\" -c:v qtrle ");

    private static string GenerateStill() => RunFfmpeg(
        "fixture-still.png",
        "-y " +
        $"-f lavfi -i testsrc2=size={ImageWidth}x{ImageHeight}:rate=1:duration=1 " +
        "-frames:v 1 -vf format=rgba ");

    /// <summary>Emits a numbered PNG run (<c>frame_0001.png</c>…<c>frame_00NN.png</c>) into a dedicated <c>seq</c>
    /// dir and returns that dir. testsrc2 varies per frame, so the frames differ.</summary>
    private static string GenerateSequence()
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "seq");
        Directory.CreateDirectory(dir);
        string first = System.IO.Path.Combine(dir, "frame_0001.png");
        if (File.Exists(first))
            return dir;

        double duration = (double)SequenceFrameCount / Fps;
        RunFfmpegTo(
            System.IO.Path.Combine(dir, "frame_%04d.png"),
            "-y " +
            $"-f lavfi -i testsrc2=size={ImageWidth}x{ImageHeight}:rate={Fps}:duration={duration.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"-frames:v {SequenceFrameCount} -start_number 1 ");
        return dir;
    }

    /// <summary>Runs the <c>ffmpeg</c> CLI to produce <paramref name="fileName"/> in the test <c>fixtures</c> dir
    /// with the given <paramref name="args"/> (the output path is appended), cached across runs.</summary>
    private static string RunFfmpeg(string fileName, string args)
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, fileName);
        if (File.Exists(path))
            return path;
        RunFfmpegTo(path, args);
        return path;
    }

    /// <summary>Runs the <c>ffmpeg</c> CLI with <paramref name="args"/> and the given output <paramref name="path"/>
    /// appended (a plain file or a printf pattern for an image run).</summary>
    private static void RunFfmpegTo(string path, string args)
    {
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
            throw new InvalidOperationException($"ffmpeg failed to generate the fixture.\n{stderr}");
    }
}
