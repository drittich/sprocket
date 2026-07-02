using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sprocket.Core.Audio;
using Sprocket.Core.Timing;

namespace Sprocket.Export;

/// <summary>
/// Writes interleaved float32 PCM as a standard IEEE-float WAV file — the render cache's audio intermediate
/// (ARCHITECTURE.md §20, PLAN.md step 32: "audio as uncompressed PCM"). Float32 is the mixer's native sample
/// format, so the cached mix is bit-identical to a live mix of the same state; WAV (rather than raw PCM) keeps
/// the file self-describing and playable in any tool. Pure managed I/O — no libav pipeline, so writing can never
/// interact with the in-process muxer hazard the proxy transcoder documents.
/// </summary>
public sealed class WavePcmWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _channels;
    private long _dataBytes;
    private bool _finished;

    /// <summary>Creates/overwrites <paramref name="path"/> and writes the header (sizes patched by
    /// <see cref="Finish"/>, so an unfinished file is detectably truncated rather than silently short).</summary>
    public WavePcmWriter(string path, int sampleRate, int channels)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channels, 0);
        _channels = channels;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0); // RIFF size — patched in Finish
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);                       // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 3);                        // WAVE_FORMAT_IEEE_FLOAT
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * channels * 4)); // byte rate
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(channels * 4));   // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 32);                       // bits per sample
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0); // data size — patched in Finish
        _stream.Write(header);
    }

    private const int HeaderBytes = 44;

    /// <summary>Appends interleaved float32 samples (length must be a whole number of sample-frames).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException("Length must be a whole number of sample-frames.", nameof(interleaved));
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(interleaved); // little-endian on all supported platforms
        _stream.Write(bytes);
        _dataBytes += bytes.Length;
    }

    /// <summary>Patches the header sizes and closes the file. A file not finished (a cancelled/failed render)
    /// carries zero sizes and is rejected by <see cref="WavePcmReader.Open"/>.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;

        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(_dataBytes + (HeaderBytes - 8), uint.MaxValue));
        _stream.Position = 4;
        _stream.Write(size);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(_dataBytes, uint.MaxValue));
        _stream.Position = 40;
        _stream.Write(size);
        _stream.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            _stream.Dispose(); // sizes left zero: the file reads as empty/invalid rather than as a short mix
        }
    }
}

/// <summary>
/// Reads a <see cref="WavePcmWriter"/>-shaped IEEE-float WAV back as an <see cref="IPcmReader"/> — the render
/// cache's audio intermediate surfaced through the same seam every live source uses (ARCHITECTURE.md §20). Pure
/// managed I/O; a cached read never opens a libav decoder.
/// </summary>
public sealed class WavePcmReader : IPcmReader
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly long _totalFrames;

    private WavePcmReader(FileStream stream, int sampleRate, int channels, long dataOffset, long dataBytes)
    {
        _stream = stream;
        SampleRate = sampleRate;
        Channels = channels;
        _dataOffset = dataOffset;
        _totalFrames = dataBytes / (channels * 4L);
    }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>Total sample-frames in the file.</summary>
    public long TotalFrames => _totalFrames;

    /// <summary>Opens <paramref name="path"/>, validating it is a float32 WAV with a non-empty data chunk.
    /// Throws <see cref="InvalidDataException"/> on any other shape (incl. an unfinished writer's zero sizes).</summary>
    public static WavePcmReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
                throw new InvalidDataException("Not a WAV file.");

            int sampleRate = 0, channels = 0, format = 0, bits = 0;
            long dataOffset = -1, dataBytes = 0;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            while (stream.Position + 8 <= stream.Length)
            {
                stream.ReadExactly(chunk);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (size < 16)
                        throw new InvalidDataException("WAV fmt chunk is too small.");
                    stream.ReadExactly(fmt);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                    sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                    stream.Position += size - 16;
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                {
                    dataOffset = stream.Position;
                    dataBytes = Math.Min(size, stream.Length - dataOffset);
                    break; // our writer puts data last; anything after it is not ours to interpret
                }
                else
                {
                    stream.Position += size + (size & 1); // skip unknown chunk (word-aligned)
                }
            }

            if (format != 3 || bits != 32 || channels <= 0 || sampleRate <= 0)
                throw new InvalidDataException("Not a float32 WAV.");
            if (dataOffset < 0 || dataBytes <= 0)
                throw new InvalidDataException("The WAV has no PCM data (an unfinished render?).");

            var reader = new WavePcmReader(stream, sampleRate, channels, dataOffset, dataBytes);
            stream.Position = dataOffset;
            return reader;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destinationInterleaved)
    {
        if (destinationInterleaved.Length % Channels != 0)
            throw new ArgumentException("Length must be a whole number of sample-frames.", nameof(destinationInterleaved));

        long positionFrames = (_stream.Position - _dataOffset) / (Channels * 4L);
        int wantFrames = destinationInterleaved.Length / Channels;
        int frames = (int)Math.Clamp(_totalFrames - positionFrames, 0, wantFrames);
        if (frames <= 0)
            return 0;

        Span<byte> bytes = MemoryMarshal.AsBytes(destinationInterleaved[..(frames * Channels)]);
        _stream.ReadExactly(bytes);
        return frames;
    }

    /// <inheritdoc />
    public void SeekTo(Timecode sourceTime)
    {
        long frame = Math.Clamp(sourceTime.ToSampleIndex(SampleRate), 0, _totalFrames);
        _stream.Position = _dataOffset + frame * Channels * 4L;
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();
}
