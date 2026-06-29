using System;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// Pure logic for the Edit-menu clip clipboard (cut / copy / paste) and clip nudge (PLAN.md step 16c). Kept
/// free of Avalonia types so the copy/paste and clamp math are unit-testable headlessly; the menu wiring in
/// <see cref="MainWindow"/> and the public clip-edit methods on <see cref="Timeline.TimelineControl"/> rest on
/// this + manual verification (the App is a UI-bound WinExe). Cut / paste / delete / nudge all run through the
/// existing command stack (step 10), so every operation is undoable by construction.
/// </summary>
public static class ClipboardOps
{
    /// <summary>
    /// A detached deep copy of <paramref name="clip"/> for the clipboard: the effect stack is cloned (so later
    /// edits to the original don't bleed into the clipboard) and the link group is left cleared — a pasted clip
    /// is an independent copy, not a companion of the original's linked A/V pair (PLAN.md step 13). The source
    /// span and timeline start are preserved as a faithful snapshot; <see cref="Paste"/> chooses the placement.
    /// </summary>
    public static Clip Copy(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var copy = new Clip(clip.MediaRefId, clip.SourceIn, clip.SourceOut, clip.TimelineStart);
        foreach (EffectInstance e in clip.Effects)
            copy.Effects.Add(e.Clone());
        return copy; // LinkGroupId intentionally left null — a pasted clip is independent
    }

    /// <summary>
    /// Builds the clip to paste from a clipboard <paramref name="snapshot"/>, placed at timeline time
    /// <paramref name="at"/> (clamped to the origin). Effects are cloned again so repeated pastes stay
    /// independent of one another and of the snapshot.
    /// </summary>
    public static Clip Paste(Clip snapshot, Timecode at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long start = Math.Max(0, at.Ticks);
        var copy = new Clip(snapshot.MediaRefId, snapshot.SourceIn, snapshot.SourceOut, new Timecode(start));
        foreach (EffectInstance e in snapshot.Effects)
            copy.Effects.Add(e.Clone());
        return copy;
    }

    /// <summary>
    /// Clamps a nudge of <paramref name="deltaTicks"/> so the earliest clip in a moved group never crosses the
    /// timeline origin. <paramref name="groupMinStartTicks"/> is the smallest <see cref="Clip.TimelineStart"/>
    /// among the clips being moved; the returned delta is what every member shifts by (a left nudge can be
    /// shortened to keep the group at <c>t = 0</c>, a right nudge is unaffected).
    /// </summary>
    public static long ClampGroupNudge(long deltaTicks, long groupMinStartTicks) =>
        Math.Max(deltaTicks, -groupMinStartTicks);
}
