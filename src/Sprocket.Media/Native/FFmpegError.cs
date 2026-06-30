using System.Runtime.InteropServices;

namespace Sprocket.Media.Native;

/// <summary>Thrown when an FFmpeg call fails; carries the numeric AVERROR and its decoded message (§15).</summary>
public sealed class FFmpegException(string operation, int error, string message)
    : Exception($"{operation} failed: {message} (AVERROR {error})")
{
    public int Error { get; } = error;
}

/// <summary>Outcome of a <c>receive_frame</c>/<c>receive_packet</c> pull, mirroring the old Sdcb CodecResult.</summary>
internal enum CodecResult
{
    Success,
    Again,   // AVERROR(EAGAIN): decoder/encoder needs more input
    Eof,     // AVERROR_EOF: fully drained
    Other,   // some other negative code (a real error)
}

/// <summary>Error-code helpers translating FFmpeg's negative AVERROR returns at the Media boundary (§15).</summary>
internal static unsafe class FFmpegError
{
    // AVERROR_EOF = -(MKTAG('E','O','F',' ')). AVERROR(e) = -e; EAGAIN is 35 on macOS/BSD, 11 elsewhere.
    public const int AverrorEof = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));
    public static int AverrorEagain { get; } = -(OperatingSystem.IsMacOS() ? 35 : 11);

    /// <summary>Throws <see cref="FFmpegException"/> when <paramref name="ret"/> is negative; otherwise returns it.</summary>
    public static int Check(int ret, string operation)
    {
        if (ret < 0)
            throw new FFmpegException(operation, ret, Describe(ret));
        return ret;
    }

    /// <summary>Classifies a receive_* return into the success / again / eof / other buckets.</summary>
    public static CodecResult Classify(int ret)
    {
        if (ret >= 0) return CodecResult.Success;
        if (ret == AverrorEof) return CodecResult.Eof;
        if (ret == AverrorEagain) return CodecResult.Again;
        return CodecResult.Other;
    }

    /// <summary>Decodes an AVERROR code to its human-readable FFmpeg message.</summary>
    public static string Describe(int error)
    {
        byte* buf = stackalloc byte[256];
        LibAv.av_strerror(error, buf, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error {error}";
    }
}
