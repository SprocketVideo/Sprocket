namespace Sprocket.Audio.Effects;

/// <summary>
/// One RBJ Audio-EQ-Cookbook biquad band shared by the EQ effects (PLAN.md steps 31/48): normalized
/// coefficients shared across channels plus per-channel z1/z2 state (transposed direct form II), skipped
/// entirely when <see cref="Active"/> is false so a bypassed band is an exact pass-through that costs
/// nothing. Extracted from <see cref="ParametricEqEffect"/> so the step-48 Shelving EQ reuses the same
/// shelf derivation (generalized from the fixed slope S = 1 to a caller-supplied S) instead of re-deriving it.
/// </summary>
internal struct BiquadBand
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float[]? _z1, _z2;

    /// <summary>Whether the band participates in <see cref="Run"/>; the owner sets this from its bypass rule
    /// (a 0 dB / disabled band stays inactive and is never configured).</summary>
    public bool Active;

    /// <summary>Allocates (or reallocates on a format change) the per-channel state. Call before configuring
    /// an active band; steady-state processing then allocates nothing.</summary>
    public void EnsureState(int channels, bool formatChanged)
    {
        if (_z1 is null || _z1.Length < channels || formatChanged)
        {
            _z1 = new float[channels];
            _z2 = new float[channels];
        }
    }

    /// <summary>Zeroes the filter state (coefficients are untouched).</summary>
    public void ClearState()
    {
        _z1?.AsSpan().Clear();
        _z2?.AsSpan().Clear();
    }

    /// <summary>
    /// RBJ cookbook low/high shelf with variable shelf slope <paramref name="slope"/> (S; 1 = the classic
    /// maximally steep monotonic shelf the step-31 parametric EQ uses, smaller = gentler transition).
    /// </summary>
    public void ConfigureShelf(double gainDb, double freq, double slope, bool high, int sampleRate)
    {
        double a = Math.Pow(10, gainDb / 40.0);
        double w0 = 2 * Math.PI * ClampFreq(freq, sampleRate) / sampleRate;
        double cos = Math.Cos(w0);
        // alpha = sin(w0)/2 · sqrt((A + 1/A)(1/S − 1) + 2); at S = 1 this is the familiar sin/2 · √2. The
        // argument is floored so an extreme gain/slope combination stays defined rather than going NaN.
        double alpha = Math.Sin(w0) / 2 * Math.Sqrt(Math.Max(1e-6, (a + 1 / a) * (1 / slope - 1) + 2));
        double sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;
        double sign = high ? -1 : 1; // the high shelf mirrors the low shelf's cos terms

        double b0 = a * ((a + 1) - sign * (a - 1) * cos + sqrtA2Alpha);
        double b1 = sign * 2 * a * ((a - 1) - sign * (a + 1) * cos);
        double b2 = a * ((a + 1) - sign * (a - 1) * cos - sqrtA2Alpha);
        double a0 = (a + 1) + sign * (a - 1) * cos + sqrtA2Alpha;
        double a1 = sign * -2 * ((a - 1) + sign * (a + 1) * cos);
        double a2 = (a + 1) + sign * (a - 1) * cos - sqrtA2Alpha;
        Normalize(b0, b1, b2, a0, a1, a2);
    }

    /// <summary>RBJ cookbook peaking EQ.</summary>
    public void ConfigurePeak(double gainDb, double freq, double q, int sampleRate)
    {
        double a = Math.Pow(10, gainDb / 40.0);
        double w0 = 2 * Math.PI * ClampFreq(freq, sampleRate) / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);

        double b0 = 1 + alpha * a;
        double b1 = -2 * cos;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        double a1 = -2 * cos;
        double a2 = 1 - alpha / a;
        Normalize(b0, b1, b2, a0, a1, a2);
    }

    /// <summary>Runs the band in place over an interleaved buffer; a no-op when inactive.</summary>
    public void Run(Span<float> buffer, int frames, int channels)
    {
        if (!Active)
            return;
        for (int ch = 0; ch < channels; ch++)
        {
            float z1 = _z1![ch], z2 = _z2![ch];
            for (int f = 0; f < frames; f++)
            {
                int i = f * channels + ch;
                float x = buffer[i];
                float y = _b0 * x + z1;
                z1 = _b1 * x - _a1 * y + z2;
                z2 = _b2 * x - _a2 * y;
                buffer[i] = y;
            }
            _z1[ch] = z1;
            _z2[ch] = z2;
        }
    }

    /// <summary>Keeps the band frequency inside (0, Nyquist) so the trig stays well-defined.</summary>
    private static double ClampFreq(double freq, int sampleRate) =>
        Math.Clamp(freq, 1.0, sampleRate / 2.0 - 1.0);

    private void Normalize(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
    }
}
