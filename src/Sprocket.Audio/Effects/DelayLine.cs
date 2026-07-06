namespace Sprocket.Audio.Effects;

/// <summary>
/// A fixed-size circular delay line shared by the step-46 delay effects: allocated once for the maximum
/// delay it must serve, so parameter changes never allocate and steady state is allocation-free (§1, §19).
/// Same read-before-write semantics as <see cref="StudioReverbEffect"/>'s internal line: <see cref="Tap"/> /
/// <see cref="TapFrac"/> are called <em>before</em> this sample's <see cref="Push"/>, so a tap of
/// <c>d</c> returns the sample pushed <c>d</c> pushes ago.
/// </summary>
internal sealed class DelayLine
{
    private readonly float[] _buffer;
    private int _write;

    public DelayLine(int capacity) => _buffer = new float[Math.Max(2, capacity)];

    public void Push(float value)
    {
        _buffer[_write] = value;
        if (++_write >= _buffer.Length)
            _write = 0;
    }

    /// <summary>The sample pushed <paramref name="delay"/> pushes ago (call before this sample's push).</summary>
    public float Tap(int delay)
    {
        int i = _write - delay;
        if (i < 0)
            i += _buffer.Length;
        return _buffer[i];
    }

    /// <summary>Linear-interpolated fractional tap, for modulated (wow &amp; flutter) reads.</summary>
    public float TapFrac(double delay)
    {
        var whole = (int)delay;
        var frac = (float)(delay - whole);
        int i0 = _write - whole;
        if (i0 < 0)
            i0 += _buffer.Length;
        int i1 = i0 - 1;
        if (i1 < 0)
            i1 += _buffer.Length;
        return _buffer[i0] + (_buffer[i1] - _buffer[i0]) * frac;
    }

    public void Clear()
    {
        _buffer.AsSpan().Clear();
        _write = 0;
    }
}
