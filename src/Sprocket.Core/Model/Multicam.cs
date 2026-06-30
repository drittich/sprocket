using Sprocket.Core.Timing;

namespace Sprocket.Core.Model;

/// <summary>A stable, serialization-friendly identifier for a <see cref="MulticamSource"/> in the project.</summary>
public readonly record struct MulticamId(Guid Value)
{
    /// <summary>Creates a fresh unique id.</summary>
    public static MulticamId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// One camera angle in a <see cref="MulticamSource"/> (PLAN.md step 24): a source to draw video (and audio)
/// from, plus the <see cref="SyncOffset"/> that aligns it with the other angles. Plain data so the whole
/// synced group serializes with the project and command undo is a simple field capture.
/// </summary>
/// <remarks>
/// <para>The angles share a common <em>multicam time</em> (the synced group's timeline). At multicam time
/// <c>s</c>, this angle's source frame is at <c>s + <see cref="SyncOffset"/></c> — so a positive offset means the
/// angle's recording runs <em>ahead</em> of the reference angle and a negative one means it lags. Sync analysis
/// (by timecode, in/out markers, or audio-waveform cross-correlation) computes these offsets; see
/// <see cref="ClipSync"/>.</para>
/// <para>An angle's audio defaults to the same <see cref="MediaRefId"/> as its video; a separately-recorded sound
/// source (dual-system audio) can be named by <see cref="AudioMediaRefId"/>.</para>
/// </remarks>
public sealed class MulticamAngle
{
    /// <summary>Creates an angle. <paramref name="audioMediaRefId"/> defaults to <paramref name="mediaRefId"/>
    /// (audio comes from the same file as the picture).</summary>
    public MulticamAngle(string name, MediaRefId mediaRefId, Timecode syncOffset = default, MediaRefId? audioMediaRefId = null)
    {
        Name = name ?? string.Empty;
        MediaRefId = mediaRefId;
        SyncOffset = syncOffset;
        AudioMediaRefId = audioMediaRefId;
    }

    /// <summary>Display name (e.g. "Cam 1", "Wide", "GoPro").</summary>
    public string Name { get; set; }

    /// <summary>The source this angle's video is decoded from.</summary>
    public MediaRefId MediaRefId { get; set; }

    /// <summary>The source this angle's audio is pulled from, or <see langword="null"/> to use
    /// <see cref="MediaRefId"/> (audio from the same file as the picture). Set for dual-system sound.</summary>
    public MediaRefId? AudioMediaRefId { get; set; }

    /// <summary>The source time that aligns to multicam time 0 — the per-angle offset the sync step computes.
    /// At multicam time <c>s</c> the angle's source frame is at <c>s + SyncOffset</c>. May be negative.</summary>
    public Timecode SyncOffset { get; set; }

    /// <summary>The source this angle's audio actually comes from (<see cref="AudioMediaRefId"/> if set, else
    /// the video <see cref="MediaRefId"/>).</summary>
    public MediaRefId EffectiveAudioRefId => AudioMediaRefId ?? MediaRefId;

    /// <summary>A deep copy of this angle (used when cloning a multicam source).</summary>
    public MulticamAngle Clone() => new(Name, MediaRefId, SyncOffset, AudioMediaRefId);
}

/// <summary>
/// A synced group of camera angles exposed to the render graph as a single switchable source (PLAN.md step 24).
/// This is the "specialized synced sequence" the plan describes: the angles are aligned by their per-angle
/// <see cref="MulticamAngle.SyncOffset"/>s, and a <see cref="ClipKind.Multicam"/> clip placed on a timeline plays
/// whichever angle is <see cref="Clip.ActiveAngle"/> at each point — switching angles lays down cuts (each clip
/// segment carries its own active angle). Because the active angle resolves to an ordinary media frame at the
/// synced source time, multicam rides the existing media seam (the same seam nested sequences and proxies use,
/// ARCHITECTURE.md §17) — preview, export, and the audio mixer all work unchanged.
/// </summary>
public sealed class MulticamSource
{
    /// <summary>Creates a multicam source with the given id, name, and (optionally pre-built) angles.</summary>
    public MulticamSource(MulticamId id, string name, IEnumerable<MulticamAngle>? angles = null)
    {
        Id = id;
        Name = name ?? string.Empty;
        if (angles is not null)
            Angles.AddRange(angles);
    }

    /// <summary>Stable id used by clips (<see cref="Clip.SourceMulticamId"/>) to reference this group.</summary>
    public MulticamId Id { get; }

    /// <summary>Display name (e.g. "Multicam 1").</summary>
    public string Name { get; set; }

    /// <summary>The camera angles, in angle order (angle index 0 is the first/reference angle). Edited through
    /// the command stack so changes are undoable.</summary>
    public List<MulticamAngle> Angles { get; } = new();

    /// <summary>The angle at <paramref name="index"/>, or <see langword="null"/> if the index is out of range
    /// (a stale <see cref="Clip.ActiveAngle"/> after angles were removed renders as nothing, §15).</summary>
    public MulticamAngle? AngleAt(int index) =>
        index >= 0 && index < Angles.Count ? Angles[index] : null;
}
