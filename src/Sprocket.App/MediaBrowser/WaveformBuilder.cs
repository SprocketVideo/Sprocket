using System;
using System.Collections.Generic;

namespace Sprocket.App.MediaBrowser;

/// <summary>
/// Reduces interleaved PCM to a small array of per-bucket amplitude peaks for drawing a waveform thumbnail
/// (PLAN.md step 15, UI.md §3.3). Pure and allocation-light so it is unit-testable without FFmpeg or a UI; the
/// <see cref="ThumbnailService"/> reads PCM through an <c>IPcmReader</c> and feeds it here, and the tile draws
/// the returned peaks as mirrored bars around a centre line.
/// </summary>
public static class WaveformBuilder
{
    /// <summary>
    /// Computes <paramref name="bucketCount"/> peaks in [0, 1] from interleaved float PCM. Each output bucket is
    /// the maximum absolute mono-mixed sample over its slice of the timeline. Returns an all-zero array when
    /// there is no audio. <paramref name="channels"/> must be ≥ 1.
    /// </summary>
    public static float[] BuildPeaks(IReadOnlyList<float> interleaved, int channels, int bucketCount)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be at least 1.");
        if (bucketCount < 1)
            throw new ArgumentOutOfRangeException(nameof(bucketCount), "Bucket count must be at least 1.");

        var peaks = new float[bucketCount];
        int frames = interleaved.Count / channels;
        if (frames == 0)
            return peaks;

        for (int frame = 0; frame < frames; frame++)
        {
            // Mono-mix this frame (average of channels) and track the peak in its bucket.
            double sum = 0;
            int baseIndex = frame * channels;
            for (int c = 0; c < channels; c++)
                sum += interleaved[baseIndex + c];
            float mono = (float)Math.Abs(sum / channels);

            // Map the frame to a bucket; the last frame maps to the last bucket exactly.
            int bucket = (int)((long)frame * bucketCount / frames);
            if (bucket >= bucketCount)
                bucket = bucketCount - 1;
            if (mono > peaks[bucket])
                peaks[bucket] = mono;
        }

        return peaks;
    }
}

/// <summary>
/// The streaming form of <see cref="WaveformBuilder.BuildPeaks"/> for mono PCM: feed chunks as they are decoded and
/// read <see cref="Peaks"/> at the end, instead of materialising the whole stretch of samples first (the bin's
/// waveform thumbnails used to buffer up to 4M samples per source). Because the bucket a sample lands in depends on
/// the total count, that count must be known up front — the caller derives it from the probed duration. Given
/// exactly <c>expectedFrames</c> samples the result is identical to <see cref="WaveformBuilder.BuildPeaks"/>;
/// samples past the expected count are ignored, and a short stream leaves its trailing buckets at zero.
/// </summary>
public sealed class WaveformPeakAccumulator
{
    private readonly float[] _peaks;
    private readonly long _expectedFrames;
    private long _frame;

    /// <summary>Creates an accumulator for <paramref name="bucketCount"/> peaks over
    /// <paramref name="expectedFrames"/> mono samples (both ≥ 1).</summary>
    public WaveformPeakAccumulator(int bucketCount, long expectedFrames)
    {
        if (bucketCount < 1)
            throw new ArgumentOutOfRangeException(nameof(bucketCount), "Bucket count must be at least 1.");
        if (expectedFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedFrames), "Expected frame count must be at least 1.");
        _peaks = new float[bucketCount];
        _expectedFrames = expectedFrames;
    }

    /// <summary>True once the expected number of samples has been consumed — the caller can stop reading.</summary>
    public bool IsFull => _frame >= _expectedFrames;

    /// <summary>The per-bucket peaks in [0, 1] accumulated so far (the live array; do not mutate).</summary>
    public float[] Peaks => _peaks;

    /// <summary>Folds the next chunk of mono samples into the peaks; anything past the expected count is dropped.</summary>
    public void Add(ReadOnlySpan<float> mono)
    {
        int bucketCount = _peaks.Length;
        for (int i = 0; i < mono.Length && _frame < _expectedFrames; i++, _frame++)
        {
            float amp = Math.Abs(mono[i]);
            // Same mapping as BuildPeaks: the last frame maps to the last bucket exactly.
            int bucket = (int)(_frame * bucketCount / _expectedFrames);
            if (bucket >= bucketCount)
                bucket = bucketCount - 1;
            if (amp > _peaks[bucket])
                _peaks[bucket] = amp;
        }
    }
}
