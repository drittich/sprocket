namespace Sprocket.Media.Native;

/// <summary>
/// Thin RAII wrappers over the raw FFmpeg handles (§15). Each owns one native allocation, frees it in
/// <see cref="IDisposable.Dispose"/>, and translates error codes to <see cref="FFmpegException"/> at the
/// Media boundary. This is the one audited place that knows the binding's pointer/offset details, so the
/// rest of Sprocket.Media reads like ordinary managed code and a future binding change stays contained.
/// All wrappers are single-thread-affine, matching the decode/encode workers that own them.
/// </summary>
internal static class Handles { }

/// <summary>Owns an <c>AVFormatContext*</c> — an opened input demuxer or an allocated output muxer.</summary>
internal sealed unsafe class FormatContextHandle : IDisposable
{
    private IntPtr _p;
    private readonly bool _output;

    private FormatContextHandle(IntPtr p, bool output) { _p = p; _output = output; }
    private AvFormatContext* F => (AvFormatContext*)_p;

    public IntPtr Ptr => _p;

    public static FormatContextHandle OpenInput(string path)
    {
        FFmpegError.Check(LibAv.avformat_open_input(out IntPtr p, path, IntPtr.Zero, IntPtr.Zero), "avformat_open_input");
        try
        {
            FFmpegError.Check(LibAv.avformat_find_stream_info(p, IntPtr.Zero), "avformat_find_stream_info");
            return new FormatContextHandle(p, output: false);
        }
        catch
        {
            LibAv.avformat_close_input(ref p);
            throw;
        }
    }

    public static FormatContextHandle AllocOutput(string path)
    {
        FFmpegError.Check(LibAv.avformat_alloc_output_context2(out IntPtr p, IntPtr.Zero, null, path), "avformat_alloc_output_context2");
        return new FormatContextHandle(p, output: true);
    }

    // ---- input ----
    public long Duration => F->duration;
    public uint StreamCount => F->nb_streams;
    public IntPtr Stream(int i) => ((IntPtr*)F->streams)[i];

    /// <summary>av_find_best_stream for a media type, returning the stream pointer + its default decoder.</summary>
    public bool TryFindBestStream(int mediaType, out int index, out IntPtr stream, out IntPtr decoder)
    {
        int idx = LibAv.av_find_best_stream(_p, mediaType, -1, -1, out decoder, 0);
        if (idx < 0) { index = -1; stream = IntPtr.Zero; return false; }
        index = idx;
        stream = Stream(idx);
        return true;
    }

    /// <summary>Reads one packet; false at end of stream (or read error — the consumer then drains).</summary>
    public bool ReadFrame(AvPacketHandle packet) => LibAv.av_read_frame(_p, packet.Ptr) >= 0;

    public void SeekFrame(long timestamp, int streamIndex, int flags)
        => FFmpegError.Check(LibAv.av_seek_frame(_p, streamIndex, timestamp, flags), "av_seek_frame");

    // ---- output ----
    public int OutputFlags => ((AvOutputFormat*)F->oformat)->flags;

    public IntPtr NewStream(IntPtr codec)
    {
        IntPtr s = LibAv.avformat_new_stream(_p, codec);
        if (s == IntPtr.Zero) throw new FFmpegException("avformat_new_stream", 0, "returned null");
        return s;
    }

    public void OpenOutputFile(string path)
    {
        FFmpegError.Check(LibAv.avio_open(out IntPtr pb, path, AvConst.AvioFlagWrite), "avio_open");
        F->pb = pb;
    }

    public void WriteHeader() => FFmpegError.Check(LibAv.avformat_write_header(_p, IntPtr.Zero), "avformat_write_header");
    public void InterleavedWriteFrame(AvPacketHandle pkt) => FFmpegError.Check(LibAv.av_interleaved_write_frame(_p, pkt.Ptr), "av_interleaved_write_frame");
    public void WriteTrailer() => FFmpegError.Check(LibAv.av_write_trailer(_p), "av_write_trailer");

    public void Dispose()
    {
        if (_p == IntPtr.Zero) return;
        if (_output)
        {
            if ((OutputFlags & AvConst.FmtNoFile) == 0 && F->pb != IntPtr.Zero)
            {
                IntPtr pb = F->pb;
                LibAv.avio_closep(ref pb);
                F->pb = IntPtr.Zero;
            }
            LibAv.avformat_free_context(_p);
            _p = IntPtr.Zero;
        }
        else
        {
            LibAv.avformat_close_input(ref _p);
        }
    }
}

/// <summary>Owns an <c>AVCodecContext*</c> (decoder or encoder).</summary>
internal sealed unsafe class CodecContextHandle : IDisposable
{
    private IntPtr _p;
    private CodecContextHandle(IntPtr p) => _p = p;
    private AvCodecContext* C => (AvCodecContext*)_p;

    public IntPtr Ptr => _p;

    public static CodecContextHandle Alloc(IntPtr codec)
    {
        IntPtr p = LibAv.avcodec_alloc_context3(codec);
        if (p == IntPtr.Zero) throw new FFmpegException("avcodec_alloc_context3", 0, "returned null");
        return new CodecContextHandle(p);
    }

    public int Width { get => C->width; set => C->width = value; }
    public int Height { get => C->height; set => C->height = value; }
    public int PixFmt { get => C->pix_fmt; set => C->pix_fmt = value; }
    public int SampleRate { get => C->sample_rate; set => C->sample_rate = value; }
    public int SampleFmt { get => C->sample_fmt; set => C->sample_fmt = value; }
    public int FrameSize => C->frame_size;
    public long BitRate { get => C->bit_rate; set => C->bit_rate = value; }
    public int GopSize { get => C->gop_size; set => C->gop_size = value; }
    public int Flags { get => C->flags; set => C->flags = value; }
    public AvRational TimeBase { get => C->time_base; set => C->time_base = value; }
    public AvRational Framerate { get => C->framerate; set => C->framerate = value; }
    public AvChannelLayout* ChLayout => (AvChannelLayout*)((byte*)_p + 352);
    public IntPtr GetFormat { set => C->get_format = value; }
    public IntPtr HwDeviceCtx { set => C->hw_device_ctx = value; }

    public string CodecName
    {
        get
        {
            IntPtr codec = C->codec;
            if (codec == IntPtr.Zero) return "";
            IntPtr name = ((AvCodec*)codec)->name;
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(name) ?? "";
        }
    }

    public void ApplyParameters(IntPtr codecpar) => FFmpegError.Check(LibAv.avcodec_parameters_to_context(_p, codecpar), "avcodec_parameters_to_context");
    public void ParametersToStream(IntPtr codecpar) => FFmpegError.Check(LibAv.avcodec_parameters_from_context(codecpar, _p), "avcodec_parameters_from_context");

    /// <summary>Opens the codec, optionally with a dictionary of private options (crf/preset). Frees the dict.</summary>
    public void Open(IntPtr codec, IntPtr options = default)
    {
        IntPtr opts = options;
        int rc = LibAv.avcodec_open2(_p, codec, ref opts);
        if (opts != IntPtr.Zero) LibAv.av_dict_free(ref opts);
        FFmpegError.Check(rc, "avcodec_open2");
    }

    public void FlushBuffers() => LibAv.avcodec_flush_buffers(_p);
    public void SendPacket(AvPacketHandle? pkt) => FFmpegError.Check(LibAv.avcodec_send_packet(_p, pkt?.Ptr ?? IntPtr.Zero), "avcodec_send_packet");
    public void SendFrame(AvFrameHandle? frame) => FFmpegError.Check(LibAv.avcodec_send_frame(_p, frame?.Ptr ?? IntPtr.Zero), "avcodec_send_frame");
    public CodecResult ReceiveFrame(AvFrameHandle frame) => FFmpegError.Classify(LibAv.avcodec_receive_frame(_p, frame.Ptr));
    public CodecResult ReceivePacket(AvPacketHandle pkt) => FFmpegError.Classify(LibAv.avcodec_receive_packet(_p, pkt.Ptr));

    public void Dispose()
    {
        if (_p == IntPtr.Zero) return;
        LibAv.avcodec_free_context(ref _p);   // also unrefs hw_device_ctx
    }
}

/// <summary>Owns an <c>AVFrame*</c>. Pixel/sample buffers stay native (§1).</summary>
internal sealed unsafe class AvFrameHandle : IDisposable
{
    private IntPtr _p;

    public AvFrameHandle()
    {
        _p = LibAv.av_frame_alloc();
        if (_p == IntPtr.Zero) throw new FFmpegException("av_frame_alloc", 0, "returned null");
    }

    public IntPtr Ptr => _p;
    private AvFrame* F => (AvFrame*)_p;

    /// <summary>Plane base pointer (data[i]); data[8] lives at frame offset 0.</summary>
    public IntPtr Data(int i) => ((IntPtr*)_p)[i];
    /// <summary>Plane stride (linesize[i]); linesize[8] lives at frame offset 64.</summary>
    public int Linesize(int i) => *((int*)((byte*)_p + 64) + i);

    public int Width { get => F->width; set => F->width = value; }
    public int Height { get => F->height; set => F->height = value; }
    public int Format { get => F->format; set => F->format = value; }
    public int NbSamples { get => F->nb_samples; set => F->nb_samples = value; }
    public int SampleRate { get => F->sample_rate; set => F->sample_rate = value; }
    public long Pts { get => F->pts; set => F->pts = value; }
    public long BestEffortTimestamp => F->best_effort_timestamp;
    public AvChannelLayout* ChLayout => (AvChannelLayout*)((byte*)_p + 384);

    public void GetBuffer(int align) => FFmpegError.Check(LibAv.av_frame_get_buffer(_p, align), "av_frame_get_buffer");
    public void MakeWritable() => FFmpegError.Check(LibAv.av_frame_make_writable(_p), "av_frame_make_writable");
    public void Unref() => LibAv.av_frame_unref(_p);

    /// <summary>Allocates a video frame with a fresh buffer of the given format/size.</summary>
    public static AvFrameHandle CreateVideo(int width, int height, int pixFmt, int align)
    {
        var f = new AvFrameHandle();
        f.Format = pixFmt;
        f.Width = width;
        f.Height = height;
        f.GetBuffer(align);
        return f;
    }

    public void Dispose()
    {
        if (_p == IntPtr.Zero) return;
        LibAv.av_frame_free(ref _p);
    }
}

/// <summary>Owns an <c>AVPacket*</c>.</summary>
internal sealed unsafe class AvPacketHandle : IDisposable
{
    private IntPtr _p;

    public AvPacketHandle()
    {
        _p = LibAv.av_packet_alloc();
        if (_p == IntPtr.Zero) throw new FFmpegException("av_packet_alloc", 0, "returned null");
    }

    public IntPtr Ptr => _p;
    private AvPacket* P => (AvPacket*)_p;

    public int StreamIndex { get => P->stream_index; set => P->stream_index = value; }
    public IntPtr Data => P->data;
    public int Size => P->size;

    public void Unref() => LibAv.av_packet_unref(_p);
    public void RescaleTs(AvRational src, AvRational dst) => LibAv.av_packet_rescale_ts(_p, src, dst);

    public void Dispose()
    {
        if (_p == IntPtr.Zero) return;
        LibAv.av_packet_free(ref _p);
    }
}
