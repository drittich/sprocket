using Sprocket.Core.Timing;
using Sprocket.Media.Native;

namespace Sprocket.Media;

/// <summary>
/// Conversions between FFmpeg stream timestamps (in a stream's <c>AVRational</c> time base) and the
/// editor's canonical <see cref="Timecode"/> ticks. This is the only place FFmpeg's time base meets
/// Core's tick clock; Core never sees an <c>AVRational</c> (ARCHITECTURE.md §3, §11).
/// </summary>
/// <remarks>
/// All arithmetic widens to <see cref="Int128"/> so the intermediate products
/// (<c>pts * num * TicksPerSecond</c>) never overflow for realistic durations, mirroring
/// <see cref="Timecode"/>'s own conversions.
/// </remarks>
internal static class MediaTime
{
    /// <summary>FFmpeg's sentinel for "no timestamp" (<c>AV_NOPTS_VALUE</c>). Exposed as a const because the
    /// generated <c>ffmpeg.AV_NOPTS_VALUE</c> is not a compile-time constant.</summary>
    public const long NoPts = long.MinValue;

    /// <summary>
    /// Converts a stream timestamp (<paramref name="pts"/>, in <paramref name="timeBase"/> units) to a
    /// <see cref="Timecode"/>. Rounds to the nearest tick.
    /// </summary>
    public static Timecode ToTimecode(long pts, AvRational timeBase)
    {
        // ticks = pts * (num/den) seconds * TicksPerSecond = pts * num * TicksPerSecond / den
        Int128 numerator = (Int128)pts * timeBase.Num * Timecode.TicksPerSecond;
        return new Timecode((long)RoundedDivide(numerator, timeBase.Den));
    }

    /// <summary>
    /// Converts a <see cref="Timecode"/> to a stream timestamp in <paramref name="timeBase"/> units
    /// (for seeking). Rounds to the nearest unit; never returns negative.
    /// </summary>
    public static long ToStreamTimestamp(Timecode time, AvRational timeBase)
    {
        // pts = ticks/TicksPerSecond seconds / (num/den) = ticks * den / (TicksPerSecond * num)
        Int128 numerator = (Int128)time.Ticks * timeBase.Den;
        Int128 denominator = (Int128)Timecode.TicksPerSecond * timeBase.Num;
        long ts = (long)RoundedDivide(numerator, denominator);
        return ts < 0 ? 0 : ts;
    }

    /// <summary>Converts an <c>AV_TIME_BASE</c> (microsecond) duration to a <see cref="Timecode"/>.</summary>
    public static Timecode FromMicroseconds(long microseconds)
    {
        Int128 numerator = (Int128)microseconds * Timecode.TicksPerSecond;
        return new Timecode((long)RoundedDivide(numerator, 1_000_000));
    }

    /// <summary>Integer division rounded to nearest (ties away from zero), with a positive divisor assumed for the editor's bases.</summary>
    private static Int128 RoundedDivide(Int128 num, Int128 den)
    {
        if (den < 0)
        {
            num = -num;
            den = -den;
        }
        Int128 half = den / 2;
        return num >= 0 ? (num + half) / den : (num - half) / den;
    }
}
