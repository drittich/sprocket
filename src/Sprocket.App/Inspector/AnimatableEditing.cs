using System;
using System.Collections.Generic;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App.Inspector;

/// <summary>
/// Pure transformations on <see cref="AnimatableValue"/> for the Inspector's numeric/slider editing and
/// keyframe affordances (PLAN.md step 16). Kept separate from the control so the keyframe semantics are
/// unit-testable headlessly. <see cref="AnimatableValue"/> is immutable, so each method returns a new value
/// the caller hands to a <c>SetEffectParameterCommand</c> (undoable by construction, PLAN.md step 10).
/// </summary>
public static class AnimatableEditing
{
    /// <summary>
    /// The new value for a slider/numeric edit at <paramref name="time"/>. A constant value is replaced with a
    /// new constant; an animated value gets a keyframe upserted at <paramref name="time"/> (so editing scrubs a
    /// keyframe in at the playhead, the standard NLE gesture).
    /// </summary>
    public static AnimatableValue SetValueAt(AnimatableValue current, Timecode time, double value)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.IsAnimated ? UpsertKeyframe(current, time, value) : AnimatableValue.Constant(value);
    }

    /// <summary>
    /// Turns a constant value into an animated one with a single keyframe at <paramref name="time"/> carrying
    /// the current (evaluated) value — the "start keyframing" affordance. An already-animated value is returned
    /// unchanged.
    /// </summary>
    public static AnimatableValue EnableKeyframing(AnimatableValue current, Timecode time)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.IsAnimated)
            return current;
        return AnimatableValue.Animated([new Keyframe(time, current.Evaluate(time), Interpolation.Linear)]);
    }

    /// <summary>
    /// Collapses an animated value back to a constant equal to its value at <paramref name="time"/> — the
    /// "stop keyframing" affordance. A constant value is returned unchanged in effect (re-wrapped as constant).
    /// </summary>
    public static AnimatableValue DisableKeyframing(AnimatableValue current, Timecode time)
    {
        ArgumentNullException.ThrowIfNull(current);
        return AnimatableValue.Constant(current.Evaluate(time));
    }

    /// <summary>
    /// Inserts or replaces the keyframe at <paramref name="time"/> on an animated value, preserving every other
    /// keyframe (and the edited keyframe's interpolation mode when it already existed). A constant value becomes
    /// animated with this single keyframe.
    /// </summary>
    public static AnimatableValue UpsertKeyframe(AnimatableValue current, Timecode time, double value)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsAnimated)
            return AnimatableValue.Animated([new Keyframe(time, value, Interpolation.Linear)]);

        var keyframes = new List<Keyframe>(current.Keyframes.Count + 1);
        bool replaced = false;
        foreach (Keyframe k in current.Keyframes)
        {
            if (k.Time.Ticks == time.Ticks)
            {
                keyframes.Add(k with { Value = value });
                replaced = true;
            }
            else
            {
                keyframes.Add(k);
            }
        }

        if (!replaced)
            keyframes.Add(new Keyframe(time, value, Interpolation.Linear));

        return AnimatableValue.Animated(keyframes);
    }

    /// <summary>
    /// Moves the keyframe at <paramref name="from"/> to <paramref name="to"/> (its value and interpolation
    /// preserved), keeping the rest. If a keyframe already sits at <paramref name="to"/> it is overwritten by
    /// the moved one. A no-op when there is no keyframe at <paramref name="from"/> or the value isn't animated.
    /// Used to drag a keyframe along the keyframe lane (PLAN.md step 16b).
    /// </summary>
    public static AnimatableValue MoveKeyframe(AnimatableValue current, Timecode from, Timecode to)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsAnimated || from.Ticks == to.Ticks)
            return current;

        Keyframe? moved = null;
        var kept = new List<Keyframe>(current.Keyframes.Count);
        foreach (Keyframe k in current.Keyframes)
        {
            if (k.Time.Ticks == from.Ticks)
                moved = k;
            else if (k.Time.Ticks != to.Ticks) // drop any keyframe sitting at the destination
                kept.Add(k);
        }
        if (moved is not { } m)
            return current;

        kept.Add(m with { Time = to });
        return AnimatableValue.Animated(kept);
    }

    /// <summary>
    /// Removes the keyframe at <paramref name="time"/> (PLAN.md step 16b). If it was the last remaining
    /// keyframe the value collapses back to a constant equal to its evaluated value (an animated value must
    /// keep at least one keyframe). A no-op when no keyframe sits at <paramref name="time"/>.
    /// </summary>
    public static AnimatableValue RemoveKeyframe(AnimatableValue current, Timecode time)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsAnimated)
            return current;

        var kept = new List<Keyframe>(current.Keyframes.Count);
        foreach (Keyframe k in current.Keyframes)
            if (k.Time.Ticks != time.Ticks)
                kept.Add(k);

        if (kept.Count == current.Keyframes.Count)
            return current; // nothing matched
        if (kept.Count == 0)
            return AnimatableValue.Constant(current.Evaluate(time));
        return AnimatableValue.Animated(kept);
    }

    /// <summary>
    /// Sets the interpolation mode of the keyframe at <paramref name="time"/> (the Hold↔Linear toggle,
    /// PLAN.md step 16b), preserving its value and the other keyframes. A no-op when no keyframe sits there.
    /// </summary>
    public static AnimatableValue SetInterpolation(AnimatableValue current, Timecode time, Interpolation mode)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsAnimated)
            return current;

        var keyframes = new List<Keyframe>(current.Keyframes.Count);
        bool changed = false;
        foreach (Keyframe k in current.Keyframes)
        {
            if (k.Time.Ticks == time.Ticks && k.Interpolation != mode)
            {
                keyframes.Add(k with { Interpolation = mode });
                changed = true;
            }
            else
            {
                keyframes.Add(k);
            }
        }
        return changed ? AnimatableValue.Animated(keyframes) : current;
    }

    /// <summary>
    /// Cycles the keyframe at <paramref name="time"/> through the interpolation modes
    /// (Hold → Linear → Ease In → Ease Out → Ease In/Out → Hold), the Premiere-parity successor to the
    /// step-16b Hold↔Linear toggle (PLAN.md step 16d). A no-op when no keyframe sits there.
    /// </summary>
    public static AnimatableValue CycleInterpolation(AnimatableValue current, Timecode time)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsAnimated)
            return current;
        foreach (Keyframe k in current.Keyframes)
            if (k.Time.Ticks == time.Ticks)
                return SetInterpolation(current, time, NextMode(k.Interpolation));
        return current;
    }

    /// <summary>The next interpolation mode in the cycle used by <see cref="CycleInterpolation"/>.</summary>
    public static Interpolation NextMode(Interpolation mode) => mode switch
    {
        Interpolation.Hold => Interpolation.Linear,
        Interpolation.Linear => Interpolation.EaseIn,
        Interpolation.EaseIn => Interpolation.EaseOut,
        Interpolation.EaseOut => Interpolation.EaseInOut,
        Interpolation.EaseInOut => Interpolation.Bezier,
        Interpolation.Bezier => Interpolation.Hold,
        _ => Interpolation.Linear,
    };

    /// <summary>
    /// Sets the <em>outgoing</em> Bezier handle of the keyframe at <paramref name="time"/> and switches it to
    /// <see cref="Interpolation.Bezier"/> so the segment leaving it uses the custom velocity curve (the editable
    /// velocity graph, PLAN.md step 16d). A no-op when no keyframe sits there or the value isn't animated.
    /// </summary>
    public static AnimatableValue SetOutgoingHandle(AnimatableValue current, Timecode time, BezierHandle handle)
        => RemapKeyframeAt(current, time, k => k with { Interpolation = Interpolation.Bezier, EaseOut = handle });

    /// <summary>
    /// Sets the <em>incoming</em> Bezier handle of the keyframe at <paramref name="time"/> — the second control
    /// point of the segment arriving at it (only in effect when the previous keyframe is
    /// <see cref="Interpolation.Bezier"/>). The keyframe's own mode is left unchanged. A no-op when no keyframe
    /// sits there or the value isn't animated.
    /// </summary>
    public static AnimatableValue SetIncomingHandle(AnimatableValue current, Timecode time, BezierHandle handle)
        => RemapKeyframeAt(current, time, k => k with { EaseIn = handle });

    /// <summary>Applies <paramref name="map"/> to the keyframe at <paramref name="time"/>, keeping the rest;
    /// a no-op when nothing matches.</summary>
    private static AnimatableValue RemapKeyframeAt(AnimatableValue current, Timecode time, Func<Keyframe, Keyframe> map)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsAnimated)
            return current;

        var keyframes = new List<Keyframe>(current.Keyframes.Count);
        bool changed = false;
        foreach (Keyframe k in current.Keyframes)
        {
            if (k.Time.Ticks == time.Ticks)
            {
                keyframes.Add(map(k));
                changed = true;
            }
            else
            {
                keyframes.Add(k);
            }
        }
        return changed ? AnimatableValue.Animated(keyframes) : current;
    }

    /// <summary>
    /// Shifts every keyframe whose time is in <paramref name="times"/> by <paramref name="deltaTicks"/>
    /// (negative = left) as one operation — the keyframe nudge / multi-select move (PLAN.md step 16d). Times
    /// are matched by exact tick. If a shifted keyframe lands on an unselected one the shifted keyframe wins
    /// (the unselected is dropped), mirroring <see cref="MoveKeyframe"/>; collisions between two shifted
    /// keyframes keep the last. A no-op when nothing is animated, the delta is zero, or no time matches.
    /// </summary>
    public static AnimatableValue NudgeKeyframes(AnimatableValue current, IReadOnlyCollection<long> times, long deltaTicks)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(times);
        if (!current.IsAnimated || deltaTicks == 0 || times.Count == 0)
            return current;

        var selected = new HashSet<long>(times);
        // Destination ticks of the shifted keyframes — used to drop the unselected keyframes they land on.
        var shiftedTo = new HashSet<long>();
        bool any = false;
        foreach (Keyframe k in current.Keyframes)
            if (selected.Contains(k.Time.Ticks))
            {
                shiftedTo.Add(k.Time.Ticks + deltaTicks);
                any = true;
            }
        if (!any)
            return current;

        var result = new List<Keyframe>(current.Keyframes.Count);
        foreach (Keyframe k in current.Keyframes)
        {
            if (selected.Contains(k.Time.Ticks))
                result.Add(k with { Time = new Timecode(k.Time.Ticks + deltaTicks) });
            else if (!shiftedTo.Contains(k.Time.Ticks)) // an unselected keyframe a shifted one lands on is dropped
                result.Add(k);
        }
        return AnimatableValue.Animated(result);
    }

    /// <summary>The keyframes at the given times, in ascending time order — the "copy" payload for a keyframe
    /// copy/paste (PLAN.md step 16d). Empty when nothing matches or the value isn't animated.</summary>
    public static IReadOnlyList<Keyframe> CopyKeyframes(AnimatableValue current, IReadOnlyCollection<long> times)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(times);
        if (!current.IsAnimated || times.Count == 0)
            return [];
        var selected = new HashSet<long>(times);
        var copied = new List<Keyframe>();
        foreach (Keyframe k in current.Keyframes)
            if (selected.Contains(k.Time.Ticks))
                copied.Add(k);
        copied.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return copied;
    }

    /// <summary>
    /// Pastes <paramref name="clipboard"/> keyframes onto <paramref name="current"/> so the earliest one lands
    /// at <paramref name="at"/> and the rest keep their relative spacing (PLAN.md step 16d). Each pasted
    /// keyframe is upserted (value + interpolation), overwriting any existing keyframe at the same time. A
    /// constant value becomes animated. A no-op when the clipboard is empty.
    /// </summary>
    public static AnimatableValue PasteKeyframes(AnimatableValue current, IReadOnlyList<Keyframe> clipboard, Timecode at)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.Count == 0)
            return current;

        long origin = clipboard[0].Time.Ticks;
        foreach (Keyframe k in clipboard)
            origin = Math.Min(origin, k.Time.Ticks);

        AnimatableValue result = current;
        foreach (Keyframe k in clipboard)
        {
            var t = new Timecode(at.Ticks + (k.Time.Ticks - origin));
            result = UpsertKeyframe(result, t, k.Value);
            result = SetInterpolation(result, t, k.Interpolation);
        }
        return result;
    }
}
