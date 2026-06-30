using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>
/// What the decoder for one source resolved to (diagnostics, for the playback-stats overlay): the video codec
/// and, when GPU decode is engaged, the hardware device backing it. Stable for the life of a decoder.
/// </summary>
/// <param name="CodecName">The video codec short name the decoder uses (e.g. <c>"h264"</c>, <c>"hevc"</c>).</param>
/// <param name="HardwareDeviceName">The GPU device type powering decode (e.g. <c>"d3d11va"</c>, <c>"cuda"</c>),
/// or <see langword="null"/> when decoding in software.</param>
public readonly record struct VideoDecodeInfo(string CodecName, string? HardwareDeviceName)
{
    /// <summary>True when a hardware device is attached to the decoder (GPU decode); false for software decode.</summary>
    public bool IsHardwareAccelerated => !string.IsNullOrEmpty(HardwareDeviceName);
}

/// <summary>
/// Opens one source media file with the hand-rolled FFmpeg 8 binding, probes its streams, and decodes its
/// video stream to native RGBA frames with frame-accurate seeking (ARCHITECTURE.md §11). This is the
/// concrete backing for the timeline's source media; the playback layer wraps it in a ring buffer
/// (<see cref="VideoDecodeRing"/>) and the render graph reaches it through the <c>IFrameSource</c> seam.
/// </summary>
/// <remarks>
/// <para><b>Not thread-safe.</b> A <see cref="MediaSource"/> holds a single decoder and reusable packet/
/// frame; all of <see cref="TryDecodeNextFrame"/> and <see cref="SeekTo"/> must run on one thread (the
/// decode worker). The decoded pixels live in the pool's native buffers — never on the managed heap.</para>
/// <para>Decode model (§11): <c>ReadFrame → SendPacket → ReceiveFrame</c>, draining the decoder before
/// feeding the next packet, then a flush packet at end-of-input to emit buffered frames.</para>
/// <para><b>Hardware decode</b> (<see cref="HardwareAccelMode.Auto"/>, the default): if the decoder has a
/// hardware config for a platform-preferred device (§11) and the device opens, frames decode on the GPU and
/// are downloaded to a CPU frame (<c>av_hwframe_transfer_data</c>) before the swscale → RGBA step. Any
/// failure degrades to software decode, so callers always get RGBA frames regardless of hardware.</para>
/// </remarks>
public sealed unsafe class MediaSource : IDisposable
{
    // The desired GPU pixel format per decoder context, read by the static get_format callback (which can't
    // capture state). Keyed by the AVCodecContext pointer; entries are removed on Dispose.
    private static readonly ConcurrentDictionary<IntPtr, int> WantHwFormat = new();

    private readonly FormatContextHandle _format;
    private readonly CodecContextHandle _decoder;
    private readonly SwsScaler _converter = new();
    private readonly AvPacketHandle _packet = new();
    private readonly AvFrameHandle _yuv = new();    // reusable decoder output (source/hw pixel format)
    private readonly AvRational _videoTimeBase;
    private readonly int _videoIndex;

    private readonly IHardwareContext? _hwDevice;   // null when decoding in software
    private readonly int _hwPixelFormat;            // the GPU frame format to expect when hw is active
    private readonly AvFrameHandle? _hwTransfer;    // CPU frame the GPU frame is downloaded into

    private bool _inputEof;     // ReadFrame returned no more packets
    private bool _flushed;      // sent the end-of-stream flush packet to the decoder
    private long _discardBeforePts = MediaTime.NoPts; // decode-to-target: skip frames before this (seek)
    private bool _disposed;

    private MediaSource(
        FormatContextHandle format, CodecContextHandle decoder, int videoIndex, AvRational videoTimeBase,
        ProbedMediaInfo info, IHardwareContext? hwDevice, int hwPixelFormat)
    {
        _format = format;
        _decoder = decoder;
        _videoIndex = videoIndex;
        _videoTimeBase = videoTimeBase;
        _hwDevice = hwDevice;
        _hwPixelFormat = hwPixelFormat;
        _hwTransfer = hwDevice is null ? null : new AvFrameHandle();
        Info = info;
    }

    /// <summary>Streams/duration/format probed at open. Mirrors what is stored on the project's <see cref="MediaRef"/>.</summary>
    public ProbedMediaInfo Info { get; }

    /// <summary>Whether the source has a decodable video stream (always true for an opened <see cref="MediaSource"/>).</summary>
    public bool HasVideo => Info.HasVideo;

    /// <summary>The hardware device powering decode (e.g. <c>"d3d11va"</c>), or <c>null</c> when decoding in software.</summary>
    public string? HardwareDeviceName => _hwDevice?.Name;

    /// <summary>The video codec short name (e.g. <c>"h264"</c>) the decoder resolved to.</summary>
    public string VideoDecoderName => _decoder.CodecName;

    /// <summary>How this source's video decodes — codec + hardware device — for the diagnostics overlay.</summary>
    public VideoDecodeInfo DecodeInfo => new(_decoder.CodecName, HardwareDeviceName);

    /// <summary>
    /// Opens and probes <paramref name="path"/>, opening its video decoder. Throws if the file cannot be
    /// opened or has no decodable video stream (the slice is video-led; audio is probed but decoded by
    /// <see cref="AudioSource"/>). <paramref name="hwAccel"/> selects GPU decode with software fallback.
    /// </summary>
    public static MediaSource Open(string path, HardwareAccelMode hwAccel = HardwareAccelMode.Auto)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Ensure any FFmpeg natives bundled beside the executable are loaded + the version is FFmpeg 8 before
        // the first FFmpeg call (ARCHITECTURE.md §11); no-op on Windows / local dev once resolved.
        FFmpegLoader.EnsureBundledNativesLoaded();

        FormatContextHandle format = FormatContextHandle.OpenInput(path);
        try
        {
            if (!format.TryFindBestStream(AvConst.MediaTypeVideo, out int videoIndex, out IntPtr videoStream, out IntPtr decoderCodec)
                || decoderCodec == IntPtr.Zero)
                throw new InvalidOperationException($"No video stream found in '{path}'.");

            var st = (AvStream*)videoStream;
            IntPtr codecpar = st->codecpar;
            AvRational videoTimeBase = st->time_base;

            // Negotiate a hardware device + its decoder pixel format, then open the decoder. If hardware setup
            // or open fails, tear it down and open a plain software decoder (the guaranteed fallback, §11).
            IHardwareContext? hw = null;
            int hwFmt = AvConst.PixFmtNone;
            if (hwAccel == HardwareAccelMode.Auto)
                (hw, hwFmt) = NegotiateHardware(decoderCodec);

            CodecContextHandle decoder;
            try
            {
                decoder = CreateDecoder(decoderCodec, codecpar, hw, hwFmt);
            }
            catch when (hw is not null)
            {
                hw.Dispose();
                hw = null;
                hwFmt = AvConst.PixFmtNone;
                decoder = CreateDecoder(decoderCodec, codecpar, null, AvConst.PixFmtNone);
            }

            ProbedMediaInfo info = Probe(format, videoStream, decoder);
            return new MediaSource(format, decoder, videoIndex, videoTimeBase, info, hw, hwFmt);
        }
        catch
        {
            format.Dispose();
            throw;
        }
    }

    /// <summary>Finds the first platform-preferred hardware device the decoder supports and that opens.</summary>
    private static (IHardwareContext?, int) NegotiateHardware(IntPtr codec)
    {
        foreach (HardwareDeviceType type in HardwareDevice.PlatformPreferredTypes())
        {
            if (!TryFindHwConfig(codec, (int)type, out int pixFmt))
                continue;
            HardwareDevice? device = HardwareDevice.TryCreate(type);
            if (device is not null)
                return (device, pixFmt);
        }
        return (null, AvConst.PixFmtNone);
    }

    /// <summary>Scans the decoder's hardware configs for one that uses an attachable device context of the
    /// given type, yielding the GPU pixel format frames will carry.</summary>
    private static bool TryFindHwConfig(IntPtr codec, int type, out int pixFmt)
    {
        for (int i = 0; ; i++)
        {
            IntPtr cfg = LibAv.avcodec_get_hw_config(codec, i);
            if (cfg == IntPtr.Zero)
                break;
            var config = (AvCodecHwConfig*)cfg;
            if ((config->methods & AvConst.HwConfigMethodDeviceCtx) != 0 && config->device_type == type)
            {
                pixFmt = config->pix_fmt;
                return true;
            }
        }
        pixFmt = AvConst.PixFmtNone;
        return false;
    }

    /// <summary>Creates and opens the video decoder, attaching the hardware device + a <c>get_format</c> that
    /// selects the GPU pixel format when <paramref name="hw"/> is present.</summary>
    private static CodecContextHandle CreateDecoder(IntPtr codec, IntPtr codecpar, IHardwareContext? hw, int hwFmt)
    {
        CodecContextHandle decoder = CodecContextHandle.Alloc(codec);
        try
        {
            decoder.ApplyParameters(codecpar);

            if (hw is not null)
            {
                WantHwFormat[decoder.Ptr] = hwFmt;
                decoder.GetFormat = (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&PickFormat;
                decoder.HwDeviceCtx = LibAv.av_buffer_ref(hw.DeviceContextRef);
            }

            decoder.Open(codec);
            return decoder;
        }
        catch
        {
            WantHwFormat.TryRemove(decoder.Ptr, out _);
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>The decoder's <c>get_format</c> callback (unmanaged): pick the GPU format if offered, else the
    /// first (software) format so decode still proceeds. Reads the wanted format from <see cref="WantHwFormat"/>.</summary>
    [UnmanagedCallersOnly]
    private static int PickFormat(IntPtr ctx, int* formats)
    {
        if (WantHwFormat.TryGetValue(ctx, out int want))
            for (int* p = formats; *p != AvConst.PixFmtNone; p++)
                if (*p == want)
                    return want;
        return *formats;
    }

    private static ProbedMediaInfo Probe(FormatContextHandle format, IntPtr videoStream, CodecContextHandle decoder)
    {
        var st = (AvStream*)videoStream;
        Rational frameRate = ReadFrameRate(st);

        Timecode duration = format.Duration > 0
            ? MediaTime.FromMicroseconds(format.Duration)          // AV_TIME_BASE (µs) container duration
            : st->duration > 0
                ? MediaTime.ToTimecode(st->duration, st->time_base)
                : Timecode.Zero;

        bool hasAudio = format.TryFindBestStream(AvConst.MediaTypeAudio, out _, out IntPtr audioStream, out _);
        int sampleRate = 0, channels = 0;
        if (hasAudio)
        {
            var audioPar = (AvCodecParameters*)((AvStream*)audioStream)->codecpar;
            sampleRate = audioPar->sample_rate;
            channels = audioPar->ch_layout.nb_channels;
        }

        return new ProbedMediaInfo(
            Duration: duration,
            HasVideo: true,
            FrameRate: frameRate,
            Width: decoder.Width,
            Height: decoder.Height,
            HasAudio: hasAudio,
            SampleRate: sampleRate,
            Channels: channels);
    }

    /// <summary>Reads the video frame rate, preferring the average rate and falling back to the real base rate.</summary>
    private static Rational ReadFrameRate(AvStream* videoStream)
    {
        AvRational avg = videoStream->avg_frame_rate;
        if (avg.Num > 0 && avg.Den > 0)
            return new Rational(avg.Num, avg.Den);

        AvRational real = videoStream->r_frame_rate;
        if (real.Num > 0 && real.Den > 0)
            return new Rational(real.Num, real.Den);

        return Rational.Zero;
    }

    /// <summary>
    /// Decodes the next video frame in presentation order into a frame leased from <paramref name="pool"/>.
    /// Returns false at end of stream. After <see cref="SeekTo"/>, frames before the seek target are decoded
    /// and discarded (without an RGBA conversion) so the first returned frame lands at/just after the target.
    /// </summary>
    public bool TryDecodeNextFrame(VideoFramePool pool, [NotNullWhen(true)] out VideoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pool);

        while (TryReceiveYuv())
        {
            long pts = FramePts(_yuv);

            // Decode-to-target: drop frames before the seek point. No swscale work for discarded frames.
            if (_discardBeforePts != MediaTime.NoPts && pts != MediaTime.NoPts && pts < _discardBeforePts)
                continue;
            _discardBeforePts = MediaTime.NoPts;

            if (!TryGetCpuFrame(out AvFrameHandle? source))
                continue; // a failed GPU download — skip this frame rather than crash (§15)

            VideoFrame rgba = pool.Rent();
            _converter.Convert(source, rgba.Native);
            rgba.Pts = pts == MediaTime.NoPts ? Timecode.Zero : MediaTime.ToTimecode(pts, _videoTimeBase);
            frame = rgba;
            return true;
        }

        frame = null;
        return false;
    }

    /// <summary>Returns the CPU-side frame to convert: the decoded frame directly in software, or the GPU
    /// frame downloaded via <c>av_hwframe_transfer_data</c> when hardware decode produced a GPU frame.</summary>
    private bool TryGetCpuFrame([NotNullWhen(true)] out AvFrameHandle? source)
    {
        if (_hwDevice is null || _yuv.Format != _hwPixelFormat)
        {
            source = _yuv; // software decode, or a per-frame software fallback the decoder chose
            return true;
        }

        _hwTransfer!.Unref(); // let the transfer allocate fresh download buffers / choose the CPU format
        if (LibAv.av_hwframe_transfer_data(_hwTransfer.Ptr, _yuv.Ptr, 0) < 0)
        {
            source = null;
            return false;
        }
        source = _hwTransfer;
        return true;
    }

    /// <summary>
    /// Seeks so the next <see cref="TryDecodeNextFrame"/> returns the frame at/just after <paramref name="target"/>:
    /// seek to the I-frame at or before the target, flush the decoder, then arm decode-to-target discard
    /// (ARCHITECTURE.md §8 "seeking").
    /// </summary>
    public void SeekTo(Timecode target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long targetPts = MediaTime.ToStreamTimestamp(target, _videoTimeBase);

        // AVSEEK_FLAG_BACKWARD: land on the I-frame at or before the target so the GOP decodes cleanly.
        _format.SeekFrame(targetPts, _videoIndex, AvConst.SeekBackward);
        _decoder.FlushBuffers();

        _inputEof = false;
        _flushed = false;
        _discardBeforePts = targetPts;
    }

    /// <summary>Resets to the start of the stream (seek to time zero).</summary>
    public void Rewind() => SeekTo(Timecode.Zero);

    /// <summary>
    /// Pulls one decoded frame into <see cref="_yuv"/>, feeding packets / a flush packet as the decoder
    /// asks for more input. Returns false only at true end of stream.
    /// </summary>
    private bool TryReceiveYuv()
    {
        while (true)
        {
            switch (_decoder.ReceiveFrame(_yuv))
            {
                case CodecResult.Success:
                    return true;
                case CodecResult.Eof:
                    return false;
                case CodecResult.Again:
                    if (!FeedDecoder())
                        return false; // flush already sent and decoder still hungry → nothing left
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Feeds the decoder its next input: the next video packet, or — once input is exhausted — a single
    /// flush (null) packet to drain buffered frames. Returns false when there is nothing left to feed.
    /// </summary>
    private bool FeedDecoder()
    {
        if (_inputEof)
        {
            if (_flushed)
                return false;
            _flushed = true;
            _decoder.SendPacket(null); // enter draining mode: emit any frames still held by the decoder
            return true;
        }

        while (_format.ReadFrame(_packet))
        {
            try
            {
                if (_packet.StreamIndex == _videoIndex)
                {
                    _decoder.SendPacket(_packet);
                    return true;
                }
            }
            finally
            {
                _packet.Unref();
            }
        }

        // No more packets: switch to draining on the next call.
        _inputEof = true;
        return FeedDecoder();
    }

    /// <summary>The frame's presentation timestamp, preferring the best-effort estimate over the raw PTS.</summary>
    private static long FramePts(AvFrameHandle frame) =>
        frame.BestEffortTimestamp != MediaTime.NoPts ? frame.BestEffortTimestamp : frame.Pts;

    /// <summary>Closes the decoder and source. Pooled frames are owned by the pool, not by the source.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        WantHwFormat.TryRemove(_decoder.Ptr, out _);
        _packet.Dispose();
        _yuv.Dispose();
        _hwTransfer?.Dispose();
        _converter.Dispose();
        _decoder.Dispose();      // unrefs the decoder's hw_device_ctx ref
        _hwDevice?.Dispose();    // unrefs our device-context ref
        _format.Dispose();
    }
}
