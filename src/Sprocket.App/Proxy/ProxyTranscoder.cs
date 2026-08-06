using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App.Proxy;

/// <summary>
/// The seam <see cref="ProxyService"/> builds proxies through (PLAN.md step 18). Production is
/// <see cref="FfmpegProxyTranscoder"/>, which shells out to the <c>ffmpeg</c> CLI; tests substitute a fake so the
/// service's state machine — pause/cancel/requeue, stale-completion guards, progress throttling — is exercised
/// headlessly without an encoder on PATH.
/// </summary>
public interface IProxyTranscoder
{
    /// <summary>
    /// Builds the proxy for <paramref name="media"/> at <paramref name="target"/> resolution, writing it to
    /// <paramref name="outputPath"/> and reporting a 0..1 completion fraction to <paramref name="progress"/>.
    /// Returns <see langword="true"/> only on a clean, complete build. Must honour
    /// <paramref name="cancellationToken"/> promptly and must never throw for an ordinary failure (a bad source, no
    /// encoder available) — it returns <see langword="false"/> so the source keeps previewing on its original (§15).
    /// </summary>
    bool Generate(MediaRef media, Resolution target, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>The production <see cref="IProxyTranscoder"/>: a thin adapter over <see cref="ProxyTranscoder"/>.</summary>
public sealed class FfmpegProxyTranscoder : IProxyTranscoder
{
    /// <inheritdoc />
    public bool Generate(MediaRef media, Resolution target, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
        ProxyTranscoder.Generate(media, target, outputPath, progress, cancellationToken);
}

/// <summary>
/// Generates one lower-resolution preview proxy for a source by invoking the <c>ffmpeg</c> CLI out-of-process
/// (PLAN.md step 18). Proxies are video-only (audio always reads from the original through the mixer) and encoded
/// for <b>speed, not size</b> — x264 <c>ultrafast</c>, the documented cross-platform fallback (hardware /
/// all-intra codecs for the cache are a later refinement, step 23c / ARCHITECTURE.md §11).
/// </summary>
/// <remarks>
/// <para><b>Why a separate process, not the in-process <see cref="Sprocket.Media.MediaEncoder"/>:</b> proxy
/// generation runs in the background <em>while</em> the live preview is decoding and the GPU compositor is
/// rendering. Driving a second libav* muxer/encoder in-process alongside that pipeline proved fragile (a native
/// access violation in the muxer). Shelling out to the <c>ffmpeg</c> CLI keeps proxy encoding entirely off our
/// process's FFmpeg state and threads, can't corrupt the
/// live pipelines, and is cleanly cancellable by killing the child. If the <c>ffmpeg</c> CLI isn't on PATH the
/// build simply fails and the source keeps previewing on its original (§15) — no crash, no dead-end.</para>
/// <para>The output is written to a temp file and atomically promoted only on a clean exit, so a cancelled or
/// failed run never leaves a half-written proxy the resolver would mistake for a complete one.</para>
/// <para><b>The child is deliberately throttled</b> — capped thread count and below-normal scheduling priority
/// (see <see cref="EncodeThreadCount(int)"/>). Left alone, <c>ffmpeg</c> saturates every core, and the proxy build
/// runs precisely when the user is watching the preview: the decode ring, the GPU compositor, and the audio
/// callback all compete with it, and audio is the master clock (§6), so an underrun there desyncs video too.
/// Proxy generation is background work — finishing a minute later is invisible, a stuttering preview is not.</para>
/// <para><b>Both child streams are drained asynchronously.</b> <c>-progress pipe:1</c> makes <c>ffmpeg</c> write
/// to stdout continuously for the whole encode; an unread pipe buffer fills and blocks the child forever, which
/// would deadlock against the <see cref="Process.WaitForExit(int)"/> poll below. Reading stdout also happens to be
/// where per-file progress comes from, and stderr is kept as a short ring for failure diagnostics.</para>
/// </remarks>
internal static class ProxyTranscoder
{
    /// <summary>How many trailing stderr lines to keep for a failure message (ffmpeg's last words are the useful
    /// ones; the stream is drained regardless of whether we keep any of it).</summary>
    private const int StderrRingSize = 8;

    /// <summary>
    /// How many encoder threads a proxy child process may use on a machine with <paramref name="processorCount"/>
    /// cores: half of them, floored at 1 and capped at 8. Half leaves the live decode/render/audio pipelines room
    /// on modest hardware; the cap keeps a many-core desktop from spawning far more x264 threads than the encode
    /// can usefully parallelise. Pure and testable — <see cref="Generate"/> passes the real core count.
    /// </summary>
    internal static int EncodeThreadCount(int processorCount) => Math.Clamp(processorCount / 2, 1, 8);

    /// <summary>
    /// The 0..1 completion fraction implied by one <c>-progress</c> line from <c>ffmpeg</c>, or
    /// <see langword="null"/> when the line carries no usable position (any other key, an <c>N/A</c> value, or an
    /// unknown source duration). <c>ffmpeg</c> writes one <c>key=value</c> per line and repeats the block every
    /// reporting interval; only <c>out_time_us</c> (microseconds of output written) tells us how far along it is.
    /// Pure and testable — the microsecond→tick conversion goes through the global time base (§3), never
    /// <c>double</c> seconds.
    /// </summary>
    internal static double? ProgressFraction(string? line, long durationTicks)
    {
        if (line is null || durationTicks <= 0)
            return null;

        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal))
            return null;
        if (!long.TryParse(line.AsSpan(key.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds)
            || microseconds < 0)
        {
            return null; // "N/A" before the first frame is written, or a malformed line
        }

        // us → ticks: 240000 ticks/s ÷ 1e6 us/s. Int128 keeps a multi-hour source exact.
        long ticks = (long)((Int128)microseconds * Timecode.TicksPerSecond / 1_000_000);
        return Math.Clamp((double)ticks / durationTicks, 0, 1);
    }

    /// <summary>
    /// The <c>-vf scale=…</c> argument for a build, or <see cref="string.Empty"/> when the target equals the
    /// source size — a same-resolution codec-conversion proxy (a 1080p HEVC/10-bit source at the FullHd tier)
    /// needs no spatial filter; the <c>-pix_fmt yuv420p</c> conversion is the proxy's whole value there. Pure
    /// and testable.
    /// </summary>
    internal static string ScaleArgs(int srcWidth, int srcHeight, Resolution target) =>
        target.Width == srcWidth && target.Height == srcHeight
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $"-vf scale={target.Width}:{target.Height}:flags=bilinear ");

    /// <summary>
    /// Builds the proxy for <paramref name="media"/> at <paramref name="target"/> resolution, writing it to
    /// <paramref name="outputPath"/> and reporting a 0..1 fraction to <paramref name="progress"/>. Returns
    /// <see langword="true"/> on success. Honours <paramref name="cancellationToken"/> (kills the child); any
    /// failure — bad source, no <c>ffmpeg</c> on PATH, non-zero exit — returns <see langword="false"/> so the
    /// source keeps previewing on its original (§15).
    /// </summary>
    public static bool Generate(
        MediaRef media, Resolution target, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (target.Width <= 0 || target.Height <= 0)
            return false;

        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4";
        long durationTicks = media.Info.Duration.Ticks;

        // -an: video-only (audio mixes from the original). scale to the fixed proxy tier (omitted when the target
        // is the source size — a pure codec-conversion proxy); ultrafast/CRF 28 = speed.
        // -threads: leave cores for the live preview (see the class remarks) rather than letting ffmpeg take all.
        // -progress pipe:1 -nostats: machine-readable progress on stdout (both streams are drained below).
        string scale = ScaleArgs(media.Info.Width, media.Info.Height, target);
        int threads = EncodeThreadCount(Environment.ProcessorCount);
        var psi = new ProcessStartInfo("ffmpeg",
            $"-y -nostdin -loglevel error -progress pipe:1 -nostats -i \"{media.AbsolutePath}\" -an {scale}" +
            $"-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p -threads {threads.ToString(CultureInfo.InvariantCulture)} \"{tempPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        Process? process = null;
        var stderrTail = new Queue<string>(StderrRingSize);
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return false;

            double lastReported = -1;
            process.OutputDataReceived += (_, e) =>
            {
                if (ProgressFraction(e.Data, durationTicks) is not { } fraction || fraction <= lastReported)
                    return;
                lastReported = fraction;
                progress?.Report(fraction);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                lock (stderrTail)
                {
                    if (stderrTail.Count == StderrRingSize)
                        stderrTail.Dequeue();
                    stderrTail.Enqueue(e.Data);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            TryLowerPriority(process);

            // Wait for completion, but bail out promptly (killing the child) if the build is cancelled — a pause,
            // a tier change, or session teardown.
            while (!process.WaitForExit(200))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return false;
                }
            }

            if (process.ExitCode != 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                return false;

            File.Move(tempPath, outputPath, overwrite: true); // promote atomically, replacing any stale file
            progress?.Report(1.0);
            return true;
        }
        catch
        {
            // ffmpeg not found on PATH, or any spawn/IO failure: no proxy, keep the original.
            if (process is not null)
                TryKill(process);
            return false;
        }
        finally
        {
            process?.Dispose();
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Drops the child to below-normal scheduling priority so the OS prefers the live preview's threads whenever
    /// they contend. Best-effort: lowering priority needs no privilege on any of our platforms, but a short source
    /// can exit before we get here (and some sandboxes refuse outright) — neither is a reason to fail the build.
    /// </summary>
    private static void TryLowerPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* already exited, or the platform/sandbox won't allow it — encode at normal priority */ }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup of the temp file */ }
    }
}
