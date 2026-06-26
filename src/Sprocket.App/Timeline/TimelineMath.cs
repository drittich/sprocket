using System;
using System.Collections.Generic;
using Sprocket.Core.Timing;

namespace Sprocket.App;

/// <summary>
/// The active timeline tool (UI.md §3.2 palette, PLAN.md step 13). <see cref="Select"/> moves/trims clips;
/// <see cref="Blade"/> splits a clip at the cursor; <see cref="Slip"/> shifts a clip's source in/out without
/// moving it on the timeline; <see cref="Hand"/> pans the view and <see cref="Zoom"/> zooms it (view-only).
/// </summary>
public enum EditTool
{
    /// <summary>Default arrow: select, move, and edge-trim clips.</summary>
    Select,

    /// <summary>Razor: click a clip to split it at the cursor.</summary>
    Blade,

    /// <summary>Slip a clip's source in/out (its visible content) without moving it on the timeline.</summary>
    Slip,

    /// <summary>Pan the timeline view (drag to scroll).</summary>
    Hand,

    /// <summary>Zoom the timeline view (click to zoom in, modifier-click to zoom out).</summary>
    Zoom,
}

/// <summary>What part of a clip a pointer is over — selects the drag behaviour.</summary>
public enum ClipDragMode
{
    /// <summary>Not over the clip.</summary>
    None,

    /// <summary>Over the body: drag moves the clip along the timeline.</summary>
    Move,

    /// <summary>Over the left edge: drag trims the in-point (and slides the start, right edge fixed).</summary>
    TrimStart,

    /// <summary>Over the right edge: drag trims the out-point.</summary>
    TrimEnd,
}

/// <summary>
/// Pure geometry for the timeline control (PLAN.md step 12): tick↔pixel mapping, snapping, edge hit-testing,
/// and ruler-interval selection. Kept free of Avalonia types so it is unit-tested headlessly — the rendering
/// and pointer interaction in <see cref="TimelineControl"/> rest on this and on manual verification.
/// </summary>
public static class TimelineMath
{
    /// <summary>The on-screen X (px) of a timeline tick, given zoom (px/second), horizontal scroll and the
    /// width of the fixed track-header column.</summary>
    public static double XAtTicks(long ticks, double pxPerSecond, double scrollX, double headerWidth)
        => headerWidth - scrollX + (double)ticks * pxPerSecond / Timecode.TicksPerSecond;

    /// <summary>The timeline tick at an on-screen X (px) — the inverse of <see cref="XAtTicks"/>.</summary>
    public static long TicksAtX(double x, double pxPerSecond, double scrollX, double headerWidth)
        => pxPerSecond <= 0 ? 0 : (long)Math.Round((x - headerWidth + scrollX) * Timecode.TicksPerSecond / pxPerSecond);

    /// <summary>The width in px of a tick span at the given zoom.</summary>
    public static double WidthOfTicks(long ticks, double pxPerSecond)
        => (double)ticks * pxPerSecond / Timecode.TicksPerSecond;

    /// <summary>Clamps a tick value to be non-negative (the timeline starts at 0).</summary>
    public static long ClampNonNegative(long ticks) => ticks < 0 ? 0 : ticks;

    /// <summary>
    /// Snaps <paramref name="ticks"/> to the nearest <paramref name="candidates"/> entry that is within
    /// <paramref name="tolerancePx"/> on screen; returns the original value if none is close enough. Used to
    /// snap a dragged clip edge to other clip edges, the playhead, and the timeline origin.
    /// </summary>
    public static long Snap(long ticks, IReadOnlyList<long> candidates, double tolerancePx, double pxPerSecond)
    {
        long best = ticks;
        double bestPx = tolerancePx;
        foreach (long c in candidates)
        {
            double dpx = Math.Abs(WidthOfTicks(ticks - c, pxPerSecond));
            if (dpx <= bestPx)
            {
                bestPx = dpx;
                best = c;
            }
        }
        return best;
    }

    /// <summary>
    /// Classifies a pointer X against a clip's on-screen span <c>[clipX0, clipX1]</c>: within
    /// <paramref name="edgeGrip"/> px of an edge is a trim, inside is a move, outside is <see cref="ClipDragMode.None"/>.
    /// The left edge wins ties so a very narrow clip is still trimmable from the start.
    /// </summary>
    public static ClipDragMode HitMode(double pointerX, double clipX0, double clipX1, double edgeGrip)
    {
        if (pointerX >= clipX0 - edgeGrip && pointerX <= clipX0 + edgeGrip)
            return ClipDragMode.TrimStart;
        if (pointerX >= clipX1 - edgeGrip && pointerX <= clipX1 + edgeGrip)
            return ClipDragMode.TrimEnd;
        if (pointerX >= clipX0 && pointerX <= clipX1)
            return ClipDragMode.Move;
        return ClipDragMode.None;
    }

    /// <summary>
    /// Clamps a slip <paramref name="delta"/> (ticks added to both source in/out) so the source window stays
    /// within the media: <c>SourceIn ≥ 0</c> and <c>SourceOut ≤ mediaDuration</c>. The clip's duration and
    /// timeline position are unchanged — slip only changes which part of the source plays. Returns 0 when the
    /// clip already spans the whole source (no room to slip).
    /// </summary>
    public static long ClampSlip(long origIn, long origOut, long mediaDuration, long delta)
    {
        long minDelta = -origIn;                  // can't pull SourceIn below 0
        long maxDelta = mediaDuration - origOut;  // can't push SourceOut past the media end
        if (maxDelta < minDelta)
            return 0;
        return Math.Clamp(delta, minDelta, maxDelta);
    }

    /// <summary>
    /// Picks a "nice" ruler tick interval (in ticks) so labels land roughly <paramref name="targetPx"/> apart
    /// at the current zoom — stepping through 0.5/1/2/5/10/15/30/60/120/300/600-second intervals.
    /// </summary>
    public static long RulerIntervalTicks(double pxPerSecond, double targetPx)
    {
        double secondsPerLabel = targetPx / Math.Max(1e-6, pxPerSecond);
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        double chosen = steps[^1];
        foreach (double s in steps)
        {
            if (s >= secondsPerLabel)
            {
                chosen = s;
                break;
            }
        }
        return (long)Math.Round(chosen * Timecode.TicksPerSecond);
    }
}
