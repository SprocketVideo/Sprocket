using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>
/// GOP-aware backward frame access over a <see cref="MediaSource"/> (PLAN.md step 21 remainder, reverse
/// playback). Compressed video can only be decoded forward from a keyframe, so "the frame before this one"
/// means: seek to the keyframe at/before the target, decode forward to the target, and keep the tail of that
/// run. This window does exactly that — <see cref="FillUpTo"/> decodes the frames at or before a target and
/// retains the last <see cref="Capacity"/> of them (in presentation order) in pooled <see cref="VideoFrame"/>s,
/// so a reverse walker can serve them newest-first and only re-decode when it steps below the window.
/// </summary>
/// <remarks>
/// <para>Pixels stay in the pool's native buffers throughout (ARCHITECTURE.md §1); the window holds at most
/// <see cref="Capacity"/> frames plus one in flight. A GOP longer than the capacity costs one extra forward
/// decode of its head per window refill — the standard trade leading editors make for bounded memory.</para>
/// <para>Not thread-safe: one window per source, driven by one thread (the reverse decode worker or the export
/// thread). The window owns the frames it holds; <see cref="Take"/> transfers ownership of one to the caller.</para>
/// </remarks>
public sealed class GopFrameWindow : IDisposable
{
    /// <summary>The byte budget a window sizes itself to by default (<see cref="DefaultCapacityFor"/>): 256 MB of
    /// pooled RGBA — 32 frames at 1080p, 8 at 4K — so a reversed 4K clip doesn't pin a gigabyte per source.</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    /// <summary>Fewest frames a window retains whatever the budget (a reverse walk needs a little run to serve).</summary>
    public const int MinCapacity = 4;

    /// <summary>Most frames a window retains whatever the budget.</summary>
    public const int MaxCapacity = 64;

    /// <summary>The frame count <see cref="DefaultBudgetBytes"/> affords at the given frame size, within
    /// [<see cref="MinCapacity"/>, <see cref="MaxCapacity"/>].</summary>
    public static int DefaultCapacityFor(int width, int height)
    {
        long bytesPerFrame = Math.Max(1L, (long)width * height * 4);
        return (int)Math.Clamp(DefaultBudgetBytes / bytesPerFrame, MinCapacity, MaxCapacity);
    }

    // A frame whose PTS is within this tolerance past the request still counts as "at or before" it, so
    // sub-tick rounding between the timeline clock and the source PTS never drops the correct frame.
    private static readonly long MatchToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    private readonly MediaSource _source;
    private readonly VideoFramePool _pool;
    private readonly List<VideoFrame> _frames = new(); // ascending PTS
    private bool _disposed;

    /// <param name="source">The source to decode. The window does <b>not</b> own it.</param>
    /// <param name="pool">The frame pool to rent into. Not owned.</param>
    /// <param name="capacity">Maximum frames retained per fill; 0 (the default) sizes the window to the byte budget
    /// for the source's frame size (<see cref="DefaultCapacityFor"/>).</param>
    public GopFrameWindow(MediaSource source, VideoFramePool pool, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pool);
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be non-negative.");
        _source = source;
        _pool = pool;
        Capacity = capacity == 0 ? DefaultCapacityFor(source.Info.Width, source.Info.Height) : capacity;
    }

    /// <summary>Maximum frames the window retains.</summary>
    public int Capacity { get; }

    /// <summary>Frames currently held, in ascending presentation order.</summary>
    public int Count => _frames.Count;

    /// <summary>PTS of the earliest held frame, or <see langword="null"/> when empty.</summary>
    public Timecode? FirstPts => _frames.Count > 0 ? _frames[0].Pts : null;

    /// <summary>PTS of the latest held frame, or <see langword="null"/> when empty.</summary>
    public Timecode? LastPts => _frames.Count > 0 ? _frames[^1].Pts : null;

    /// <summary>
    /// The latest target that <em>excludes</em> the frame at <paramref name="pts"/> — one past the match tolerance
    /// before it. A reverse walk refills with <c>FillUpTo(JustBefore(FirstPts))</c> so the boundary frame is not
    /// admitted twice, and a reversed clip's mapped time (an exclusive upper bound) is looked up the same way.
    /// </summary>
    public static Timecode JustBefore(Timecode pts) => new(pts.Ticks - MatchToleranceTicks - 1);

    /// <summary>The reversed-clip lookup: like <see cref="FillUpTo"/> but retaining only frames strictly
    /// <em>before</em> <paramref name="exclusiveEnd"/>.</summary>
    public int FillBelow(Timecode exclusiveEnd) => FillUpTo(JustBefore(exclusiveEnd));

    /// <summary>The reversed-clip lookup: like <see cref="PeekAtOrBefore"/> but for the latest frame strictly
    /// <em>before</em> <paramref name="exclusiveEnd"/>.</summary>
    public VideoFrame? PeekBelow(Timecode exclusiveEnd) => PeekAtOrBefore(JustBefore(exclusiveEnd));

    /// <summary>
    /// Replaces the window's contents with the last <see cref="Capacity"/> decodable frames whose PTS is at or
    /// before <paramref name="target"/>: seeks the source (which lands on the preceding keyframe), decodes forward
    /// until the first frame past the target, and keeps the tail. Returns the number of frames held afterwards —
    /// zero when the source has no frame at/before the target.
    /// </summary>
    public int FillUpTo(Timecode target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Clear();

        // MediaSource.SeekTo lands on the keyframe at/before the requested time and discards decoded frames up to
        // it, so a seek to the target itself would throw away exactly the frames we want. Seek one window's worth
        // of time *earlier* instead: the frames between there and the target then decode cleanly from the GOP's
        // keyframe and are retained (ring-trimmed to Capacity), without needing an explicit keyframe index.
        Timecode back = StepBack(target);
        _source.SeekTo(back);

        while (_source.TryDecodeNextFrame(_pool, out VideoFrame? frame))
        {
            if (frame.Pts.Ticks > target.Ticks + MatchToleranceTicks)
            {
                frame.Dispose(); // the first frame past the target ends the run
                break;
            }
            _frames.Add(frame);
            if (_frames.Count > Capacity)
            {
                _frames[0].Dispose();
                _frames.RemoveAt(0);
            }
        }
        return _frames.Count;
    }

    /// <summary>
    /// Removes and returns the latest held frame (the next one in reverse order), or <see langword="null"/> when
    /// the window is empty. Ownership transfers to the caller, who must dispose it.
    /// </summary>
    public VideoFrame? Take()
    {
        if (_frames.Count == 0)
            return null;
        VideoFrame last = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        return last;
    }

    /// <summary>The latest held frame with PTS at or before <paramref name="target"/> (not removed), or
    /// <see langword="null"/>. Frames later than the target are dropped from the window, since a reverse walk never
    /// needs them again.</summary>
    public VideoFrame? PeekAtOrBefore(Timecode target)
    {
        while (_frames.Count > 0 && _frames[^1].Pts.Ticks > target.Ticks + MatchToleranceTicks)
        {
            _frames[^1].Dispose();
            _frames.RemoveAt(_frames.Count - 1);
        }
        return _frames.Count > 0 ? _frames[^1] : null;
    }

    /// <summary>Disposes and forgets every held frame.</summary>
    public void Clear()
    {
        foreach (VideoFrame f in _frames)
            f.Dispose();
        _frames.Clear();
    }

    // The earliest source time this fill should retain: Capacity frames before the target at the source's frame
    // rate (a generous span at an unknown/variable rate). The seek lands on the keyframe at/before it, so every
    // retained frame decodes cleanly.
    private Timecode StepBack(Timecode target)
    {
        Rational rate = _source.Info.FrameRate;
        long frameTicks = rate.Num > 0 ? Timecode.TicksPerSecond * rate.Den / rate.Num : Timecode.TicksPerSecond / 24;
        long ticks = target.Ticks - frameTicks * Capacity;
        return new Timecode(Math.Max(0, ticks));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Clear();
    }
}
