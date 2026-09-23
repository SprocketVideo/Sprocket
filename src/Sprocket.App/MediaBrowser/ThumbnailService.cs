using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media;
using Sprocket.Render;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// Generates media-bin thumbnails (PLAN.md step 15, UI.md §3.3): a poster frame for video sources and a
/// waveform image for audio. Each is produced once on a background thread (decode is slow and must not block
/// the UI) and cached by source + size, then handed back as an Avalonia <see cref="Bitmap"/>. The encoded PNG is
/// also persisted in the per-user <see cref="ThumbnailDiskCache"/>, so re-opening a project reuses it instead of
/// re-decoding every source.
/// </summary>
/// <remarks>
/// Decoding a single poster frame / a stretch of PCM and rasterising it once is a one-off cost, NOT the
/// per-frame render hot path, so the no-managed-pixels rule (ARCHITECTURE.md §1) does not apply here — a
/// thumbnail is deliberately copied into a small managed bitmap. Poster decode forces the software path
/// (<see cref="HardwareAccelMode.Disabled"/>) so it is deterministic and carries no GPU dependency.
/// <para>
/// Opening a project with a large bin must not spike the CPU or stall the UI, so generation is throttled
/// process-wide (<see cref="GenerationGate"/>: a quarter of the cores, capped at 4), each thumbnail decoder runs
/// single-threaded (<see cref="MediaOpenRequest.DecoderThreads"/> = 1) instead of spawning a thread per core,
/// waveforms reduce PCM to peaks as it streams rather than buffering it, and <see cref="Dispose"/> cancels work
/// still queued. Disk-cache hits bypass the gate — they only read a small PNG.
/// </para>
/// </remarks>
public sealed class ThumbnailService : IDisposable
{
    // Background colour behind letterboxed poster frames (matches the panel's raised surface).
    private static readonly SKColor PosterBg = new(0x22, 0x22, 0x2B);
    private static readonly SKColor WaveBg = new(0x22, 0x22, 0x2B);
    private static readonly SKColor WaveFill = new(0x4F, 0x7A, 0x60);

    // At most this many mono samples are read for a waveform; longer audio is summarised from the lead-in.
    private const int MaxWaveformSamples = 4_000_000;

    // Process-wide cap on concurrent thumbnail generation (decode + rasterise). Static so several windows / a
    // project re-open share one budget; a quarter of the cores leaves the UI and playback headroom.
    private static readonly int GenerationConcurrency = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    private static readonly SemaphoreSlim GenerationGate = new(GenerationConcurrency, GenerationConcurrency);

    // Disk-cache kind tags (part of the cache key).
    private const string PosterKind = "poster";
    private const string WaveKind = "wave";
    private const string StripKind = "strip";

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ThumbnailDiskCache? _disk;
    private volatile bool _disposed;

    /// <summary>Creates the service over <paramref name="disk"/> (defaults to the per-user
    /// <see cref="ThumbnailDiskCache.Default"/>).</summary>
    public ThumbnailService(ThumbnailDiskCache? disk = null) => _disk = disk ?? ThumbnailDiskCache.Default;

    /// <summary>
    /// Returns the poster-frame thumbnail for a video source, scaled to fit <paramref name="width"/>×
    /// <paramref name="height"/> (letterboxed). Returns <see langword="null"/> for an offline / no-video source
    /// or on any decode failure. Cached; concurrent callers share one decode.
    /// </summary>
    public Task<Bitmap?> GetPosterAsync(MediaRef media, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_disposed || !media.Info.HasVideo)
            return Task.FromResult<Bitmap?>(null);

        string key = $"poster:{media.Id}:{width}x{height}";
        return _cache.GetOrAdd(key, _ => Task.Run(() =>
            ProduceAsync(media, PosterKind, width, height, frames: 0, ct => RenderPoster(media, width, height, ct))));
    }

    /// <summary>
    /// Returns the waveform thumbnail for an audio source at <paramref name="width"/>×<paramref name="height"/>.
    /// Returns <see langword="null"/> for an offline / no-audio source or on failure. Cached.
    /// </summary>
    public Task<Bitmap?> GetWaveformAsync(MediaRef media, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_disposed || !media.Info.HasAudio)
            return Task.FromResult<Bitmap?>(null);

        string key = $"wave:{media.Id}:{width}x{height}";
        return _cache.GetOrAdd(key, _ => Task.Run(() =>
            ProduceAsync(media, WaveKind, width, height, frames: 0, ct => RenderWaveform(media, width, height, ct))));
    }

    /// <summary>
    /// Returns a wide filmstrip bitmap for a video source — <paramref name="frames"/> evenly spaced frames laid
    /// side by side, each <paramref name="frameWidth"/>×<paramref name="frameHeight"/> (letterboxed) — for
    /// hover-scrubbing the bin thumbnail. Decoded once, lazily, and cached like posters. Returns
    /// <see langword="null"/> for a no-video source, a still (one frame, nothing to scrub), or on failure.
    /// </summary>
    public Task<Bitmap?> GetFilmstripAsync(MediaRef media, int frameWidth, int frameHeight, int frames = FilmstripMath.DefaultFrames)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_disposed || !media.Info.HasVideo || media.HasUnboundedDuration || frames <= 0)
            return Task.FromResult<Bitmap?>(null);

        string key = $"strip:{media.Id}:{frameWidth}x{frameHeight}x{frames}";
        return _cache.GetOrAdd(key, _ => Task.Run(() =>
            ProduceAsync(media, StripKind, frameWidth, frameHeight, frames, ct => RenderFilmstrip(media, frameWidth, frameHeight, frames, ct))));
    }

    /// <summary>
    /// The shared body of every thumbnail request, run on a pool thread: serve the disk cache when it holds this
    /// thumbnail (no gate — a hit is a small file read), otherwise wait for a <see cref="GenerationGate"/> slot,
    /// render the PNG, persist it, and decode it into a <see cref="Bitmap"/>. Returns <see langword="null"/> on
    /// failure or once <see cref="Dispose"/> has cancelled the service.
    /// </summary>
    private async Task<Bitmap?> ProduceAsync(MediaRef media, string kind, int width, int height, int frames,
        Func<CancellationToken, byte[]?> render)
    {
        CancellationToken ct = _cts.Token;
        if (ct.IsCancellationRequested)
            return null;

        string? diskKey = null;
        if (_disk is not null)
        {
            _disk.EnsurePruneStarted();
            diskKey = ThumbnailDiskCache.KeyFor(media, kind, width, height, frames);
            if (diskKey is not null && _disk.TryRead(diskKey) is { } cached && Decode(cached) is { } hit)
                return KeepUnlessDisposed(hit);
        }

        try
        {
            await GenerationGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        byte[]? png;
        try
        {
            // Re-check after the (possibly long) wait: a project closed while queued shouldn't start decoding.
            if (ct.IsCancellationRequested)
                return null;
            png = render(ct);
        }
        finally
        {
            GenerationGate.Release();
        }

        if (png is null)
            return null;
        if (diskKey is not null)
            _disk!.TryWrite(diskKey, png);
        return Decode(png) is { } bitmap ? KeepUnlessDisposed(bitmap) : null;
    }

    // A result that lands after Dispose has already swept the cache would otherwise leak its native bitmap.
    private Bitmap? KeepUnlessDisposed(Bitmap bitmap)
    {
        if (!_disposed)
            return bitmap;
        bitmap.Dispose();
        return null;
    }

    private static Bitmap? Decode(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png, writable: false);
            return new Bitmap(ms);
        }
        catch
        {
            return null; // a corrupt cache file falls through to regeneration
        }
    }

    // Thumbnail decoders run single-threaded: the gate already parallelises across sources, and a thread-per-core
    // pool per open is what made a big bin spike every core on project open.
    private static MediaOpenRequest ThumbnailOpenRequest(MediaRef media) =>
        MediaOpenRequest.FromMediaRef(media) with { DecoderThreads = 1 };

    private static byte[]? RenderFilmstrip(MediaRef media, int frameWidth, int frameHeight, int frames, CancellationToken ct)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || frames <= 0)
            return null;
        try
        {
            using MediaSource source = MediaSource.Open(ThumbnailOpenRequest(media), HardwareAccelMode.Disabled);
            using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);

            var dstInfo = new SKImageInfo(frames * frameWidth, frameHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface surface = SKSurface.Create(dstInfo);
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(PosterBg);

            for (int slot = 0; slot < frames; slot++)
            {
                if (ct.IsCancellationRequested)
                    return null;
                source.SeekTo(FilmstripMath.SampleTime(media.Info.Duration, frames, slot));
                if (!source.TryDecodeNextFrame(pool, out VideoFrame? frame))
                    continue; // a slot whose decode fails stays background-coloured rather than failing the strip

                using (frame)
                {
                    var srcInfo = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    using SKImage src = SKImage.FromPixels(srcInfo, frame.Pixels, frame.RowBytes);
                    SKRect cell = SKRect.Create(slot * frameWidth, 0, frameWidth, frameHeight);
                    SKRect dest = FramePresenter.ComputeFitRect(cell, frame.Width, frame.Height);
                    canvas.DrawImage(src, dest, new SKSamplingOptions(SKFilterMode.Linear));
                }
            }

            return Encode(surface);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? RenderPoster(MediaRef media, int width, int height, CancellationToken ct)
    {
        if (width <= 0 || height <= 0)
            return null;
        try
        {
            // An image sequence opens through the image2 demuxer; a still / ordinary file opens by path (step 42).
            using MediaSource source = MediaSource.Open(ThumbnailOpenRequest(media), HardwareAccelMode.Disabled);
            using var pool = new VideoFramePool(source.Info.Width, source.Info.Height);

            // Seek a little into the clip for a representative poster (avoids a black/leader first frame). A still has
            // only one frame at time zero, so never seek past it (PLAN.md step 42) — its poster is that frame.
            if (!media.HasUnboundedDuration)
            {
                Timecode poster = Timecode.Min(Timecode.FromSeconds(1), new Timecode(media.Info.Duration.Ticks / 2));
                if (poster.Ticks > 0)
                    source.SeekTo(poster);
            }

            if (ct.IsCancellationRequested || !source.TryDecodeNextFrame(pool, out VideoFrame? frame))
                return null;

            using (frame)
            {
                var dstInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using SKSurface surface = SKSurface.Create(dstInfo);
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(PosterBg);

                var srcInfo = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using SKImage src = SKImage.FromPixels(srcInfo, frame.Pixels, frame.RowBytes);
                SKRect dest = FramePresenter.ComputeFitRect(SKRect.Create(width, height), frame.Width, frame.Height);
                canvas.DrawImage(src, dest, new SKSamplingOptions(SKFilterMode.Linear));

                return Encode(surface);
            }
        }
        catch
        {
            return null; // offline media / unsupported codec — the tile shows a fallback (§15)
        }
    }

    private static byte[]? RenderWaveform(MediaRef media, int width, int height, CancellationToken ct)
    {
        if (width <= 0 || height <= 0)
            return null;
        try
        {
            int sampleRate = media.Info.SampleRate > 0 ? media.Info.SampleRate : 48000;
            using IPcmReader reader = AudioSource.Open(media.AbsolutePath, sampleRate, channels: 1);

            // Read up to a bounded number of mono samples and reduce them to one peak per output column.
            int buckets = Math.Max(1, width);
            var chunk = new float[8192];
            int n;
            float[] peaks;
            long expected = ExpectedWaveformSamples(media.Info.Duration, sampleRate);
            if (expected > 0)
            {
                // Known length: stream the reduction, so no sample buffer is ever materialised.
                var accumulator = new WaveformPeakAccumulator(buckets, expected);
                while (!accumulator.IsFull && (n = reader.Read(chunk)) > 0)
                {
                    if (ct.IsCancellationRequested)
                        return null;
                    accumulator.Add(chunk.AsSpan(0, n));
                }
                peaks = accumulator.Peaks;
            }
            else
            {
                // Unknown duration: the bucket mapping needs the total, so buffer (bounded) and reduce at the end.
                var samples = new List<float>();
                while (samples.Count < MaxWaveformSamples && (n = reader.Read(chunk)) > 0)
                {
                    if (ct.IsCancellationRequested)
                        return null;
                    for (int i = 0; i < n; i++)
                        samples.Add(chunk[i]);
                }
                peaks = WaveformBuilder.BuildPeaks(samples, channels: 1, bucketCount: buckets);
            }

            return DrawWaveform(peaks, width, height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The number of mono samples a waveform summarises: the probed <paramref name="duration"/> at
    /// <paramref name="sampleRate"/>, capped at <see cref="MaxWaveformSamples"/>; 0 when the duration is unknown.</summary>
    internal static long ExpectedWaveformSamples(Timecode duration, int sampleRate)
    {
        if (duration.Ticks <= 0 || sampleRate <= 0)
            return 0;
        long samples = (long)((Int128)duration.Ticks * sampleRate / Timecode.TicksPerSecond);
        return Math.Min(MaxWaveformSamples, samples);
    }

    private static byte[] DrawWaveform(float[] peaks, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(WaveBg);

        float mid = height / 2f;
        float maxAmp = mid - 2;
        using var paint = new SKPaint { Color = WaveFill, IsAntialias = false, StrokeWidth = 1 };
        for (int x = 0; x < peaks.Length && x < width; x++)
        {
            float amp = Math.Clamp(peaks[x], 0f, 1f) * maxAmp;
            canvas.DrawLine(x + 0.5f, mid - amp, x + 0.5f, mid + amp, paint);
        }

        // A faint centre line so silent regions still read as a waveform.
        using var centre = new SKPaint { Color = WaveFill.WithAlpha(80), StrokeWidth = 1 };
        canvas.DrawLine(0, mid, width, mid, centre);

        return Encode(surface);
    }

    // PNG-encodes the surface; the bytes are both persisted to the disk cache and decoded into the Bitmap.
    private static byte[] Encode(SKSurface surface)
    {
        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Disposes every cached bitmap and cancels queued / in-flight generation (a request still waiting
    /// for a gate slot never starts; a waveform stops at its next chunk). Late results are dropped.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        foreach (Task<Bitmap?> task in _cache.Values)
        {
            if (task.IsCompletedSuccessfully)
                task.Result?.Dispose();
        }
        _cache.Clear();
    }
}
