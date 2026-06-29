using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Pure playhead navigation over a clip's keyframes (PLAN.md step 16d): given the current time, find the
/// previous / next keyframe across <em>every</em> animated parameter of <em>every</em> effect on the clip, so
/// the transport's jump-to-previous / jump-to-next-keyframe lands on the nearest animation event regardless of
/// which parameter it belongs to (the Adobe-Premiere convention). Keyframe times are absolute timeline times —
/// the domain the render graph evaluates effects in (ARCHITECTURE.md §5/§9) — so the results are timeline
/// positions the transport can seek to directly. Kept in Core (pure model reasoning, no UI) and unit-testable
/// headlessly alongside <see cref="RenderGraph"/>.
/// </summary>
public static class KeyframeNavigation
{
    /// <summary>
    /// The latest keyframe time strictly before <paramref name="t"/> among all of the clip's animated
    /// parameters, or <see langword="null"/> when none exists (no animated parameter, or <paramref name="t"/>
    /// is at/before the first keyframe).
    /// </summary>
    public static Timecode? PreviousKeyframe(Clip clip, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Timecode? best = null;
        foreach (Timecode time in KeyframeTimes(clip))
            if (time < t && (best is null || time > best.Value))
                best = time;
        return best;
    }

    /// <summary>
    /// The earliest keyframe time strictly after <paramref name="t"/> among all of the clip's animated
    /// parameters, or <see langword="null"/> when none exists.
    /// </summary>
    public static Timecode? NextKeyframe(Clip clip, Timecode t)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Timecode? best = null;
        foreach (Timecode time in KeyframeTimes(clip))
            if (time > t && (best is null || time < best.Value))
                best = time;
        return best;
    }

    /// <summary>Whether the clip has any animated (keyframed) parameter at all.</summary>
    public static bool HasKeyframes(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        foreach (EffectInstance effect in clip.Effects)
            foreach (AnimatableValue value in effect.Parameters.Values)
                if (value.IsAnimated)
                    return true;
        return false;
    }

    private static IEnumerable<Timecode> KeyframeTimes(Clip clip)
    {
        foreach (EffectInstance effect in clip.Effects)
            foreach (AnimatableValue value in effect.Parameters.Values)
                foreach (Keyframe k in value.Keyframes)
                    yield return k.Time;
    }
}
