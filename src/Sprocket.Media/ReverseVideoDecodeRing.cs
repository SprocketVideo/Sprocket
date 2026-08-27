using System.Threading.Channels;
using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>
/// The reverse-order counterpart of <see cref="VideoDecodeRing"/> (PLAN.md step 21 remainder, reverse
/// playback): a single background worker serves one <see cref="MediaSource"/>'s frames in <em>descending</em>
/// presentation order into a bounded, pooled <see cref="Channel{T}"/>. After a seek to a target it decodes the
/// GOP tail at/before the target through a <see cref="GopFrameWindow"/>, emits those frames newest-first, then
/// steps the window back below its earliest frame and repeats — so a reverse walk re-decodes each GOP once
/// (plus a bounded head re-decode for GOPs longer than the window) instead of once per frame.
/// </summary>
/// <remarks>
/// <para>Same contract as the forward ring so the playback player can drive either through
/// <c>IVideoFrameFeed</c>: <see cref="RequestSeek"/> is generation-tagged (stale frames are discarded by the
/// reader), the worker parks at "end of stream" — here, source time zero — until the next seek, and
/// <see cref="ReadAsync"/> returns <c>null</c> at that end. Pixels stay in pooled native buffers (ARCHITECTURE.md
/// §1).</para>
/// <para>Threading: one producer (the worker) owns the source and the window; one consumer calls
/// <see cref="ReadAsync"/>; <see cref="RequestSeek"/> may be called from any thread.</para>
/// </remarks>
public sealed class ReverseVideoDecodeRing : IAsyncDisposable
{
    private readonly record struct Item(long Generation, VideoFrame? Frame);

    private readonly MediaSource _source;
    private readonly VideoFramePool _pool;
    private readonly GopFrameWindow _window;
    private readonly Channel<Item> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private CancellationTokenSource _seekSignal = new();
    private CancellationTokenSource? _writeCts;
    private Timecode? _pendingSeek;
    private long _currentGeneration;
    private long _writeGeneration;
    private bool _atStart;          // walked back to source time zero: park until a seek
    private Timecode? _nextTarget;  // where the next window fill ends (exclusive of frames already emitted)
    private Task? _worker;
    private bool _disposed;

    /// <summary>Creates a reverse ring over <paramref name="source"/>. The ring owns the source and disposes it.</summary>
    /// <param name="capacity">Maximum frames buffered ahead of the consumer.</param>
    /// <param name="windowCapacity">Frames retained per GOP window fill (<see cref="GopFrameWindow.Capacity"/>); 0 sizes
    /// the window to the default byte budget for the source's frame size.</param>
    public ReverseVideoDecodeRing(
        MediaSource source, int capacity = VideoDecodeRing.DefaultCapacity, int windowCapacity = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.HasVideo)
            throw new ArgumentException("Source has no video stream to decode.", nameof(source));
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");

        _source = source;
        _pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        _window = new GopFrameWindow(source, _pool, windowCapacity);
        _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _atStart = true; // nothing to emit until the first seek names a target
    }

    /// <summary>How the underlying source's video decodes — codec + hardware device — for the diagnostics overlay.</summary>
    public VideoDecodeInfo DecodeInfo => _source.DecodeInfo;

    /// <summary>Starts the background decode worker. Idempotent.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_worker is not null)
                return;
            _writeCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _seekSignal.Token);
            _worker = Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>
    /// Requests that the feed resume from the latest frame strictly <em>before</em> <paramref name="target"/> (a
    /// reversed clip's mapped time is an exclusive upper bound) and continue backwards. Buffered frames from before
    /// this call are discarded by <see cref="ReadAsync"/>. Any thread.
    /// </summary>
    public void RequestSeek(Timecode target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _pendingSeek = target;
            Interlocked.Increment(ref _currentGeneration);
            _seekSignal.Cancel();
        }
    }

    /// <summary>
    /// Returns the next frame in <em>descending</em> presentation order, or <c>null</c> once the walk reaches the
    /// start of the source (until the next seek). Stale frames from before the latest seek are skipped and
    /// recycled. The caller owns the returned frame and must dispose it.
    /// </summary>
    public async ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out Item item))
            {
                if (item.Generation < Volatile.Read(ref _currentGeneration))
                {
                    item.Frame?.Dispose();
                    continue;
                }
                return item.Frame;
            }
        }
        return null;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                CancellationToken writeToken = ApplyPendingSeek();

                if (_atStart)
                {
                    await ParkAsync(writeToken).ConfigureAwait(false);
                    continue;
                }

                // Serve the window newest-first; refill it below its earliest frame when it runs dry.
                VideoFrame? frame = _window.Take();
                if (frame is null)
                {
                    if (_nextTarget is not { } target || target.Ticks < 0 || _window.FillUpTo(target) == 0)
                    {
                        _atStart = true;
                        await WriteAsync(new Item(_writeGeneration, null), writeToken).ConfigureAwait(false);
                        continue;
                    }
                    // The next fill must end strictly before this window's earliest frame (past the match tolerance).
                    _nextTarget = GopFrameWindow.JustBefore(_window.FirstPts!.Value);
                    continue;
                }

                await WriteAsync(new Item(_writeGeneration, frame), writeToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            return;
        }

        _channel.Writer.TryComplete();
    }

    private CancellationToken ApplyPendingSeek()
    {
        lock (_gate)
        {
            if (_seekSignal.IsCancellationRequested)
            {
                _seekSignal.Dispose();
                _seekSignal = new CancellationTokenSource();
                _writeCts?.Dispose();
                _writeCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _seekSignal.Token);
            }

            if (_pendingSeek is { } target)
            {
                _pendingSeek = null;
                _writeGeneration = Volatile.Read(ref _currentGeneration);
                _window.Clear();
                _nextTarget = GopFrameWindow.JustBefore(target); // exclusive: the frame *at* the target belongs to forward play
                _atStart = false;
            }

            return _writeCts!.Token;
        }
    }

    private async ValueTask WriteAsync(Item item, CancellationToken writeToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(item, writeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Frame?.Dispose();
        }
    }

    private static async Task ParkAsync(CancellationToken wake)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, wake).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Stops the worker, drains and recycles buffered frames, and disposes the window, source and pool.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _stop.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out Item item))
            item.Frame?.Dispose();

        _window.Dispose();
        _source.Dispose();
        _pool.Dispose();
        _stop.Dispose();
        _seekSignal.Dispose();
        _writeCts?.Dispose();
    }
}
