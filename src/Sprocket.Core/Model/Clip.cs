using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>What a clip's frame is reconstructed from (PLAN.md step 19).</summary>
public enum ClipKind
{
    /// <summary>Decoded from a source media file in the <see cref="MediaPool"/> (the default).</summary>
    Media,

    /// <summary>Produced procedurally by a <see cref="GeneratorSpec"/> (title/text, colour matte) — no source media.</summary>
    Generator,

    /// <summary>An adjustment layer: no content of its own; its effect stack applies to the composite of every
    /// track beneath it for its time span (ARCHITECTURE.md §5, modelled like Premiere).</summary>
    Adjustment,
}

/// <summary>
/// A non-destructive placement of a portion of a source on a track (ARCHITECTURE.md §4).
/// The source bytes are never modified: trimming edits <see cref="SourceIn"/>/<see cref="SourceOut"/>,
/// moving edits <see cref="TimelineStart"/>, and effects are an additive ordered list. The frame at
/// any timeline time is reconstructed on demand from these descriptors.
/// A clip may instead be a <see cref="ClipKind.Generator"/> (procedural content) or a
/// <see cref="ClipKind.Adjustment"/> layer (effects over the tracks below) — both have no source media but
/// trim / move / stack and carry effects like any clip (PLAN.md step 19).
/// </summary>
public sealed class Clip
{
    /// <summary>Creates a media clip referencing a source span and placing it on the timeline.</summary>
    public Clip(MediaRefId mediaRefId, Timecode sourceIn, Timecode sourceOut, Timecode timelineStart)
        : this(ClipKind.Media, mediaRefId, generator: null, sourceIn, sourceOut, timelineStart)
    {
    }

    private Clip(ClipKind kind, MediaRefId mediaRefId, GeneratorSpec? generator,
        Timecode sourceIn, Timecode sourceOut, Timecode timelineStart)
    {
        if (sourceOut < sourceIn)
            throw new ArgumentException("SourceOut must not precede SourceIn.", nameof(sourceOut));

        Kind = kind;
        MediaRefId = mediaRefId;
        Generator = generator;
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        TimelineStart = timelineStart;
    }

    /// <summary>
    /// Creates a generator clip (PLAN.md step 19): a synthetic source spanning <c>[0, <paramref name="duration"/>)</c>,
    /// produced by <paramref name="generator"/>. Trimming/slipping behaves like media (the synthetic source is
    /// unbounded), and the clip can carry effects.
    /// </summary>
    public static Clip CreateGenerator(GeneratorSpec generator, Timecode duration, Timecode timelineStart)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new Clip(ClipKind.Generator, default, generator, Timecode.Zero, duration, timelineStart);
    }

    /// <summary>
    /// Creates an adjustment-layer clip (PLAN.md step 19): no content of its own, spanning
    /// <c>[0, <paramref name="duration"/>)</c>; its <see cref="Effects"/> apply to the composite of the tracks
    /// beneath it over the clip's time span.
    /// </summary>
    public static Clip CreateAdjustment(Timecode duration, Timecode timelineStart) =>
        new(ClipKind.Adjustment, default, generator: null, Timecode.Zero, duration, timelineStart);

    /// <summary>What this clip's frame is reconstructed from.</summary>
    public ClipKind Kind { get; }

    /// <summary>The generator producing this clip's content, or <see langword="null"/> unless <see cref="Kind"/> is
    /// <see cref="ClipKind.Generator"/>.</summary>
    public GeneratorSpec? Generator { get; }

    /// <summary>Which source (by id) this clip draws from. Unused (default) for generator / adjustment clips.</summary>
    public MediaRefId MediaRefId { get; set; }

    /// <summary>In-point within the SOURCE (non-destructive trim).</summary>
    public Timecode SourceIn { get; set; }

    /// <summary>Out-point within the SOURCE (exclusive).</summary>
    public Timecode SourceOut { get; set; }

    /// <summary>Where the clip sits on the timeline.</summary>
    public Timecode TimelineStart { get; set; }

    private Rational _speedRatio = Rational.One;

    /// <summary>
    /// Playback speed as an exact ratio of source time to timeline time (retime, PLAN.md step 21): 1/1 = normal,
    /// 2/1 = double speed (the source span plays in half the timeline span), 1/2 = half-speed slow motion. Must be
    /// strictly positive. Non-destructive: the source bytes and the selected source span
    /// (<see cref="SourceIn"/>/<see cref="SourceOut"/>) are untouched — only the clip's timeline
    /// <see cref="Duration"/> and the <see cref="MapToSource"/> time map derive from it.
    /// </summary>
    /// <remarks>Reverse playback, keyframed speed ramps, and freeze-frame (speed 0) are deferred (PLAN.md step 21);
    /// the speed is a single constant ratio here.</remarks>
    public Rational SpeedRatio
    {
        get => _speedRatio;
        set
        {
            if (value.Num <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "SpeedRatio must be strictly positive.");
            _speedRatio = value;
        }
    }

    /// <summary>
    /// Identifies a linked-clip group (PLAN.md step 13, UI.md §3.2). Clips that share a non-null
    /// <see cref="LinkGroupId"/> are companion A/V — a video clip and its source's audio — and the editor
    /// moves / blades them together when "Linked" is on. <see langword="null"/> means the clip is unlinked.
    /// </summary>
    public Guid? LinkGroupId { get; set; }

    /// <summary>Ordered effect stack, applied bottom→top (ARCHITECTURE.md §5d).</summary>
    public List<EffectInstance> Effects { get; } = new();

    /// <summary>Clip markers, positioned within the clip's source (so they move/trim with the clip). Edited
    /// through the command stack, drawn on the clip body, and listed in the markers panel (PLAN.md step 20).</summary>
    public List<Marker> Markers { get; } = new();

    /// <summary>
    /// Duration on the timeline, derived from the trimmed source span and the playback <see cref="SpeedRatio"/>:
    /// <c>(SourceOut - SourceIn) / Speed</c> (so a 2× clip is half as long, a ½× clip twice as long). At the
    /// default 1/1 speed this is simply the source span.
    /// </summary>
    public Timecode Duration => (SourceOut - SourceIn).Scale(_speedRatio.Inverse());

    /// <summary>Exclusive end of the clip on the timeline (<see cref="TimelineStart"/> + <see cref="Duration"/>).</summary>
    public Timecode TimelineEnd => TimelineStart + Duration;

    /// <summary>Whether the clip is active at timeline time <paramref name="t"/> (start inclusive, end exclusive).</summary>
    public bool Contains(Timecode t) => t >= TimelineStart && t < TimelineEnd;

    /// <summary>
    /// Maps a timeline time within this clip to the corresponding time within the source
    /// (ARCHITECTURE.md §5b): <c>sourceT = SourceIn + (t - TimelineStart) × Speed</c>. At the default 1/1 speed
    /// this is the plain <c>SourceIn + (t - TimelineStart)</c>; a faster clip walks the source proportionally
    /// faster (PLAN.md step 21).
    /// </summary>
    public Timecode MapToSource(Timecode t) => SourceIn + (t - TimelineStart).Scale(_speedRatio);

    /// <summary>
    /// A new clip of the same <see cref="Kind"/> and content (media id / cloned generator) over the given span and
    /// placement, <em>without</em> effects or link group. The blade split uses this for the right-hand half so the
    /// new clip keeps a media/generator/adjustment clip's nature (PLAN.md steps 13/19).
    /// </summary>
    internal Clip CloneContentForSpan(Timecode sourceIn, Timecode sourceOut, Timecode timelineStart) =>
        new(Kind, MediaRefId, Generator?.Clone(), sourceIn, sourceOut, timelineStart) { SpeedRatio = _speedRatio };
}
