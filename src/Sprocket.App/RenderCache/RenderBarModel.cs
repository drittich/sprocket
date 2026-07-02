using System;
using System.Collections.Generic;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;

namespace Sprocket.App.RenderCache;

/// <summary>A render-bar span's colour, following the Premiere convention (PLAN.md step 32).</summary>
public enum RenderBarState
{
    /// <summary>Yellow: un-rendered content that likely previews in real time (effect chains, adjustment layers).</summary>
    NeedsRender,

    /// <summary>Red: un-rendered content the live preview cannot fully composite — nested sequences and
    /// transition blends (their full fidelity needs a render; see the step-23/25 deferrals).</summary>
    NeedsRenderHeavy,

    /// <summary>Green: covered by a valid cached render — playback replays the intermediate.</summary>
    Rendered,
}

/// <summary>One coloured span of the render bar, in timeline ticks.</summary>
public readonly record struct RenderBarSpan(long InTicks, long OutTicks, RenderBarState State);

/// <summary>
/// Computes the render bar over the timeline ruler (ARCHITECTURE.md §20): green where a valid cached render
/// covers the timeline, red over un-rendered content the live preview can't fully composite (nested sequences,
/// transition windows), yellow over un-rendered effect-bearing content. Pure — a function of the timeline and
/// the valid cached ranges — so it's headlessly testable and recomputes cheaply after every model change.
/// </summary>
public static class RenderBarModel
{
    /// <summary>Computes the merged, non-overlapping coloured spans for <paramref name="timeline"/>, given the
    /// currently-valid cached video ranges. Uncoloured (real-time) stretches produce no span.</summary>
    public static List<RenderBarSpan> Compute(
        Timeline timeline, IReadOnlyList<(Timecode In, Timecode Out)> validVideoRanges)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        // Severity per source interval; Rendered coverage overrides need-render severities at the same instant.
        var intervals = new List<(long In, long Out, RenderBarState State)>();

        foreach (VideoTrack track in timeline.VideoTracks)
        {
            if (!track.Enabled)
                continue;

            foreach (Clip clip in track.Clips)
            {
                RenderBarState? state = clip.Kind switch
                {
                    ClipKind.Sequence => RenderBarState.NeedsRenderHeavy,
                    ClipKind.Adjustment => RenderBarState.NeedsRender,
                    _ => clip.Effects.Count > 0 ? RenderBarState.NeedsRender : null,
                };
                if (state is { } s && clip.TimelineEnd > clip.TimelineStart)
                    intervals.Add((clip.TimelineStart.Ticks, clip.TimelineEnd.Ticks, s));
            }

            foreach (Transition transition in track.Transitions)
            {
                if (transition.End > transition.Start)
                    intervals.Add((transition.Start.Ticks, transition.End.Ticks, RenderBarState.NeedsRenderHeavy));
            }
        }

        foreach ((Timecode rangeIn, Timecode rangeOut) in validVideoRanges)
        {
            if (rangeOut > rangeIn)
                intervals.Add((rangeIn.Ticks, rangeOut.Ticks, RenderBarState.Rendered));
        }

        if (intervals.Count == 0)
            return [];

        // Boundary sweep: between each adjacent pair of boundaries the state is constant — Rendered wherever a
        // valid cache covers, else the strongest need-render severity, else nothing.
        var boundaries = new SortedSet<long>();
        foreach ((long start, long end, _) in intervals)
        {
            boundaries.Add(start);
            boundaries.Add(end);
        }

        var spans = new List<RenderBarSpan>();
        long? previous = null;
        foreach (long boundary in boundaries)
        {
            if (previous is { } from && boundary > from)
            {
                RenderBarState? state = StateOver(intervals, from, boundary);
                if (state is { } s)
                {
                    // Merge with the preceding span when contiguous and same-coloured.
                    if (spans.Count > 0 && spans[^1].OutTicks == from && spans[^1].State == s)
                        spans[^1] = spans[^1] with { OutTicks = boundary };
                    else
                        spans.Add(new RenderBarSpan(from, boundary, s));
                }
            }
            previous = boundary;
        }
        return spans;
    }

    private static RenderBarState? StateOver(List<(long In, long Out, RenderBarState State)> intervals, long from, long to)
    {
        RenderBarState? strongest = null;
        foreach ((long start, long end, RenderBarState state) in intervals)
        {
            if (start >= to || end <= from)
                continue;
            if (state == RenderBarState.Rendered)
                return RenderBarState.Rendered; // a valid render wins outright
            if (strongest is not { } current || state > current)
                strongest = state;
        }
        return strongest;
    }
}
