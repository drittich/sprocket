using System.Diagnostics.CodeAnalysis;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>
/// Opens one source media file with library-level FFmpeg (Sdcb.FFmpeg), probes its streams, and decodes
/// its video stream to native RGBA frames with frame-accurate seeking (ARCHITECTURE.md §11). This is the
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
    // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX — the decoder accepts an attached AVHWDeviceContext.
    private const int HwConfigMethodDeviceCtx = 0x01;

    private readonly FormatContext _format;
    private readonly CodecContext _decoder;
    private readonly MediaStream _videoStream;
    private readonly VideoFrameConverter _converter = new();
    private readonly Packet _packet = new();
    private readonly Frame _yuv = new();          // reusable decoder output (source/hw pixel format)
    private readonly AVRational _videoTimeBase;
    private readonly int _videoIndex;

    private readonly IHardwareContext? _hwDevice;       // null when decoding in software
    private readonly AVPixelFormat _hwPixelFormat;       // the GPU frame format to expect when hw is active
    private readonly Frame? _hwTransfer;                 // CPU frame the GPU frame is downloaded into
    private readonly AVCodecContext_get_format? _getFormat; // held alive for the decoder's lifetime

    private bool _inputEof;     // ReadFrame returned no more packets
    private bool _flushed;      // sent the end-of-stream flush packet to the decoder
    private long _discardBeforePts = MediaTime.NoPts; // decode-to-target: skip frames before this (seek)
    private bool _disposed;

    private MediaSource(
        FormatContext format, CodecContext decoder, MediaStream videoStream, ProbedMediaInfo info,
        IHardwareContext? hwDevice, AVPixelFormat hwPixelFormat, AVCodecContext_get_format? getFormat)
    {
        _format = format;
        _decoder = decoder;
        _videoStream = videoStream;
        _videoIndex = videoStream.Index;
        _videoTimeBase = videoStream.TimeBase;
        _hwDevice = hwDevice;
        _hwPixelFormat = hwPixelFormat;
        _getFormat = getFormat;
        _hwTransfer = hwDevice is null ? null : new Frame();
        Info = info;
    }

    /// <summary>Streams/duration/format probed at open. Mirrors what is stored on the project's <see cref="MediaRef"/>.</summary>
    public ProbedMediaInfo Info { get; }

    /// <summary>Whether the source has a decodable video stream (always true for an opened <see cref="MediaSource"/>).</summary>
    public bool HasVideo => Info.HasVideo;

    /// <summary>The hardware device powering decode (e.g. <c>"d3d11va"</c>), or <c>null</c> when decoding in software.</summary>
    public string? HardwareDeviceName => _hwDevice?.Name;

    /// <summary>
    /// Opens and probes <paramref name="path"/>, opening its video decoder. Throws if the file cannot be
    /// opened or has no decodable video stream (the slice is video-led; audio is probed but decoded by
    /// <see cref="AudioSource"/>). <paramref name="hwAccel"/> selects GPU decode with software fallback.
    /// </summary>
    public static MediaSource Open(string path, HardwareAccelMode hwAccel = HardwareAccelMode.Auto)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Ensure any FFmpeg natives bundled beside the executable are loaded in dependency order before
        // the first FFmpeg call (ARCHITECTURE.md §11); no-op on Windows / local dev.
        FFmpegLoader.EnsureBundledNativesLoaded();

        FormatContext format = FormatContext.OpenInputUrl(path);
        try
        {
            format.LoadStreamInfo();

            MediaStream? videoStreamOrNull = format.FindBestStreamOrNull(AVMediaType.Video, -1, -1);
            if (videoStreamOrNull is null)
                throw new InvalidOperationException($"No video stream found in '{path}'.");

            MediaStream videoStream = videoStreamOrNull.Value;
            Codec codec = Codec.FindDecoderById(videoStream.Codecpar!.CodecId);

            // Negotiate a hardware device + its decoder pixel format, then open the decoder. If hardware setup
            // or open fails, tear it down and open a plain software decoder (the guaranteed fallback, §11).
            IHardwareContext? hw = null;
            AVPixelFormat hwFmt = AVPixelFormat.None;
            if (hwAccel == HardwareAccelMode.Auto)
                (hw, hwFmt) = NegotiateHardware(codec);

            CodecContext decoder;
            AVCodecContext_get_format? getFormat = null;
            try
            {
                decoder = CreateDecoder(codec, videoStream, hw, hwFmt, out getFormat);
            }
            catch when (hw is not null)
            {
                hw.Dispose();
                hw = null;
                hwFmt = AVPixelFormat.None;
                decoder = CreateDecoder(codec, videoStream, null, AVPixelFormat.None, out getFormat);
            }

            ProbedMediaInfo info = Probe(format, videoStream, decoder);
            return new MediaSource(format, decoder, videoStream, info, hw, hwFmt, getFormat);
        }
        catch
        {
            format.Dispose();
            throw;
        }
    }

    /// <summary>Finds the first platform-preferred hardware device the decoder supports and that opens.</summary>
    private static (IHardwareContext?, AVPixelFormat) NegotiateHardware(Codec codec)
    {
        foreach (AVHWDeviceType type in HardwareDevice.PlatformPreferredTypes())
        {
            if (!TryFindHwConfig(codec, type, out AVPixelFormat pixFmt))
                continue;
            HardwareDevice? device = HardwareDevice.TryCreate(type);
            if (device is not null)
                return (device, pixFmt);
        }
        return (null, AVPixelFormat.None);
    }

    /// <summary>Scans the decoder's hardware configs for one that uses an attachable device context of the
    /// given type, yielding the GPU pixel format frames will carry.</summary>
    private static bool TryFindHwConfig(Codec codec, AVHWDeviceType type, out AVPixelFormat pixFmt)
    {
        AVCodec* codecPtr = (Codec?)codec;
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codecPtr, i);
            if (config is null)
                break;
            if ((config->methods & HwConfigMethodDeviceCtx) != 0 && config->device_type == type)
            {
                pixFmt = config->pix_fmt;
                return true;
            }
        }
        pixFmt = AVPixelFormat.None;
        return false;
    }

    /// <summary>Creates and opens the video decoder, attaching the hardware device + a <c>get_format</c> that
    /// selects the GPU pixel format when <paramref name="hw"/> is present.</summary>
    private static CodecContext CreateDecoder(
        Codec codec, MediaStream videoStream, IHardwareContext? hw, AVPixelFormat hwFmt,
        out AVCodecContext_get_format? getFormat)
    {
        getFormat = null;
        var decoder = new CodecContext(codec);
        try
        {
            decoder.FillParameters(videoStream.Codecpar!);

            if (hw is not null)
            {
                AVCodecContext* raw = decoder;
                AVPixelFormat want = hwFmt;
                var pick = new AVCodecContext_get_format((_, formats) => PickFormat(formats, want));
                raw->get_format = pick;
                raw->hw_device_ctx = ffmpeg.av_buffer_ref((AVBufferRef*)hw.DeviceContextRef);
                getFormat = pick;
            }

            decoder.Open();
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>The decoder's <c>get_format</c> callback: pick the GPU format if offered, else the first
    /// (software) format so decode still proceeds.</summary>
    private static AVPixelFormat PickFormat(AVPixelFormat* formats, AVPixelFormat want)
    {
        for (AVPixelFormat* p = formats; *p != AVPixelFormat.None; p++)
        {
            if (*p == want)
                return want;
        }
        return *formats;
    }

    private static ProbedMediaInfo Probe(FormatContext format, MediaStream videoStream, CodecContext decoder)
    {
        Rational frameRate = ReadFrameRate(videoStream);

        Timecode duration = format.Duration > 0
            ? MediaTime.FromMicroseconds(format.Duration)         // AV_TIME_BASE (µs) container duration
            : videoStream.Duration > 0
                ? MediaTime.ToTimecode(videoStream.Duration, videoStream.TimeBase)
                : Timecode.Zero;

        MediaStream? audioStream = format.FindBestStreamOrNull(AVMediaType.Audio, -1, -1);
        bool hasAudio = audioStream is not null;
        int sampleRate = 0, channels = 0;
        if (audioStream is { } audio)
        {
            CodecParameters audioPar = audio.Codecpar!;
            sampleRate = audioPar.SampleRate;
            channels = audioPar.ChLayout.nb_channels;
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
    private static Rational ReadFrameRate(MediaStream videoStream)
    {
        AVRational avg = videoStream.AvgFrameRate;
        if (avg.Num > 0 && avg.Den > 0)
            return new Rational(avg.Num, avg.Den);

        AVRational real = videoStream.RFrameRate;
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

            if (!TryGetCpuFrame(out Frame? source))
                continue; // a failed GPU download — skip this frame rather than crash (§15)

            VideoFrame rgba = pool.Rent();
            _converter.ConvertFrame(source, rgba.Native, SWS.Bilinear);
            rgba.Pts = pts == MediaTime.NoPts ? Timecode.Zero : MediaTime.ToTimecode(pts, _videoTimeBase);
            frame = rgba;
            return true;
        }

        frame = null;
        return false;
    }

    /// <summary>Returns the CPU-side frame to convert: the decoded frame directly in software, or the GPU
    /// frame downloaded via <c>av_hwframe_transfer_data</c> when hardware decode produced a GPU frame.</summary>
    private bool TryGetCpuFrame([NotNullWhen(true)] out Frame? source)
    {
        if (_hwDevice is null || (AVPixelFormat)_yuv.Format != _hwPixelFormat)
        {
            source = _yuv; // software decode, or a per-frame software fallback the decoder chose
            return true;
        }

        _hwTransfer!.Unref(); // let the transfer allocate fresh download buffers / choose the CPU format
        if (ffmpeg.av_hwframe_transfer_data(_hwTransfer, _yuv, 0) < 0)
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
    /// (ARCHITECTURE.md §8 "seeking"). ("I-frame" = the codec GOP key picture; "keyframe" is reserved for the
    /// animation sense, PLAN.md step 16d.)
    /// </summary>
    public void SeekTo(Timecode target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long targetPts = MediaTime.ToStreamTimestamp(target, _videoTimeBase);

        // AVSEEK_FLAG.Backward: land on the I-frame at or before the target so the GOP decodes cleanly.
        _format.SeekFrame(targetPts, _videoIndex, AVSEEK_FLAG.Backward);
        FlushDecoder();

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
            CodecResult result = _decoder.ReceiveFrame(_yuv);
            switch (result)
            {
                case CodecResult.Success:
                    return true;
                case CodecResult.EOF:
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

        while (_format.ReadFrame(_packet) == CodecResult.Success)
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

    /// <summary>Resets decoder state after a seek so it does not emit pre-seek frames or stale references.</summary>
    private void FlushDecoder() => ffmpeg.avcodec_flush_buffers(_decoder);

    /// <summary>The frame's presentation timestamp, preferring the best-effort estimate over the raw PTS.</summary>
    private static long FramePts(Frame frame) =>
        frame.BestEffortTimestamp != MediaTime.NoPts ? frame.BestEffortTimestamp : frame.Pts;

    /// <summary>Closes the decoder and source. Pooled frames are owned by the pool, not by the source.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _packet.Dispose();
        _yuv.Dispose();
        _hwTransfer?.Dispose();
        _converter.Dispose();
        _decoder.Dispose();      // unrefs the decoder's hw_device_ctx ref
        _hwDevice?.Dispose();    // unrefs our device-context ref
        _format.Dispose();
    }
}
