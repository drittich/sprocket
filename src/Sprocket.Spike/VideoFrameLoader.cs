using System;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;

namespace Sprocket.Spike;

/// <summary>
/// Decodes a single video frame to a native RGBA buffer using library-level FFmpeg
/// (Sdcb.FFmpeg). The decoded pixels live in FFmpeg's native AVFrame buffer — they are
/// never copied into a managed array. The returned <see cref="DecodedFrame"/> owns that
/// native buffer and must be kept alive for as long as any SKImage wraps its pointer.
/// </summary>
public sealed class DecodedFrame : IDisposable
{
    private readonly Frame _rgba;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Pointer to the first (only) plane of tightly-ish packed RGBA8888 pixels.</summary>
    public IntPtr Pixels => _rgba.Data[0];

    /// <summary>Bytes per row (stride) of the RGBA buffer.</summary>
    public int RowBytes => _rgba.Linesize[0];

    private DecodedFrame(Frame rgba, int width, int height)
    {
        _rgba = rgba;
        Width = width;
        Height = height;
    }

    public static DecodedFrame DecodeFirst(string path)
    {
        using FormatContext fc = FormatContext.OpenInputUrl(path);
        fc.LoadStreamInfo();

        MediaStream videoStream = fc.GetVideoStream();

        using var decoder = new CodecContext(Codec.FindDecoderById(videoStream.Codecpar!.CodecId));
        decoder.FillParameters(videoStream.Codecpar);
        decoder.Open();

        int width = decoder.Width;
        int height = decoder.Height;

        // Destination: a persistent native RGBA frame we hand to Skia by pointer.
        Frame rgba = Frame.CreateVideo(width, height, AVPixelFormat.Rgba);
        rgba.EnsureBuffer(align: 4);

        using var packet = new Packet();
        using var srcFrame = new Frame();
        using var converter = new VideoFrameConverter();

        try
        {
            while (fc.ReadFrame(packet) == CodecResult.Success)
            {
                try
                {
                    if (packet.StreamIndex != videoStream.Index)
                        continue;

                    decoder.SendPacket(packet);
                    if (decoder.ReceiveFrame(srcFrame) == CodecResult.Success)
                    {
                        // YUV -> RGBA on the CPU (swscale). For the slice this is fine;
                        // the hw-accel milestone moves this to a GPU texture (FromTexture).
                        converter.ConvertFrame(srcFrame, rgba, SWS.Bilinear);
                        return new DecodedFrame(rgba, width, height);
                    }
                }
                finally
                {
                    packet.Unref();
                }
            }
        }
        catch
        {
            rgba.Dispose();
            throw;
        }

        rgba.Dispose();
        throw new InvalidOperationException($"No decodable video frame found in '{path}'.");
    }

    public void Dispose() => _rgba.Dispose();
}
