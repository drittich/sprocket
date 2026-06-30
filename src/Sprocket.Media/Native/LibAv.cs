using System.Runtime.InteropServices;

namespace Sprocket.Media.Native;

/// <summary>
/// The curated, hand-rolled FFmpeg 8 binding surface — exactly the libav* functions Sprocket calls,
/// as source-generated <see cref="LibraryImportAttribute"/> P/Invokes (blittable, AOT/trim-clean). This
/// is the bounded subset chosen by the Phase-0 spike (see <c>Native/SPIKE_RESULTS.md</c>); FFmpeg 8.1
/// shipped the headers these were transcribed from, and exact x64 struct offsets live in
/// <see cref="AvStructs"/>. Native resolution goes through <see cref="FFmpegLoader"/>'s DllImport resolver,
/// which maps the bare stem (e.g. <c>"avcodec"</c>) to the bundled versioned file (<c>avcodec-62.dll</c>).
/// </summary>
/// <remarks>
/// Pointers are passed as <see cref="IntPtr"/>; the caller layers typed access via <see cref="AvStructs"/>
/// and the RAII handle wrappers. Strings marshal as UTF-8 (FFmpeg's char* convention).
/// </remarks>
internal static unsafe partial class LibAv
{
    private const string Avformat = "avformat";
    private const string Avcodec = "avcodec";
    private const string Avutil = "avutil";
    private const string Swscale = "swscale";
    private const string Swresample = "swresample";

    // ---- libavformat ----
    [LibraryImport(Avformat, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avformat_open_input(out IntPtr ps, string url, IntPtr fmt, IntPtr options);
    [LibraryImport(Avformat)] internal static partial int avformat_find_stream_info(IntPtr ic, IntPtr options);
    [LibraryImport(Avformat)] internal static partial void avformat_close_input(ref IntPtr s);
    [LibraryImport(Avformat)] internal static partial int av_read_frame(IntPtr s, IntPtr pkt);
    [LibraryImport(Avformat)] internal static partial int av_seek_frame(IntPtr s, int streamIndex, long timestamp, int flags);
    [LibraryImport(Avformat)] internal static partial int av_find_best_stream(IntPtr ic, int type, int wanted, int related, out IntPtr decoder, int flags);
    [LibraryImport(Avformat, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avformat_alloc_output_context2(out IntPtr ctx, IntPtr oformat, string? formatName, string filename);
    [LibraryImport(Avformat)] internal static partial IntPtr avformat_new_stream(IntPtr s, IntPtr c);
    [LibraryImport(Avformat)] internal static partial int avformat_write_header(IntPtr s, IntPtr options);
    [LibraryImport(Avformat)] internal static partial int av_interleaved_write_frame(IntPtr s, IntPtr pkt);
    [LibraryImport(Avformat)] internal static partial int av_write_trailer(IntPtr s);
    [LibraryImport(Avformat)] internal static partial void avformat_free_context(IntPtr s);
    [LibraryImport(Avformat, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avio_open(out IntPtr pb, string url, int flags);
    [LibraryImport(Avformat)] internal static partial int avio_closep(ref IntPtr pb);
    [LibraryImport(Avformat)] internal static partial uint avformat_version();

    // ---- libavcodec ----
    [LibraryImport(Avcodec)] internal static partial IntPtr avcodec_find_decoder(int id);
    [LibraryImport(Avcodec)] internal static partial IntPtr avcodec_find_encoder(int id);
    [LibraryImport(Avcodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr avcodec_find_encoder_by_name(string name);
    [LibraryImport(Avcodec)] internal static partial IntPtr avcodec_alloc_context3(IntPtr codec);
    [LibraryImport(Avcodec)] internal static partial void avcodec_free_context(ref IntPtr ctx);
    [LibraryImport(Avcodec)] internal static partial int avcodec_parameters_to_context(IntPtr ctx, IntPtr par);
    [LibraryImport(Avcodec)] internal static partial int avcodec_parameters_from_context(IntPtr par, IntPtr ctx);
    [LibraryImport(Avcodec)] internal static partial int avcodec_open2(IntPtr ctx, IntPtr codec, ref IntPtr options);
    [LibraryImport(Avcodec)] internal static partial int avcodec_send_packet(IntPtr ctx, IntPtr pkt);
    [LibraryImport(Avcodec)] internal static partial int avcodec_receive_frame(IntPtr ctx, IntPtr frame);
    [LibraryImport(Avcodec)] internal static partial int avcodec_send_frame(IntPtr ctx, IntPtr frame);
    [LibraryImport(Avcodec)] internal static partial int avcodec_receive_packet(IntPtr ctx, IntPtr pkt);
    [LibraryImport(Avcodec)] internal static partial void avcodec_flush_buffers(IntPtr ctx);
    [LibraryImport(Avcodec)] internal static partial IntPtr avcodec_get_hw_config(IntPtr codec, int index);
    [LibraryImport(Avcodec)] internal static partial IntPtr av_packet_alloc();
    [LibraryImport(Avcodec)] internal static partial void av_packet_free(ref IntPtr pkt);
    [LibraryImport(Avcodec)] internal static partial void av_packet_unref(IntPtr pkt);
    [LibraryImport(Avcodec)] internal static partial void av_packet_rescale_ts(IntPtr pkt, AvRational src, AvRational dst);
    [LibraryImport(Avcodec)] internal static partial uint avcodec_version();

    // ---- libavutil ----
    [LibraryImport(Avutil)] internal static partial IntPtr av_frame_alloc();
    [LibraryImport(Avutil)] internal static partial void av_frame_free(ref IntPtr frame);
    [LibraryImport(Avutil)] internal static partial int av_frame_get_buffer(IntPtr frame, int align);
    [LibraryImport(Avutil)] internal static partial int av_frame_make_writable(IntPtr frame);
    [LibraryImport(Avutil)] internal static partial void av_frame_unref(IntPtr frame);
    [LibraryImport(Avutil)] internal static partial IntPtr av_buffer_ref(IntPtr buf);
    [LibraryImport(Avutil)] internal static partial void av_buffer_unref(ref IntPtr buf);
    [LibraryImport(Avutil, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int av_hwdevice_ctx_create(out IntPtr deviceCtx, int type, string? device, IntPtr opts, int flags);
    [LibraryImport(Avutil)] internal static partial int av_hwframe_transfer_data(IntPtr dst, IntPtr src, int flags);
    [LibraryImport(Avutil)] internal static partial IntPtr av_hwdevice_get_type_name(int type);
    [LibraryImport(Avutil)] internal static partial int av_hwdevice_iterate_types(int prev);
    [LibraryImport(Avutil)] internal static partial int av_channel_layout_default(AvChannelLayout* layout, int nbChannels);
    [LibraryImport(Avutil)] internal static partial void av_channel_layout_uninit(AvChannelLayout* layout);
    [LibraryImport(Avutil)] internal static partial int av_channel_layout_copy(AvChannelLayout* dst, AvChannelLayout* src);
    [LibraryImport(Avutil, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int av_dict_set(ref IntPtr dict, string key, string value, int flags);
    [LibraryImport(Avutil)] internal static partial void av_dict_free(ref IntPtr dict);
    [LibraryImport(Avutil)] internal static partial int av_strerror(int errnum, byte* errbuf, nuint errbufSize);
    [LibraryImport(Avutil)] internal static partial uint avutil_version();
    [LibraryImport(Avutil)] internal static partial IntPtr av_version_info();

    // ---- libswscale ----
    [LibraryImport(Swscale)] internal static partial IntPtr sws_getContext(int sw, int sh, int sfmt, int dw, int dh, int dfmt, int flags, IntPtr srcF, IntPtr dstF, IntPtr param);
    [LibraryImport(Swscale)] internal static partial int sws_scale(IntPtr c, byte** srcSlice, int* srcStride, int srcY, int srcH, byte** dst, int* dstStride);
    [LibraryImport(Swscale)] internal static partial void sws_freeContext(IntPtr c);
    [LibraryImport(Swscale)] internal static partial uint swscale_version();

    // ---- libswresample ----
    [LibraryImport(Swresample)] internal static partial int swr_alloc_set_opts2(
        out IntPtr swr, AvChannelLayout* outLayout, int outFmt, int outRate,
        AvChannelLayout* inLayout, int inFmt, int inRate, int logOffset, IntPtr logCtx);
    [LibraryImport(Swresample)] internal static partial int swr_init(IntPtr swr);
    [LibraryImport(Swresample)] internal static partial void swr_free(ref IntPtr swr);
    [LibraryImport(Swresample)] internal static partial int swr_convert(IntPtr swr, byte** outBuf, int outCount, byte** inBuf, int inCount);
    [LibraryImport(Swresample)] internal static partial int swr_get_out_samples(IntPtr swr, int inCount);
    [LibraryImport(Swresample)] internal static partial uint swresample_version();
}
