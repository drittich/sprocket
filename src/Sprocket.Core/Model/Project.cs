using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>Project-wide settings. Kept minimal for the slice; grows without touching the model shape.</summary>
public sealed class ProjectSettings
{
    /// <summary>Master output gain in decibels, applied to the final audio mix (0 dB = unity).</summary>
    public double MasterGainDb { get; set; }

    /// <summary>
    /// Whether the preview uses lower-resolution proxies when available (PLAN.md step 18). Default-on: <em>on</em>
    /// means "use a proxy once one is ready, else the original", so playback is never interrupted while proxies
    /// build in the background. Export always pulls full-resolution originals regardless (ARCHITECTURE.md §17).
    /// </summary>
    public bool UseProxies { get; set; } = true;

    /// <summary>The resolution tier proxies are generated at (PLAN.md step 18). Default <see cref="ProxyTier.Half"/>.</summary>
    public ProxyTier ProxyTier { get; set; } = ProxyTier.Half;
}

/// <summary>
/// The root of the editor's document model (ARCHITECTURE.md §4): the imported media, the sequences being
/// edited, and project settings. Plain data with no native handles, so it serializes cleanly and round-trips
/// in tests.
/// </summary>
/// <remarks>
/// A project holds one or more <see cref="Sequence"/>s (PLAN.md step 23); the <see cref="ActiveSequence"/> is the
/// one currently open in the timeline. <see cref="Timeline"/> delegates to the active sequence, so the rest of the
/// stack (render graph, playback, export, the App) addresses the active sequence's timeline exactly as before —
/// multiple sequences + nesting are an additive layer on top.
/// </remarks>
public sealed class Project
{
    private Sequence _active;

    /// <summary>Creates an empty project with a single 1080p / 30fps / 48kHz sequence.</summary>
    public Project()
        : this(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000))
    {
    }

    /// <summary>Creates a project whose single active sequence wraps the given timeline (named "Sequence 1").</summary>
    public Project(Timeline timeline)
        : this(new Sequence(SequenceId.New(), "Sequence 1", timeline))
    {
    }

    /// <summary>Creates a project with <paramref name="sequence"/> as its single, active sequence.</summary>
    public Project(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _active = sequence;
        Sequences.Add(_active);
    }

    /// <summary>Imported source media, addressed by stable id.</summary>
    public MediaPool MediaPool { get; } = new();

    /// <summary>All sequences in the project, in creation order. Always contains <see cref="ActiveSequence"/>.</summary>
    public List<Sequence> Sequences { get; } = new();

    /// <summary>Synced multicam angle groups (PLAN.md step 24), referenced by <see cref="ClipKind.Multicam"/>
    /// clips via <see cref="Clip.SourceMulticamId"/>. Built from synced source clips and edited through the
    /// command stack; a source can be referenced by many clips and sequences (references, not copies).</summary>
    public List<MulticamSource> MulticamSources { get; } = new();

    /// <summary>
    /// The sequence currently open for editing. Setting it must name a sequence that is in
    /// <see cref="Sequences"/> (switching the open sequence is navigation, not a model edit — it is not undone).
    /// </summary>
    public Sequence ActiveSequence
    {
        get => _active;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Sequences.Contains(value))
                throw new ArgumentException("The active sequence must be one of the project's sequences.", nameof(value));
            _active = value;
        }
    }

    /// <summary>The active sequence's timeline being edited (ARCHITECTURE.md §4). Shorthand for
    /// <c>ActiveSequence.Timeline</c>, kept so the render/playback/export stack is unchanged by step 23.</summary>
    public Timeline Timeline => _active.Timeline;

    /// <summary>Finds a sequence by id, or <see langword="null"/> if no such sequence exists (e.g. a dangling
    /// nested-clip reference after the child was deleted — rendered as nothing, §15).</summary>
    public Sequence? GetSequence(SequenceId id)
    {
        foreach (Sequence s in Sequences)
            if (s.Id == id)
                return s;
        return null;
    }

    /// <summary>Finds a multicam source by id, or <see langword="null"/> if none exists (a dangling reference
    /// after the source was deleted renders as nothing, §15).</summary>
    public MulticamSource? GetMulticam(MulticamId id)
    {
        foreach (MulticamSource m in MulticamSources)
            if (m.Id == id)
                return m;
        return null;
    }

    /// <summary>Project-wide settings.</summary>
    public ProjectSettings Settings { get; } = new();
}
