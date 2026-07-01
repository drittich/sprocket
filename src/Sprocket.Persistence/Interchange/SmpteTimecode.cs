using Sprocket.Core.Timing;

namespace Sprocket.Persistence.Interchange;

/// <summary>
/// Pure conversion between Sprocket's tick-based <see cref="Timecode"/> and SMPTE wall-clock timecode
/// (<c>HH:MM:SS:FF</c>, or <c>HH:MM:SS;FF</c> for drop-frame), used by the EDL exporter (PLAN.md step 28). Kept
/// separate from any I/O so it is unit-testable headlessly and correct for the NTSC rational rates.
/// </summary>
/// <remarks>
/// A timecode is a labelling of frame indices. The <b>FF</b> field counts in the <see cref="NominalRate"/> (the real
/// rate rounded — 30 for 29.97, 24 for 23.976). <b>Non-drop</b> timecode simply divides the frame index by that
/// nominal rate, so for NTSC it drifts from wall-clock. <b>Drop-frame</b> (NTSC 29.97 / 59.94 only) skips two (or
/// four) frame <em>numbers</em> each minute except every tenth, keeping the label within ~a frame of real time —
/// implemented here with the reference integer algorithm.
/// </remarks>
public static class SmpteTimecode
{
    /// <summary>The real rate rounded to the nearest whole frame rate used as the FF divisor (30, 25, 24, 60, …).</summary>
    public static int NominalRate(Rational frameRate)
    {
        if (frameRate.Num <= 0 || frameRate.Den <= 0)
            return 0;
        return (int)Math.Round((double)frameRate.Num / frameRate.Den);
    }

    /// <summary>Whether a rate is a non-integer (NTSC-style) rate, i.e. its exact frames-per-second is fractional.</summary>
    public static bool IsNtsc(Rational frameRate) => frameRate.Num % frameRate.Den != 0;

    /// <summary>Whether drop-frame timecode applies by convention: NTSC rates whose nominal rate is 30 or 60.</summary>
    public static bool IsDropFrameRate(Rational frameRate)
    {
        if (!IsNtsc(frameRate))
            return false;
        int nominal = NominalRate(frameRate);
        return nominal == 30 || nominal == 60;
    }

    /// <summary>The CMX3600 frame-code-mode label for a rate ("DROP FRAME" / "NON-DROP FRAME").</summary>
    public static string FrameCodeMode(Rational frameRate) => IsDropFrameRate(frameRate) ? "DROP FRAME" : "NON-DROP FRAME";

    /// <summary>Formats <paramref name="time"/> as SMPTE timecode at <paramref name="frameRate"/>, using drop-frame
    /// when <paramref name="dropFrame"/> is set (defaults to the rate's convention).</summary>
    public static string Format(Timecode time, Rational frameRate, bool? dropFrame = null)
    {
        int nominal = NominalRate(frameRate);
        if (nominal <= 0)
            return "00:00:00:00";
        bool df = dropFrame ?? IsDropFrameRate(frameRate);
        long frameNumber = Math.Max(0, time.ToFrameIndex(frameRate));
        return FramesToString(frameNumber, nominal, df, frameRate);
    }

    /// <summary>Parses a SMPTE timecode string (<c>HH:MM:SS:FF</c> or <c>HH:MM:SS;FF</c>) back to a
    /// <see cref="Timecode"/> at <paramref name="frameRate"/>. Drop-frame is inferred from a <c>;</c> / <c>.</c>
    /// separator before the frames field, or forced via <paramref name="dropFrame"/>.</summary>
    public static Timecode Parse(string timecode, Rational frameRate, bool? dropFrame = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(timecode);
        int nominal = NominalRate(frameRate);
        if (nominal <= 0)
            throw new ArgumentException("Invalid frame rate.", nameof(frameRate));

        // Frames are separated from seconds by ':' (non-drop) or ';'/'.' (drop-frame); the rest are ':'.
        bool df = dropFrame ?? (timecode.Contains(';') || timecode.Contains('.'));
        string[] parts = timecode.Split([':', ';', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            throw new FormatException($"'{timecode}' is not a HH:MM:SS:FF timecode.");
        int hh = int.Parse(parts[0]);
        int mm = int.Parse(parts[1]);
        int ss = int.Parse(parts[2]);
        int ff = int.Parse(parts[3]);

        long frameNumber = (((long)hh * 3600 + mm * 60 + ss) * nominal) + ff;
        if (df)
        {
            int dropFrames = nominal == 60 ? 4 : 2;
            long totalMinutes = (long)hh * 60 + mm;
            frameNumber -= dropFrames * (totalMinutes - totalMinutes / 10);
        }
        return Timecode.FromFrames(frameNumber, frameRate);
    }

    private static string FramesToString(long frameNumber, int nominal, bool dropFrame, Rational frameRate)
    {
        if (dropFrame)
        {
            int dropFrames = nominal == 60 ? 4 : 2;
            long framesPer10Minutes = (long)Math.Round((double)frameRate.Num * 600 / frameRate.Den);
            long framesPerMinute = (long)nominal * 60 - dropFrames;

            long d = frameNumber / framesPer10Minutes;
            long m = frameNumber % framesPer10Minutes;
            frameNumber += dropFrames * 9 * d;
            if (m > dropFrames)
                frameNumber += dropFrames * ((m - dropFrames) / framesPerMinute);
        }

        int frames = (int)(frameNumber % nominal);
        long totalSeconds = frameNumber / nominal;
        int seconds = (int)(totalSeconds % 60);
        int minutes = (int)((totalSeconds / 60) % 60);
        int hours = (int)((totalSeconds / 3600) % 24);
        char frameSep = dropFrame ? ';' : ':';
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}{frameSep}{frames:D2}";
    }
}
