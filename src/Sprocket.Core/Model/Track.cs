using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>How a video track is blended onto the layers beneath it.</summary>
public enum BlendMode
{
    /// <summary>Source-over alpha compositing (the default).</summary>
    Normal,

    /// <summary>Multiply.</summary>
    Multiply,

    /// <summary>Screen.</summary>
    Screen,

    /// <summary>Additive.</summary>
    Add,
}

/// <summary>
/// A lane of non-overlapping clips. For the vertical slice a track holds at most one active clip at
/// any instant; overlaps/transitions are a post-slice extension of clip resolution (ARCHITECTURE.md §17).
/// </summary>
public abstract class Track
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the track contributes to the render. Disabled tracks are skipped entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Clips on this track, in placement order.</summary>
    public List<Clip> Clips { get; } = new();

    /// <summary>
    /// Transitions placed on this track's cuts (PLAN.md step 25). Each blends the two adjacent clips at its
    /// <see cref="Transition.CutPoint"/> over its window; they are an overlay on the clips, not part of clip
    /// placement, so they never change <see cref="Clips"/>. Resolved by the render graph for video tracks.
    /// </summary>
    public List<Transition> Transitions { get; } = new();

    /// <summary>
    /// Resolves the single clip active at timeline time <paramref name="t"/>, or <see langword="null"/>.
    /// If clips overlap (out of scope for the slice), the last one in <see cref="Clips"/> wins, so the
    /// result is deterministic and a clip placed later sits on top.
    /// </summary>
    public Clip? ResolveActiveClip(Timecode t)
    {
        Clip? active = null;
        foreach (Clip clip in Clips)
        {
            if (clip.Contains(t))
                active = clip;
        }
        return active;
    }

    /// <summary>
    /// Resolves the transition active at timeline time <paramref name="t"/>, or <see langword="null"/>
    /// (PLAN.md step 25). If transitions somehow overlap, the last one in <see cref="Transitions"/> wins, so the
    /// result is deterministic.
    /// </summary>
    public Transition? ResolveTransitionAt(Timecode t)
    {
        Transition? active = null;
        foreach (Transition transition in Transitions)
        {
            if (transition.Contains(t))
                active = transition;
        }
        return active;
    }

    /// <summary>
    /// The outgoing (<c>From</c>) and incoming (<c>To</c>) clips a transition blends: the clip covering just
    /// before the cut and the clip covering the cut itself (PLAN.md step 25). For an ordinary cut between two
    /// adjacent clips these are the left and right clip. Either may be <see langword="null"/>, or they may be the
    /// same clip (a degenerate transition with no real cut) — the render graph treats those as invalid and renders
    /// the clips normally.
    /// </summary>
    public (Clip? From, Clip? To) ResolveTransitionClips(Transition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        Clip? from = ResolveActiveClip(transition.CutPoint - new Timecode(1));
        Clip? to = ResolveActiveClip(transition.CutPoint);
        return (from, to);
    }
}

/// <summary>A video track. Composited onto lower tracks with its <see cref="Opacity"/> and <see cref="BlendMode"/>.</summary>
public sealed class VideoTrack : Track
{
    /// <summary>Track opacity in [0, 1], applied when compositing onto the tracks below.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>How this track blends onto the tracks below.</summary>
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
}

/// <summary>An audio track. Summed into the mix at its <see cref="GainDb"/>, honouring mute/solo.</summary>
public sealed class AudioTrack : Track
{
    /// <summary>Track gain in decibels (0 dB = unity).</summary>
    public double GainDb { get; set; }

    private double _pan;

    /// <summary>
    /// Stereo balance in [-1, 1] (PLAN.md step 30): 0 = centre (both channels at unity), -1 = hard left, +1 = hard
    /// right. Applied by the mixer as a per-channel gain (a linear balance law that keeps the centre at unity, so a
    /// centred track is byte-identical to the pre-pan mix). Out-of-range values are clamped.
    /// </summary>
    public double Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1.0, 1.0);
    }

    /// <summary>Whether the track is muted (excluded from the mix).</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Whether the track is soloed. If any audio track is soloed, only soloed tracks are audible
    /// (ARCHITECTURE.md §6).
    /// </summary>
    public bool Solo { get; set; }
}
