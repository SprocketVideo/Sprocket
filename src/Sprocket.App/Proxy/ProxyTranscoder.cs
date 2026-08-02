using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Sprocket.Core.Model;

namespace Sprocket.App.Proxy;

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
/// </remarks>
internal static class ProxyTranscoder
{
    /// <summary>
    /// How many encoder threads a proxy child process may use on a machine with <paramref name="processorCount"/>
    /// cores: half of them, floored at 1 and capped at 8. Half leaves the live decode/render/audio pipelines room
    /// on modest hardware; the cap keeps a many-core desktop from spawning far more x264 threads than the encode
    /// can usefully parallelise. Pure and testable — <see cref="Generate"/> passes the real core count.
    /// </summary>
    internal static int EncodeThreadCount(int processorCount) => Math.Clamp(processorCount / 2, 1, 8);

    /// <summary>
    /// Builds the proxy for <paramref name="media"/> at <paramref name="target"/> resolution, writing it to
    /// <paramref name="outputPath"/>. Returns <see langword="true"/> on success. Honours
    /// <paramref name="cancellationToken"/> (kills the child); any failure — bad source, no <c>ffmpeg</c> on PATH,
    /// non-zero exit — returns <see langword="false"/> so the source keeps previewing on its original (§15).
    /// </summary>
    public static bool Generate(MediaRef media, Resolution target, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (target.Width <= 0 || target.Height <= 0)
            return false;

        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4";

        // -an: video-only (audio mixes from the original). scale to the fixed proxy tier; ultrafast/CRF 28 = speed.
        // -threads: leave cores for the live preview (see the class remarks) rather than letting ffmpeg take all.
        string scale = string.Create(CultureInfo.InvariantCulture, $"scale={target.Width}:{target.Height}:flags=bilinear");
        int threads = EncodeThreadCount(Environment.ProcessorCount);
        var psi = new ProcessStartInfo("ffmpeg",
            $"-y -nostdin -loglevel error -i \"{media.AbsolutePath}\" -an -vf {scale} " +
            $"-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p -threads {threads.ToString(CultureInfo.InvariantCulture)} \"{tempPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return false;

            TryLowerPriority(process);

            // Wait for completion, but bail out promptly (killing the child) if the session is torn down.
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
