using Sprocket.Core.Timing;
using Sprocket.Media;

namespace Sprocket.Export;

/// <summary>
/// Supplies the decoded full-resolution frame for one source media at a requested source time during export.
/// Export pulls frames at the <b>full source resolution</b> (never a proxy, ARCHITECTURE.md §17) and walks the
/// timeline forward, so this decodes sequentially — keeping a one-frame look-ahead and only seeking backward
/// if a request ever moves back (e.g. a clip whose source runs in reverse, not in the slice).
/// </summary>
/// <remarks>
/// Decode runs in software (<see cref="HardwareAccelMode.Disabled"/>) for bit-deterministic output, which is
/// what makes golden-frame export testing meaningful. Not thread-safe: one provider per source, driven by the
/// single export thread. The returned frame is owned by this provider and stays valid only until the next
/// <see cref="GetFrame"/> call (the caller composites it immediately), so callers must not hold it.
/// </remarks>
internal sealed class ExportFrameProvider : IDisposable
{
    // A frame whose PTS is within this tolerance of (or before) the request counts as "at or before" it, so
    // sub-tick rounding between the timeline clock and the source PTS never skips the correct frame.
    private static readonly long MatchToleranceTicks = Timecode.TicksPerSecond / 1000; // 1 ms

    private readonly MediaSource _source;
    private readonly VideoFramePool _pool;

    private VideoFrame? _current;   // the frame currently "on screen" for the last request
    private VideoFrame? _pending;   // decoded look-ahead whose PTS is past the last request
    private bool _started;
    private bool _eof;
    private bool _disposed;

    public ExportFrameProvider(MediaSource source)
    {
        _source = source;
        _pool = new VideoFramePool(source.Info.Width, source.Info.Height);
    }

    /// <summary>Source frame width in pixels.</summary>
    public int Width => _source.Info.Width;

    /// <summary>Source frame height in pixels.</summary>
    public int Height => _source.Info.Height;

    /// <summary>
    /// Returns the source frame to display at <paramref name="sourceTime"/> — the latest decoded frame whose
    /// PTS is at or before it — advancing the decoder as needed, or <see langword="null"/> only if the source
    /// yields no frames at all. The result is valid until the next call.
    /// </summary>
    public VideoFrame? GetFrame(Timecode sourceTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_started)
        {
            _source.SeekTo(sourceTime);
            _started = true;
        }
        else if (_current is not null && sourceTime.Ticks < _current.Pts.Ticks - MatchToleranceTicks)
        {
            // The request moved backwards (a scrub-style jump); re-seek and rebuild the look-ahead.
            Reset();
            _source.SeekTo(sourceTime);
        }

        while (true)
        {
            if (_pending is null && !_eof)
            {
                if (_source.TryDecodeNextFrame(_pool, out VideoFrame? next))
                    _pending = next;
                else
                    _eof = true;
            }

            // Promote the look-ahead to current while it is still at or before the requested time.
            if (_pending is not null && _pending.Pts.Ticks <= sourceTime.Ticks + MatchToleranceTicks)
            {
                _current?.Dispose();
                _current = _pending;
                _pending = null;
                continue;
            }

            break;
        }

        // Prefer the promoted current frame; fall back to the first decoded frame if the request precedes it.
        return _current ?? _pending;
    }

    private void Reset()
    {
        _current?.Dispose();
        _current = null;
        _pending?.Dispose();
        _pending = null;
        _eof = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _current?.Dispose();
        _pending?.Dispose();
        _pool.Dispose();
        _source.Dispose();
    }
}
