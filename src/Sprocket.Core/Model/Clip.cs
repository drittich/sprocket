using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// A non-destructive placement of a portion of a source on a track (ARCHITECTURE.md §4).
/// The source bytes are never modified: trimming edits <see cref="SourceIn"/>/<see cref="SourceOut"/>,
/// moving edits <see cref="TimelineStart"/>, and effects are an additive ordered list. The frame at
/// any timeline time is reconstructed on demand from these descriptors.
/// </summary>
public sealed class Clip
{
    /// <summary>Creates a clip referencing a source span and placing it on the timeline.</summary>
    public Clip(MediaRefId mediaRefId, Timecode sourceIn, Timecode sourceOut, Timecode timelineStart)
    {
        if (sourceOut < sourceIn)
            throw new ArgumentException("SourceOut must not precede SourceIn.", nameof(sourceOut));

        MediaRefId = mediaRefId;
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        TimelineStart = timelineStart;
    }

    /// <summary>Which source (by id) this clip draws from.</summary>
    public MediaRefId MediaRefId { get; set; }

    /// <summary>In-point within the SOURCE (non-destructive trim).</summary>
    public Timecode SourceIn { get; set; }

    /// <summary>Out-point within the SOURCE (exclusive).</summary>
    public Timecode SourceOut { get; set; }

    /// <summary>Where the clip sits on the timeline.</summary>
    public Timecode TimelineStart { get; set; }

    /// <summary>
    /// Identifies a linked-clip group (PLAN.md step 13, UI.md §3.2). Clips that share a non-null
    /// <see cref="LinkGroupId"/> are companion A/V — a video clip and its source's audio — and the editor
    /// moves / blades them together when "Linked" is on. <see langword="null"/> means the clip is unlinked.
    /// </summary>
    public Guid? LinkGroupId { get; set; }

    /// <summary>Ordered effect stack, applied bottom→top (ARCHITECTURE.md §5d).</summary>
    public List<EffectInstance> Effects { get; } = new();

    /// <summary>Duration on the timeline, derived from the trimmed source span.</summary>
    public Timecode Duration => SourceOut - SourceIn;

    /// <summary>Exclusive end of the clip on the timeline (<see cref="TimelineStart"/> + <see cref="Duration"/>).</summary>
    public Timecode TimelineEnd => TimelineStart + Duration;

    /// <summary>Whether the clip is active at timeline time <paramref name="t"/> (start inclusive, end exclusive).</summary>
    public bool Contains(Timecode t) => t >= TimelineStart && t < TimelineEnd;

    /// <summary>
    /// Maps a timeline time within this clip to the corresponding time within the source
    /// (ARCHITECTURE.md §5b): <c>sourceT = SourceIn + (t - TimelineStart)</c>.
    /// </summary>
    public Timecode MapToSource(Timecode t) => SourceIn + (t - TimelineStart);
}
