using Sprocket.Core.Timing;

namespace Sprocket.Export;

/// <summary>
/// A half-open sub-range <c>[<see cref="In"/>, <see cref="Out"/>)</c> of a sequence's timeline to export
/// (PLAN.md step 29 export queue — "in-out ranges"). A job whose range is <see langword="null"/> exports the
/// whole timeline; a range lets one queued job deliver a slice (e.g. an in-to-out review selection) while
/// another delivers the whole sequence. The exported file starts at timeline <see cref="In"/> but its own
/// timestamps start at zero.
/// </summary>
/// <param name="In">The inclusive start on the timeline.</param>
/// <param name="Out">The exclusive end on the timeline.</param>
public readonly record struct ExportRange(Timecode In, Timecode Out)
{
    /// <summary>The range length (<see cref="Out"/> − <see cref="In"/>); may be non-positive for a degenerate range.</summary>
    public Timecode Duration => Out - In;

    /// <summary>Whether the range is non-empty (<see cref="Out"/> strictly after <see cref="In"/>).</summary>
    public bool IsValid => Out > In;

    /// <summary>
    /// This range clamped to the valid timeline span <c>[0, <paramref name="timelineDuration"/>]</c>: the start is
    /// pinned to at least zero and the end to at most the timeline duration, with the start never past the end.
    /// The exporter uses this so an out-of-bounds or reversed range degrades to a valid slice (or an empty one the
    /// caller can reject) rather than reading past the timeline.
    /// </summary>
    public ExportRange ClampTo(Timecode timelineDuration)
    {
        Timecode start = Timecode.Max(Timecode.Zero, Timecode.Min(In, timelineDuration));
        Timecode end = Timecode.Max(start, Timecode.Min(Out, timelineDuration));
        return new ExportRange(start, end);
    }

    /// <summary>The whole timeline as a range (<c>[0, <paramref name="timelineDuration"/>)</c>).</summary>
    public static ExportRange Whole(Timecode timelineDuration) => new(Timecode.Zero, timelineDuration);

    /// <summary>
    /// This range grown by <paramref name="head"/> before <see cref="In"/> and <paramref name="tail"/> after
    /// <see cref="Out"/> — export <b>handles</b> (PLAN.md step 29): extra frames around the selection so a review /
    /// conform output carries room to adjust the cut downstream. Not clamped here; the exporter pins the result to
    /// the timeline with <see cref="ClampTo"/> (a handle can only reach media that actually exists on the timeline).
    /// Negative handles are treated as zero.
    /// </summary>
    public ExportRange WithHandles(Timecode head, Timecode tail)
    {
        Timecode h = Timecode.Max(Timecode.Zero, head);
        Timecode t = Timecode.Max(Timecode.Zero, tail);
        return new ExportRange(In - h, Out + t);
    }
}
