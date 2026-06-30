using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>
/// Pure computation of multicam angle sync offsets (PLAN.md step 24). Aligning a set of source clips into a
/// synced group ("clip sync") reduces, whatever the method, to one number per angle: the source time at which a
/// shared real instant occurs in that angle. From those, the per-angle <see cref="MulticamAngle.SyncOffset"/>s
/// fall out. The three methods the brief lists feed this the same way:
/// <list type="bullet">
/// <item><b>In/out markers</b> — the marked source time in each angle is its sync point.</item>
/// <item><b>Timecode</b> — at a common wall-clock timecode, the angle's source time is its sync point (the App
/// computes it from each source's start timecode).</item>
/// <item><b>Audio-waveform cross-correlation</b> — <see cref="AudioSync.FindBestLag"/> gives each angle's lag
/// relative to the reference; that lag <em>is</em> its sync point (reference = 0).</item>
/// </list>
/// All the methods produce model data (the offsets), set through the command stack so they are undoable. This
/// helper is pure model reasoning, headless-tested like <see cref="SequenceGraph"/>.
/// </summary>
public static class ClipSync
{
    /// <summary>
    /// Computes a sync offset per angle from its <paramref name="syncPoints"/> (the source time of the shared
    /// instant in each angle), relative to angle <paramref name="referenceIndex"/>: the reference angle gets a
    /// zero offset and every other angle's offset is its sync point minus the reference's. Applying these makes
    /// every angle present the same instant at the same multicam time. The offset may be negative.
    /// </summary>
    public static IReadOnlyList<Timecode> ComputeOffsets(IReadOnlyList<Timecode> syncPoints, int referenceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(syncPoints);
        if (syncPoints.Count == 0)
            return [];
        if (referenceIndex < 0 || referenceIndex >= syncPoints.Count)
            throw new ArgumentOutOfRangeException(nameof(referenceIndex), "The reference index is out of range.");

        Timecode reference = syncPoints[referenceIndex];
        var offsets = new Timecode[syncPoints.Count];
        for (int i = 0; i < syncPoints.Count; i++)
            offsets[i] = syncPoints[i] - reference;
        return offsets;
    }

    /// <summary>
    /// The source time within an angle that corresponds to multicam time <paramref name="multicamTime"/>:
    /// <c>multicamTime + <see cref="MulticamAngle.SyncOffset"/></c>. This is the time a <see cref="ClipKind.Multicam"/>
    /// clip's active angle is sampled at (the render graph uses it).
    /// </summary>
    public static Timecode AngleSourceTime(MulticamAngle angle, Timecode multicamTime)
    {
        ArgumentNullException.ThrowIfNull(angle);
        return multicamTime + angle.SyncOffset;
    }

    /// <summary>
    /// Applies computed <paramref name="offsets"/> to <paramref name="source"/>'s angles in order (count must
    /// match). Used after a sync pass; the actual mutation is wrapped in a command by the caller so it is undoable.
    /// </summary>
    public static void ApplyOffsets(MulticamSource source, IReadOnlyList<Timecode> offsets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(offsets);
        if (offsets.Count != source.Angles.Count)
            throw new ArgumentException("There must be one offset per angle.", nameof(offsets));
        for (int i = 0; i < source.Angles.Count; i++)
            source.Angles[i].SyncOffset = offsets[i];
    }
}
