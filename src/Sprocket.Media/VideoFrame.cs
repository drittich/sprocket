using Sprocket.Core.Timing;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>
/// One decoded video frame: RGBA8888 pixels living in a native FFmpeg <c>AVFrame</c> buffer, plus the
/// timeline-agnostic source <see cref="Pts"/>. The pixels are exposed by <em>pointer</em>
/// (<see cref="Pixels"/>) and are never copied to the managed heap (ARCHITECTURE.md §1); the
/// Render/Playback layer wraps them with <c>SKImage.FromPixels</c> inside the GPU lease.
/// </summary>
/// <remarks>
/// A frame is leased from a <see cref="VideoFramePool"/> and its native buffer is reused, so callers
/// MUST keep the frame alive (undisposed) for exactly as long as something reads its pixels, then
/// <see cref="Dispose"/> it to return it to the pool. Disposing after the source/pool is gone simply
/// frees the native buffer. Not thread-safe for concurrent use of a single instance.
/// </remarks>
public sealed class VideoFrame : IDisposable
{
    private readonly VideoFramePool? _pool;
    private readonly AvFrameHandle _rgba;
    private bool _disposed;

    internal VideoFrame(VideoFramePool? pool, int width, int height)
    {
        _pool = pool;
        Width = width;
        Height = height;
        _rgba = AvFrameHandle.CreateVideo(width, height, AvConst.PixFmtRgba, align: 4);
    }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Presentation time of this frame within its source media.</summary>
    public Timecode Pts { get; internal set; }

    /// <summary>Pointer to the first (only) plane of RGBA8888 pixels. Valid until <see cref="Dispose"/>.</summary>
    public IntPtr Pixels => _rgba.Data(0);

    /// <summary>Bytes per row (stride) of the RGBA buffer; may exceed <c>Width*4</c> due to alignment.</summary>
    public int RowBytes => _rgba.Linesize(0);

    /// <summary>The underlying native frame, for the decoder's swscale destination (Media-internal).</summary>
    internal AvFrameHandle Native => _rgba;

    /// <summary>Returns this frame to its pool for reuse, or frees the native buffer if it has no pool.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // A pooled frame goes back to the pool for reuse; the pool frees the buffer on its own disposal.
        if (_pool is not null && _pool.TryReturn(this))
            return;

        _disposed = true;
        _rgba.Dispose();
    }

    /// <summary>Frees the native buffer unconditionally. Called by the pool when it is itself disposed.</summary>
    internal void FreeNative()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rgba.Dispose();
    }
}
