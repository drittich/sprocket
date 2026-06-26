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
}
