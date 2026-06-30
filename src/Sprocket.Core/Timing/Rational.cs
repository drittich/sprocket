namespace Sprocket.Core.Timing;

/// <summary>
/// An exact rational number, mirroring FFmpeg's <c>AVRational</c>. Used for frame rates
/// (e.g. 30000/1001 for 29.97 fps) so timing never accumulates <see cref="double"/> error.
/// See ARCHITECTURE.md §3.
/// </summary>
/// <remarks>
/// Always stored in reduced form with a positive denominator, so equality is value equality
/// of the reduced fraction (29.97 == 30000/1001 == 60000/2002).
/// </remarks>
public readonly record struct Rational : IComparable<Rational>
{
    /// <summary>Numerator (may be negative; carries the sign).</summary>
    public int Num { get; }

    /// <summary>Denominator (always positive after reduction).</summary>
    public int Den { get; }

    /// <summary>Creates a reduced rational. The denominator must be non-zero.</summary>
    public Rational(int num, int den)
    {
        if (den == 0)
            throw new ArgumentOutOfRangeException(nameof(den), "Rational denominator must be non-zero.");

        // Normalise sign onto the numerator, then reduce by the GCD.
        if (den < 0)
        {
            num = -num;
            den = -den;
        }

        int g = Gcd(Math.Abs(num), den);
        if (g > 1)
        {
            num /= g;
            den /= g;
        }

        Num = num;
        Den = den;
    }

    /// <summary>Zero (0/1).</summary>
    public static Rational Zero => new(0, 1);

    /// <summary>One (1/1) — the identity factor (e.g. normal playback speed, PLAN.md step 21).</summary>
    public static Rational One => new(1, 1);

    /// <summary>The fraction as a <see cref="double"/>. For display/approximation only — never for timing math.</summary>
    public double ToDouble() => (double)Num / Den;

    /// <summary>The reciprocal (Den/Num). Throws if this is zero.</summary>
    public Rational Inverse()
    {
        if (Num == 0)
            throw new InvalidOperationException("Cannot invert a zero rational.");
        return new Rational(Den, Num);
    }

    /// <inheritdoc />
    public int CompareTo(Rational other)
    {
        // a/b ? c/d  ->  a*d ? c*b  (both denominators positive). Widen to long to avoid overflow.
        long left = (long)Num * other.Den;
        long right = (long)other.Num * Den;
        return left.CompareTo(right);
    }

    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;
    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;
    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;

    /// <inheritdoc />
    public override string ToString() => $"{Num}/{Den}";

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a == 0 ? 1 : a;
    }
}
