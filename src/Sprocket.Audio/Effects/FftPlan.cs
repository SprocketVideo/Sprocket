using System.Numerics;

namespace Sprocket.Audio.Effects;

/// <summary>
/// A fixed-size radix-2 complex FFT over split real/imaginary <c>float</c> arrays (PLAN.md step 49): the
/// managed, deterministic transform behind the Convolution Reverb's partitioned convolver. The plan holds the
/// precomputed bit-reversal permutation and twiddle tables for one power-of-two size, so <see cref="Forward"/>
/// / <see cref="Inverse"/> allocate nothing and can run on the audio thread. The tables are read-only after
/// construction, so one plan is safely shared between the audio thread and the background IR preprocessor —
/// callers supply their own scratch buffers.
/// </summary>
internal sealed class FftPlan
{
    private readonly int[] _reverse;
    private readonly float[] _cos; // twiddle table: e^(-2πi·k/N) for k in [0, N/2)
    private readonly float[] _sin;

    public FftPlan(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "FFT size must be a power of two ≥ 2.");
        Size = size;
        int bits = BitOperations.Log2((uint)size);
        _reverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double angle = -2 * Math.PI * k / size;
            _cos[k] = (float)Math.Cos(angle);
            _sin[k] = (float)Math.Sin(angle);
        }
    }

    /// <summary>The transform length N.</summary>
    public int Size { get; }

    /// <summary>In-place forward DFT: <c>X[k] = Σ x[n]·e^(-2πi·kn/N)</c> (unscaled).</summary>
    public void Forward(Span<float> re, Span<float> im) => Transform(re, im, inverse: false);

    /// <summary>In-place inverse DFT, scaled by 1/N so <c>Inverse(Forward(x)) == x</c>.</summary>
    public void Inverse(Span<float> re, Span<float> im) => Transform(re, im, inverse: true);

    private void Transform(Span<float> re, Span<float> im, bool inverse)
    {
        int n = Size;
        if (re.Length != n || im.Length != n)
            throw new ArgumentException("Buffers must match the plan size.");

        // Bit-reversal permutation (each pair swapped exactly once).
        for (int i = 0; i < n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Iterative Cooley–Tukey butterflies; the inverse conjugates the twiddles.
        float sign = inverse ? -1f : 1f;
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len; // twiddle stride into the N/2 table
            for (int start = 0; start < n; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step];
                    float wi = sign * _sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    float tr = re[b] * wr - im[b] * wi;
                    float ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }

        if (inverse)
        {
            float scale = 1f / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= scale;
                im[i] *= scale;
            }
        }
    }
}
