using System.Numerics;
using System.Runtime.InteropServices;

namespace Sprocket.Audio.Effects;

/// <summary>
/// One channel of zero-latency uniformly partitioned convolution (Gardner 1995; PLAN.md step 49). Partition 0
/// (the first <see cref="ImpulseResponse.BlockSize"/> taps) is convolved directly in the time domain sample by
/// sample, so the output never lags the input; partitions 1..P−1 run as overlap-save FFT blocks — each time a
/// B-sample input block completes, its spectrum enters a frequency-domain delay line and the tail contribution
/// for the <em>next</em> block is multiply-accumulated across every partition and inverse-transformed. Those
/// later partitions need input only from at least one block ago, which is exactly why the FFT half can be
/// scheduled a block ahead and the whole thing stays latency-free.
/// </summary>
/// <remarks>
/// Every buffer is allocated once in the constructor for the IR's partition count, so steady-state processing
/// is allocation-free (§1, §19); the head dot products and the complex MAC are <see cref="Vector{T}"/>-widened.
/// Per-sample cost is O(B) for the head plus an amortised O(P·N/B) for the tail. <see cref="PartitionGains"/>
/// lets the owner scale each partition's contribution per block — the IR-length trim shortens the tail that way
/// with no re-transform. Deterministic on a given machine: same input + IR ⇒ bit-identical output run to run
/// and regardless of host buffer framing (the vector lane count fixes the summation order, so results can
/// differ in the last bits between AVX2 / AVX-512 / NEON hosts).
/// </remarks>
internal sealed class PartitionedConvolver
{
    private const int B = ImpulseResponse.BlockSize;
    private const int N = ImpulseResponse.FftSize;

    private readonly float[] _headReversed;   // head taps reversed so both direct-conv halves are contiguous dots
    private readonly float[][] _hRe, _hIm;    // partitions 1..P−1 (shared with the IR — read-only)
    private readonly float[][] _fdlRe, _fdlIm; // ring of the last P−1 input-block spectra
    private float[] _cur, _prev;              // the block being filled and the one before it
    private readonly float[] _tail;           // this block's precomputed partitions-1..P−1 contribution
    private readonly float[] _accRe, _accIm;  // N-point MAC / transform scratch
    private int _pos;                         // write index within _cur
    private int _fdlHead;                     // ring slot of the most recent spectrum
    private int _blocksDone;                  // how many spectra the ring holds (bounded by its size)

    public PartitionedConvolver(ImpulseResponse ir, int channel)
    {
        ArgumentNullException.ThrowIfNull(ir);
        int ch = channel % ir.Channels; // a mono IR feeds every output channel
        float[] head = ir.Heads[ch];
        _headReversed = new float[B];
        for (int i = 0; i < B; i++)
            _headReversed[i] = head[B - 1 - i];
        _hRe = ir.SpectraRe[ch];
        _hIm = ir.SpectraIm[ch];
        int slots = _hRe.Length;
        _fdlRe = new float[slots][];
        _fdlIm = new float[slots][];
        for (int s = 0; s < slots; s++)
        {
            _fdlRe[s] = new float[N];
            _fdlIm[s] = new float[N];
        }
        _cur = new float[B];
        _prev = new float[B];
        _tail = new float[B];
        _accRe = new float[N];
        _accIm = new float[N];
        PartitionGains = new float[ir.PartitionCount];
        Array.Fill(PartitionGains, 1f);
    }

    /// <summary>Per-partition output gains (index 0 = the direct head), applied at MAC time. Owned by the effect,
    /// which rewrites them per block from the IR-length trim; length = <see cref="ImpulseResponse.PartitionCount"/>.</summary>
    public float[] PartitionGains { get; }

    /// <summary>Convolves one input sample, returning the output sample at the same time index (zero latency).</summary>
    public float Process(float x)
    {
        int p = _pos;
        _cur[p] = x;

        // Head: Σ_{m≤p} h[m]·cur[p−m] + Σ_{m>p} h[m]·prev[B+p−m]. With h reversed both sums are plain dot
        // products over contiguous spans: hr[B−1−p..B) · cur[0..p] and hr[0..B−1−p) · prev[p+1..B).
        int split = B - 1 - p;
        float y = Dot(_headReversed.AsSpan(split, p + 1), _cur.AsSpan(0, p + 1))
                + Dot(_headReversed.AsSpan(0, split), _prev.AsSpan(p + 1, split));
        y = y * PartitionGains[0] + _tail[p];

        if (++_pos == B)
            BlockDone();
        return y;
    }

    /// <summary>Clears all state (input history, spectra ring, pending tail).</summary>
    public void Reset()
    {
        _cur.AsSpan().Clear();
        _prev.AsSpan().Clear();
        _tail.AsSpan().Clear();
        foreach (float[] s in _fdlRe) s.AsSpan().Clear();
        foreach (float[] s in _fdlIm) s.AsSpan().Clear();
        _pos = 0;
        _fdlHead = 0;
        _blocksDone = 0;
    }

    /// <summary>A B-sample block just completed: transform [prev, cur], push its spectrum, and precompute the
    /// tail (partitions 1..P−1) for the block about to start.</summary>
    private void BlockDone()
    {
        int slots = _fdlRe.Length;
        if (slots > 0)
        {
            // Overlap-save frame = the last two blocks; its spectrum is the newest FDL entry.
            _fdlHead = (_fdlHead + 1) % slots;
            float[] xRe = _fdlRe[_fdlHead];
            float[] xIm = _fdlIm[_fdlHead];
            _prev.CopyTo(xRe, 0);
            _cur.CopyTo(xRe, B);
            xIm.AsSpan().Clear();
            ImpulseResponse.Plan.Forward(xRe, xIm);
            _blocksDone = Math.Min(_blocksDone + 1, slots);

            // Partition k pairs with the spectrum from k−1 blocks ago; slots not yet filled hold zeros, so the
            // loop is simply bounded by how many blocks have been seen.
            _accRe.AsSpan().Clear();
            _accIm.AsSpan().Clear();
            for (int k = 1; k <= _blocksDone; k++)
            {
                float g = PartitionGains[k];
                if (g == 0f)
                    continue;
                int slot = _fdlHead - (k - 1);
                if (slot < 0)
                    slot += slots;
                MultiplyAccumulate(_accRe, _accIm, _hRe[k - 1], _hIm[k - 1], _fdlRe[slot], _fdlIm[slot], g);
            }
            ImpulseResponse.Plan.Inverse(_accRe, _accIm);
            _accRe.AsSpan(B, B).CopyTo(_tail); // the linear (aliasing-free) half of the circular result
        }

        (_prev, _cur) = (_cur, _prev);
        _pos = 0;
    }

    /// <summary>acc += g · (H ⊙ X), complex, vector-widened.</summary>
    private static void MultiplyAccumulate(
        float[] accRe, float[] accIm, float[] hRe, float[] hIm, float[] xRe, float[] xIm, float g)
    {
        Span<Vector<float>> ar = MemoryMarshal.Cast<float, Vector<float>>(accRe.AsSpan());
        Span<Vector<float>> ai = MemoryMarshal.Cast<float, Vector<float>>(accIm.AsSpan());
        ReadOnlySpan<Vector<float>> hr = MemoryMarshal.Cast<float, Vector<float>>(hRe);
        ReadOnlySpan<Vector<float>> hi = MemoryMarshal.Cast<float, Vector<float>>(hIm);
        ReadOnlySpan<Vector<float>> xr = MemoryMarshal.Cast<float, Vector<float>>(xRe);
        ReadOnlySpan<Vector<float>> xi = MemoryMarshal.Cast<float, Vector<float>>(xIm);
        var gain = new Vector<float>(g);
        for (int i = 0; i < ar.Length; i++)
        {
            ar[i] += gain * (hr[i] * xr[i] - hi[i] * xi[i]);
            ai[i] += gain * (hr[i] * xi[i] + hi[i] * xr[i]);
        }
    }

    /// <summary>Dot product of two equal-length spans, vector-widened with a scalar remainder.</summary>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int width = Vector<float>.Count;
        int i = 0;
        var acc = Vector<float>.Zero;
        for (; i + width <= a.Length; i += width)
            acc += new Vector<float>(a.Slice(i, width)) * new Vector<float>(b.Slice(i, width));
        float sum = Vector.Sum(acc);
        for (; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }
}
