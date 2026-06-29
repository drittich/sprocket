using System;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure geometry for the keyframe lane's <em>value-graph</em> (velocity-graph) mode (PLAN.md step 16d): mapping a
/// parameter value to a Y within the graph and back, and converting a Bezier handle's segment-normalized
/// progress to/from a value. The time axis reuses <see cref="KeyframeLaneMath"/>; this adds the value axis and
/// the handle-progress conversions so the editable velocity curve can be dragged. Kept free of Avalonia types so
/// it is unit-testable headlessly, mirroring <see cref="KeyframeLaneMath"/> / <see cref="Timeline.TimelineMath"/>;
/// the graph's drawing + pointer interaction rest on this and on manual verification.
/// </summary>
public static class KeyframeGraphMath
{
    /// <summary>
    /// The Y (px) for a parameter <paramref name="value"/> within a graph of drawable height
    /// <paramref name="height"/>: <paramref name="max"/> maps to the top (y=0), <paramref name="min"/> to the
    /// bottom (y=height). Clamped to the graph. A degenerate range (min ≥ max) centres everything.
    /// </summary>
    public static double YForValue(double value, double min, double max, double height)
    {
        double span = max - min;
        if (span <= 0)
            return height / 2;
        double frac = Math.Clamp((value - min) / span, 0, 1);
        return (1 - frac) * height;
    }

    /// <summary>The parameter value at a Y (px) within the graph — the inverse of <see cref="YForValue"/>,
    /// clamped to <c>[min, max]</c>. A degenerate range or height returns <paramref name="min"/>.</summary>
    public static double ValueForY(double y, double min, double max, double height)
    {
        double span = max - min;
        if (span <= 0 || height <= 0)
            return min;
        double frac = Math.Clamp(1 - (y / height), 0, 1);
        return min + (frac * span);
    }

    /// <summary>
    /// The value a Bezier handle points at, given its <paramref name="progress"/> (0 = the segment's start
    /// keyframe value, 1 = its end keyframe value; may overshoot). <c>value = k0 + progress·(k1 − k0)</c>.
    /// </summary>
    public static double ValueForProgress(double progress, double startValue, double endValue)
        => startValue + (progress * (endValue - startValue));

    /// <summary>
    /// The inverse of <see cref="ValueForProgress"/>: the handle progress that places it at
    /// <paramref name="value"/> along the segment. Guards a flat segment (equal endpoint values) by returning
    /// <paramref name="fallback"/>, since progress is then undefined (easing a constant changes nothing).
    /// </summary>
    public static double ProgressForValue(double value, double startValue, double endValue, double fallback)
    {
        double delta = endValue - startValue;
        if (Math.Abs(delta) < 1e-9)
            return fallback;
        return (value - startValue) / delta;
    }
}
