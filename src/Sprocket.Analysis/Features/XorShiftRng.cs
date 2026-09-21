namespace Sprocket.Analysis.Features;

/// <summary>
/// A tiny deterministic pseudo-random generator (xorshift64*). Used to seed RANSAC so that analysis
/// is bit-identical run to run (a golden-frame requirement, ARCHITECTURE.md §7). It is a mutable
/// <c>struct</c> so it can live on the stack and be re-seeded without allocating.
/// </summary>
public struct XorShiftRng(ulong seed)
{
    private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    /// <summary>Next 64 random bits.</summary>
    public ulong Next()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>A non-negative integer in <c>[0, bound)</c>. <paramref name="bound"/> must be positive.</summary>
    public int NextInt(int bound) => (int)(Next() % (ulong)bound);
}
