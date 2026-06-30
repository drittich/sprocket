using System.Reflection;
using Sprocket.Core.Timing;
using Sprocket.Media.Native;

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
/// <para>Packets from the two encoders are interleaved by the muxer (<c>av_interleaved_write_frame</c>); the
/// caller feeds frames in roughly timeline order so the interleave stays within the muxer's buffer.
/// Call <see cref="Finish"/> exactly once to flush both encoders and write the trailer before disposal.</para>
/// </remarks>
public sealed unsafe class MediaEncoder : IDisposable
{
    private const long DefaultAudioBitRate = 192_000;

    private readonly FormatContextHandle _format;

    // Video
    private readonly CodecContextHandle _videoEncoder;
    private readonly IntPtr _videoStream;        // AVStream* — time_base/index read LIVE (post-WriteHeader)
    private readonly AvRational _videoEncTimeBase;
    private readonly SwsScaler _converter = new();
    private readonly AvFrameHandle _rgbaFrame;   // staging: incoming RGBA copied here, then swscaled to yuv
    private readonly AvFrameHandle _yuvFrame;    // swscale destination + encoder input
    private readonly int _width;
    private readonly int _height;

    // Audio (optional)
    private readonly CodecContextHandle? _audioEncoder;
    private readonly IntPtr _audioStream;        // AVStream*
    private readonly AvRational _audioEncTimeBase;
    private readonly AvFrameHandle? _audioFrame;     // fltp encoder input (deinterleaved)
    private readonly SwrResampler? _swr;             // flt (interleaved) → fltp (planar), same rate
    private readonly int _channels;

    private readonly AvPacketHandle _packet = new();
    private bool _finished;
    private bool _disposed;

    private MediaEncoder(
        FormatContextHandle format,
        CodecContextHandle videoEncoder, IntPtr videoStream,
        AvFrameHandle rgbaFrame, AvFrameHandle yuvFrame,
        CodecContextHandle? audioEncoder, IntPtr audioStream,
        AvFrameHandle? audioFrame, SwrResampler? swr, int channels)
    {
        _format = format;
        _videoEncoder = videoEncoder;
        _videoStream = videoStream;
        _videoEncTimeBase = videoEncoder.TimeBase;
        _rgbaFrame = rgbaFrame;
        _yuvFrame = yuvFrame;
        _width = videoEncoder.Width;
        _height = videoEncoder.Height;
        _audioEncoder = audioEncoder;
        _audioStream = audioStream;
        _audioEncTimeBase = audioEncoder?.TimeBase ?? default;
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
    public string VideoEncoderName => _videoEncoder.CodecName;

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
        // 4:2:0 H.264 (yuv420p) subsamples chroma 2×2, so both dimensions must be even. libx264 rejects odd
        // dimensions at Open; fail here with a clear managed error instead (callers should pass even sizes).
        if ((video.Width & 1) != 0 || (video.Height & 1) != 0)
            throw new ArgumentException("Output dimensions must be even for 4:2:0 H.264 encoding.", nameof(video));
        if (video.FrameRate.Num <= 0 || video.FrameRate.Den <= 0)
            throw new ArgumentOutOfRangeException(nameof(video), "Output frame rate must be positive.");

        FFmpegLoader.EnsureBundledNativesLoaded();

        FormatContextHandle format = FormatContextHandle.AllocOutput(path);
        try
        {
            bool globalHeader = (format.OutputFlags & AvConst.FmtGlobalHeader) != 0;
            bool noFile = (format.OutputFlags & AvConst.FmtNoFile) != 0;

            (CodecContextHandle videoEncoder, IntPtr videoStream, AvFrameHandle rgbaFrame, AvFrameHandle yuvFrame) =
                OpenVideo(format, video, globalHeader);

            CodecContextHandle? audioEncoder = null;
            IntPtr audioStream = IntPtr.Zero;
            AvFrameHandle? audioFrame = null;
            SwrResampler? swr = null;
            int channels = 0;
            int audioFrameSize = 0;
            if (audio is { } a)
            {
                (audioEncoder, audioStream, audioFrame, swr, audioFrameSize) = OpenAudio(format, a, globalHeader);
                channels = a.Channels;
            }

            // Tag the file as Sprocket's output so players / ffprobe surface its provenance. Must be set
            // before WriteHeader — the muxer serializes the metadata dictionary as part of the header.
            WriteCreationMetadata(format);

            // Open the output file (unless the muxer is file-less) and write the container header. NOTE:
            // avformat_write_header may rewrite each stream's time_base (the MP4 muxer picks its own
            // timescale), so packet timestamps must be rescaled to the LIVE stream time_base read after
            // this call — see DrainPackets.
            if (!noFile)
                format.OpenOutputFile(path);
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

    /// <summary>The "encoded with" tag value, e.g. <c>"Sprocket 0.1.27"</c> — built once from the assembly version.</summary>
    private static readonly string EncoderTag = BuildEncoderTag();

    /// <summary>Writes container-level provenance metadata identifying Sprocket as the producing application.</summary>
    private static void WriteCreationMetadata(FormatContextHandle format)
    {
        // `encoder` is the conventional "creating software" tag (MP4 ©too atom; FFmpeg would otherwise
        // stamp its own "Lavf…"); `comment` carries a human-readable note most players surface.
        format.SetMetadata("encoder", EncoderTag);
        format.SetMetadata("comment", "Created with Sprocket");
    }

    private static string BuildEncoderTag()
    {
        Assembly asm = typeof(MediaEncoder).Assembly;
        string? version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString();
        // InformationalVersion can carry a "+<git-sha>" build suffix; drop it for a clean tag.
        int plus = version?.IndexOf('+') ?? -1;
        if (plus >= 0) version = version![..plus];
        return string.IsNullOrEmpty(version) ? "Sprocket" : $"Sprocket {version}";
    }

    private static (CodecContextHandle, IntPtr stream, AvFrameHandle rgba, AvFrameHandle yuv) OpenVideo(
        FormatContextHandle format, VideoEncoderSettings v, bool globalHeader)
    {
        IntPtr codec = LibAv.avcodec_find_encoder_by_name("libx264");
        if (codec == IntPtr.Zero) codec = LibAv.avcodec_find_encoder(AvConst.CodecIdH264);
        if (codec == IntPtr.Zero) throw new FFmpegException("avcodec_find_encoder(H264)", 0, "no H.264 encoder available");

        CodecContextHandle encoder = CodecContextHandle.Alloc(codec);
        encoder.Width = v.Width;
        encoder.Height = v.Height;
        encoder.PixFmt = AvConst.PixFmtYuv420p;
        // Time base = 1/fps so a frame's PTS is simply its frame index.
        encoder.TimeBase = new AvRational(v.FrameRate.Den, v.FrameRate.Num);
        encoder.Framerate = new AvRational(v.FrameRate.Num, v.FrameRate.Den);
        if (v.BitRate > 0) encoder.BitRate = v.BitRate;
        if (v.GopSize > 0) encoder.GopSize = v.GopSize;
        if (globalHeader) encoder.Flags |= AvConst.CodecFlagGlobalHeader;

        // CRF/preset drive quality when no explicit bitrate is set; deterministic for golden-frame tests.
        IntPtr options = IntPtr.Zero;
        if (v.BitRate <= 0) LibAv.av_dict_set(ref options, "crf", "20", 0);
        LibAv.av_dict_set(ref options, "preset", "medium", 0);
        encoder.Open(codec, options);

        IntPtr stream = format.NewStream(codec);
        var st = (AvStream*)stream;
        encoder.ParametersToStream(st->codecpar);
        st->time_base = encoder.TimeBase;

        AvFrameHandle rgba = AvFrameHandle.CreateVideo(v.Width, v.Height, AvConst.PixFmtRgba, align: 4);
        AvFrameHandle yuv = AvFrameHandle.CreateVideo(v.Width, v.Height, AvConst.PixFmtYuv420p, align: 32);
        return (encoder, stream, rgba, yuv);
    }

    private static (CodecContextHandle, IntPtr stream, AvFrameHandle, SwrResampler, int frameSize) OpenAudio(
        FormatContextHandle format, AudioEncoderSettings a, bool globalHeader)
    {
        if (a.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(a), "Audio sample rate must be positive.");
        if (a.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(a), "Audio channel count must be positive.");

        IntPtr codec = LibAv.avcodec_find_encoder(AvConst.CodecIdAac);
        if (codec == IntPtr.Zero) throw new FFmpegException("avcodec_find_encoder(AAC)", 0, "no AAC encoder available");

        AvChannelLayout layout = default;
        LibAv.av_channel_layout_default(&layout, a.Channels);
        try
        {
            CodecContextHandle encoder = CodecContextHandle.Alloc(codec);
            encoder.SampleFmt = AvConst.SampleFmtFltp;   // AAC encodes planar float
            encoder.SampleRate = a.SampleRate;
            LibAv.av_channel_layout_copy(encoder.ChLayout, &layout);
            encoder.BitRate = a.BitRate > 0 ? a.BitRate : DefaultAudioBitRate;
            encoder.TimeBase = new AvRational(1, a.SampleRate);
            if (globalHeader) encoder.Flags |= AvConst.CodecFlagGlobalHeader;
            encoder.Open(codec);

            IntPtr stream = format.NewStream(codec);
            var ast = (AvStream*)stream;
            encoder.ParametersToStream(ast->codecpar);
            ast->time_base = encoder.TimeBase;

            // Some encoders report frame size 0 (any size accepted); use a standard AAC frame then.
            int frameSize = encoder.FrameSize > 0 ? encoder.FrameSize : 1024;

            var frame = new AvFrameHandle();
            frame.Format = AvConst.SampleFmtFltp;
            LibAv.av_channel_layout_copy(frame.ChLayout, &layout);
            frame.SampleRate = a.SampleRate;
            frame.NbSamples = frameSize;
            frame.GetBuffer(0);

            // Resampler only deinterleaves flt → fltp (same rate/layout); swr_convert returns samples 1:1.
            var swr = new SwrResampler();
            swr.Configure(&layout, AvConst.SampleFmtFltp, a.SampleRate, &layout, AvConst.SampleFmtFlt, a.SampleRate);

            return (encoder, stream, frame, swr, frameSize);
        }
        finally
        {
            LibAv.av_channel_layout_uninit(&layout);
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
        _converter.Convert(_rgbaFrame, _yuvFrame);
        _yuvFrame.Pts = frameIndex;

        _videoEncoder.SendFrame(_yuvFrame);
        DrainPackets(_videoEncoder, _videoStream, _videoEncTimeBase);
    }

    /// <summary>Copies the incoming RGBA pixels (which may have a wider stride than the frame) into the staging
    /// frame's native buffer row by row — a native→native copy, allowed on the throughput-bound export path.</summary>
    private void CopyRgbaIntoFrame(nint rgbaPixels, int rowBytes)
    {
        byte* dst = (byte*)_rgbaFrame.Data(0);
        int dstStride = _rgbaFrame.Linesize(0);
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
        if (_audioEncoder is null || _audioFrame is null || _swr is null)
            throw new InvalidOperationException("This encoder has no audio stream.");
        if (interleaved.Length == 0)
            return;

        int samples = interleaved.Length / _channels;
        _audioFrame.MakeWritable();
        _audioFrame.NbSamples = samples;
        _audioFrame.Pts = startSampleIndex;

        fixed (float* inPtr = interleaved)
        {
            byte** inPlanes = stackalloc byte*[8];
            inPlanes[0] = (byte*)inPtr;
            byte** outPlanes = stackalloc byte*[8];
            for (int ch = 0; ch < _channels; ch++)
                outPlanes[ch] = (byte*)_audioFrame.Data(ch);

            int got = _swr.Convert(outPlanes, samples, inPlanes, samples);
            if (got < 0)
                throw new InvalidOperationException("Audio resample (deinterleave) failed during export.");
            _audioFrame.NbSamples = got;
        }

        _audioEncoder.SendFrame(_audioFrame);
        DrainPackets(_audioEncoder, _audioStream, _audioEncTimeBase);
    }

    /// <summary>Flushes both encoders (drains buffered packets) and writes the container trailer. Idempotent.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
            return;
        _finished = true;

        _videoEncoder.SendFrame(null);
        DrainPackets(_videoEncoder, _videoStream, _videoEncTimeBase);

        if (_audioEncoder is not null)
        {
            _audioEncoder.SendFrame(null);
            DrainPackets(_audioEncoder, _audioStream, _audioEncTimeBase);
        }

        _format.WriteTrailer();
    }

    /// <summary>Pulls every ready packet out of <paramref name="encoder"/>, stamps it for the stream, and
    /// interleaves it into the muxer. Stops at <c>Again</c> (needs more input) or <c>EOF</c> (flushed).
    /// Reads the stream index + time_base LIVE from the AVStream — the muxer may have rewritten the
    /// time_base in <c>avformat_write_header</c>, so the pre-header value would be wrong.</summary>
    private void DrainPackets(CodecContextHandle encoder, IntPtr streamPtr, AvRational encTimeBase)
    {
        var st = (AvStream*)streamPtr;
        while (encoder.ReceivePacket(_packet) == CodecResult.Success)
        {
            _packet.StreamIndex = st->index;
            _packet.RescaleTs(encTimeBase, st->time_base);
            _format.InterleavedWriteFrame(_packet); // takes ownership and unrefs the packet
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
        _swr?.Dispose();

        _format.Dispose();
    }
}
