using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SkiaSharp;
using Sprocket.Audio;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Stabilization;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;

namespace Sprocket.Export;

/// <summary>Tunables for an export. The defaults produce a CRF-quality H.264 + AAC MP4 at the project's format,
/// so <c>default(ExportOptions)</c> reproduces the step-8 behaviour; set <see cref="Format"/> for the wider
/// container/codec matrix (PLAN.md step 27).</summary>
/// <param name="Format">The container × video-codec × audio-codec to deliver. <c>default</c> is MP4 / H.264 / AAC.</param>
/// <param name="Quality">The constant-quality (CRF) tier used in <see cref="ExportRateControl.Quality"/> mode when
/// no explicit <see cref="Crf"/> is set.</param>
/// <param name="Channels">Audio channel count to render and encode (default stereo).</param>
/// <param name="VideoBitRate">Target video bit rate in bits/s for <see cref="ExportRateControl.Bitrate"/> mode, or
/// <c>0</c> for a resolution-scaled default (<see cref="ExportCodecs.DefaultTargetBitrate"/>). Ignored in
/// <see cref="ExportRateControl.Quality"/> mode.</param>
/// <param name="AudioBitRate">Target audio bit rate in bits/s, or <c>0</c> for the encoder default.</param>
/// <param name="GopSize">Keyframe interval in frames, or <c>0</c> for the encoder default.</param>
/// <param name="RateControl">How the video size/quality trade-off is driven: constant quality (the default —
/// <see cref="Crf"/>/<see cref="Quality"/> hold the picture steady, size floats) or a target bit rate
/// (<see cref="VideoBitRate"/>/<see cref="MaxBitRate"/> make size predictable). The two-mode rate control
/// leading NLEs expose (Resolve's Quality vs "Restrict to", Premiere's VBR target).</param>
/// <param name="Crf">An explicit constant-quality value on the codec's own scale (x264/x265 0–51, AV1/VP9 0–63;
/// lower = better), or <c>0</c> to derive it from the <see cref="Quality"/> tier. Only used in
/// <see cref="ExportRateControl.Quality"/> mode.</param>
/// <param name="MaxBitRate">An optional VBR ceiling in bits/s for <see cref="ExportRateControl.Bitrate"/> mode
/// (the encoder's <c>maxrate</c>), or <c>0</c> for none. Ignored in quality mode.</param>
/// <param name="PixelFormat">An explicit encoder pixel-format name, or <see langword="null"/> to use the codec's
/// default (yuv420p for most; yuv422p10le for ProRes).</param>
/// <param name="HandleFrames">Export <b>handles</b> (PLAN.md step 29): extra frames rendered before the range's
/// in-point and after its out-point, for review / conform outputs. Clamped to the timeline, so handles only reach
/// media that exists there; with no in-out range (a whole-timeline export) there is nothing to extend into.</param>
/// <param name="BurnIns">Optional burn-in overlays (timecode / clip name / watermark) baked onto every exported
/// frame (PLAN.md step 29). <see langword="null"/> or empty means no burn-ins. These touch only the deterministic
/// export render, never the preview hot path (ARCHITECTURE.md §5/§7).</param>
/// <param name="Resolution">An explicit output resolution (PLAN.md step 29 presets), or <see langword="null"/> to
/// use the sequence's own. Either way it is capped at 4K and rounded to an even size by
/// <see cref="VideoExporter.ComputeExportResolution"/>.</param>
/// <param name="FrameRate">An explicit output frame rate (PLAN.md step 29 presets), or <see langword="null"/> to use
/// the sequence's own. The render graph is a pure function of time, so a different rate simply samples the timeline
/// at the new frame instants (frames duplicated / dropped as needed) — the standard NLE resample-on-export.</param>
/// <param name="Acceleration">Whether to encode with the deterministic software encoder (the default) or a platform
/// GPU encoder with automatic software fallback (PLAN.md step 29). <see cref="ExportAcceleration.Hardware"/> is a
/// speed option for review/intermediate outputs; final delivery keeps the software default for reproducibility.</param>
/// <param name="Preset">An explicit software-encoder speed/quality preset (x264: <c>"ultrafast"</c>…<c>"veryslow"</c>),
/// or <see langword="null"/> for the codec's default. Used by the render cache's speed-first intermediates
/// (PLAN.md step 32); hardware encoders ignore it.</param>
/// <param name="VideoOnly">Skips the audio stream entirely (no mixing, no audio encode). Used by the render cache's
/// video intermediates (PLAN.md step 32), whose audio side is cached separately as PCM.</param>
/// <param name="BakeColorTransform">Whether the per-clip input color transform (log → Rec.709, PLAN.md step 37)
/// is baked into the output (the default — deliverables match the preview) or stripped so the export <b>passes
/// through the log encoding</b> for downstream grading. Pass-through is pure plan surgery
/// (<see cref="RenderGraph.StripEffects"/>); the project is untouched.</param>
/// <param name="MetaTitle">Container <c>title</c> metadata tag, or <see langword="null"/>/empty for none
/// (PLAN.md step 38 export-metadata defaults; the export dialog prefills these from the user settings).</param>
/// <param name="MetaAuthor">Container author metadata (FFmpeg's generic <c>artist</c> key), or <see langword="null"/>.</param>
/// <param name="MetaCopyright">Container <c>copyright</c> metadata tag, or <see langword="null"/>.</param>
/// <param name="MetaComment">Container <c>comment</c> metadata tag, or <see langword="null"/> to keep the
/// default "Created with Sprocket" provenance note.</param>
/// <param name="AudioFormat">When set, the export is <b>audio-only</b> (PLAN.md step 44): the sequence's master mix
/// is written as sound in this format, with no video stream rendered or muxed. The video-side options
/// (<see cref="Format"/>'s codecs, <see cref="Resolution"/>, <see cref="FrameRate"/>, <see cref="BurnIns"/>,
/// <see cref="Acceleration"/>, <see cref="VideoOnly"/>) are ignored; <see langword="null"/> keeps the normal
/// A/V (or <see cref="VideoOnly"/>) export. Additive — <c>default(ExportOptions)</c> is unchanged (MP4/H.264/AAC).</param>
public readonly record struct ExportOptions(
    ExportFormat Format = default,
    ExportQuality Quality = ExportQuality.High,
    int Channels = 2,
    long VideoBitRate = 0,
    long AudioBitRate = 0,
    int GopSize = 0,
    ExportRateControl RateControl = ExportRateControl.Quality,
    int Crf = 0,
    long MaxBitRate = 0,
    string? PixelFormat = null,
    int HandleFrames = 0,
    IReadOnlyList<BurnIn>? BurnIns = null,
    Resolution? Resolution = null,
    Rational? FrameRate = null,
    ExportAcceleration Acceleration = ExportAcceleration.Software,
    string? Preset = null,
    bool VideoOnly = false,
    bool BakeColorTransform = true,
    string? MetaTitle = null,
    string? MetaAuthor = null,
    string? MetaCopyright = null,
    string? MetaComment = null,
    ExportAudioFormat? AudioFormat = null);

/// <summary>
/// Renders a <see cref="Project"/> offline to a full-resolution movie in the chosen container/codec matrix
/// (PLAN.md step 8 + step 27). This is the export half of "the same render graph serves preview and export"
/// (ARCHITECTURE.md §5): for each output frame it resolves the <see cref="VideoFramePlan"/> with
/// <see cref="RenderGraph"/> — exactly as the preview does — composites the layers onto an offscreen Skia surface
/// with the step-7 effect shaders, reads the pixels back, and hands them to the <see cref="MediaEncoder"/>. Audio
/// is mixed by <see cref="AudioMixer"/> over the same timeline and encoded alongside, interleaved by output
/// timestamp. Only the muxer/encoder back end changes with <see cref="ExportOptions.Format"/> — the render is
/// identical, so the export stays deterministic (§5/§17).
/// </summary>
/// <remarks>
/// <para>Export is throughput-bound, not real-time: it renders to a <b>raster</b> Skia surface (deterministic,
/// no GPU/display needed) and decodes sources in software at <b>full resolution</b> (never proxies, §17). The
/// determinism is what makes golden-frame export testing possible. The render is single-threaded for the slice;
/// the parallel decode→effect→encode pipeline the architecture allows is a later throughput optimization.</para>
/// <para>Frames are pulled lazily per source via <see cref="ExportFrameProvider"/> and the audio readers are
/// owned by the mixer, so a multi-minute timeline streams through with bounded memory. An offline/missing source
/// renders as black / silence rather than failing the export (§15).</para>
/// </remarks>
public static class VideoExporter
{
    /// <summary>
    /// Exports the project's active sequence to <paramref name="outputPath"/>. Reports progress in [0, 1] over the
    /// timeline and honours <paramref name="cancellationToken"/> between frames. Throws
    /// <see cref="ArgumentException"/> for an empty timeline.
    /// </summary>
    public static void Export(
        Project project,
        string outputPath,
        ExportOptions options = default,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IMotionTrackProvider? motionTracks = null)
        => Export(project, outputPath, options, sequenceId: null, range: null, progress, cancellationToken, motionTracks);

    /// <summary>
    /// Exports one sequence — or a sub-range of it — to <paramref name="outputPath"/> (PLAN.md step 29 export queue).
    /// <paramref name="sequenceId"/> selects which sequence (<see langword="null"/> = the project's active sequence);
    /// <paramref name="range"/> selects a half-open <c>[In, Out)</c> timeline slice (<see langword="null"/> = the
    /// whole timeline). The exported file's own timestamps start at zero regardless of the range start. Reports
    /// progress in [0, 1] over the exported range and honours <paramref name="cancellationToken"/> between frames.
    /// </summary>
    public static void Export(
        Project project,
        string outputPath,
        ExportOptions options,
        SequenceId? sequenceId,
        ExportRange? range,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IMotionTrackProvider? motionTracks = null)
        => ExportWithSummary(project, outputPath, options, sequenceId, range, progress, cancellationToken, motionTracks);

    /// <summary>
    /// Exports exactly as <see cref="Export(Project, string, ExportOptions, SequenceId?, ExportRange?, IProgress{double}?, CancellationToken, IMotionTrackProvider?)"/>
    /// and returns the measured <see cref="ExportRunSummary"/> — the encoder that actually opened, whether hardware
    /// engaged, what was written, and per-stage timings (export-speed phase 1). The summary is returned only after
    /// the output is finalized; cancellation / failure still throws and deletes the partial file.
    /// </summary>
    public static ExportRunSummary ExportWithSummary(
        Project project,
        string outputPath,
        ExportOptions options,
        SequenceId? sequenceId,
        ExportRange? range,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IMotionTrackProvider? motionTracks = null)
        => ExportCore(project, outputPath, options, sequenceId, range, progress, cancellationToken, motionTracks, pipelined: true);

    /// <summary>Output surfaces per render worker (export-speed phase 2): one being rendered while one waits for or
    /// is being encoded. Each is a full-size raster surface allocated once per export (~8 MB at 1080p, ~33 MB at 4K),
    /// so the steady state allocates no pixel memory.</summary>
    internal const int SurfacesPerWorker = 2;

    /// <summary>Upper bound on concurrent render workers (see <see cref="RenderWorkerCount"/>).</summary>
    internal const int MaxRenderWorkers = 4;

    /// <summary>
    /// The export implementation. <paramref name="pipelined"/> selects the staged pipeline (export-speed phase 2):
    /// N render workers each plan + composite every Nth frame into their own small ring of surfaces (with their own
    /// effect pipeline and decoders, each source prefetching its next frame in the background) while the calling
    /// thread muxes — encoding the frames in timeline order and interleaving the audio. Without it everything runs
    /// one frame at a time on the calling thread (the pre-phase-2 schedule). Rendering is a pure function of
    /// (project, t) and the encoder receives the identical frames and audio in the identical order, so both
    /// schedules produce byte-identical files; tests export both ways to prove it.
    /// </summary>
    /// <param name="renderWorkers">Forces the render worker count (tests), or 0 for the automatic
    /// <see cref="RenderWorkerCount"/>. A project with a CPU plugin effect always renders on one worker.</param>
    internal static ExportRunSummary ExportCore(
        Project project,
        string outputPath,
        ExportOptions options,
        SequenceId? sequenceId,
        ExportRange? range,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        IMotionTrackProvider? motionTracks,
        bool pipelined,
        int renderWorkers = 0)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        Sequence sequence = sequenceId is { } id
            ? project.GetSequence(id) ?? throw new ArgumentException($"No sequence with id {id} in the project.", nameof(sequenceId))
            : project.ActiveSequence;

        // Audio-only delivery (PLAN.md step 44): render the master mix as sound, no video stream. Takes over the whole
        // export — the video-side options are ignored — while reusing the range / handles / progress / cancellation
        // and partial-file cleanup below.
        if (options.AudioFormat is { } audioFormat)
            return ExportAudioOnly(project, sequence, outputPath, audioFormat, options, range, progress, cancellationToken);

        // Total covers setup (encoder/device open is user-visible latency) through the successful Finish().
        long totalStart = Stopwatch.GetTimestamp();

        // `default(ExportOptions)` leaves Channels = 0; treat that as the documented stereo default.
        int channels = options.Channels > 0 ? options.Channels : 2;

        ExportFormat format = options.Format;
        if (!format.IsValid)
            throw new ArgumentException(
                $"{ExportCodecs.Video(format.VideoCodec).DisplayName} / {ExportCodecs.Audio(format.AudioCodec).DisplayName} " +
                $"is not a valid combination for the {ExportCodecs.Container(format.Container).DisplayName} container.",
                nameof(options));

        Timeline timeline = sequence.Timeline;
        Timecode fullDuration = timeline.Duration;
        if (fullDuration <= Timecode.Zero)
            throw new ArgumentException("The timeline is empty — nothing to export.", nameof(project));

        Rational timelineFps = timeline.FrameRate;
        if (timelineFps.Num <= 0 || timelineFps.Den <= 0)
            throw new ArgumentException("The timeline has no valid frame rate.", nameof(project));

        // Optional frame-rate override (PLAN.md step 29 presets): the render graph is a pure function of time, so a
        // different output rate just samples the timeline at the new frame instants. A null / degenerate override
        // keeps the sequence's own rate (the pre-step-29 behaviour).
        Rational fps = options.FrameRate is { Num: > 0, Den: > 0 } overrideFps ? overrideFps : timelineFps;

        // Resolve the export sub-range, clamped to the sequence. The frame/sample loops below run over the range
        // duration, sampling the timeline at rangeIn + offset; encoder timestamps start at zero (a slice becomes a
        // file that plays from 0). A null range exports the whole timeline (the pre-step-29 behaviour).
        ExportRange effectiveRange = (range ?? ExportRange.Whole(fullDuration)).ClampTo(fullDuration);

        // Handles (PLAN.md step 29): grow the range by N frames each side for review / conform outputs, then
        // re-clamp — handles can only reach media that exists on the timeline, so a whole-timeline export is
        // unaffected and a slice extends only as far as its surrounding frames allow. Handles count in the timeline's
        // own frames (a frame of the source material), independent of any output frame-rate override.
        if (options.HandleFrames > 0)
        {
            Timecode handle = Timecode.FromFrames(options.HandleFrames, timelineFps);
            effectiveRange = effectiveRange.WithHandles(handle, handle).ClampTo(fullDuration);
        }

        Timecode rangeIn = effectiveRange.In;
        Timecode duration = effectiveRange.Duration;
        if (duration <= Timecode.Zero)
            throw new ArgumentException("The export range is empty — nothing to export.", nameof(range));

        IReadOnlyList<BurnIn>? burnIns = options.BurnIns is { Count: > 0 } ? options.BurnIns : null;

        // The output resolution is the preset's override (PLAN.md step 29) or the sequence's own. Either way it is
        // then capped at 4K (export-side limit only — the timeline/canvas are unrestricted, PLAN.md step 27), scaling
        // down to fit while preserving aspect, and rounded down to even (≤ 1px) so a 4:2:0 codec accepts it. The
        // offscreen surface uses the same size — render and encode agree, and the composite fits into it (a resolution
        // that changes the aspect letterboxes media, exactly as an NLE scale-on-export does).
        (int srcWidth, int srcHeight) = options.Resolution is { Width: > 0, Height: > 0 } outRes
            ? (outRes.Width, outRes.Height)
            : (timeline.Resolution.Width, timeline.Resolution.Height);
        (int outWidth, int outHeight) = ComputeExportResolution(srcWidth, srcHeight);
        if (outWidth <= 0 || outHeight <= 0)
            throw new ArgumentException("The timeline resolution is too small to export.", nameof(project));
        int sampleRate = timeline.SampleRate > 0 ? timeline.SampleRate : 48000;

        // Muted-only / solo-excluded timelines skip audio mixing + encoding entirely (planner-matched audibility).
        bool wantAudio = !options.VideoOnly && RenderGraph.HasAudibleAudio(project, sequence);

        VideoCodecInfo videoCodec = ExportCodecs.Video(format.VideoCodec);
        // Hardware acceleration (PLAN.md step 29): probe the platform GPU encoders for this codec before the
        // software encoder, which stays the guaranteed fallback (MediaEncoder engages the first that opens). The
        // software `CodecName` is unchanged, so a machine with no usable GPU produces the identical software output.
        IReadOnlyList<string>? hwCandidates = options.Acceleration == ExportAcceleration.Hardware
            ? ExportCodecs.HardwareEncoderCandidates(format.VideoCodec)
            : null;
        // Rate control: the mode decides which knob drives the encoder. Quality mode resolves a CRF (an explicit
        // value, else the tier's per-codec mapping) and leaves the bit rate 0; bitrate mode resolves a target
        // (an explicit rate, else the resolution-scaled default) and leaves CRF 0 — so the two never fight.
        bool bitrateMode = options.RateControl == ExportRateControl.Bitrate;
        int crf = bitrateMode ? 0
            : options.Crf > 0 ? options.Crf
            : ExportCodecs.CrfFor(format.VideoCodec, options.Quality);
        long bitRate = !bitrateMode ? 0
            : options.VideoBitRate > 0 ? options.VideoBitRate
            : ExportCodecs.DefaultTargetBitrate(outWidth, outHeight, fps);
        var video = new VideoEncoderSettings(
            outWidth, outHeight, fps,
            CodecName: videoCodec.EncoderName,
            PixelFormat: options.PixelFormat ?? videoCodec.PixelFormat,
            BitRate: bitRate,
            GopSize: options.GopSize,
            Crf: crf,
            MaxBitRate: bitrateMode ? options.MaxBitRate : 0,
            Preset: options.Preset ?? videoCodec.DefaultPreset,
            HardwareCandidates: hwCandidates);

        AudioEncoderSettings? audio = wantAudio
            ? new AudioEncoderSettings(sampleRate, channels, ExportCodecs.Audio(format.AudioCodec).EncoderName, options.AudioBitRate)
            : null;

        // Render workers (export-speed phase 2): each owns its own effect pipeline, decoders, and surface ring, and
        // renders every Nth frame. Built-in rendering is a pure function of (project, t), so any worker produces the
        // same pixels for a frame; CPU (frei0r) plugins keep native state across frames, so a timeline using one
        // renders on a single worker to keep its frame history intact.
        int workerCount = !pipelined ? 1
            : renderWorkers > 0 && !UsesCpuEffect(project) ? renderWorkers
            : RenderWorkerCount(project, outWidth, outHeight);
        var workers = new RenderWorker[workerCount];
        AudioMixer? mixer = null;
        MediaEncoder? encoder = null;
        float[] mixBuffer = [];
        bool completed = false;
        ExportRunSummary summary;

        try
        {
            MediaEncoder enc = encoder = MediaEncoder.Create(outputPath, video, audio, format.MuxerName, BuildMetadata(options));

            var info = new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            for (int w = 0; w < workers.Length; w++)
                workers[w] = new RenderWorker(info, pipelined ? SurfacesPerWorker : 1, prefetch: pipelined, motionTracks);
            var fullRect = SKRect.Create(0, 0, outWidth, outHeight);

            if (encoder.HasAudio)
            {
                mixer = new AudioMixer(sampleRate, channels, id => OpenPcmReader(project, id, sampleRate, channels));
                mixBuffer = new float[encoder.AudioFrameSize * channels];
            }

            long totalFrames = CountFrames(fps, duration);
            long totalSamples = encoder.HasAudio ? duration.ToSampleIndex(sampleRate) : 0;
            long nextVideoIndex = 0;
            long nextSample = 0;
            var muxTimer = new StageTimer(); // written by the mux stage only (render time lives on each worker)

            // Render stage: plan + composite output frame `index` into `target` with one worker's pipeline/decoders.
            // Render time is the call's elapsed time minus the decode it actually blocked on (background prefetch that
            // overlapped is not render time).
            void RenderInto(RenderWorker worker, long index, SKSurface target)
            {
                TimeSpan blockedBefore = worker.Providers.BlockingDecodeElapsed;
                long t0 = Stopwatch.GetTimestamp();
                RenderVideoFrame(project, sequence, rangeIn, index, fps, target, worker.Effects, fullRect, worker.Providers, burnIns, options.BakeColorTransform);
                worker.RenderTime += Stopwatch.GetElapsedTime(t0) - (worker.Providers.BlockingDecodeElapsed - blockedBefore);
            }

            // Mux stage: the one owner of the encoder. Emits whichever stream's next packet sits earlier on the
            // timeline, so the muxer interleaves cleanly; `acquire` yields the rendered surface for the next frame (in
            // order) and `release` hands it back once its pixels are encoded.
            void Mux(Func<long, SKSurface> acquire, Action release, CancellationToken token)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    bool videoDone = nextVideoIndex >= totalFrames;
                    bool audioDone = !enc.HasAudio || nextSample >= totalSamples;
                    if (videoDone && audioDone)
                        break;

                    long videoTick = videoDone ? long.MaxValue : Timecode.FromFrames(nextVideoIndex, fps).Ticks;
                    long audioTick = audioDone ? long.MaxValue : Timecode.FromSamples(nextSample, sampleRate).Ticks;

                    if (!videoDone && (audioDone || videoTick <= audioTick))
                    {
                        SKSurface frame = acquire(nextVideoIndex);
                        long t0 = Stopwatch.GetTimestamp();
                        using (SKPixmap pixels = frame.PeekPixels())
                            enc.WriteVideoFrame(pixels.GetPixels(), pixels.RowBytes, nextVideoIndex);
                        muxTimer.VideoEncode += Stopwatch.GetElapsedTime(t0);
                        release();
                        nextVideoIndex++;
                    }
                    else
                    {
                        int chunk = (int)Math.Min(enc.AudioFrameSize, totalSamples - nextSample);
                        Span<float> buffer = mixBuffer.AsSpan(0, chunk * channels);
                        long t0 = Stopwatch.GetTimestamp();
                        mixer!.MixInto(buffer, rangeIn + Timecode.FromSamples(nextSample, sampleRate), project, sequence);
                        long t1 = Stopwatch.GetTimestamp();
                        enc.WriteAudioFrame(buffer, nextSample);
                        muxTimer.AudioMix += Stopwatch.GetElapsedTime(t0, t1);
                        muxTimer.AudioEncode += Stopwatch.GetElapsedTime(t1);
                        nextSample += chunk;
                    }

                    progress?.Report(ComputeProgress(nextVideoIndex, fps, duration));
                }
            }

            if (pipelined)
            {
                RunPipelined(workers, totalFrames, RenderInto, Mux, cancellationToken);
            }
            else
            {
                RenderWorker only = workers[0];
                SKSurface surface = only.Surfaces[0];
                Mux(index => { RenderInto(only, index, surface); return surface; }, static () => { }, cancellationToken);
            }

            // Every frame is rendered: retire the decoders (waiting out any trailing prefetch) so the totals are final.
            TimeSpan decode = TimeSpan.Zero, render = TimeSpan.Zero;
            foreach (RenderWorker worker in workers)
            {
                worker.Providers.Dispose();
                decode += worker.Providers.DecodeElapsed;
                render += worker.RenderTime;
            }
            encoder.Finish();
            ExportStageTimings timings = new(
                decode, render, muxTimer.VideoEncode, muxTimer.AudioMix, muxTimer.AudioEncode,
                Stopwatch.GetElapsedTime(totalStart));
            summary = new ExportRunSummary(
                options.Acceleration, videoCodec.EncoderName, encoder.VideoEncoderName, encoder.IsHardwareVideo,
                nextVideoIndex, nextSample, timings);
            progress?.Report(1.0);
            completed = true;
        }
        finally
        {
            mixer?.Dispose(); // disposes the audio readers it owns
            foreach (RenderWorker? worker in workers)
                worker?.Dispose();
            encoder?.Dispose();

            // A failed or cancelled export never wrote the MP4 trailer (the moov atom): `MediaEncoder.Finish`
            // writes it, `Dispose` does not. Leaving the file behind hands the caller a full-size but
            // unplayable .mp4, so delete the partial output once the encoder has released its file handle.
            if (!completed)
                TryDelete(outputPath);
        }
        return summary;
    }

    /// <summary>The mux stage's accumulated encode / audio time (a mutable struct written only by the mux stage).
    /// Decode and render time are summed from the render workers.</summary>
    private struct StageTimer
    {
        public TimeSpan VideoEncode, AudioMix, AudioEncode;

        /// <summary>The timings of an export with no video stages (audio-only).</summary>
        public readonly ExportStageTimings ToAudioOnlyTimings(TimeSpan total) =>
            new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, AudioMix, AudioEncode, total);
    }

    /// <summary>The number of output frames covering <paramref name="duration"/>: the first index whose instant
    /// reaches the duration (a partial final frame still renders).</summary>
    private static long CountFrames(Rational fps, Timecode duration)
    {
        long frames = 0;
        while (Timecode.FromFrames(frames, fps) < duration)
            frames++;
        return frames;
    }

    /// <summary>
    /// How many render workers to run: roughly one per two cores (the encoder and decoders run their own threads),
    /// at most <see cref="MaxRenderWorkers"/>, halved above 1440p to bound memory (each worker holds
    /// <see cref="SurfacesPerWorker"/> output surfaces plus its decoders' frames), and exactly one when the project
    /// uses a CPU plugin effect — those keep native per-instance state across frames (a temporal frei0r filter's
    /// history), so splitting frames across instances would change their output.
    /// </summary>
    private static int RenderWorkerCount(Project project, int width, int height)
    {
        if (UsesCpuEffect(project))
            return 1;
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, MaxRenderWorkers);
        if ((long)width * height > 2560L * 1440)
            workers = Math.Max(1, workers / 2);
        return workers;
    }

    /// <summary>Whether any clip in any sequence carries a CPU (frei0r) plugin effect.</summary>
    private static bool UsesCpuEffect(Project project)
    {
        foreach (Sequence sequence in project.Sequences)
            foreach (Track track in sequence.Timeline.Tracks)
                foreach (Clip clip in track.Clips)
                    foreach (EffectInstance effect in clip.Effects)
                        if (SkiaEffectPipeline.IsCpuEffect(effect.EffectTypeId))
                            return true;
        return false;
    }

    /// <summary>
    /// Runs the render and mux stages concurrently (export-speed phase 2). Worker <c>w</c> of N renders frames
    /// <c>w, w+N, w+2N…</c> in order, each into a free surface of its own ring, on its own thread; the calling thread
    /// is the mux stage, taking frame <c>i</c> from worker <c>i mod N</c>'s FIFO — so frames reach the encoder in
    /// exact timeline order with no reorder buffer. Every queue is bounded by the worker's surface count, so a worker
    /// that gets ahead simply waits for the encoder (and the encoder waits for a slow frame).
    /// </summary>
    /// <remarks>A fault or cancellation anywhere cancels the shared token so every other stage unblocks; all workers
    /// are joined before returning, so no worker touches a surface or decoder after this method exits. User
    /// cancellation surfaces as <see cref="OperationCanceledException"/>; otherwise the <em>originating</em> fault is
    /// rethrown, never the cancellation it induced in its peers.</remarks>
    private static void RunPipelined(
        RenderWorker[] workers, long totalFrames,
        Action<RenderWorker, long, SKSurface> render,
        Action<Func<long, SKSurface>, Action, CancellationToken> mux,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int count = workers.Length;
        var tasks = new Task[count];
        for (int w = 0; w < count; w++)
        {
            RenderWorker worker = workers[w];
            long first = w;
            tasks[w] = Task.Factory.StartNew(() =>
            {
                try
                {
                    for (long index = first; index < totalFrames; index += count)
                    {
                        int slot = worker.Free.Take(stop.Token);
                        render(worker, index, worker.Surfaces[slot]);
                        worker.Filled.Add(slot, stop.Token);
                    }
                }
                catch
                {
                    stop.Cancel(); // unblock the mux stage and the other workers
                    throw;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        Exception? muxFault = null;
        try
        {
            RenderWorker? holder = null;
            int held = -1; // the surface being encoded, and the worker it belongs to (mux thread only)
            mux(
                index =>
                {
                    holder = workers[index % count];
                    held = holder.Filled.Take(stop.Token);
                    return holder.Surfaces[held];
                },
                () => holder!.Free.Add(held),
                stop.Token);
        }
        catch (Exception ex)
        {
            muxFault = ex;
            stop.Cancel(); // unblock the workers
        }

        Exception? renderFault = null;
        foreach (Task task in tasks)
        {
            try { task.Wait(CancellationToken.None); }
            catch (AggregateException ex) when (renderFault is null or OperationCanceledException)
            {
                renderFault = ex.InnerException;
            }
            catch (AggregateException) { /* keep the first real fault */ }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (muxFault is not null and not OperationCanceledException)
            ExceptionDispatchInfo.Throw(muxFault);
        if (renderFault is not null)
            ExceptionDispatchInfo.Throw(renderFault);
        if (muxFault is not null)
            ExceptionDispatchInfo.Throw(muxFault);
    }

    /// <summary>
    /// One render stage of the export pipeline: its own effect pipeline (compiled shaders, scratch surfaces, CPU
    /// stage), its own decoders, and a ring of output surfaces with the free / filled queues that bound it. Used by
    /// exactly one render thread at a time, so nothing inside needs locking; the queues are the only cross-thread
    /// hand-off (the mux stage takes filled surfaces and returns them free).
    /// </summary>
    private sealed class RenderWorker : IDisposable
    {
        public RenderWorker(SKImageInfo info, int surfaceCount, bool prefetch, IMotionTrackProvider? motionTracks)
        {
            Providers = new FrameProviders(prefetch);
            Effects = new SkiaEffectPipeline { MotionTracks = motionTracks };
            Surfaces = new SKSurface[surfaceCount];
            Free = new BlockingCollection<int>(surfaceCount);
            Filled = new BlockingCollection<int>(surfaceCount);
            try
            {
                for (int i = 0; i < surfaceCount; i++)
                {
                    Surfaces[i] = SKSurface.Create(info)
                        ?? throw new InvalidOperationException("Failed to create the offscreen export surface.");
                    Free.Add(i);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public FrameProviders Providers { get; }
        public SkiaEffectPipeline Effects { get; }
        public SKSurface[] Surfaces { get; }
        public BlockingCollection<int> Free { get; }
        public BlockingCollection<int> Filled { get; }

        /// <summary>Render time accumulated by this worker's thread (decode it blocked on excluded).</summary>
        public TimeSpan RenderTime { get; set; }

        public void Dispose()
        {
            Providers.Dispose();
            Effects.Dispose();
            foreach (SKSurface? surface in Surfaces)
                surface?.Dispose();
            Free.Dispose();
            Filled.Dispose();
        }
    }

    /// <summary>
    /// The export's full-resolution frame providers, one per source media, opened lazily and cached (a
    /// <see langword="null"/> entry is an offline / video-less source that renders black). Render-thread only; with
    /// <c>prefetch</c> each provider decodes its next frame in the background (export-speed phase 2).
    /// </summary>
    private sealed class FrameProviders(bool prefetch) : IDisposable
    {
        private readonly Dictionary<MediaRefId, ExportFrameProvider?> _providers = new();
        private bool _disposed;

        /// <summary>Total decode time across every opened provider, background prefetch included.</summary>
        public TimeSpan DecodeElapsed
        {
            get
            {
                TimeSpan sum = TimeSpan.Zero;
                foreach (ExportFrameProvider? provider in _providers.Values)
                    if (provider is not null)
                        sum += provider.DecodeElapsed;
                return sum;
            }
        }

        /// <summary>The decode time the render thread actually waited on, across every opened provider.</summary>
        public TimeSpan BlockingDecodeElapsed
        {
            get
            {
                TimeSpan sum = TimeSpan.Zero;
                foreach (ExportFrameProvider? provider in _providers.Values)
                    if (provider is not null)
                        sum += provider.BlockingDecodeElapsed;
                return sum;
            }
        }

        /// <summary>Resolves (and caches) the full-resolution frame provider for a media id, or <see langword="null"/>
        /// if the source is offline / has no video (it renders as black).</summary>
        public ExportFrameProvider? Resolve(Project project, MediaRefId id)
        {
            if (_providers.TryGetValue(id, out ExportFrameProvider? provider))
                return provider;

            provider = null;
            MediaRef? media = project.MediaPool.Get(id);
            if (media is { Info.HasVideo: true })
            {
                try
                {
                    // Software decode for bit-deterministic, GPU-independent export output. Export always pulls the
                    // full-resolution original — the request mapper routes an image sequence through the image2
                    // demuxer, and a still is held as one frame across its span (PLAN.md step 42), so preview == export.
                    MediaOpenRequest request = MediaOpenRequest.FromMediaRef(media);
                    provider = new ExportFrameProvider(
                        MediaSource.Open(request, HardwareAccelMode.Disabled),
                        isStill: media.Kind == MediaKind.Still, prefetch: prefetch);
                }
                catch
                {
                    provider = null; // offline/unreadable source → black frames, don't fail the export (§15)
                }
            }

            _providers[id] = provider;
            return provider;
        }

        /// <summary>Disposes every provider (waiting out in-flight prefetches). Idempotent; the decode totals stay
        /// readable afterwards.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (ExportFrameProvider? provider in _providers.Values)
                provider?.Dispose();
        }
    }

    /// <summary>
    /// Exports the sequence's master mix as an <b>audio-only</b> file (PLAN.md step 44): reuses
    /// <see cref="RenderGraph.PlanAudioBuffer"/> via the same <see cref="AudioMixer"/> the preview and A/V export use,
    /// so the output is bit-for-bit the mix heard in playback and measured by the loudness tools — but never opens a
    /// video encoder or renders a frame. Honours the export range / handles, reports progress, observes cancellation,
    /// and deletes a partial file on failure, matching the video export job plumbing.
    /// </summary>
    private static ExportRunSummary ExportAudioOnly(
        Project project, Sequence sequence, string outputPath, ExportAudioFormat audioFormat,
        ExportOptions options, ExportRange? range,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        long totalStart = Stopwatch.GetTimestamp();
        Timeline timeline = sequence.Timeline;
        Timecode fullDuration = timeline.Duration;
        if (fullDuration <= Timecode.Zero)
            throw new ArgumentException("The timeline is empty — nothing to export.", nameof(project));

        Rational timelineFps = timeline.FrameRate;
        if (timelineFps.Num <= 0 || timelineFps.Den <= 0)
            throw new ArgumentException("The timeline has no valid frame rate.", nameof(project));

        // Same range + handles resolution as the video path — handles count in the timeline's own frames and are
        // re-clamped, so a whole-timeline export is unaffected and a slice extends only as far as media allows.
        ExportRange effectiveRange = (range ?? ExportRange.Whole(fullDuration)).ClampTo(fullDuration);
        if (options.HandleFrames > 0)
        {
            Timecode handle = Timecode.FromFrames(options.HandleFrames, timelineFps);
            effectiveRange = effectiveRange.WithHandles(handle, handle).ClampTo(fullDuration);
        }

        Timecode rangeIn = effectiveRange.In;
        Timecode duration = effectiveRange.Duration;
        if (duration <= Timecode.Zero)
            throw new ArgumentException("The export range is empty — nothing to export.", nameof(range));

        int channels = options.Channels > 0 ? options.Channels : 2;
        int sampleRate = timeline.SampleRate > 0 ? timeline.SampleRate : 48000;

        AudioFormatInfo info = ExportCodecs.AudioFormat(audioFormat);
        // A lossless target ignores any target bit rate; a lossy one honours an explicit rate (0 → encoder default).
        long bitRate = info.Lossless ? 0 : options.AudioBitRate;
        var audio = new AudioEncoderSettings(sampleRate, channels, info.EncoderName, bitRate);

        MediaEncoder? encoder = null;
        AudioMixer? mixer = null;
        float[] mixBuffer = [];
        bool completed = false;
        ExportRunSummary summary;
        try
        {
            encoder = MediaEncoder.CreateAudioOnly(outputPath, audio, info.MuxerName, BuildMetadata(options));
            mixer = new AudioMixer(sampleRate, channels, id => OpenPcmReader(project, id, sampleRate, channels));
            mixBuffer = new float[encoder.AudioFrameSize * channels];

            long totalSamples = duration.ToSampleIndex(sampleRate);
            long nextSample = 0;
            var timer = new StageTimer();
            while (nextSample < totalSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunk = (int)Math.Min(encoder.AudioFrameSize, totalSamples - nextSample);
                Span<float> buffer = mixBuffer.AsSpan(0, chunk * channels);
                long t0 = Stopwatch.GetTimestamp();
                mixer.MixInto(buffer, rangeIn + Timecode.FromSamples(nextSample, sampleRate), project, sequence);
                long t1 = Stopwatch.GetTimestamp();
                encoder.WriteAudioFrame(buffer, nextSample);
                timer.AudioMix += Stopwatch.GetElapsedTime(t0, t1);
                timer.AudioEncode += Stopwatch.GetElapsedTime(t1);
                nextSample += chunk;

                progress?.Report(totalSamples <= 0 ? 1.0 : Math.Clamp((double)nextSample / totalSamples, 0.0, 1.0));
            }

            encoder.Finish();
            // Audio-only: no video stream, so no encoder names, no frames, and zero video timings.
            summary = new ExportRunSummary(
                options.Acceleration, RequestedVideoEncoder: "", ActualVideoEncoder: "", HardwareVideoEngaged: false,
                VideoFrames: 0, AudioSampleFrames: nextSample, timer.ToAudioOnlyTimings(Stopwatch.GetElapsedTime(totalStart)));
            progress?.Report(1.0);
            completed = true;
        }
        finally
        {
            mixer?.Dispose(); // disposes the audio readers it owns
            encoder?.Dispose();
            if (!completed)
                TryDelete(outputPath);
        }
        return summary;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup of a partial export; never mask the original failure */ }
    }

    /// <summary>The container metadata tags for an export, or <see langword="null"/> when none are set —
    /// FFmpeg generic keys (<c>artist</c> is the author tag), mapped per container by the muxer
    /// (PLAN.md step 38). Exposed for tests.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>>? BuildMetadata(in ExportOptions options)
    {
        List<KeyValuePair<string, string>>? tags = null;
        Add("title", options.MetaTitle);
        Add("artist", options.MetaAuthor);
        Add("copyright", options.MetaCopyright);
        Add("comment", options.MetaComment);
        return tags;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                (tags ??= []).Add(new KeyValuePair<string, string>(key, value.Trim()));
        }
    }

    /// <summary>4K delivery cap (PLAN.md step 27): DCI-4K on the long edge (UHD 3840 fits) and 4K frame height on
    /// the short edge, applied orientation-agnostically — a portrait 2160×3840 delivery is as legitimate as its
    /// landscape rotation and passes the cap unscaled. An export-side limit only — import, the timeline, and the
    /// sequence canvas are unrestricted.</summary>
    public const int MaxExportWidth = 4096;

    /// <inheritdoc cref="MaxExportWidth"/>
    public const int MaxExportHeight = 2160;

    /// <summary>Computes the encoded resolution for a sequence of <paramref name="width"/>×<paramref name="height"/>:
    /// scaled down (preserving aspect) to fit within the 4K cap when larger — the cap is orientation-aware (long
    /// edge ≤ <see cref="MaxExportWidth"/>, short edge ≤ <see cref="MaxExportHeight"/>), so portrait sequences cap
    /// at the rotated 4K frame — then rounded down to even so a 4:2:0 codec accepts it. Sequences at or below 4K
    /// encode at their exact even size. Exposed so the UI can show the resolution an export will actually produce.</summary>
    public static (int width, int height) ComputeExportResolution(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (0, 0);

        int longEdge = Math.Max(width, height);
        int shortEdge = Math.Min(width, height);
        double scale = Math.Min(1.0,
            Math.Min((double)MaxExportWidth / longEdge, (double)MaxExportHeight / shortEdge));
        int w = (int)Math.Round(width * scale);
        int h = (int)Math.Round(height * scale);
        // Clamp against the (orientation-aware) cap — rounding can nudge to cap+1 — then force even.
        (int maxW, int maxH) = width >= height ? (MaxExportWidth, MaxExportHeight) : (MaxExportHeight, MaxExportWidth);
        w = Math.Min(w, maxW) & ~1;
        h = Math.Min(h, maxH) & ~1;
        return (w, h);
    }

    /// <summary>Composites the frame at output index <paramref name="frameIndex"/> onto <paramref name="surface"/>:
    /// clear to black, then draw each resolved layer bottom→top with its effect chain, opacity, and blend. The
    /// timeline time sampled is <paramref name="rangeIn"/> + the frame's offset, so a sub-range export starts at the
    /// range's in-point while the output frame index (and thus the file) starts at zero.</summary>
    private static void RenderVideoFrame(
        Project project, Sequence sequence, Timecode rangeIn, long frameIndex, Rational fps,
        SKSurface surface, SkiaEffectPipeline pipeline, SKRect fullRect,
        FrameProviders providers,
        IReadOnlyList<BurnIn>? burnIns,
        bool bakeColorTransform = true)
    {
        Timecode t = rangeIn + Timecode.FromFrames(frameIndex, fps);
        VideoFramePlan plan = RenderGraph.PlanVideoFrame(project, sequence, t);
        // Pass-through-log export (PLAN.md step 37): strip the input color transform from the resolved plan
        // (pure plan surgery — the project and the shared preview path are untouched).
        if (!bakeColorTransform)
            plan = RenderGraph.StripEffects(plan, EffectTypeIds.ColorTransform);

        surface.Canvas.Clear(SKColors.Black);
        CompositePlan(project, plan, surface, pipeline, fullRect, providers);

        // Burn-ins are baked last, over the finished composite (PLAN.md step 29): the timecode shows the record
        // (timeline) time t, so a conform/review output's TC matches the project regardless of the export range.
        if (burnIns is not null)
            DrawBurnIns(surface.Canvas, fullRect, burnIns, project, sequence, t);

        surface.Canvas.Flush();
    }

    /// <summary>Resolves each burn-in to its display string at timeline time <paramref name="t"/> (Core) and draws
    /// the non-empty lines over the frame (Render). Kept off the preview path — export only.</summary>
    private static void DrawBurnIns(
        SKCanvas canvas, SKRect frame, IReadOnlyList<BurnIn> burnIns,
        Project project, Sequence sequence, Timecode t)
    {
        var lines = new List<(BurnInPosition Position, string Text)>(burnIns.Count);
        foreach (BurnIn item in burnIns)
        {
            string text = BurnInResolver.Resolve(item, project, sequence, t);
            if (!string.IsNullOrEmpty(text))
                lines.Add((item.Position, text));
        }
        if (lines.Count > 0)
            BurnInRenderer.Draw(canvas, frame, lines);
    }

    /// <summary>
    /// Composites one resolved plan onto <paramref name="surface"/> (already cleared by the caller): each layer
    /// bottom→top with its effect chain, opacity, and blend. A nested-sequence layer renders its child plan into a
    /// transparent offscreen surface and composites the result like any image layer (PLAN.md step 23) — the
    /// recursive "the graph turns a (timeline, t) into a frame" rule, on the deterministic export path.
    /// </summary>
    private static void CompositePlan(
        Project project, VideoFramePlan plan,
        SKSurface surface, SkiaEffectPipeline pipeline, SKRect bounds,
        FrameProviders providers)
    {
        SKCanvas canvas = surface.Canvas;
        pipeline.FrameTimeSeconds = plan.Time.Ticks / (double)Timecode.TicksPerSecond; // for time-driven CPU plugins (step 59)
        foreach (VideoLayer layer in plan.Layers)
        {
            switch (layer.Kind)
            {
                case LayerKind.Generator:
                    // A generator fills the sequence frame; render at full resolution then composite (PLAN.md step 19).
                    pipeline.DrawGenerator(
                        canvas, bounds, layer.Generator!, (int)bounds.Width, (int)bounds.Height,
                        layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode));
                    break;

                case LayerKind.Adjustment:
                    // Apply the adjustment's effects to everything composited beneath it (PLAN.md step 19).
                    pipeline.DrawAdjustment(surface, bounds, layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode));
                    break;

                case LayerKind.Sequence:
                {
                    // Render the child sequence into its own transparent surface, then composite it like any image
                    // layer with this (nesting) clip's effect chain / opacity / blend (PLAN.md step 23).
                    if (RenderNestedSequence(project, layer.NestedPlan!, pipeline, providers) is not { } nestedImage)
                        break;
                    using (nestedImage)
                    {
                        SKRect dest = FramePresenter.ComputeConformRect(bounds, nestedImage.Width, nestedImage.Height, layer.ConformMode);
                        bool clip = layer.ConformMode == ClipConformMode.Fill; // a fill rect overflows the frame — crop it
                        if (clip) { canvas.Save(); canvas.ClipRect(bounds); }
                        pipeline.DrawImageLayer(canvas, dest, nestedImage, layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode));
                        if (clip) canvas.Restore();
                    }
                    break;
                }

                case LayerKind.Transition:
                {
                    // Blend the two clips' frames per the transition (PLAN.md step 25). Each side's content is
                    // snapshotted into an independent image first, so a transition between two clips of the SAME
                    // source (one provider) doesn't have its first frame recycled by the second decode.
                    ResolvedTransition tr = layer.Transition!;
                    SKImage? fromImg = RenderSideContent(project, tr.From, pipeline, bounds, providers);
                    SKImage? toImg = RenderSideContent(project, tr.To, pipeline, bounds, providers);
                    SKBlendMode blend = ToBlendMode(layer.BlendMode);
                    try
                    {
                        // If a side is missing (offline/empty), composite the other on its own rather than failing.
                        if (fromImg is null && toImg is null)
                            break;
                        if (fromImg is null)
                        {
                            DrawSide(canvas, bounds, toImg!, tr.To, pipeline, layer.Opacity, blend);
                            break;
                        }
                        if (toImg is null)
                        {
                            DrawSide(canvas, bounds, fromImg, tr.From, pipeline, layer.Opacity, blend);
                            break;
                        }
                        pipeline.DrawTransition(
                            canvas, bounds, fromImg, tr.From.Effects, toImg, tr.To.Effects, tr, layer.Opacity, blend);
                    }
                    finally
                    {
                        fromImg?.Dispose();
                        toImg?.Dispose();
                    }
                    break;
                }

                default:
                {
                    ExportFrameProvider? provider = providers.Resolve(project, layer.MediaRefId);
                    VideoFrame? frame = provider?.GetFrame(layer.SourceTime, layer.Reverse);
                    if (frame is null)
                        continue;

                    SKRect dest = FramePresenter.ComputeConformRect(bounds, frame.Width, frame.Height, layer.ConformMode);
                    bool clip = layer.ConformMode == ClipConformMode.Fill; // a fill rect overflows the frame — crop it
                    if (clip) { canvas.Save(); canvas.ClipRect(bounds); }
                    pipeline.DrawLayer(
                        canvas, dest, frame.Pixels, frame.RowBytes, frame.Width, frame.Height,
                        layer.Effects, layer.Opacity, ToBlendMode(layer.BlendMode), frame.HasAlpha);
                    if (clip) canvas.Restore();
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Renders one side of a transition to a standalone content-only <see cref="SKImage"/> (no effects — those are
    /// applied by <see cref="SkiaEffectPipeline.DrawTransition"/>), or <see langword="null"/> when it produces no
    /// pixels (an offline media source / empty nested sequence). A media side is copied out of the decoder's buffer
    /// (<see cref="SKImage.FromPixelCopy(SKImageInfo, nint, int)"/>) so both sides stay valid even when they share a
    /// source/provider (PLAN.md step 25).
    /// </summary>
    private static SKImage? RenderSideContent(
        Project project, VideoLayer side, SkiaEffectPipeline pipeline, SKRect bounds,
        FrameProviders providers)
    {
        switch (side.Kind)
        {
            case LayerKind.Generator:
            {
                int w = Math.Max(1, (int)bounds.Width);
                int h = Math.Max(1, (int)bounds.Height);
                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                using SKSurface? offscreen = SKSurface.Create(info);
                if (offscreen is null)
                    return null;
                offscreen.Canvas.Clear(SKColors.Transparent);
                pipeline.DrawGenerator(offscreen.Canvas, SKRect.Create(0, 0, w, h), side.Generator!, w, h, []);
                offscreen.Canvas.Flush();
                return offscreen.Snapshot();
            }

            case LayerKind.Sequence:
                return RenderNestedSequence(project, side.NestedPlan!, pipeline, providers);

            default:
            {
                ExportFrameProvider? provider = providers.Resolve(project, side.MediaRefId);
                VideoFrame? frame = provider?.GetFrame(side.SourceTime, side.Reverse);
                if (frame is null)
                    return null;
                // Alpha sides copy out as straight (unpremultiplied) RGBA so the transition blend composites them
                // premultiplied-correctly; opaque sides stay Opaque (the alpha bytes are ignored). PLAN.md step 26.
                SKAlphaType alphaType = frame.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
                var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, alphaType);
                return SKImage.FromPixelCopy(info, frame.Pixels, frame.RowBytes);
            }
        }
    }

    /// <summary>Composites one transition side's image on its own (under the side clip's conform policy) — the
    /// graceful path when the other side produced no pixels.</summary>
    private static void DrawSide(
        SKCanvas canvas, SKRect bounds, SKImage image, VideoLayer side,
        SkiaEffectPipeline pipeline, double opacity, SKBlendMode blend)
    {
        SKRect dest = FramePresenter.ComputeConformRect(bounds, image.Width, image.Height, side.ConformMode);
        bool clip = side.ConformMode == ClipConformMode.Fill; // a fill rect overflows the frame — crop it
        if (clip) { canvas.Save(); canvas.ClipRect(bounds); }
        pipeline.DrawImageLayer(canvas, dest, image, side.Effects, opacity, blend);
        if (clip) canvas.Restore();
    }

    /// <summary>Renders a nested sequence's plan to a transparent offscreen <see cref="SKImage"/> at the child
    /// sequence's resolution (recursing for deeper nests), or <see langword="null"/> if the surface can't be made.</summary>
    private static SKImage? RenderNestedSequence(
        Project project, VideoFramePlan nestedPlan,
        SkiaEffectPipeline pipeline, FrameProviders providers)
    {
        int w = Math.Max(1, nestedPlan.Resolution.Width);
        int h = Math.Max(1, nestedPlan.Resolution.Height);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface? nested = SKSurface.Create(info);
        if (nested is null)
            return null;

        nested.Canvas.Clear(SKColors.Transparent); // transparent so empty child areas reveal the parent's lower layers
        CompositePlan(project, nestedPlan, nested, pipeline, SKRect.Create(0, 0, w, h), providers);
        nested.Canvas.Flush();
        return nested.Snapshot();
    }

    /// <summary>Opens a PCM reader for the mixer, or <see langword="null"/> (mixed as silence) when the media is
    /// offline / has no audio. The mixer owns and disposes the returned reader. Shared with the render cache's
    /// audio pre-render (PLAN.md step 32), which mixes the identical full-resolution sources.</summary>
    internal static IPcmReader? OpenPcmReader(Project project, MediaRefId id, int sampleRate, int channels)
    {
        MediaRef? media = project.MediaPool.Get(id);
        if (media is not { Info.HasAudio: true })
            return null;
        try
        {
            return AudioSource.Open(media.AbsolutePath, sampleRate, channels);
        }
        catch
        {
            return null;
        }
    }

    private static double ComputeProgress(long nextVideoIndex, Rational fps, Timecode duration)
    {
        double done = Timecode.FromFrames(nextVideoIndex, fps).ToSeconds();
        double total = duration.ToSeconds();
        return total <= 0 ? 1.0 : Math.Clamp(done / total, 0.0, 1.0);
    }

    private static SKBlendMode ToBlendMode(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Add => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };
}
