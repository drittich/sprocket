using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// How a keyframe's value blends across the segment that <em>leaves</em> it toward the next keyframe
/// (ARCHITECTURE.md §9). Beyond the original <see cref="Hold"/>/<see cref="Linear"/> the enum gains the
/// eased modes that bring keyframing to Adobe-Premiere parity (PLAN.md step 16d) — additive, so old
/// projects (which only stored Hold/Linear) load unchanged. The eased modes shape the segment's
/// velocity curve; the labels follow the NLE convention of naming the gentle end:
/// <see cref="EaseOut"/> eases the <em>departure</em> from this keyframe, <see cref="EaseIn"/> eases the
/// <em>arrival</em> at the next, and <see cref="EaseInOut"/> (the "Auto Bezier" / smooth case) eases both.
/// </summary>
public enum Interpolation
{
    /// <summary>Hold the value until the next keyframe (step).</summary>
    Hold,

    /// <summary>Linearly interpolate toward the next keyframe's value (constant velocity).</summary>
    Linear,

    /// <summary>Ease the <em>arrival</em> at the next keyframe: fast departure, gentle (decelerating) end.</summary>
    EaseIn,

    /// <summary>Ease the <em>departure</em> from this keyframe: gentle (accelerating) start, fast arrival.</summary>
    EaseOut,

    /// <summary>Smooth both ends ("Auto Bezier"): zero velocity at the start and the end of the segment.</summary>
    EaseInOut,

    /// <summary>
    /// A fully <em>custom</em> velocity curve: the segment is a cubic Bezier driven by the keyframe's own
    /// outgoing handle (<see cref="Keyframe.EaseOut"/>) and the next keyframe's incoming handle
    /// (<see cref="Keyframe.EaseIn"/>) — the editable "velocity graph" case (PLAN.md step 16d). Null handles
    /// fall back to a gentle ease (<see cref="BezierHandle.DefaultEaseOut"/>/<see cref="BezierHandle.DefaultEaseIn"/>).
    /// </summary>
    Bezier,
}

/// <summary>
/// A cubic-Bezier control handle for a custom velocity curve (PLAN.md step 16d), in <em>segment-normalized</em>
/// space: <see cref="X"/> is the time fraction along the segment (0 = the segment's start keyframe, 1 = its end)
/// and <see cref="Y"/> is the value progress (0 = the start keyframe's value, 1 = the end keyframe's value).
/// Y may go outside [0,1] for an overshoot ("bounce") curve. This matches the CSS / After-Effects
/// <c>cubic-bezier(x1,y1,x2,y2)</c> model: a segment's curve passes through (0,0) and (1,1) with the start
/// keyframe's <see cref="Keyframe.EaseOut"/> as the first control point and the end keyframe's
/// <see cref="Keyframe.EaseIn"/> as the second.
/// </summary>
public readonly record struct BezierHandle(double X, double Y)
{
    /// <summary>Default outgoing control point (first Bezier control point) for a gentle "easy ease" start.</summary>
    public static BezierHandle DefaultEaseOut => new(0.33, 0.0);

    /// <summary>Default incoming control point (second Bezier control point) for a gentle "easy ease" end.</summary>
    public static BezierHandle DefaultEaseIn => new(0.67, 1.0);
}

/// <summary>A single animation keyframe: a value at a time, how it blends toward the next one, and (for
/// <see cref="Interpolation.Bezier"/>) the custom velocity handles.</summary>
/// <param name="Time">Timeline time of this keyframe.</param>
/// <param name="Value">The value at this time.</param>
/// <param name="Interpolation">How this keyframe blends into the following one.</param>
/// <param name="EaseOut">The outgoing Bezier handle — the first control point of the segment <em>leaving</em>
/// this keyframe (used only when <see cref="Interpolation"/> is <see cref="Interpolation.Bezier"/>). Null = the
/// gentle default.</param>
/// <param name="EaseIn">The incoming Bezier handle — the second control point of the segment <em>arriving</em>
/// at this keyframe (used only when the <em>previous</em> keyframe's interpolation is
/// <see cref="Interpolation.Bezier"/>). Null = the gentle default.</param>
public readonly record struct Keyframe(
    Timecode Time,
    double Value,
    Interpolation Interpolation = Interpolation.Linear,
    BezierHandle? EaseOut = null,
    BezierHandle? EaseIn = null);

/// <summary>
/// An effect parameter that is either a constant or a list of keyframes (ARCHITECTURE.md §9).
/// The render graph calls <see cref="Evaluate"/> at the frame's time before building the effect, so
/// the very same mechanism that drives a fade also drives any future keyframed parameter — no model
/// change required.
/// </summary>
public sealed class AnimatableValue
{
    private readonly double _constant;
    private readonly Keyframe[] _keyframes; // sorted ascending by time; empty when constant

    private AnimatableValue(double constant, Keyframe[] keyframes)
    {
        _constant = constant;
        _keyframes = keyframes;
    }

    /// <summary>Whether this value is animated (has keyframes) rather than constant.</summary>
    public bool IsAnimated => _keyframes.Length > 0;

    /// <summary>The keyframes, in ascending time order (empty if constant).</summary>
    public IReadOnlyList<Keyframe> Keyframes => _keyframes;

    /// <summary>Creates a constant (non-animated) value.</summary>
    public static AnimatableValue Constant(double value) => new(value, []);

    /// <summary>
    /// Creates an animated value from one or more keyframes. The keyframes are sorted by time;
    /// at least one is required.
    /// </summary>
    public static AnimatableValue Animated(IEnumerable<Keyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        Keyframe[] sorted = [.. keyframes];
        if (sorted.Length == 0)
            throw new ArgumentException("An animated value needs at least one keyframe.", nameof(keyframes));
        Array.Sort(sorted, static (a, b) => a.Time.CompareTo(b.Time));
        return new AnimatableValue(0, sorted);
    }

    /// <summary>
    /// Evaluates the value at timeline time <paramref name="t"/>. Constant values ignore <paramref name="t"/>.
    /// For animated values: clamps to the first/last keyframe outside the keyframe range, and interpolates
    /// within it according to the outgoing keyframe's <see cref="Interpolation"/> mode (the eased modes shape
    /// the segment's velocity curve — see <see cref="Ease"/>).
    /// </summary>
    public double Evaluate(Timecode t)
    {
        if (_keyframes.Length == 0)
            return _constant;

        if (_keyframes.Length == 1 || t <= _keyframes[0].Time)
            return _keyframes[0].Value;

        Keyframe last = _keyframes[^1];
        if (t >= last.Time)
            return last.Value;

        // Find the segment [k0, k1] with k0.Time <= t < k1.Time.
        for (int i = 0; i < _keyframes.Length - 1; i++)
        {
            Keyframe k0 = _keyframes[i];
            Keyframe k1 = _keyframes[i + 1];
            if (t < k1.Time)
            {
                if (k0.Interpolation == Interpolation.Hold)
                    return k0.Value;

                long span = k1.Time.Ticks - k0.Time.Ticks;
                double frac = span == 0 ? 0 : (double)(t.Ticks - k0.Time.Ticks) / span;
                double eased = k0.Interpolation == Interpolation.Bezier
                    // Custom velocity curve: this keyframe's outgoing handle + the next's incoming handle.
                    ? CubicBezierEase(frac, k0.EaseOut ?? BezierHandle.DefaultEaseOut, k1.EaseIn ?? BezierHandle.DefaultEaseIn)
                    : Ease(k0.Interpolation, frac);
                return k0.Value + (k1.Value - k0.Value) * eased;
            }
        }

        // Unreachable given the clamps above, but keeps the compiler happy.
        return last.Value;
    }

    /// <summary>
    /// Maps a linear segment fraction <paramref name="x"/> ∈ [0,1] to an eased fraction for the given
    /// <paramref name="mode"/>. Each curve is monotonic with f(0)=0 and f(1)=1, so the endpoints still land
    /// exactly on the keyframe values; only the velocity between them changes. <see cref="Interpolation.Hold"/>
    /// never reaches here (handled before the call). The shapes are deliberately simple polynomials (a
    /// quadratic accel/decel and a cubic smoothstep) — Bezier-like velocity without a curve solver, exact and
    /// trivially testable; a fully editable per-keyframe velocity graph is the step-16d follow-on.
    /// </summary>
    internal static double Ease(Interpolation mode, double x) => mode switch
    {
        // Gentle, accelerating start (zero velocity at x=0).
        Interpolation.EaseOut => x * x,
        // Gentle, decelerating end (zero velocity at x=1).
        Interpolation.EaseIn => 1 - (1 - x) * (1 - x),
        // Smoothstep: zero velocity at both ends.
        Interpolation.EaseInOut => x * x * (3 - 2 * x),
        // Linear (and any future mode) falls through to the straight fraction.
        _ => x,
    };

    /// <summary>
    /// Evaluates a cubic-Bezier easing curve at time fraction <paramref name="x"/> ∈ [0,1] given the two
    /// control points (the segment runs (0,0)→<paramref name="c1"/>→<paramref name="c2"/>→(1,1)). Solves
    /// Bx(t)=x for the curve parameter t, then returns By(t) — the value progress. The control X's are clamped
    /// to [0,1] so the curve is a well-defined function of time; the Y's are left free so the curve can
    /// overshoot. Newton-Raphson with a bisection fallback (the standard browser approach). Exact at the
    /// endpoints, so a Bezier segment still lands on its keyframe values.
    /// </summary>
    internal static double CubicBezierEase(double x, BezierHandle c1, BezierHandle c2)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;

        double x1 = Math.Clamp(c1.X, 0, 1), x2 = Math.Clamp(c2.X, 0, 1);
        double t = SolveForT(x, x1, x2);
        return Bez(t, c1.Y, c2.Y);
    }

    // One coordinate of the cubic Bezier B(t) with endpoints 0 and 1 and control values p1, p2.
    private static double Bez(double t, double p1, double p2)
    {
        double mt = 1 - t;
        return (3 * mt * mt * t * p1) + (3 * mt * t * t * p2) + (t * t * t);
    }

    private static double BezDeriv(double t, double p1, double p2)
    {
        double mt = 1 - t;
        return (3 * mt * mt * p1) + (6 * mt * t * (p2 - p1)) + (3 * t * t * (1 - p2));
    }

    private static double SolveForT(double x, double x1, double x2)
    {
        const double eps = 1e-7;
        double t = x; // a good initial guess since Bx is near-identity for gentle curves
        for (int i = 0; i < 8; i++)
        {
            double err = Bez(t, x1, x2) - x;
            if (Math.Abs(err) < eps)
                return t;
            double d = BezDeriv(t, x1, x2);
            if (Math.Abs(d) < eps)
                break;
            t -= err / d;
        }

        // Bisection fallback when Newton stalls (flat derivative / out-of-range step).
        double lo = 0, hi = 1;
        t = x;
        for (int i = 0; i < 24; i++)
        {
            double xt = Bez(t, x1, x2);
            if (Math.Abs(xt - x) < eps)
                break;
            if (xt < x)
                lo = t;
            else
                hi = t;
            t = (lo + hi) / 2;
        }
        return t;
    }
}
