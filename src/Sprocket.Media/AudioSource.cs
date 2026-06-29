using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using Sprocket.Core.Audio;
using Sprocket.Core.Timing;

namespace Sprocket.Media;

/// <summary>
/// Opens one source file's audio stream with library-level FFmpeg, decodes it, and resamples it to
/// <b>interleaved float32 at a target sample rate and channel count</b> via libswresample — implementing
/// <see cref="IPcmReader"/> so the mixer (Sprocket.Audio) only ever sums uniform buffers (ARCHITECTURE.md
/// §6, §11). Resampling happens here, once at decode, exactly as the architecture prescribes.
/// </summary>
/// <remarks>
/// <para><b>Not thread-safe</b> (like <see cref="MediaSource"/>): one decoder + reusable frame/packet, driven
/// by a single mixing thread. Seeking is I-frame-seek → flush → sample-accurate discard, mirroring the video
/// path (§8). Resampled samples are buffered in a small managed leftover between <see cref="Read"/> calls —
/// audio is the one data class allowed on the managed heap (§1), and the buffer holds at most one decoded
/// frame's overflow so steady-state reads add no allocation.</para>
/// </remarks>
public sealed unsafe class AudioSource : IPcmReader
{
    private readonly FormatContext _format;
    private readonly CodecContext _decoder;
    private readonly int _audioIndex;
    private readonly AVRational _timeBase;
    private readonly Packet _packet = new();
    private readonly Frame _srcFrame = new();        // reusable decoder output (source format/layout)
    private SwrContext* _swr;

    private nint _scratch;                            // native interleaved-float resample destination
    private int _scratchFrames;                       // capacity of _scratch in sample-frames
    private float[] _leftover = [];                   // resampled samples not yet returned (interleaved)
    private int _leftoverOffset;                      // read cursor into _leftover, in floats
    private int _leftoverCount;                        // valid floats in _leftover from the cursor

    private bool _inputEof;       // ReadFrame returned no more packets
    private bool _packetFlushed;  // sent the end-of-stream flush packet to the decoder
    private bool _decoderDone;    // decoder fully drained; only swr's internal buffer remains
    private bool _swrDrained;     // swr flush returned no more samples

    private bool _needDiscardSetup;   // a seek happened; compute the discard from the next frame's PTS
    private Timecode _seekTarget;
    private long _discardFrames;       // sample-frames still to drop after a seek (decode-to-target)

    private bool _disposed;

    private AudioSource(FormatContext format, CodecContext decoder, MediaStream audioStream, int sampleRate, int channels)
    {
        _format = format;
        _decoder = decoder;
        _audioIndex = audioStream.Index;
        _timeBase = audioStream.TimeBase;
        SampleRate = sampleRate;
        Channels = channels;
        InitResampler();
    }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>
    /// Opens <paramref name="path"/> and its best audio stream, decoding to <paramref name="sampleRate"/> Hz /
    /// <paramref name="channels"/> interleaved float32. Throws if the file has no decodable audio stream
    /// (callers gate on <see cref="ProbedMediaInfo.HasAudio"/> first).
    /// </summary>
    public static AudioSource Open(string path, int sampleRate, int channels)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        FormatContext format = FormatContext.OpenInputUrl(path);
        try
        {
            format.LoadStreamInfo();

            MediaStream? audioStreamOrNull = format.FindBestStreamOrNull(AVMediaType.Audio, -1, -1);
            if (audioStreamOrNull is null)
                throw new InvalidOperationException($"No audio stream found in '{path}'.");

            MediaStream audioStream = audioStreamOrNull.Value;
            var decoder = new CodecContext(Codec.FindDecoderById(audioStream.Codecpar!.CodecId));
            decoder.FillParameters(audioStream.Codecpar);
            decoder.Open();

            return new AudioSource(format, decoder, audioStream, sampleRate, channels);
        }
        catch
        {
            format.Dispose();
            throw;
        }
    }

    private void InitResampler()
    {
        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, Channels);
        AVChannelLayout inLayout = _decoder.ChLayout; // a copy; swr copies it again internally

        SwrContext* swr = null;
        int rc = ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outLayout, AVSampleFormat.Flt, SampleRate,
            &inLayout, _decoder.SampleFormat, _decoder.SampleRate,
            0, null);
        if (rc < 0 || swr is null)
        {
            ffmpeg.av_channel_layout_uninit(&outLayout);
            throw new InvalidOperationException($"Failed to allocate audio resampler (swr_alloc_set_opts2 = {rc}).");
        }

        rc = ffmpeg.swr_init(swr);
        ffmpeg.av_channel_layout_uninit(&outLayout);
        if (rc < 0)
        {
            ffmpeg.swr_free(&swr);
            throw new InvalidOperationException($"Failed to initialise audio resampler (swr_init = {rc}).");
        }
        _swr = swr;
    }

    /// <inheritdoc />
    public int Read(Span<float> destinationInterleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int capacityFrames = destinationInterleaved.Length / Channels;
        if (capacityFrames == 0)
            return 0;

        int written = 0; // sample-frames

        written += DrainLeftover(destinationInterleaved, written, capacityFrames);

        while (written < capacityFrames)
        {
            int produced = FillScratch();
            if (produced == 0)
                break; // end of stream

            int srcFrame = 0;
            if (_discardFrames > 0)
            {
                int skip = (int)Math.Min(_discardFrames, produced);
                srcFrame += skip;
                produced -= skip;
                _discardFrames -= skip;
                if (produced == 0)
                    continue;
            }

            int take = Math.Min(capacityFrames - written, produced);
            CopyScratch(srcFrame, take, destinationInterleaved, written);
            written += take;

            int overflow = produced - take;
            if (overflow > 0)
                StashLeftover(srcFrame + take, overflow);
        }

        return written;
    }

    /// <inheritdoc />
    public void SeekTo(Timecode sourceTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long ts = MediaTime.ToStreamTimestamp(sourceTime, _timeBase);
        _format.SeekFrame(ts, _audioIndex, AVSEEK_FLAG.Backward);
        ffmpeg.avcodec_flush_buffers(_decoder);
        ffmpeg.swr_init(_swr); // re-initialising flushes the resampler's internal buffer

        _inputEof = false;
        _packetFlushed = false;
        _decoderDone = false;
        _swrDrained = false;
        _leftoverOffset = 0;
        _leftoverCount = 0;
        _discardFrames = 0;
        _needDiscardSetup = true;
        _seekTarget = sourceTime;
    }

    /// <summary>Copies as many leftover frames as fit into the destination, returning the frame count written.</summary>
    private int DrainLeftover(Span<float> dest, int destFrameOffset, int capacityFrames)
    {
        if (_leftoverCount == 0)
            return 0;

        int available = _leftoverCount / Channels;
        int take = Math.Min(capacityFrames - destFrameOffset, available);
        int floats = take * Channels;
        _leftover.AsSpan(_leftoverOffset, floats).CopyTo(dest.Slice(destFrameOffset * Channels, floats));
        _leftoverOffset += floats;
        _leftoverCount -= floats;
        if (_leftoverCount == 0)
            _leftoverOffset = 0;
        return take;
    }

    /// <summary>Resamples the next decoded frame (or flushes swr at EOF) into <see cref="_scratch"/>;
    /// returns the number of sample-frames produced, or 0 only when the stream is fully drained.</summary>
    private int FillScratch()
    {
        while (true)
        {
            if (_decoderDone)
                return _swrDrained ? 0 : ResampleFlush();

            if (!TryReceiveSrcFrame())
            {
                _decoderDone = true;
                continue; // drain the resampler on the next iteration
            }

            if (_needDiscardSetup)
                SetupSeekDiscard();

            int produced = ResampleFrame();
            if (produced > 0)
                return produced;
            // swr buffered the input without emitting yet — feed it another frame.
        }
    }

    /// <summary>Computes how many resampled frames to drop so the first returned sample lands at the seek
    /// target, from the just-decoded frame's presentation time.</summary>
    private void SetupSeekDiscard()
    {
        _needDiscardSetup = false;
        long pts = _srcFrame.BestEffortTimestamp != MediaTime.NoPts ? _srcFrame.BestEffortTimestamp : _srcFrame.Pts;
        if (pts == MediaTime.NoPts)
            return;

        Timecode landing = MediaTime.ToTimecode(pts, _timeBase);
        long delta = _seekTarget.Ticks - landing.Ticks;
        _discardFrames = delta <= 0 ? 0 : (long)Math.Round((double)delta / Timecode.TicksPerSecond * SampleRate);
    }

    private int ResampleFrame()
    {
        int inCount = _srcFrame.NbSamples;
        int outMax = ffmpeg.swr_get_out_samples(_swr, inCount);
        EnsureScratch(outMax);

        byte_ptrArray8 inPlanes = default;
        for (int i = 0; i < 8; i++)
            inPlanes[i] = _srcFrame.Data[i];

        byte_ptrArray8 outPlanes = default;
        outPlanes[0] = _scratch;

        int got = ffmpeg.swr_convert(_swr, (byte**)&outPlanes, _scratchFrames, (byte**)&inPlanes, inCount);
        return got < 0 ? 0 : got;
    }

    private int ResampleFlush()
    {
        int outMax = ffmpeg.swr_get_out_samples(_swr, 0);
        if (outMax <= 0)
        {
            _swrDrained = true;
            return 0;
        }
        EnsureScratch(outMax);

        byte_ptrArray8 outPlanes = default;
        outPlanes[0] = _scratch;

        int got = ffmpeg.swr_convert(_swr, (byte**)&outPlanes, _scratchFrames, null, 0);
        if (got <= 0)
        {
            _swrDrained = true;
            return 0;
        }
        return got;
    }

    /// <summary>Receives one decoded audio frame, feeding packets / a flush packet as the decoder asks for
    /// input. Returns false only at true end of stream. Mirrors <see cref="MediaSource"/>'s video loop.</summary>
    private bool TryReceiveSrcFrame()
    {
        while (true)
        {
            CodecResult result = _decoder.ReceiveFrame(_srcFrame);
            switch (result)
            {
                case CodecResult.Success:
                    return true;
                case CodecResult.EOF:
                    return false;
                case CodecResult.Again:
                    if (!FeedDecoder())
                        return false;
                    continue;
                default:
                    return false;
            }
        }
    }

    private bool FeedDecoder()
    {
        if (_inputEof)
        {
            if (_packetFlushed)
                return false;
            _packetFlushed = true;
            _decoder.SendPacket(null); // drain mode
            return true;
        }

        while (_format.ReadFrame(_packet) == CodecResult.Success)
        {
            try
            {
                if (_packet.StreamIndex == _audioIndex)
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

        _inputEof = true;
        return FeedDecoder();
    }

    private void EnsureScratch(int frames)
    {
        if (frames <= _scratchFrames)
            return;
        int bytes = frames * Channels * sizeof(float);
        _scratch = _scratch == 0 ? Marshal.AllocHGlobal(bytes) : Marshal.ReAllocHGlobal(_scratch, bytes);
        _scratchFrames = frames;
    }

    private void CopyScratch(int srcFrame, int frames, Span<float> dest, int destFrame)
    {
        var src = new ReadOnlySpan<float>((float*)_scratch + srcFrame * Channels, frames * Channels);
        src.CopyTo(dest.Slice(destFrame * Channels, frames * Channels));
    }

    private void StashLeftover(int srcFrame, int frames)
    {
        int floats = frames * Channels;
        if (_leftover.Length < floats)
            _leftover = new float[floats];
        new ReadOnlySpan<float>((float*)_scratch + srcFrame * Channels, floats).CopyTo(_leftover);
        _leftoverOffset = 0;
        _leftoverCount = floats;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_swr is not null)
        {
            SwrContext* swr = _swr;
            ffmpeg.swr_free(&swr);
            _swr = null;
        }
        if (_scratch != 0)
        {
            Marshal.FreeHGlobal(_scratch);
            _scratch = 0;
        }

        _packet.Dispose();
        _srcFrame.Dispose();
        _decoder.Dispose();
        _format.Dispose();
    }
}
