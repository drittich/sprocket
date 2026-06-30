using System.Threading.Channels;
using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>
/// A bounded read-ahead decode feed for one <see cref="MediaSource"/>: a single background worker decodes
/// video frames into a pooled, capacity-limited <see cref="Channel{T}"/> (ARCHITECTURE.md §8). The bound
/// applies backpressure so the worker never reads arbitrarily far ahead of the consumer, and the pool
/// keeps the path free of per-frame allocation.
/// </summary>
/// <remarks>
/// <para><b>Seeking</b> (scrub/jump) is generation-tagged: <see cref="RequestSeek"/> bumps a generation
/// counter and signals the worker, which flushes the source and re-seeks. Frames already buffered from the
/// old generation are discarded by the reader, so a seek takes effect immediately without the producer and
/// consumer racing to drain the channel.</para>
/// <para>The worker <em>parks</em> at end of stream instead of completing the channel, so a later
/// <see cref="RequestSeek"/> (e.g. scrubbing back) resumes decoding. <see cref="ReadAsync"/> returns
/// <c>null</c> at end of stream; after a seek it yields frames again.</para>
/// <para>Threading: one producer (the worker) owns the <see cref="MediaSource"/>; one consumer calls
/// <see cref="ReadAsync"/>. <see cref="RequestSeek"/> may be called from any thread — it only touches
/// guarded coordination state, never the source.</para>
/// </remarks>
public sealed class VideoDecodeRing : IAsyncDisposable
{
    /// <summary>Default read-ahead depth — enough to smooth decode jitter without unbounded look-ahead.</summary>
    public const int DefaultCapacity = 8;

    // A channel item carries the generation it was decoded under; a null frame is the end-of-stream marker.
    private readonly record struct Item(long Generation, VideoFrame? Frame);

    private readonly MediaSource _source;
    private readonly VideoFramePool _pool;
    private readonly Channel<Item> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private CancellationTokenSource _seekSignal = new();
    private CancellationTokenSource? _writeCts;     // linked(stop, seekSignal); recreated only per-seek, not per-frame
    private Timecode? _pendingSeek;
    private long _currentGeneration;                // latest requested generation; frames older than this are stale
    private long _writeGeneration;                  // generation the worker currently tags frames with
    private bool _atEof;
    private Task? _worker;
    private bool _disposed;

    /// <summary>Creates a ring over <paramref name="source"/>. The ring owns a frame pool sized to the source.</summary>
    /// <param name="source">The source to decode. The ring owns it and disposes it on <see cref="DisposeAsync"/>.</param>
    /// <param name="capacity">Maximum frames buffered ahead of the consumer.</param>
    public VideoDecodeRing(MediaSource source, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.HasVideo)
            throw new ArgumentException("Source has no video stream to decode.", nameof(source));
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");

        _source = source;
        _pool = new VideoFramePool(source.Info.Width, source.Info.Height);
        _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
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
    /// Requests a seek so the feed resumes from the frame at/just after <paramref name="target"/>. Buffered
    /// frames from before this call are discarded by <see cref="ReadAsync"/>. Safe to call from any thread.
    /// </summary>
    public void RequestSeek(Timecode target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _pendingSeek = target;
            Interlocked.Increment(ref _currentGeneration); // supersede older frames still in the channel
            _seekSignal.Cancel();                          // interrupt a parked / backpressured worker
        }
    }

    /// <summary>
    /// Returns the next decoded frame in presentation order, or <c>null</c> at end of stream. Stale frames
    /// (from before the latest <see cref="RequestSeek"/>) are skipped and recycled internally. The caller
    /// owns the returned frame and MUST <see cref="VideoFrame.Dispose"/> it to return it to the pool.
    /// </summary>
    public async ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out Item item))
            {
                if (item.Generation < Volatile.Read(ref _currentGeneration))
                {
                    item.Frame?.Dispose(); // superseded by a newer seek → recycle and skip
                    continue;
                }
                return item.Frame; // a real frame, or null = end-of-stream marker for the current generation
            }
        }
        return null; // channel completed (disposed)
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                CancellationToken writeToken = ApplyPendingSeek();

                if (_atEof)
                {
                    // Nothing more to decode for this generation; park until a seek (or stop) wakes us.
                    await ParkAsync(writeToken).ConfigureAwait(false);
                    continue;
                }

                if (!_source.TryDecodeNextFrame(_pool, out VideoFrame? frame))
                {
                    _atEof = true;
                    await WriteAsync(new Item(_writeGeneration, null), writeToken).ConfigureAwait(false);
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

    /// <summary>Applies any pending seek and returns the (cached) token that cancels when the next seek arrives.</summary>
    private CancellationToken ApplyPendingSeek()
    {
        lock (_gate)
        {
            // A tripped seek signal means a seek was requested; refresh it and the linked write token.
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
                _source.SeekTo(target);
                _atEof = false;
            }

            return _writeCts!.Token;
        }
    }

    /// <summary>Writes an item, dropping it (recycling the frame) if a seek/stop interrupts the backpressured write.</summary>
    private async ValueTask WriteAsync(Item item, CancellationToken writeToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(item, writeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Frame?.Dispose(); // superseded before it landed; recycle to the pool
        }
    }

    /// <summary>Waits until the next seek (or stop) without busy-looping.</summary>
    private static async Task ParkAsync(CancellationToken wake)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, wake).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Woken by a seek request or by stop; the caller's loop re-evaluates.
        }
    }

    /// <summary>Stops the worker, drains and recycles buffered frames, and disposes the source and pool.</summary>
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

        _source.Dispose();
        _pool.Dispose();
        _stop.Dispose();
        _seekSignal.Dispose();
        _writeCts?.Dispose();
    }
}
