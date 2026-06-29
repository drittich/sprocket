using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;
using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>Settings for the exported H.264 video stream.</summary>
/// <param name="Width">Output frame width in pixels.</param>
/// <param name="Height">Output frame height in pixels.</param>
/// <param name="FrameRate">Output frame rate (the encoder time base is its reciprocal).</param>
/// <param name="BitRate">Target bit rate in bits/s, or <c>0</c> to let the CRF quality setting drive size.</param>
/// <param name="GopSize">Maximum I-frame (GOP key picture) interval in frames (<c>0</c> = encoder default).</param>
public readonly record struct VideoEncoderSettings(int Width, int Height, Rational FrameRate, long BitRate = 0, int GopSize = 0);

/// <summary>Settings for the exported AAC audio stream.</summary>
/// <param name="SampleRate">Output sample rate in Hz.</param>
/// <param name="Channels">Output channel count.</param>
/// <param name="BitRate">Target bit rate in bits/s (<c>0</c> = a sensible default).</param>
public readonly record struct AudioEncoderSettings(int SampleRate, int Channels, long BitRate = 0);

/// <summary>
/// Encodes composited RGBA frames + interleaved float PCM to an H.264/AAC MP4 file — the reverse of
/// <see cref="MediaSource"/>/<see cref="AudioSource"/> (ARCHITECTURE.md §11 "Encoder (export): mirror in
/// reverse"). This is the export path's muxer: all FFmpeg interop stays here in <c>Sprocket.Media</c>; the
/// orchestrator (<c>Sprocket.Export</c>) feeds it pixels and samples and never sees libav*.
/// </summary>
/// <remarks>
/// <para><b>Not thread-safe.</b> One encoder per output file, driven from a single export thread. The slice
/// uses the software <c>libx264</c>/native AAC encoders (hardware encode — <c>h264_nvenc</c> etc. — is a later
/// addition behind the same shape, §11). Video frames are converted RGBA → yuv420p with swscale; audio is
/// deinterleaved flt → fltp with swresample, both at write time.</para>
/// <para>Packets from the two encoders are interleaved by the muxer (<c>InterleavedWritePacket</c>); the caller
/// feeds frames in roughly timeline order (see the exporter) so the interleave stays within the muxer's buffer.
/// Call <see cref="Finish"/> exactly once to flush both encoders and write the trailer before disposal.</para>
/// </remarks>
public sealed unsafe class MediaEncoder : IDisposable
{
    private const long DefaultAudioBitRate = 192_000;

    private readonly FormatContext _format;

    // Video
    private readonly CodecContext _videoEncoder;
    private readonly MediaStream _videoStream;
    private readonly VideoFrameConverter _converter = new();
    private readonly Frame _rgbaFrame;   // staging: incoming RGBA copied here, then swscaled to yuv
    private readonly Frame _yuvFrame;    // swscale destination + encoder input
    private readonly int _width;
    private readonly int _height;

    // Audio (optional)
    private readonly CodecContext? _audioEncoder;
    private readonly MediaStream _audioStream;
    private readonly Frame? _audioFrame;          // fltp encoder input (deinterleaved)
    private SwrContext* _swr;                       // flt (interleaved) → fltp (planar), same rate
    private readonly int _channels;

    private readonly Packet _packet = new();
    private bool _finished;
    private bool _disposed;

    private MediaEncoder(
        FormatContext format,
        CodecContext videoEncoder, MediaStream videoStream, Frame rgbaFrame, Frame yuvFrame,
        CodecContext? audioEncoder, MediaStream audioStream, Frame? audioFrame, SwrContext* swr, int channels)
    {
        _format = format;
        _videoEncoder = videoEncoder;
        _videoStream = videoStream;
        _rgbaFrame = rgbaFrame;
        _yuvFrame = yuvFrame;
        _width = videoEncoder.Width;
        _height = videoEncoder.Height;
        _audioEncoder = audioEncoder;
        _audioStream = audioStream;
        _audioFrame = audioFrame;
        _swr = swr;
        _channels = channels;
    }

    /// <summary>Whether the file has an audio stream (<see cref="WriteAudioFrame"/> is valid).</summary>
    public bool HasAudio => _audioEncoder is not null;

    /// <summary>The number of samples (per channel) the AAC encoder wants per frame; pass exactly this many to
    /// <see cref="WriteAudioFrame"/> except for the final, shorter frame. Zero when there is no audio.</summary>
    public int AudioFrameSize { get; private set; }

    /// <summary>The hardware/software encoder name actually engaged for video (e.g. <c>"libx264"</c>).</summary>
    public string VideoEncoderName => _videoEncoder.Codec.Name;

    /// <summary>
    /// Creates an MP4 encoder at <paramref name="path"/> with an H.264 video stream and, when
    /// <paramref name="audio"/> is given, an AAC audio stream. Opens the output file and writes the header so
    /// the encoder is ready for <see cref="WriteVideoFrame"/>/<see cref="WriteAudioFrame"/>.
    /// </summary>
    public static MediaEncoder Create(string path, VideoEncoderSettings video, AudioEncoderSettings? audio = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (video.Width <= 0 || video.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(video), "Output dimensions must be positive.");
        if (video.FrameRate.Num <= 0 || video.FrameRate.Den <= 0)
            throw new ArgumentOutOfRangeException(nameof(video), "Output frame rate must be positive.");

        FormatContext format = FormatContext.AllocOutput(fileName: path);
        try
        {
            bool globalHeader = format.OutputFormat!.Value.Flags.HasFlag(AVFMT.Globalheader);

            (CodecContext videoEncoder, MediaStream videoStream, Frame rgbaFrame, Frame yuvFrame) =
                OpenVideo(format, video, globalHeader);

            CodecContext? audioEncoder = null;
            MediaStream audioStream = default;
            Frame? audioFrame = null;
            SwrContext* swr = null;
            int channels = 0;
            int audioFrameSize = 0;
            if (audio is { } a)
            {
                (audioEncoder, audioStream, audioFrame, nint swrPtr, audioFrameSize) = OpenAudio(format, a, globalHeader);
                swr = (SwrContext*)swrPtr;
                channels = a.Channels;
            }

            // Open the output file (unless the muxer is file-less) and write the container header.
            if (!format.OutputFormat!.Value.Flags.HasFlag(AVFMT.Nofile))
                format.Pb = IOContext.OpenWrite(path);
            format.WriteHeader();

            return new MediaEncoder(
                format, videoEncoder, videoStream, rgbaFrame, yuvFrame,
                audioEncoder, audioStream, audioFrame, swr, channels)
            {
                AudioFrameSize = audioFrameSize,
            };
        }
        catch
        {
            format.Dispose();
            throw;
        }
    }

    private static (CodecContext, MediaStream, Frame rgba, Frame yuv) OpenVideo(
        FormatContext format, VideoEncoderSettings v, bool globalHeader)
    {
        Codec codec = Codec.FindEncoderByName("libx264")
            ?? Codec.FindEncoderById(AVCodecID.H264);

        var encoder = new CodecContext(codec)
        {
            Width = v.Width,
            Height = v.Height,
            PixelFormat = AVPixelFormat.Yuv420p,
            // Time base = 1/fps so a frame's PTS is simply its frame index.
            TimeBase = new AVRational(v.FrameRate.Den, v.FrameRate.Num),
            Framerate = new AVRational(v.FrameRate.Num, v.FrameRate.Den),
        };
        if (v.BitRate > 0)
            encoder.BitRate = v.BitRate;
        if (v.GopSize > 0)
            encoder.GopSize = v.GopSize;
        if (globalHeader)
            encoder.Flags |= AV_CODEC_FLAG.GlobalHeader;

        // CRF/preset drive quality when no explicit bitrate is set; deterministic for golden-frame tests.
        using var options = new MediaDictionary();
        if (v.BitRate <= 0)
            options["crf"] = "20";
        options["preset"] = "medium";
        encoder.Open(codec, options);

        MediaStream stream = format.NewStream(codec);
        stream.Codecpar!.CopyFrom(encoder);
        stream.TimeBase = encoder.TimeBase;

        var rgba = Frame.CreateVideo(v.Width, v.Height, AVPixelFormat.Rgba);
        rgba.EnsureBuffer(align: 4);
        var yuv = Frame.CreateVideo(v.Width, v.Height, AVPixelFormat.Yuv420p);
        yuv.EnsureBuffer(align: 32);
        return (encoder, stream, rgba, yuv);
    }

    private static (CodecContext, MediaStream, Frame, nint swr, int frameSize) OpenAudio(
        FormatContext format, AudioEncoderSettings a, bool globalHeader)
    {
        if (a.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(a), "Audio sample rate must be positive.");
        if (a.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(a), "Audio channel count must be positive.");

        Codec codec = Codec.FindEncoderById(AVCodecID.Aac);

        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, a.Channels);

        var encoder = new CodecContext(codec)
        {
            SampleFormat = AVSampleFormat.Fltp,   // AAC encodes planar float
            SampleRate = a.SampleRate,
            ChLayout = layout,
            BitRate = a.BitRate > 0 ? a.BitRate : DefaultAudioBitRate,
            TimeBase = new AVRational(1, a.SampleRate),
        };
        if (globalHeader)
            encoder.Flags |= AV_CODEC_FLAG.GlobalHeader;

        try
        {
            encoder.Open(codec);

            MediaStream stream = format.NewStream(codec);
            stream.Codecpar!.CopyFrom(encoder);
            stream.TimeBase = encoder.TimeBase;

            // Some encoders report frame size 0 (any size accepted); use a standard AAC frame then.
            int frameSize = encoder.FrameSize > 0 ? encoder.FrameSize : 1024;

            var frame = Frame.CreateAudio(AVSampleFormat.Fltp, layout, a.SampleRate, frameSize);
            frame.EnsureBuffer(align: 0);

            // Resampler only deinterleaves flt → fltp (same rate/layout); swr_convert returns samples 1:1.
            SwrContext* swr = null;
            AVChannelLayout inLayout = layout, outLayout = layout;
            int rc = ffmpeg.swr_alloc_set_opts2(
                &swr,
                &outLayout, AVSampleFormat.Fltp, a.SampleRate,
                &inLayout, AVSampleFormat.Flt, a.SampleRate,
                0, null);
            if (rc < 0 || swr is null || ffmpeg.swr_init(swr) < 0)
            {
                if (swr is not null) ffmpeg.swr_free(&swr);
                throw new InvalidOperationException("Failed to initialise the audio export resampler.");
            }

            ffmpeg.av_channel_layout_uninit(&layout);
            return (encoder, stream, frame, (nint)swr, frameSize);
        }
        catch
        {
            ffmpeg.av_channel_layout_uninit(&layout);
            throw;
        }
    }

    /// <summary>
    /// Encodes one composited frame: the <see cref="_width"/>×<see cref="_height"/> RGBA8888 pixels at
    /// <paramref name="rgbaPixels"/> (row stride <paramref name="rowBytes"/>) at presentation index
    /// <paramref name="frameIndex"/> (its PTS in the encoder's 1/fps time base). The pixels are read during
    /// the call and need not outlive it.
    /// </summary>
    public void WriteVideoFrame(nint rgbaPixels, int rowBytes, long frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgbaPixels == 0)
            throw new ArgumentNullException(nameof(rgbaPixels));

        _rgbaFrame.MakeWritable();
        CopyRgbaIntoFrame(rgbaPixels, rowBytes);

        _yuvFrame.MakeWritable();
        _converter.ConvertFrame(_rgbaFrame, _yuvFrame, SWS.Bilinear);
        _yuvFrame.Pts = frameIndex;

        _videoEncoder.SendFrame(_yuvFrame);
        DrainPackets(_videoEncoder, _videoStream);
    }

    /// <summary>Copies the incoming RGBA pixels (which may have a wider stride than the frame) into the staging
    /// frame's native buffer row by row — a native→native copy, allowed on the throughput-bound export path.</summary>
    private void CopyRgbaIntoFrame(nint rgbaPixels, int rowBytes)
    {
        byte* dst = (byte*)_rgbaFrame.Data[0];
        int dstStride = _rgbaFrame.Linesize[0];
        var src = (byte*)rgbaPixels;
        int copyBytes = Math.Min(rowBytes, dstStride);
        for (int y = 0; y < _height; y++)
            Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * dstStride, dstStride, copyBytes);
    }

    /// <summary>
    /// Encodes one buffer of interleaved float32 PCM (<paramref name="interleaved"/>; length must be a multiple
    /// of the channel count) starting at sample index <paramref name="startSampleIndex"/> (its PTS in the
    /// encoder's 1/sampleRate time base). Pass <see cref="AudioFrameSize"/> sample-frames per call except the last.
    /// </summary>
    public void WriteAudioFrame(ReadOnlySpan<float> interleaved, long startSampleIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioEncoder is null || _audioFrame is null)
            throw new InvalidOperationException("This encoder has no audio stream.");
        if (interleaved.Length == 0)
            return;

        int samples = interleaved.Length / _channels;
        _audioFrame.MakeWritable();
        _audioFrame.NbSamples = samples;
        _audioFrame.Pts = startSampleIndex;

        fixed (float* inPtr = interleaved)
        {
            byte_ptrArray8 inPlanes = default;
            inPlanes[0] = (nint)inPtr;
            byte_ptrArray8 outPlanes = default;
            for (int ch = 0; ch < _channels; ch++)
                outPlanes[ch] = _audioFrame.Data[ch];

            int got = ffmpeg.swr_convert(_swr, (byte**)&outPlanes, samples, (byte**)&inPlanes, samples);
            if (got < 0)
                throw new InvalidOperationException("Audio resample (deinterleave) failed during export.");
            _audioFrame.NbSamples = got;
        }

        _audioEncoder.SendFrame(_audioFrame);
        DrainPackets(_audioEncoder, _audioStream);
    }

    /// <summary>Flushes both encoders (drains buffered packets) and writes the container trailer. Idempotent.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
            return;
        _finished = true;

        _videoEncoder.SendFrame(null);
        DrainPackets(_videoEncoder, _videoStream);

        if (_audioEncoder is not null)
        {
            _audioEncoder.SendFrame(null);
            DrainPackets(_audioEncoder, _audioStream);
        }

        _format.WriteTrailer();
    }

    /// <summary>Pulls every ready packet out of <paramref name="encoder"/>, stamps it for <paramref name="stream"/>,
    /// and interleaves it into the muxer. Stops at <c>Again</c> (needs more input) or <c>EOF</c> (flushed).</summary>
    private void DrainPackets(CodecContext encoder, MediaStream stream)
    {
        while (true)
        {
            CodecResult result = encoder.ReceivePacket(_packet);
            if (result != CodecResult.Success)
                return; // Again → needs more input; EOF → fully flushed

            _packet.StreamIndex = stream.Index;
            _packet.RescaleTimestamp(encoder.TimeBase, stream.TimeBase);
            _format.InterleavedWritePacket(_packet); // takes ownership and unrefs the packet
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _packet.Dispose();
        _converter.Dispose();
        _rgbaFrame.Dispose();
        _yuvFrame.Dispose();
        _videoEncoder.Dispose();

        _audioFrame?.Dispose();
        _audioEncoder?.Dispose();
        if (_swr is not null)
        {
            SwrContext* swr = _swr;
            ffmpeg.swr_free(&swr);
            _swr = null;
        }

        _format.Pb?.Dispose();
        _format.Dispose();
    }
}
