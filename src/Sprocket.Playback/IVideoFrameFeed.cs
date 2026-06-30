using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Playback;

/// <summary>
/// A source of decoded video frames in presentation order for one media source, with seeking. This is the
/// seam between the playback engine and the Media layer's decode ring (<see cref="VideoDecodeRing"/>): the
/// engine pulls frames and requests seeks without owning the decoder, which keeps the engine testable
/// against a fake feed and leaves room for a proxy/hardware feed later (ARCHITECTURE.md §17).
/// </summary>
public interface IVideoFrameFeed : IAsyncDisposable
{
    /// <summary>Starts decoding. Idempotent.</summary>
    void Start();

    /// <summary>
    /// Returns the next frame in presentation order, or <c>null</c> at end of stream. The caller owns the
    /// returned frame and must <see cref="VideoFrame.Dispose"/> it. Frames from before the latest
    /// <see cref="RequestSeek"/> are discarded internally.
    /// </summary>
    ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests that the feed resume from the frame at/just after <paramref name="sourceTarget"/>
    /// (a time within the source). Safe to call from any thread.</summary>
    void RequestSeek(Timecode sourceTarget);

    /// <summary>
    /// How this feed's video decodes — codec + hardware device — for the diagnostics overlay, or
    /// <see langword="null"/> when unknown. Stable for the life of the feed. A default implementation returns
    /// <see langword="null"/> so a minimal/alternate feed need not supply it.
    /// </summary>
    VideoDecodeInfo? DecodeInfo => null;
}

/// <summary>The default <see cref="IVideoFrameFeed"/>: a thin adapter over a <see cref="VideoDecodeRing"/>.</summary>
public sealed class RingVideoFrameFeed : IVideoFrameFeed
{
    private readonly VideoDecodeRing _ring;

    /// <summary>Wraps <paramref name="ring"/>. The feed takes ownership and disposes it on <see cref="DisposeAsync"/>.</summary>
    public RingVideoFrameFeed(VideoDecodeRing ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        _ring = ring;
    }

    /// <inheritdoc />
    public void Start() => _ring.Start();

    /// <inheritdoc />
    public ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken = default) =>
        _ring.ReadAsync(cancellationToken);

    /// <inheritdoc />
    public void RequestSeek(Timecode sourceTarget) => _ring.RequestSeek(sourceTarget);

    /// <inheritdoc />
    public VideoDecodeInfo? DecodeInfo => _ring.DecodeInfo;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _ring.DisposeAsync();
}
