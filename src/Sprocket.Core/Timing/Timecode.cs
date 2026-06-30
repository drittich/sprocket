namespace Sprocket.Core.Timing;

/// <summary>
/// A position or duration on the timeline, stored as <see cref="long"/> ticks at a fixed global
/// resolution (<see cref="TicksPerSecond"/>). This is the canonical time unit for the whole editor
/// (ARCHITECTURE.md §3): all arithmetic happens in integer ticks so long timelines never drift the
/// way accumulated <see cref="double"/> seconds would.
/// </summary>
/// <remarks>
/// Frame indices and audio sample indices both derive from the same tick clock, so video and audio
/// stay locked to one source of truth. Conversions use <see cref="Int128"/> internally so the
/// intermediate products never overflow for realistic durations.
/// </remarks>
public readonly record struct Timecode : IComparable<Timecode>
{
    /// <summary>
    /// Ticks per second — the global time base (ARCHITECTURE.md §3). 240000 is chosen because it is
    /// an exact multiple of both 48 kHz audio (5 ticks/sample) and every common frame rate, including
    /// the NTSC rationals (30000/1001 → 8008 ticks/frame, 24000/1001 → 10010, 60000/1001 → 4004), as
    /// well as 24/25/30/50/60. Exact sample boundaries matter because audio is the master clock (§8);
    /// the MPEG 90000 base the doc lists as an alternative is *not* divisible by 48000 (1.875
    /// ticks/sample) and would lose the sample round-trip. This is the single knob if it ever changes;
    /// non-exact boundaries (e.g. 44.1 kHz) still resolve cleanly via the floor/round conversions.
    /// </summary>
    public const long TicksPerSecond = 240000;

    /// <summary>The raw tick count. May be negative (e.g. as the result of a subtraction).</summary>
    public long Ticks { get; }

    /// <summary>Creates a timecode from a raw tick count.</summary>
    public Timecode(long ticks) => Ticks = ticks;

    /// <summary>Zero.</summary>
    public static Timecode Zero => new(0);

    /// <summary>Creates a timecode from a tick count (alias for the constructor, for call-site clarity).</summary>
    public static Timecode FromTicks(long ticks) => new(ticks);

    /// <summary>Creates a timecode from seconds. Convenience for tests/UI; rounds to the nearest tick.</summary>
    public static Timecode FromSeconds(double seconds) => new((long)Math.Round(seconds * TicksPerSecond));

    /// <summary>The time in seconds as a <see cref="double"/>. For display only — never for further timing math.</summary>
    public double ToSeconds() => (double)Ticks / TicksPerSecond;

    // ---- Frame conversions (video) ----

    /// <summary>
    /// Creates the timecode at the start of the given frame index at <paramref name="frameRate"/>.
    /// Rounds to the nearest tick.
    /// </summary>
    public static Timecode FromFrames(long frameIndex, Rational frameRate)
    {
        // ticks = frameIndex * (1/fps) * TicksPerSecond = frameIndex * Den * TicksPerSecond / Num
        Int128 numerator = (Int128)frameIndex * frameRate.Den * TicksPerSecond;
        return new Timecode((long)RoundedDivide(numerator, frameRate.Num));
    }

    /// <summary>
    /// The index of the frame that contains this time at <paramref name="frameRate"/>
    /// (floor — the frame currently on screen). Assumes a non-negative time.
    /// </summary>
    public long ToFrameIndex(Rational frameRate)
    {
        // frameIndex = floor(seconds * fps) = floor(Ticks * Num / (TicksPerSecond * Den))
        Int128 numerator = (Int128)Ticks * frameRate.Num;
        Int128 denominator = (Int128)TicksPerSecond * frameRate.Den;
        return (long)FloorDivide(numerator, denominator);
    }

    // ---- Sample conversions (audio) ----

    /// <summary>Creates the timecode at the start of the given audio sample index. Rounds to the nearest tick.</summary>
    public static Timecode FromSamples(long sampleIndex, int sampleRate)
    {
        Int128 numerator = (Int128)sampleIndex * TicksPerSecond;
        return new Timecode((long)RoundedDivide(numerator, sampleRate));
    }

    /// <summary>The index of the audio sample that contains this time (floor). Assumes a non-negative time.</summary>
    public long ToSampleIndex(int sampleRate)
    {
        Int128 numerator = (Int128)Ticks * sampleRate;
        return (long)FloorDivide(numerator, TicksPerSecond);
    }

    // ---- Scaling ----

    /// <summary>
    /// This time scaled by an exact rational factor, rounded to the nearest tick. Retime/speed uses this
    /// (PLAN.md step 21): a clip's timeline offset maps to a source offset by multiplying by the speed ratio,
    /// and a source span maps to a timeline duration by multiplying by the reciprocal. Done in
    /// <see cref="Int128"/> so the intermediate product never overflows for realistic durations.
    /// </summary>
    public Timecode Scale(Rational factor)
    {
        Int128 numerator = (Int128)Ticks * factor.Num;
        return new Timecode((long)RoundedDivide(numerator, factor.Den));
    }

    // ---- Arithmetic ----

    public static Timecode operator +(Timecode a, Timecode b) => new(a.Ticks + b.Ticks);
    public static Timecode operator -(Timecode a, Timecode b) => new(a.Ticks - b.Ticks);
    public static Timecode operator -(Timecode a) => new(-a.Ticks);

    public static bool operator <(Timecode a, Timecode b) => a.Ticks < b.Ticks;
    public static bool operator >(Timecode a, Timecode b) => a.Ticks > b.Ticks;
    public static bool operator <=(Timecode a, Timecode b) => a.Ticks <= b.Ticks;
    public static bool operator >=(Timecode a, Timecode b) => a.Ticks >= b.Ticks;

    /// <summary>The smaller of two timecodes.</summary>
    public static Timecode Min(Timecode a, Timecode b) => a.Ticks <= b.Ticks ? a : b;

    /// <summary>The larger of two timecodes.</summary>
    public static Timecode Max(Timecode a, Timecode b) => a.Ticks >= b.Ticks ? a : b;

    /// <inheritdoc />
    public int CompareTo(Timecode other) => Ticks.CompareTo(other.Ticks);

    /// <inheritdoc />
    public override string ToString() => $"{ToSeconds():0.###}s ({Ticks} ticks)";

    /// <summary>Integer division of <paramref name="num"/> by <paramref name="den"/>, rounded to nearest (ties away from zero).</summary>
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

    /// <summary>Floor division (rounds toward negative infinity), unlike C#'s truncating <c>/</c>.</summary>
    private static Int128 FloorDivide(Int128 num, Int128 den)
    {
        Int128 q = num / den;
        Int128 r = num % den;
        // If the remainder is non-zero and the signs of num/den differ, the truncated quotient is one too high.
        if (r != 0 && ((r < 0) != (den < 0)))
            q -= 1;
        return q;
    }
}
