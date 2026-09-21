using System.Numerics;

namespace Sprocket.Analysis.Features;

/// <summary>
/// Shi-Tomasi ("good features to track") corner detector with spatial bucketing. The image is split
/// into a grid of buckets and each bucket keeps only its strongest corners, so features stay spread
/// across the frame instead of clumping on the highest-contrast region — this both improves the
/// global motion fit and re-seeds sparse regions (a bucket with weak texture still contributes its
/// best local corners). Reuses its scratch buffers across calls, so steady-state detection allocates
/// nothing.
/// </summary>
public sealed class CornerDetector
{
    // Corner must be a 3x3 local maximum whose score clears this fraction of the frame's peak score
    // (OpenCV goodFeaturesToTrack default quality level).
    private const float QualityLevel = 0.01f;

    // Keep features off the very edge where gradients and tracking windows degrade.
    private const int Margin = 6;

    private float[] _ix = [];
    private float[] _iy = [];
    private float[] _score = [];
    private int _capW;
    private int _capH;

    // Per-bucket top-k bookkeeping, reused each call.
    private float[] _bucketScore = [];
    private float[] _bucketX = [];
    private float[] _bucketY = [];

    /// <summary>
    /// Detects corners into <paramref name="output"/>, spread across a
    /// <paramref name="bucketsX"/>×<paramref name="bucketsY"/> grid with at most
    /// <paramref name="capPerBucket"/> corners per bucket. Returns the number written.
    /// </summary>
    public int Detect(GrayImage img, int bucketsX, int bucketsY, int capPerBucket, Span<Vector2> output)
    {
        if (bucketsX <= 0 || bucketsY <= 0 || capPerBucket <= 0)
            throw new ArgumentOutOfRangeException(nameof(capPerBucket));

        int w = img.Width, h = img.Height;
        EnsureBuffers(w, h, bucketsX * bucketsY * capPerBucket);

        // 1. Gradients (central difference) over the whole image.
        float[] ix = _ix, iy = _iy;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                ix[i] = (img.Pixels[i + 1] - img.Pixels[i - 1]) * 0.5f;
                iy[i] = (img.Pixels[i + w] - img.Pixels[i - w]) * 0.5f;
            }
        }

        // 2. Shi-Tomasi score (min eigenvalue of the 3x3 structure tensor) + frame peak.
        float[] score = _score;
        Array.Clear(score, 0, w * h);
        float peak = 0f;
        for (int y = Margin; y < h - Margin; y++)
        {
            int row = y * w;
            for (int x = Margin; x < w - Margin; x++)
            {
                float a = 0f, b = 0f, c = 0f;
                for (int wy = -1; wy <= 1; wy++)
                {
                    int wr = (y + wy) * w + x;
                    for (int wx = -1; wx <= 1; wx++)
                    {
                        float gx = ix[wr + wx];
                        float gy = iy[wr + wx];
                        a += gx * gx;
                        b += gx * gy;
                        c += gy * gy;
                    }
                }
                // Smaller eigenvalue of [[a,b],[b,c]].
                float t = (a - c) * 0.5f;
                float lambda = (a + c) * 0.5f - MathF.Sqrt(t * t + b * b);
                score[row + x] = lambda;
                if (lambda > peak) peak = lambda;
            }
        }

        if (peak <= 0f)
            return 0;

        float threshold = peak * QualityLevel;

        // 3. Bucketed top-k selection over 3x3 local maxima above threshold.
        int buckets = bucketsX * bucketsY;
        Array.Fill(_bucketScore, -1f, 0, buckets * capPerBucket);

        for (int y = Margin; y < h - Margin; y++)
        {
            int row = y * w;
            for (int x = Margin; x < w - Margin; x++)
            {
                float s = score[row + x];
                if (s < threshold)
                    continue;
                // 3x3 non-maximum suppression.
                if (s < score[row + x - 1] || s < score[row + x + 1] ||
                    s < score[row - w + x] || s < score[row + w + x] ||
                    s < score[row - w + x - 1] || s < score[row - w + x + 1] ||
                    s < score[row + w + x - 1] || s < score[row + w + x + 1])
                    continue;

                int bx = x * bucketsX / w;
                int by = y * bucketsY / h;
                if (bx >= bucketsX) bx = bucketsX - 1;
                if (by >= bucketsY) by = bucketsY - 1;
                InsertIntoBucket((by * bucketsX + bx) * capPerBucket, capPerBucket, s, x, y);
            }
        }

        // 4. Gather kept corners.
        int n = 0;
        int total = buckets * capPerBucket;
        for (int i = 0; i < total && n < output.Length; i++)
        {
            if (_bucketScore[i] >= 0f)
                output[n++] = new Vector2(_bucketX[i], _bucketY[i]);
        }
        return n;
    }

    /// <summary>Keeps the top <paramref name="cap"/> scores in a bucket slot range, replacing the weakest.</summary>
    private void InsertIntoBucket(int start, int cap, float s, int x, int y)
    {
        int minIdx = start;
        float minVal = _bucketScore[start];
        for (int i = start; i < start + cap; i++)
        {
            float v = _bucketScore[i];
            if (v < 0f)   // empty slot: take it directly
            {
                _bucketScore[i] = s;
                _bucketX[i] = x;
                _bucketY[i] = y;
                return;
            }
            if (v < minVal)
            {
                minVal = v;
                minIdx = i;
            }
        }
        if (s > minVal)
        {
            _bucketScore[minIdx] = s;
            _bucketX[minIdx] = x;
            _bucketY[minIdx] = y;
        }
    }

    private void EnsureBuffers(int w, int h, int bucketSlots)
    {
        int pixels = w * h;
        if (_capW != w || _capH != h)
        {
            if (_ix.Length < pixels) { _ix = new float[pixels]; _iy = new float[pixels]; _score = new float[pixels]; }
            _capW = w;
            _capH = h;
        }
        if (_bucketScore.Length < bucketSlots)
        {
            _bucketScore = new float[bucketSlots];
            _bucketX = new float[bucketSlots];
            _bucketY = new float[bucketSlots];
        }
    }
}
